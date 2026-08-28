using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

using CSRoll.Core;
using CSRoll.Hud;

namespace CSRoll.Modifiers;

/// <summary>
/// Kill (or assist on) an enemy and you copy one of their modifiers for yourself. Doing it again
/// replaces what you took - only ever one stolen modifier at a time, so this is a "wear your last
/// victim's power" effect rather than an accumulating snowball.
///
/// Copying, not taking: the victim keeps their own modifier. Stealing it outright would mean a
/// player's round-defining modifier could be removed by someone else's kill, which is a much larger
/// (and much more frustrating) change than what was asked for.
///
/// Only per-player-scoped modifiers can be copied. Runtime.GetGrantableModifiersForSlot already
/// filters out globally-scoped actives (they apply to the thief too, so "stealing" one is a silent
/// no-op) as well as anything incompatible with what the thief is already running - so a steal that
/// finds no legal candidate simply doesn't fire, rather than producing a broken pairing.
/// </summary>
public sealed class GameModifierMimic : GameModifierBase
{
    /// <summary>What each thief currently has stolen, so the next steal knows what to hand back first. One entry per slot, by design - see the class comment.</summary>
    private readonly Dictionary<int, GameModifierBase> _stolen = [];

    private Guid _deathHookId;

    public GameModifierMimic()
    {
        Name = "Mimic";
        Description = "Steal a modifier from whoever you kill";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    /// <summary>Drawn above the modifier it steals, so the block reads as "Mimic, currently: X" rather than two unrelated HUDs stacked in arbitrary order.</summary>
    private const int HudPriority = 10;

    protected override void OnRegistered()
    {
        Core.Event.OnClientDisconnected += OnClientDisconnected;
    }

    protected override void OnUnregistered()
    {
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
    }

    protected override void OnEnabled()
    {
        _deathHookId = Core.GameEvent.HookPost<EventPlayerDeath>(OnPlayerDeath);
        Core.Event.OnTick += OnTick;
    }

    /// <summary>
    /// Releases what was stolen by a thief who is no longer one - reachable because ButterflyEffect
    /// can hand THIS modifier out and then re-roll it away again. Without this the stolen modifier
    /// would stay scoped to that player with nothing left driving or ending it. See
    /// GameModifierBase.OnSlotsRemoved.
    /// </summary>
    protected override void OnSlotsRemoved(IReadOnlyCollection<int> slots)
    {
        foreach (var slot in slots)
        {
            ReleaseSteal(slot);
        }
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_deathHookId);
        Core.Event.OnTick -= OnTick;

        // Stolen modifiers are scoped onto players by this one, so they have to be handed back when
        // it ends - otherwise a steal would outlive the round that produced it. Round start does
        // Deactivate() + Activate(), so this runs every round, not just on a real removal.
        foreach (var slot in _stolen.Keys.ToList())
        {
            ReleaseSteal(slot);
        }

        _stolen.Clear();
    }

    /// <summary>Removes the dictionary entry BEFORE revoking, so the OnSlotsRemoved that revoking may trigger on the stolen modifier can't re-enter this and revoke it a second time.</summary>
    private void ReleaseSteal(int slot)
    {
        if (_stolen.Remove(slot, out var stolen))
        {
            Runtime.RevokeModifierFromSlot(stolen, slot);
        }
    }

    /// <summary>
    /// Publishes the "what am I currently wearing" block. Reads "&lt;none&gt;" until the first steal
    /// lands, so a carrier can tell the modifier is working rather than assuming it's inert before
    /// they've killed anyone.
    /// </summary>
    private void OnTick()
    {
        foreach (var player in GetAssignedPlayers())
        {
            var header = "<span color=\"gold\" class=\"fontWeight-Bold\">Mimic</span>";

            // See GameModifierButterflyEffect.PublishIdleHud - the name is omitted when the stolen
            // modifier draws its own block directly beneath, so it isn't printed twice in a row.
            if (_stolen.TryGetValue(player.Slot, out var stolen) && HasHud(stolen, player.Slot))
            {
                SetHud(player.Slot, header, HudPriority);
                continue;
            }

            var activeName = stolen is not null
                ? CSRollUtils.GetModifierDisplayName(Core, stolen)
                : "&lt;none&gt;";

            SetHud(player.Slot,
                header + "<br/>" +
                $"<span color=\"orange\" class=\"fontWeight-Bold\">Active:</span> {activeName}",
                HudPriority);
        }
    }

    /// <summary>Slots are recycled by the next player to join, so a stale entry here would make OnDisabled revoke a modifier from someone who never stole anything.</summary>
    private void OnClientDisconnected(IOnClientDisconnectedEvent @event) => _stolen.Remove(@event.PlayerId);

    private HookResult OnPlayerDeath(EventPlayerDeath @event)
    {
        if (@event.UserIdPlayer is not { IsValid: true } victim)
        {
            return HookResult.Continue;
        }

        TrySteal(@event.AttackerPlayer, victim);

        if (Runtime.Config.Mimic.CountAssists)
        {
            TrySteal(@event.AssisterPlayer, victim);
        }

        return HookResult.Continue;
    }

    private void TrySteal(IPlayer? thief, IPlayer victim)
    {
        // Suicides and team-damage self-kills both surface as attacker == victim; a player shouldn't
        // be able to farm their own modifier off their own death.
        if (thief is not { IsValid: true } || thief.Slot == victim.Slot || !IsAssignedTo(thief.Slot))
        {
            return;
        }

        var previous = _stolen.GetValueOrDefault(thief.Slot);
        var grantable = Runtime.GetGrantableModifiersForSlot(thief.Slot, this);

        // The already-stolen one is admitted explicitly: it's scoped onto the thief right now, so
        // GetGrantableModifiersForSlot correctly excludes it as "already on them" - but if the new
        // victim happens to be running the same modifier, it's still a legitimate outcome of this
        // steal, and dropping it from the pool would bias the roll away from a duplicate.
        var candidates = Runtime.GetModifiersForSlot(victim.Slot)
            .Where(m => grantable.Contains(m) || m == previous)
            .ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        var stolen = candidates[Random.Shared.Next(candidates.Count)];

        // Re-stealing what they already have is a no-op, not a revoke-then-regrant: the round trip
        // would run the modifier's full Deactivate()/Activate() cycle (re-stripping weapons,
        // re-seeding cooldowns, replaying its announcement) for no change in what they're carrying.
        if (stolen == previous)
        {
            return;
        }

        if (previous is not null)
        {
            ReleaseSteal(thief.Slot);
        }

        if (!Runtime.GrantModifierToSlot(stolen, thief.Slot))
        {
            return;
        }

        _stolen[thief.Slot] = stolen;

        if (Runtime.Config.Mimic.AnnounceSteals)
        {
            CSRollUtils.PrintTitleToChatColored(Core, thief, $"Mimicked [gold]{CSRollUtils.GetModifierDisplayName(Core, stolen)}[default] from [gold]{victim.Name}[default]!");
        }
    }

    /// <summary>
    /// Mimic has no clock - it shows whatever modifier has been stolen, or nothing yet. Returning a
    /// Ready timer rather than null gives the row a readout without a bar.
    /// </summary>
    public override HudTimer? GetHudTimer(int slot)
    {
        if (!IsAssignedTo(slot))
        {
            return null;
        }

        return _stolen.TryGetValue(slot, out var stolen)
            ? HudTimer.Ready(CSRollUtils.GetModifierDisplayName(Core, stolen))
            : HudTimer.Ready("NONE");
    }

}
