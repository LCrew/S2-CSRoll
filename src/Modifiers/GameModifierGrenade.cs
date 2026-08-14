using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Flashbangs the assigned player throws blind for longer.
///
/// Bug fix: this used to ignore the flash's own naturally-computed blind duration entirely and set a
/// flat random 2-10s regardless of throw distance/angle - not actually a multiplier at all, despite
/// the description's "3 times longer" claim. Now reads @event.BlindDuration (the engine's own
/// already-computed value at the moment this Pre hook fires, before it's overwritten below) and
/// multiplies it by the config-tunable DurationMultiplier - a real multiplier of the natural duration.
/// </summary>
public sealed class GameModifierLongerFlashes : GameModifierBase
{
    private Guid _blindHookId;

    public override IReadOnlyDictionary<string, string>? DynamicTextTokens => new Dictionary<string, string>
    {
        ["mult"] = $"{Runtime.Config.LongerFlashes.DurationMultiplier:0.##}x",
    };

    public override string Description => $"Flash bang effect lasts {Runtime.Config.LongerFlashes.DurationMultiplier:0.##}x longer";

    public GameModifierLongerFlashes()
    {
        Name = "LongerFlashes";
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

        var duration = @event.BlindDuration * Runtime.Config.LongerFlashes.DurationMultiplier;
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
/// Randomizes HE, flashbang, and smoke fuse timers within Config.DodgyGrenades.Min/MaxFuseSeconds
/// (0.1-10s by default).
///
/// Bug fix: smoke grenades used to be skipped entirely on the assumption they had "no known
/// equivalent detonate-time field to randomize" (a limitation carried over from the original CSS
/// plugin) - CSmokeGrenadeProjectile actually inherits CBaseCSGrenadeProjectile.DetonateTime from
/// the exact same base class HE/flashbang already use, confirmed via SDK schema inheritance
/// (CSmokeGrenadeProjectile : CBaseCSGrenadeProjectile), so the existing write mechanism below
/// applies to smoke unchanged - it was simply never tried.
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
        Description = "Timers on grenades are randomized";
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
        if (@event.Entity.DesignerName is not ("hegrenade_projectile" or "flashbang_projectile" or "smokegrenade_projectile"))
        {
            return;
        }

        var grenade = @event.Entity.As<CBaseCSGrenadeProjectile>();
        var isFlashbang = @event.Entity.DesignerName == "flashbang_projectile";
        var min = Runtime.Config.DodgyGrenades.MinFuseSeconds;
        var max = Runtime.Config.DodgyGrenades.MaxFuseSeconds;
        var fuseSeconds = min + (Random.Shared.NextSingle() * (max - min));

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
            // Hardening: the deferred call runs a tick later via NextWorldUpdate - the grenade could
            // in principle have already detonated/been destroyed within that single tick, and this
            // raw entity wrapper was being dereferenced with no validity check. Low-probability (a
            // grenade rarely dies within one tick of spawning) but cheap to guard, and matches the
            // safer index+IsValid pattern already used elsewhere in this codebase (e.g. GameModifierXray).
            if (!grenade.IsValid)
            {
                return;
            }

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

/// <summary>
/// Smoke grenades pop in a random color.
///
/// Bug fix: with RandomizePlayers on (the default), the automatic per-round rotation only ever pulls
/// from the SupportsPerPlayerRandomization pool - a prior "supplementary global roll" that used to
/// also consider global-only modifiers like this one every round was removed entirely (see
/// ModifierRuntime.ApplyRandomRoundsForRound's own comment), so with SupportsPerPlayerRandomization
/// false this could only ever appear via an explicit admin !addmodifier/!addrandommodifier - reported
/// live as "I've never rolled this, is this rollable even?" Flipped to true: the modifier's own
/// IsAssignedTo(thrower) check already scopes the color-swap to just the assigned player's throws
/// regardless, so per-player assignment isn't the redundant no-op the old comment assumed - it's
/// exactly what makes this reachable through the normal rotation at all.
/// </summary>
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
        Description = "Smoke colors are randomized";
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
        if (@event.Entity.DesignerName != "smokegrenade_projectile")
        {
            return;
        }

        var grenade = @event.Entity.As<CSmokeGrenadeProjectile>();

        // Bug fix: the thrower/IsAssignedTo check used to happen synchronously here, before the
        // deferred write below - same root cause DodgyGrenades' own ApplyFuse fix documents in this
        // same file: CBaseCSGrenadeProjectile.Thrower isn't reliably populated yet at the exact
        // instant OnEntitySpawned fires, so GetThrowerPlayer() returned null and IsAssignedTo(-1)
        // failed for every smoke whenever this modifier was scoped to a specific player - silently
        // never recoloring anything. This never showed up while SupportsPerPlayerRandomization was
        // false (an empty AssignedSlots makes IsAssignedTo always true regardless of thrower
        // resolution), but broke the moment it was flipped to true so this modifier could actually be
        // reached through normal per-player rolls/!memodifier (see class doc comment) - the one path
        // it's used through in practice. Resolving the thrower inside the deferred callback instead,
        // by which point Thrower is reliably populated, fixes it the same way DodgyGrenades was fixed.
        Core.Scheduler.NextWorldUpdate(() =>
        {
            // Hardening: see DodgyGrenades.ApplyFuse's matching IsValid guard - this raw entity
            // wrapper is dereferenced a tick later with no validity check.
            if (!grenade.IsValid)
            {
                return;
            }

            var thrower = CSRollUtils.GetThrowerPlayer(Core, grenade);
            if (!IsAssignedTo(thrower?.Slot ?? -1))
            {
                return;
            }

            // Bug fix vs. the CSS original: it never called the dirty-flag equivalent here,
            // so the color change may not have replicated to other clients.
            grenade.SmokeColor = Colors[Random.Shared.Next(Colors.Length)];
            grenade.SmokeColorUpdated();
        });
    }
}
