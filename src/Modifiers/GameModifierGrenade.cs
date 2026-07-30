using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>Flashbangs blind for a randomized, longer duration.</summary>
public sealed class GameModifierLongerFlashes : GameModifierBase
{
    private Guid _blindHookId;

    public GameModifierLongerFlashes()
    {
        Name = "LongerFlashes";
        Description = "Flash bang effect lasts 3 times longer";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        _blindHookId = Core.GameEvent.HookPre<EventPlayerBlind>(OnPlayerBlind);
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_blindHookId);
    }

    private HookResult OnPlayerBlind(EventPlayerBlind @event)
    {
        if (@event.UserIdPawn is not { } pawn || @event.AttackerPlayer is not { IsValid: true } attacker || !IsAssignedTo(attacker.Slot))
        {
            return HookResult.Continue;
        }

        var duration = 1.0f + Random.Shared.Next(1, 10);
        @event.BlindDuration = duration;

        pawn.FlashDuration = duration;
        pawn.FlashDurationUpdated();

        // Bug fix: FlashDuration alone wasn't enough - BlindUntilTime is the actual absolute
        // end-time the engine reads to decide how long the blind effect lasts. Without setting
        // this too, the randomized duration above had no real effect on how long players stayed blind.
        pawn.BlindUntilTime.Value = Core.Engine.GlobalVars.CurrentTime + duration;

        return HookResult.Continue;
    }
}

/// <summary>
/// Randomizes HE and flashbang fuse timers. Smoke grenades have no known equivalent detonate-time
/// field to randomize (same limitation the original CSS plugin had).
///
/// Flashbangs additionally have their own CFlashbangProjectile.TimeToDetonate field (a relative
/// fuse length in seconds), separate from the inherited CBaseGrenade.DetonateTime (an absolute
/// GameTime_t) both types share - both are written now, since it wasn't confirmed which one the
/// engine actually reads for flashbangs specifically. The write also happens both immediately at
/// spawn AND again one tick later (same precomputed fuse value both times, so there's no
/// inconsistency) - a single deferred-only write didn't produce any noticeable randomization,
/// consistent with the grenade locking in its own think-schedule before the deferred write landed.
/// </summary>
public sealed class GameModifierRandomGrenadeTime : GameModifierBase
{
    public GameModifierRandomGrenadeTime()
    {
        Name = "DodgyGrenades";
        Description = "Timers on flashes and HE's are randomized";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        Core.Event.OnEntitySpawned += OnEntitySpawned;
    }

    protected override void OnDisabled()
    {
        Core.Event.OnEntitySpawned -= OnEntitySpawned;
    }

    private void OnEntitySpawned(IOnEntitySpawnedEvent @event)
    {
        if (@event.Entity.DesignerName is not ("hegrenade_projectile" or "flashbang_projectile"))
        {
            return;
        }

        var grenade = @event.Entity.As<CBaseCSGrenadeProjectile>();
        var isFlashbang = @event.Entity.DesignerName == "flashbang_projectile";
        var fuseSeconds = isFlashbang ? 1f + Random.Shared.NextSingle() * 4f : Random.Shared.Next(1, 12);

        // Bug fix: writing only once, deferred a tick via NextWorldUpdate, apparently lost a race
        // against whatever internal think-schedule the grenade sets up for itself right at spawn -
        // the timer never appeared to vary. Writing both immediately AND again next tick (same
        // precomputed fuseSeconds both times, so there's no inconsistency between the two writes)
        // covers whichever timing point the engine actually locks the fuse in at.
        //
        // Bug fix 2: the thrower/IsAssignedTo check used to happen synchronously here, before either
        // write - but CBaseCSGrenadeProjectile.Thrower isn't reliably populated yet at the exact
        // instant OnEntitySpawned fires, so GetThrowerPlayer() returned null and IsAssignedTo(-1)
        // failed for every grenade whenever this modifier was scoped to specific player(s) (the
        // default RandomizePlayers=true mode) - timers silently never randomized for anyone. In
        // global mode (SupportsPerPlayerRandomization=false, like RainbowSmokes) this never showed up
        // since an empty AssignedSlots makes IsAssignedTo always true regardless of thrower
        // resolution - that's why RainbowSmokes worked and this didn't. The check now happens inside
        // ApplyFuse itself, re-evaluated at both the immediate and next-tick write, so it only needs
        // Thrower to be resolved by then rather than at entity-spawn time.
        void ApplyFuse(string when)
        {
            var thrower = CSRollUtils.GetThrowerPlayer(Core, grenade);
            var assigned = IsAssignedTo(thrower?.Slot ?? -1);

            if (Runtime.DebugMode)
            {
                Core.Logger.LogInformation(
                    "[CSRoll] DodgyGrenades ({When}): thrower={Thrower} assigned={Assigned} fuse={Fuse:0.##}s",
                    when, thrower?.Controller is { IsValid: true } c ? c.PlayerName : "unresolved", assigned, fuseSeconds);
            }

            if (!assigned)
            {
                return;
            }

            grenade.DetonateTime.Value = Core.Engine.GlobalVars.CurrentTime + fuseSeconds;
            grenade.DetonateTimeUpdated();

            if (isFlashbang)
            {
                grenade.As<CFlashbangProjectile>().TimeToDetonate = fuseSeconds;
            }
        }

        ApplyFuse("immediate");
        Core.Scheduler.NextWorldUpdate(() => ApplyFuse("deferred"));
    }
}

/// <summary>Smoke grenades pop in a random color.</summary>
public sealed class GameModifierRainbowSmokes : GameModifierBase
{
    private static readonly Vector[] Colors =
    [
        new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 255, 0), new(255, 0, 255),
        new(0, 255, 255), new(255, 128, 0), new(128, 0, 255), new(255, 192, 203), new(128, 128, 128),
        new(255, 255, 255), new(0, 128, 0), new(128, 0, 0),
    ];

    public GameModifierRainbowSmokes()
    {
        Name = "RainbowSmokes";
        Description = "Smokes colors are randomized";
        SupportsRandomRounds = true;
        // Not per-player randomizable: the visible effect (a smoke pops in a random color) reads as
        // a global cosmetic quirk to every observer regardless of which specific player's roll
        // triggered it - rolling it per-player just duplicates the same experience for no
        // distinguishable per-player difference.
        SupportsPerPlayerRandomization = false;
    }

    protected override void OnEnabled()
    {
        Core.Event.OnEntitySpawned += OnEntitySpawned;
    }

    protected override void OnDisabled()
    {
        Core.Event.OnEntitySpawned -= OnEntitySpawned;
    }

    private void OnEntitySpawned(IOnEntitySpawnedEvent @event)
    {
        if (@event.Entity.DesignerName != "smokegrenade_projectile")
        {
            return;
        }

        var grenade = @event.Entity.As<CSmokeGrenadeProjectile>();
        if (!IsAssignedTo(CSRollUtils.GetThrowerPlayer(Core, grenade)?.Slot ?? -1))
        {
            return;
        }

        Core.Scheduler.NextWorldUpdate(() =>
        {
            // Bug fix vs. the CSS original: it never called the dirty-flag equivalent here,
            // so the color change may not have replicated to other clients.
            grenade.SmokeColor = Colors[Random.Shared.Next(Colors.Length)];
            grenade.SmokeColorUpdated();
        });
    }
}
