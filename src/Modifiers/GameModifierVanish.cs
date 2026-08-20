using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// An activated superpower: press Inspect Weapon to disappear completely for a couple of seconds -
/// invisible AND holding nothing at all - then reappear with everything you were carrying, on a
/// cooldown.
///
/// Reworked from the old "FullInvisibility", which was a passive: permanently invisible, knife only,
/// and unable to buy or pick anything up for the whole round. Nothing about that behaviour survives -
/// outside the brief vanish window the player is completely normal, fully visible with their real
/// loadout. The internal Name changed too ("FullInvisibility" -> "Vanish"), so any config.jsonc
/// DisabledModifiers entry or admin command still naming the old one no longer matches.
///
/// Trigger: Inspect Weapon, polled off IPlayer.PressedButtons exactly as GameModifierFlanker
/// does. GameButtonFlags.F is CS2's IN_LOOK_AT_WEAPON - SwiftlyS2 names button flags after their
/// default keybind rather than the engine action, so the "F" here is the bind, not the letter. No
/// rising-edge tracking is needed: firing pushes the cooldown into the future, so a held key simply
/// fails the readiness check on every subsequent tick.
///
/// Invisibility reuses GameModifierInvisibleBase, which hides players by blocking network transmit
/// per-viewer rather than by touching render alpha, and already handles the death/spawn/spectator/
/// x-ray resync cases. The one thing that has to change for an activated power is CheckHidePlayer:
/// it must report "hidden" only while a vanish is actually running, otherwise the base's own resync
/// (on spawn, on someone else dying, etc.) would re-hide a player whose window had already expired.
///
/// Weapon handling deliberately uses the ammo-preserving strip/restore pair: a plain GiveItem hands
/// back a weapon at full default ammo, which on a ~20s cooldown would quietly turn this into a free
/// full reload on demand. The bomb is never touched (WEAPONTYPE_C4 is excluded), so plant/defuse is
/// unaffected, matching every other stripper in this codebase.
/// </summary>
public sealed class GameModifierVanish : GameModifierInvisibleBase
{
    private const int GaugeBarWidth = 20;
    private const float HtmlRefreshIntervalSeconds = 0.1f;
    private const int HtmlDurationMs = 400;

    private readonly Dictionary<int, List<CSRollUtils.StrippedWeapon>> _cachedItems = [];
    private readonly Dictionary<int, float> _vanishEndsAt = [];
    private readonly Dictionary<int, float> _nextAvailableTime = [];
    private readonly Dictionary<int, float> _lastHtmlUpdateTime = [];

    private Guid _spawnHookId;

    public GameModifierVanish()
    {
        Name = "Vanish";
        Description = "Press Inspect to vanish briefly";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = [
            "ConditionalInvisibility",
            "RandomLoadout", "WalkingGrenadier",
        ];
    }

    /// <summary>Live config values substituted into the translated description's "{duration}"/"{cooldown}" tokens, so the text always matches what's actually configured (same pattern as PlantAnywhere).</summary>
    public override IReadOnlyDictionary<string, string>? DynamicTextTokens => new Dictionary<string, string>
    {
        ["duration"] = $"{Runtime.Config.Vanish.DurationSeconds:0.#}s",
        ["cooldown"] = $"{Runtime.Config.Vanish.CooldownSeconds:0.#}s",
    };

    /// <summary>
    /// Only while a vanish is actually running - not merely "is this player assigned the modifier".
    /// The base class re-evaluates this on spawn/death/spectator resync, so returning true for any
    /// assigned player (what the old passive version did) would strand them invisible long after
    /// their window expired.
    /// </summary>
    protected override bool CheckHidePlayer(IPlayer player) => IsVanished(player.Slot);

    private bool IsVanished(int slot) => _vanishEndsAt.ContainsKey(slot);

    private HashSet<CSWeaponType> StripTypes => Runtime.Config.Vanish.RemoveKnife
        ? CSRollUtils.AllRangedAndKnifeWeaponTypes
        : CSRollUtils.AllRangedWeaponTypes;

    protected override void OnRegistered()
    {
        base.OnRegistered();
        Core.Event.OnClientDisconnected += OnClientDisconnected;
    }

    protected override void OnUnregistered()
    {
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        base.OnUnregistered();
    }

    protected override void OnEnabled()
    {
        base.OnEnabled();

        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);
        Core.GameHooks.Weapons.CanUse.Pre += OnCanUseWeapon;
        Core.GameHooks.Items.CanAcquire.Pre += OnCanAcquireItem;
        Core.Event.OnTick += OnTick;

        // Seeded rather than left ready, so the power can't be used the instant a round starts -
        // same reasoning (and same config shape) as Flanker's round-start delay.
        var readyAt = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.Vanish.RoundStartCooldownSeconds;
        foreach (var player in GetAssignedPlayers())
        {
            _nextAvailableTime[player.Slot] = readyAt;
        }
    }

    /// <summary>Seeds the cooldown for players handed this modifier while it's already active - without this they'd default to "ready now" and could vanish immediately (see GameModifierBase.OnSlotsAdded).</summary>
    protected override void OnSlotsAdded(IReadOnlyCollection<int> slots)
    {
        var readyAt = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.Vanish.RoundStartCooldownSeconds;
        foreach (var slot in slots)
        {
            _nextAvailableTime[slot] = readyAt;
        }
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_spawnHookId);
        Core.GameHooks.Weapons.CanUse.Pre -= OnCanUseWeapon;
        Core.GameHooks.Items.CanAcquire.Pre -= OnCanAcquireItem;
        Core.Event.OnTick -= OnTick;

        // Anyone caught mid-vanish has to be put back before state is dropped, or the modifier ending
        // (round change, !removemodifier, a re-roll) would leave them permanently weaponless. Round
        // start does Deactivate()+Activate(), so this runs far more often than "the modifier is over".
        foreach (var slot in _vanishEndsAt.Keys.ToList())
        {
            EndVanish(slot);
        }

        _cachedItems.Clear();
        _vanishEndsAt.Clear();
        _nextAvailableTime.Clear();
        _lastHtmlUpdateTime.Clear();

        base.OnDisabled();
    }

    private void OnTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;

        foreach (var player in GetAssignedPlayers())
        {
            var slot = player.Slot;

            if (!player.IsAlive)
            {
                // Dying mid-vanish still has to release the state, otherwise the cached weapons are
                // never handed back and CheckHidePlayer keeps reporting hidden after they respawn.
                if (IsVanished(slot))
                {
                    EndVanish(slot);
                }

                continue;
            }

            if (_vanishEndsAt.TryGetValue(slot, out var endsAt))
            {
                if (now >= endsAt)
                {
                    EndVanish(slot);
                }
            }
            else if (now >= _nextAvailableTime.GetValueOrDefault(slot, now) &&
                     player.PressedButtons.HasFlag(GameButtonFlags.F))
            {
                StartVanish(player, now);
            }

            RefreshStatusHtml(player, now);
        }
    }

    private void StartVanish(IPlayer player, float now)
    {
        var slot = player.Slot;

        var removed = CSRollUtils.StripWeaponTypesWithAmmo(player, StripTypes);
        if (removed.Count > 0)
        {
            _cachedItems[slot] = removed;
        }

        _vanishEndsAt[slot] = now + Runtime.Config.Vanish.DurationSeconds;

        // Cooldown is measured from when the vanish ENDS, not from when it started, so the two phases
        // run back to back. That's what lets the HUD bar read as one continuous meter: it drains to
        // empty over the vanish, then refills from exactly empty over the cooldown. Overlapping them
        // would leave the bar already part-charged the instant the drain hit zero.
        _nextAvailableTime[slot] = now + Runtime.Config.Vanish.DurationSeconds + Runtime.Config.Vanish.CooldownSeconds;

        HidePlayer(player);
    }

    /// <summary>
    /// Ends a vanish and puts the player fully back: visible again, weapons returned with the ammo
    /// they had. Written against a slot rather than an IPlayer so it can also run from OnDisabled and
    /// from the death path, where the player may no longer be resolvable.
    /// </summary>
    private void EndVanish(int slot)
    {
        _vanishEndsAt.Remove(slot);

        if (Core.PlayerManager.GetPlayer(slot) is not { IsValid: true } player)
        {
            _cachedItems.Remove(slot);
            return;
        }

        UnhidePlayer(player);

        if (_cachedItems.Remove(slot, out var cached) && player.IsAlive)
        {
            CSRollUtils.RestoreWeaponsWithAmmo(Core, player, cached);
        }
    }

    /// <summary>
    /// Three lines: the modifier name in the same gold the reveal uses for a rolled modifier name, a
    /// meter, then a status line.
    ///
    /// The meter runs as one continuous cycle rather than only showing cooldown. While vanished it
    /// DRAINS from full to empty, so the bar itself is the "you are still invisible" countdown; the
    /// moment it empties the vanish ends and it starts refilling over the cooldown. Because the
    /// cooldown is timed from the end of the vanish (see StartVanish), the drain hits empty exactly
    /// as the refill begins, with no jump between the two phases.
    ///
    /// The prompt names the action rather than a key: a client's actual bind can't be read
    /// server-side, so "Inspect Weapon" is the honest label (it's F by default).
    /// </summary>
    private void RefreshStatusHtml(IPlayer player, float now)
    {
        var slot = player.Slot;
        if (_lastHtmlUpdateTime.TryGetValue(slot, out var lastUpdate) && now - lastUpdate < HtmlRefreshIntervalSeconds)
        {
            return;
        }

        // Stay off the center-HTML surface while the roll's own reveal owns it - see
        // ModifierRuntime.IsModifierHudSuppressed.
        if (Runtime.IsModifierHudSuppressed)
        {
            return;
        }

        _lastHtmlUpdateTime[slot] = now;

        var readyAt = _nextAvailableTime.GetValueOrDefault(slot, now);

        float ratio;
        string barColor;
        string statusLine;

        if (_vanishEndsAt.TryGetValue(slot, out var endsAt))
        {
            // Draining: the bar IS the invisibility countdown.
            var duration = Math.Max(0.01f, Runtime.Config.Vanish.DurationSeconds);
            ratio = Math.Clamp((endsAt - now) / duration, 0f, 1f);
            barColor = "lime";
            statusLine = "<span color=\"lime\" class=\"fontWeight-Bold\">Active</span>";
        }
        else if (readyAt > now)
        {
            // Refilling: starts at exactly empty because the cooldown began when the vanish ended.
            var cooldown = Math.Max(0.01f, Runtime.Config.Vanish.CooldownSeconds);
            ratio = Math.Clamp((cooldown - (readyAt - now)) / cooldown, 0f, 1f);
            barColor = CSRollUtils.GetGaugeBarColor(ratio);
            statusLine = "<span color=\"red\" class=\"fontWeight-Bold\">Charging</span>";
        }
        else
        {
            ratio = 1f;
            barColor = "gold";
            statusLine = "<span class=\"fontWeight-Bold\">Press </span>" +
                         "<span color=\"gold\" class=\"fontWeight-Bold\">Inspect Weapon</span>" +
                         "<span class=\"fontWeight-Bold\"> to activate</span>";
        }

        var html = "<span color=\"gold\" class=\"fontWeight-Bold\">Vanish</span><br/>" +
                   BuildBarOnlyHtml(ratio, barColor) + "<br/>" +
                   statusLine;

        player.SendCenterHTML(html, HtmlDurationMs);
    }

    /// <summary>
    /// Just the bar, without BuildGaugeHtml's own label line - this HUD supplies its own name line
    /// above and its own status line below, so the shared helper's built-in label would insert a
    /// blank line between them.
    /// </summary>
    private static string BuildBarOnlyHtml(float ratio, string barColor)
    {
        var clamped = Math.Clamp(ratio, 0f, 1f);
        var filled = (int)Math.Round(clamped * GaugeBarWidth);
        var empty = GaugeBarWidth - filled;

        var filledSegment = filled > 0 ? $"<span color=\"{barColor}\">{new string('█', filled)}</span>" : "";
        var emptySegment = empty > 0 ? $"<span color=\"grey\">{new string('░', empty)}</span>" : "";

        return $"<span class=\"fontWeight-Bold {CSRollUtils.MonoFontClass}\">[{filledSegment}{emptySegment}] {(int)Math.Round(clamped * 100f),3}%</span>";
    }

    /// <summary>Re-seeds the cooldown on respawn and clears any vanish that somehow survived the life, mirroring Flanker's per-spawn reset.</summary>
    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            if (IsVanished(player.Slot))
            {
                EndVanish(player.Slot);
            }

            _nextAvailableTime[player.Slot] = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.Vanish.RoundStartCooldownSeconds;
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// Blocks weapon use only while vanished - the whole point is being defenceless for those couple
    /// of seconds. Outside the window the player is completely unrestricted, unlike the old passive
    /// version which blocked ranged weapons permanently.
    /// </summary>
    private void OnCanUseWeapon(ref CanUseWeaponPreContext ctx)
    {
        var player = ctx.Params.Player;
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot) || !IsVanished(player.Slot))
        {
            return;
        }

        // Bug fix (inherited from the previous implementation, still load-bearing): SetReturn alone
        // is a no-op - without CancelOriginal the native function still runs afterward and allows the
        // use anyway.
        ctx.SetReturn(false);
        ctx.SetHookResult(HookResult.CancelOriginal);
    }

    /// <summary>Stops a vanished player from simply picking up a dropped weapon mid-window, which would defeat the disarm entirely.</summary>
    private void OnCanAcquireItem(ref CanAcquireItemPreContext ctx)
    {
        var player = ctx.Params.Player;
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot) || !IsVanished(player.Slot))
        {
            return;
        }

        // See OnCanUseWeapon - SetReturn needs SetHookResult(CancelOriginal) to actually stick.
        ctx.SetReturn(AcquireResult.NotAllowedByProhibition);
        ctx.SetHookResult(HookResult.CancelOriginal);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _cachedItems.Remove(@event.PlayerId);
        _vanishEndsAt.Remove(@event.PlayerId);
        _nextAvailableTime.Remove(@event.PlayerId);
        _lastHtmlUpdateTime.Remove(@event.PlayerId);
    }
}
