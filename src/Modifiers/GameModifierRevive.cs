using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Chance to survive lethal damage instead of dying, shrinking with each successful revive this
/// life (resets on next spawn). Uses Core.GameHooks.Entities.TakeDamage.Pre - the only interception
/// point that runs before the engine applies damage, unlike EventPlayerHurt/EventPlayerDeath which
/// fire after death is already underway.
///
/// Every life starts at Config.Revive.StartChancePercent (95% by default; it used to be rolled from
/// a 70-90% range per activation) - but instead of escalating UP with each successful revive (the
/// original design), the chance shrinks multiplicatively: after each revive, chance = chance * a
/// fresh random factor between 0.1 and 0.9. So it starts near-certain but gets progressively less
/// reliable the more times it's already saved you this life, rather than becoming increasingly
/// overpowered the longer you survive.
/// </summary>
public sealed class GameModifierRevive : GameModifierBase
{
    private readonly Dictionary<int, float> _currentChancePercent = [];
    private readonly Dictionary<int, int> _reviveCount = [];

    private float? _rolledBasePercent;
    private Guid _spawnHookId;

    private string RollText => $"{_rolledBasePercent ?? Runtime.Config.Revive.StartChancePercent:0.#}%";

    public override IReadOnlyDictionary<string, string>? DynamicTextTokens => new Dictionary<string, string> { ["rand%"] = RollText };

    /// <summary>The player's own chance right now - it shrinks with every revive this life.</summary>
    public override IReadOnlyDictionary<string, string>? DynamicTextTokensFor(int slot) =>
        _rolledBasePercent is not null && _currentChancePercent.TryGetValue(slot, out var current)
            ? new Dictionary<string, string> { ["rand%"] = $"{current:0.#}%" }
            : DynamicTextTokens;

    public override string Description => $"{RollText} chance to survive lethal damage, shrinking with each revive";

    public GameModifierRevive()
    {
        Name = "Revive";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

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
        _rolledBasePercent = Math.Clamp(Runtime.Config.Revive.StartChancePercent, 0f, 100f);

        Core.GameHooks.Entities.TakeDamage.Pre += OnTakeDamage;
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);
    }

    protected override void OnDisabled()
    {
        Core.GameHooks.Entities.TakeDamage.Pre -= OnTakeDamage;
        Core.GameEvent.Unhook(_spawnHookId);

        _currentChancePercent.Clear();
        _reviveCount.Clear();
        _rolledBasePercent = null;
    }

    private void OnTakeDamage(ref TakeDamageEntityPreContext ctx)
    {
        var victim = Core.PlayerManager.GetPlayerFromPawn(ctx.Params.Entity.As<CBasePlayerPawn>());
        if (victim is not { IsValid: true, IsAlive: true } || !IsAssignedTo(victim.Slot) || victim.PlayerPawn is not { IsValid: true } pawn)
        {
            return;
        }

        if (ctx.Params.Info.Damage < pawn.Health)
        {
            return;
        }

        var slot = victim.Slot;
        var chance = _currentChancePercent.GetValueOrDefault(slot, _rolledBasePercent ?? 0f);

        if (Random.Shared.NextDouble() * 100 >= chance)
        {
            return;
        }

        ctx.Params.Info.Damage = 0;
        pawn.Health = Runtime.Config.Revive.HealthAfterRevive;
        pawn.HealthUpdated();

        // Unambiguous confirmation it actually fired - a percentage this size is still a coin flip,
        // so without this it's hard to tell "not working" apart from "just hasn't rolled yet".
        CSRollUtils.PrintTitleToChat(Core, victim, $"You survived a lethal hit! (chance was {chance:0.#}%)");
        Core.Logger.LogInformation("[CSRoll] Revive triggered for slot {Slot} at {Chance}% chance.", slot, chance);

        var reviveCount = _reviveCount.GetValueOrDefault(slot, 0) + 1;
        _reviveCount[slot] = reviveCount;

        var decayFactor = 0.1f + (float)(Random.Shared.NextDouble() * 0.8f);
        _currentChancePercent[slot] = chance * decayFactor;

        // Bug fix: a revive used to leave the player exactly where they took the lethal hit -
        // usually right in front of whoever just shot them, an easy follow-up kill. Now teleported
        // to one of their own team's spawn points instead, same as a normal round respawn. Deferred a
        // tick via NextWorldUpdate rather than teleporting here directly inside TakeDamage.Pre - the
        // engine hasn't finished resolving this hit yet at this point, so re-fetch the player fresh
        // next tick before moving them.
        var team = victim.Controller is { IsValid: true } controller ? controller.Team : Team.None;
        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (Core.PlayerManager.GetPlayer(slot) is { IsValid: true, IsAlive: true } current &&
                CSRollUtils.GetSpawnLocation(Core, team) is { } spawnPosition)
            {
                CSRollUtils.TeleportPlayer(Core, current, spawnPosition);
            }
        });
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        // Bug fix: this used to reset state for every spawning player with no IsAssignedTo check,
        // unlike every other hook in this file (harmless today only because OnTakeDamage re-checks
        // IsAssignedTo before consuming these values, but it let both dictionaries accumulate an
        // entry for every player who ever spawned while Revive was active, not just the assigned
        // one - a foot-gun if that downstream guard is ever refactored away).
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            _currentChancePercent[player.Slot] = _rolledBasePercent ?? 0f;
            _reviveCount[player.Slot] = 0;
        }

        return HookResult.Continue;
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _currentChancePercent.Remove(@event.PlayerId);
        _reviveCount.Remove(@event.PlayerId);
    }
}
