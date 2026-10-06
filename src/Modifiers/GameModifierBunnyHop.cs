using System.Globalization;

using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;

using Microsoft.Extensions.Logging;

namespace CSRoll.Modifiers;

/// <summary>
/// Hold jump to bunny-hop, gaining speed with every hop, with the ground speed cap unlocked.
///
/// History, condensed: the auto-jump half went through ProcessMovement.Pre velocity injection,
/// CheckJumpButton.Pre with CancelOriginal, and finally an OnTick IPlayer.Teleport relaunch; the
/// speed-cap half went through VelocityModifier, an AirAccelerate.Pre MaxSpeed raise, the
/// MovementUnlocker binary patch, and finally the bhop convar set replicated to the player's own
/// client only (IConVar.ReplicateToClientAsString). That last combination is what was reported as
/// "doesn't feel like the correct bhop, not smooth", and the reason is structural: replication only
/// changes what the player's CLIENT predicts with. The server kept simulating the same player with the
/// real values - autobhop off, sv_airaccelerate 12, full stamina - so client and server disagreed on
/// every landing and every strafe, and each disagreement came back as a correction. The OnTick
/// relaunch made it worse: it ran after the tick's movement, so the server had the player on the
/// ground for a tick (friction and all) that the client, predicting autobhop, never had.
///
/// Current design:
///
/// Hold-to-hop: the engine's own autobhop, enabled per player through Runtime.ConVarOverrides, which
/// switches the real convars to the bhop values only while the server simulates THIS player and
/// replicates the same values to their client. Server and client run identical movement code with
/// identical values, so the native jump is predicted correctly and holding jump re-jumps on the exact
/// landing tick on both. Everyone else on the server keeps normal jumping - hold-to-hop is strictly
/// for whoever rolled this.
///
/// Speed gain: OnJumpLegacy/OnJumpModern.Post (the same hook point Jetpack's jump boost proved
/// survives native code) scales horizontal velocity up by SpeedGainPerJump on every real takeoff, up to
/// MaxHopSpeed. The base is the larger of the speeds before and after the native jump, so any speed
/// clamp the jump itself applies can't eat the gain. This one is server-only - no convar expresses it
/// - so the client sees each hop's extra speed one round trip late, as a small forward correction.
///
/// Movement unlocker: CS2 also clamps a grounded player's speed to their max speed, which would cut
/// every gained hop back down on the landing tick. That clamp is removed with the CS2Fixes
/// "ServerMovementUnlock" patch (resources/gamedata), applied while this modifier is active. It is a
/// single edit to the server binary, so it is server-wide - explicitly fine per request ("I am fine
/// with everyone having the Speed/Movement unlocked"); only hold-to-hop and the speed gain are per
/// player. Friction still applies on the ground, so anyone not hopping slows back down within a few
/// ticks, which is why this doesn't turn into a ground speed hack for everyone else.
/// </summary>
public sealed class GameModifierBunnyHop : GameModifierBase
{
    /// <summary>Must match the entry name in resources/gamedata/patches.jsonc.</summary>
    private const string MovementUnlockPatch = "CSRollServerMovementUnlock";

    /// <summary>Below this horizontal speed a jump is a standing hop with no meaningful direction to boost along.</summary>
    private const float MinSpeedForGain = 50f;

    /// <summary>A real takeoff adds the jump impulse (~301 u/s) to vertical speed. OnJump also fires for presses that don't take off (see Jetpack's history), which change nothing - this tells them apart.</summary>
    private const float MinTakeoffImpulse = 100f;

    private readonly Dictionary<int, (float HorizontalSpeed, float VerticalSpeed)> _preJump = [];
    private readonly Dictionary<int, int> _lastGainTick = [];

    private Guid _spawnHookId;
    private bool _movementUnlocked;

    public GameModifierBunnyHop()
    {
        Name = "BunnyHop";
        Description = "Hold jump to bunny-hop automatically - every hop is faster than the last";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        Core.GameHooks.Movement.OnJumpLegacy.Pre += OnJumpLegacyPre;
        Core.GameHooks.Movement.OnJumpLegacy.Post += OnJumpLegacyPost;
        Core.GameHooks.Movement.OnJumpModern.Pre += OnJumpModernPre;
        Core.GameHooks.Movement.OnJumpModern.Post += OnJumpModernPost;
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        var overrides = BuildConVarOverrides();
        foreach (var player in GetAssignedPlayers())
        {
            Runtime.ConVarOverrides.Set(this, player.Slot, overrides);
        }

        if (Runtime.Config.BunnyHop.MovementUnlocker)
        {
            ApplyMovementUnlock();
        }
    }

    protected override void OnDisabled()
    {
        Core.GameHooks.Movement.OnJumpLegacy.Pre -= OnJumpLegacyPre;
        Core.GameHooks.Movement.OnJumpLegacy.Post -= OnJumpLegacyPost;
        Core.GameHooks.Movement.OnJumpModern.Pre -= OnJumpModernPre;
        Core.GameHooks.Movement.OnJumpModern.Post -= OnJumpModernPost;
        Core.GameEvent.Unhook(_spawnHookId);

        Runtime.ConVarOverrides.RemoveAll(this);
        RevertMovementUnlock();

        _preJump.Clear();
        _lastGainTick.Clear();
    }

    /// <summary>Handed BunnyHop mid-round (ButterflyEffect, Mimic, !memodifier on an already-active modifier) - OnEnabled doesn't re-run for these, so without this they'd get the speed gain but no hold-to-hop.</summary>
    protected override void OnSlotsAdded(IReadOnlyCollection<int> slots)
    {
        var overrides = BuildConVarOverrides();
        foreach (var slot in slots)
        {
            Runtime.ConVarOverrides.Set(this, slot, overrides);
        }
    }

    protected override void OnSlotsRemoved(IReadOnlyCollection<int> slots)
    {
        foreach (var slot in slots)
        {
            Runtime.ConVarOverrides.Remove(this, slot);
            _preJump.Remove(slot);
            _lastGainTick.Remove(slot);
        }
    }

    /// <summary>
    /// The standard bhop-server set, minus everything that isn't needed for it: sv_maxvelocity's
    /// default (3500) is already far above MaxHopSpeed, and sv_accelerate_use_weapon_speed only
    /// changes ground acceleration. The stamina trio stops CS2's jump/landing fatigue from slowing
    /// each hop - zeroing the pawn's Stamina field every tick (the old approach) raced the movement
    /// code that applies the penalty, and the client never knew about it.
    /// </summary>
    private IReadOnlyList<(string Name, string Value)> BuildConVarOverrides() =>
    [
        ("sv_autobunnyhopping", "1"),
        ("sv_enablebunnyhopping", "1"),
        ("sv_airaccelerate", Runtime.Config.BunnyHop.AirAccelerate.ToString(CultureInfo.InvariantCulture)),
        ("sv_staminamax", "0"),
        ("sv_staminajumpcost", "0"),
        ("sv_staminalandcost", "0"),
    ];

    /// <summary>A player joining mid-round while this is active globally (no assigned slots - !rolltoggle) is in scope but was never handed the overrides. Set() is idempotent for players who already have them.</summary>
    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            Runtime.ConVarOverrides.Set(this, player.Slot, BuildConVarOverrides());
        }

        return HookResult.Continue;
    }

    private void ApplyMovementUnlock()
    {
        if (_movementUnlocked)
        {
            return;
        }

        try
        {
            Core.GameData.ApplyPatch(MovementUnlockPatch);
            _movementUnlocked = true;
        }
        catch (Exception ex)
        {
            // Either the signature went stale with a CS2 update (re-check CS2Fixes'
            // gamedata/cs2fixes.jsonc "ServerMovementUnlock" - see signatures.jsonc), or another
            // plugin already applied the same patch, which changes the very bytes the signature
            // looks for. Hold-to-hop and the speed gain still work without it.
            Core.Logger.LogWarning(ex, "[CSRoll] BunnyHop: couldn't apply the movement unlocker patch - out of date for this CS2 build, or already applied by another plugin.");
        }
    }

    private void RevertMovementUnlock()
    {
        if (!_movementUnlocked)
        {
            return;
        }

        _movementUnlocked = false;

        try
        {
            Core.GameData.RevertPatch(MovementUnlockPatch);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[CSRoll] BunnyHop: failed to revert the movement unlocker patch.");
        }
    }

    private void OnJumpLegacyPre(ref OnJumpLegacyMovementPreContext ctx) => RecordPreJump(ctx.Params.Player, ctx.Params.MoveData);
    private void OnJumpLegacyPost(ref OnJumpLegacyMovementPostContext ctx) => ApplyHopSpeedGain(ctx.Params.Player, ctx.Params.MoveData);
    private void OnJumpModernPre(ref OnJumpModernMovementPreContext ctx) => RecordPreJump(ctx.Params.Player, ctx.Params.MoveData);
    private void OnJumpModernPost(ref OnJumpModernMovementPostContext ctx) => ApplyHopSpeedGain(ctx.Params.Player, ctx.Params.MoveData);

    private void RecordPreJump(IPlayer? player, IMoveData moveData)
    {
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return;
        }

        var velocity = moveData.Velocity;
        _preJump[player.Slot] = (HorizontalSpeed(velocity), velocity.Z);
    }

    private void ApplyHopSpeedGain(IPlayer? player, IMoveData moveData)
    {
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return;
        }

        var slot = player.Slot;
        var config = Runtime.Config.BunnyHop;
        if (config.SpeedGainPerJump <= 0f || !_preJump.Remove(slot, out var before))
        {
            return;
        }

        var velocity = moveData.Velocity;
        if (velocity.Z - before.VerticalSpeed < MinTakeoffImpulse)
        {
            return;
        }

        // Both jump variants are hooked because which one CS2 runs isn't confirmed - should both ever
        // fire for one takeoff, the gain must still only land once.
        var tick = Core.Engine.GlobalVars.TickCount;
        if (_lastGainTick.TryGetValue(slot, out var lastTick) && lastTick == tick)
        {
            return;
        }

        var current = HorizontalSpeed(velocity);
        if (current < MinSpeedForGain)
        {
            return;
        }

        var target = Math.Min(Math.Max(current, before.HorizontalSpeed) + config.SpeedGainPerJump, config.MaxHopSpeed);
        if (target <= current)
        {
            // Already at or past the cap (air-strafing can get there) - the cap only limits the
            // gain, it never slows anyone down.
            return;
        }

        _lastGainTick[slot] = tick;

        var scale = target / current;
        moveData.Velocity = new Vector(velocity.X * scale, velocity.Y * scale, velocity.Z);

        if (Runtime.DebugMode)
        {
            Core.Logger.LogInformation("[CSRoll] BunnyHop ({Slot}): hop {From:0} -> {To:0} u/s", slot, current, target);
        }
    }

    private static float HorizontalSpeed(Vector velocity) => MathF.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y));
}
