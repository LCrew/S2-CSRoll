using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// When a player's HE, molotov/incendiary, or smoke grenade detonates, spawns 2-3 mini grenades
/// of the same type flung outward from the detonation point.
///
/// Bug fix (this was the actual reason minis never exploded, just bounced forever, across two
/// earlier attempts - manually setting IsLive/DetonateTime, then firing the documented
/// "InitializeSpawnFromWorld" entity I/O input): manually building a grenade via
/// CreateEntityByDesignerName + DispatchSpawn never reliably replicated whatever a real throw's
/// internal setup does. SwiftlyS2 exposes purpose-built factory methods for exactly this -
/// Core.Game.EmitHEGrenade/EmitMolotov/EmitSmokeGrenade(pos, angle, velocity, [team,] owner) -
/// which create a grenade the same way the engine's own throw code does, fuse and all. No manual
/// entity setup needed at all now.
///
/// Recursion guard - two different mechanisms since EventHegrenadeDetonate/EventSmokegrenadeDetonate
/// carry an EntityID but EventMolotovDetonate does not:
/// - HE/smoke: spawned mini entity indices (read straight off the Emit* return value) are tracked
///   and skipped when they detonate themselves.
/// - Molotov/incendiary: clearing the mini's Thrower field right after creation (the original idea
///   here) did NOT stop the recursion in testing - EventMolotovDetonate.UserIdPlayer apparently
///   still resolves a valid player even once Thrower is cleared (likely via CBaseGrenade's separate
///   OriginalThrower field, which was never touched). Rather than chase exactly which field the
///   event actually reads, this uses a simple per-player time debounce instead: any molotov
///   detonation from the same player within MolotovRecursionGuardSeconds of the last one they
///   triggered a cluster from is treated as one of the minis and ignored. Minis are flung a short
///   distance and detonate on impact almost immediately, so this window comfortably covers a
///   mini's lifetime without meaningfully blocking a genuine second real throw.
/// </summary>
public sealed class GameModifierClusterGrenades : GameModifierBase
{
    private const int MinClusterCount = 2;
    private const int MaxClusterCount = 3;
    private const float ClusterSpeed = 250f;
    private const float MolotovRecursionGuardSeconds = 1.5f;

    private readonly HashSet<uint> _clusterSpawnedEntityIndices = [];
    private readonly Dictionary<int, float> _lastMolotovClusterTime = [];
    private Guid _heHookId;
    private Guid _molotovHookId;
    private Guid _smokeHookId;

    public GameModifierClusterGrenades()
    {
        Name = "ClusterGrenades";
        Description = "Grenades spawn a mini cluster of grenades when they detonate";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        _heHookId = Core.GameEvent.HookPost<EventHegrenadeDetonate>(OnHegrenadeDetonate);
        _molotovHookId = Core.GameEvent.HookPost<EventMolotovDetonate>(OnMolotovDetonate);
        _smokeHookId = Core.GameEvent.HookPost<EventSmokegrenadeDetonate>(OnSmokegrenadeDetonate);
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_heHookId);
        Core.GameEvent.Unhook(_molotovHookId);
        Core.GameEvent.Unhook(_smokeHookId);
        _clusterSpawnedEntityIndices.Clear();
        _lastMolotovClusterTime.Clear();
    }

    private HookResult OnHegrenadeDetonate(EventHegrenadeDetonate @event)
    {
        if (_clusterSpawnedEntityIndices.Remove((uint)@event.EntityID))
        {
            return HookResult.Continue;
        }

        SpawnCluster(@event.UserIdPlayer, "hegrenade_projectile", new Vector(@event.X, @event.Y, @event.Z));
        return HookResult.Continue;
    }

    private HookResult OnMolotovDetonate(EventMolotovDetonate @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            var now = Core.Engine.GlobalVars.CurrentTime;
            if (_lastMolotovClusterTime.TryGetValue(player.Slot, out var lastTime) && now - lastTime < MolotovRecursionGuardSeconds)
            {
                return HookResult.Continue;
            }

            _lastMolotovClusterTime[player.Slot] = now;
        }

        // Both of weapon_molotov's two projectile classnames ("molotov_projectile" vs
        // "incgrenade_projectile") behave near-identically (fire/inferno) and this event doesn't
        // say which one just detonated, so minis are always spawned as "molotov_projectile"
        // regardless of the original.
        SpawnCluster(@event.UserIdPlayer, "molotov_projectile", new Vector(@event.X, @event.Y, @event.Z));
        return HookResult.Continue;
    }

    private HookResult OnSmokegrenadeDetonate(EventSmokegrenadeDetonate @event)
    {
        if (_clusterSpawnedEntityIndices.Remove((uint)@event.EntityID))
        {
            return HookResult.Continue;
        }

        SpawnCluster(@event.UserIdPlayer, "smokegrenade_projectile", new Vector(@event.X, @event.Y, @event.Z));
        return HookResult.Continue;
    }

    private void SpawnCluster(IPlayer? thrower, string designerName, Vector position)
    {
        if (thrower is not { IsValid: true } || !IsAssignedTo(thrower.Slot) || thrower.PlayerPawn is not { } throwerPawn)
        {
            return;
        }

        var team = thrower.Controller?.Team ?? Team.None;
        var count = Random.Shared.Next(MinClusterCount, MaxClusterCount + 1);
        for (var i = 0; i < count; i++)
        {
            SpawnMiniGrenade(designerName, position, throwerPawn, team);
        }
    }

    private void SpawnMiniGrenade(string designerName, Vector position, CCSPlayerPawn throwerPawn, Team team)
    {
        var angleRadians = Random.Shared.NextSingle() * MathF.Tau;
        var velocity = new Vector(MathF.Cos(angleRadians) * ClusterSpeed, MathF.Sin(angleRadians) * ClusterSpeed, ClusterSpeed * 0.3f);
        var angle = velocity.ToQAngles();

        switch (designerName)
        {
            case "hegrenade_projectile":
                _clusterSpawnedEntityIndices.Add(Core.Game.EmitHEGrenade(position, angle, velocity, throwerPawn).Index);
                break;

            case "smokegrenade_projectile":
                _clusterSpawnedEntityIndices.Add(Core.Game.EmitSmokeGrenade(position, angle, velocity, team, throwerPawn).Index);
                break;

            case "molotov_projectile":
                Core.Game.EmitMolotov(position, angle, velocity, team, throwerPawn);
                break;
        }
    }
}
