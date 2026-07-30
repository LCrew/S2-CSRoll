using System.Threading;

using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace CSRoll.Modifiers;

/// <summary>
/// The assigned player's thrown smoke grenades deal periodic damage to enemies standing inside
/// them. Tracks each smoke's live AbsOrigin every tick (not a cached detonation point, since smoke
/// can drift) via Core.Scheduler.RepeatBySeconds, checking distance against CS2's ~144-unit smoke
/// radius. The CancellationTokenSource RepeatBySeconds returns is how the per-smoke timer gets
/// stopped on expire/disable - same cancellation pattern used elsewhere for repeating timers.
///
/// Also gives the assigned player a single smoke grenade automatically (on activation, and again on
/// every spawn since it's used up/reset each life/round) - unlike GrenadesOnly's HE, this is a single
/// grenade, not resupplied after each throw, since the point is "your smokes are dangerous", not
/// "unlimited smokes".
/// </summary>
public sealed class GameModifierPoisonSmoke : GameModifierBase
{
    private const float SmokeRadius = 144f;
    private const float TickIntervalSeconds = 1f;
    private const string SmokeGrenadeName = "weapon_smokegrenade";

    private readonly Dictionary<int, CancellationTokenSource> _activeSmokeTimers = [];
    private Guid _detonateHookId;
    private Guid _expiredHookId;
    private Guid _spawnHookId;

    public GameModifierPoisonSmoke()
    {
        Name = "PoisonSmoke";
        Description = "Your thrown smokes deal damage to enemies standing in them";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        _detonateHookId = Core.GameEvent.HookPost<EventSmokegrenadeDetonate>(OnSmokegrenadeDetonate);
        _expiredHookId = Core.GameEvent.HookPost<EventSmokegrenadeExpired>(OnSmokegrenadeExpired);
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        foreach (var player in Core.PlayerManager.GetAlive())
        {
            if (IsAssignedTo(player.Slot))
            {
                GiveSmoke(player);
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_detonateHookId);
        Core.GameEvent.Unhook(_expiredHookId);
        Core.GameEvent.Unhook(_spawnHookId);

        foreach (var token in _activeSmokeTimers.Values)
        {
            token.Cancel();
        }

        _activeSmokeTimers.Clear();
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            GiveSmoke(player);
        }

        return HookResult.Continue;
    }

    private static void GiveSmoke(IPlayer player)
    {
        player.PlayerPawn?.ItemServices?.GiveItem(SmokeGrenadeName);
    }

    private HookResult OnSmokegrenadeDetonate(EventSmokegrenadeDetonate @event)
    {
        var thrower = @event.UserIdPlayer;
        if (thrower is not { IsValid: true } || !IsAssignedTo(thrower.Slot) || thrower.Controller is not { } throwerController)
        {
            return HookResult.Continue;
        }

        var entityIndex = (uint)@event.EntityID;
        var throwerTeam = throwerController.Team;

        _activeSmokeTimers[(int)entityIndex] = Core.Scheduler.RepeatBySeconds(TickIntervalSeconds, () => DamageEnemiesInSmoke(entityIndex, throwerTeam));

        return HookResult.Continue;
    }

    private HookResult OnSmokegrenadeExpired(EventSmokegrenadeExpired @event)
    {
        if (_activeSmokeTimers.Remove((int)@event.EntityID, out var token))
        {
            token.Cancel();
        }

        return HookResult.Continue;
    }

    private void DamageEnemiesInSmoke(uint entityIndex, Team throwerTeam)
    {
        if (Core.EntitySystem.GetEntityByIndex<CSmokeGrenadeProjectile>(entityIndex) is not { IsValid: true } smoke || smoke.AbsOrigin is not { } smokeOrigin)
        {
            return;
        }

        foreach (var player in Core.PlayerManager.GetAlive())
        {
            if (player.Controller?.Team == throwerTeam || player.PlayerPawn?.AbsOrigin is not { } playerOrigin)
            {
                continue;
            }

            if (smokeOrigin.Distance(playerOrigin) <= SmokeRadius)
            {
                player.TakeDamage(Runtime.Config.PoisonSmoke.DamagePerTick, DamageTypes_t.DMG_POISON, smoke, smoke);
            }
        }
    }
}
