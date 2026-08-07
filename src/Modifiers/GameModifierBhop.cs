using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;

using Microsoft.Extensions.Logging;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// EXPERIMENTAL: a previous Bhop attempt was removed entirely per explicit request - the
/// landing-penalty-removal half worked, but the auto-jump-without-repressing half was attempted via
/// ProcessMovement.Pre velocity injection and never worked reliably.
///
/// Second attempt: hooking CheckJumpButtonLegacy/Modern.Pre and writing moveData.Velocity.Z there,
/// then SetHookResult(CancelOriginal) to skip the native's own debounce logic. Live testing (debug
/// log confirmed the override fired every tick while grounded and holding jump) showed no actual
/// jump ever happened. Root cause: CancelOriginal only skips that one check-and-decide function, not
/// the rest of that tick's movement pipeline (friction/ground-move) that still runs immediately
/// afterward - if the native function normally also flips the player to "airborne" internally before
/// that later code runs, skipping it entirely means the rest of the tick still treats the player as
/// grounded and silently re-absorbs the velocity we just wrote before it ever becomes visible. The
/// exact same failure class as Jetpack's original ProcessMovement.Post floor, just one hook earlier
/// in the pipeline.
///
/// Third attempt (current): don't touch the movement-hook pipeline at all. Detect "grounded and
/// holding jump" from Core.Event.OnTick (outside any movement hook, so nothing downstream can
/// re-process and clobber the write within the same tick) and directly re-assert velocity via
/// IPlayer.Teleport(velocity: ...) - the exact technique that fixed Jetpack's own hold-to-thrust for
/// the identical underlying problem. A single real jump (first press) is left completely untouched -
/// the native CheckJumpButton functions run normally for that, unaffected, since nothing hooks them
/// anymore; this modifier only re-launches the player on ticks it detects them already grounded
/// again while still holding jump.
///
/// Hold-detection uses Core.Event.OnClientKeyStateChanged (confirmed working for both this and
/// Jetpack) rather than polling IPlayer.PressedButtons.
///
/// Landing-penalty removal: CS2's own anti-bhop mechanic reduces max speed based on
/// CCSPlayer_MovementServices.Stamina (a "fatigue" value that rises on each jump and decays over
/// time) - zeroed every tick for the assigned player so it never accumulates.
///
/// Speed cap: removing the timing skill (auto-relaunch) and the stamina penalty still isn't enough
/// to let bhop-gained speed actually show up - CS2's normal speed cap silently clamps it away.
///
/// Bug fix: first attempt used CCSPlayerPawn.VelocityModifier (the same mechanism Speedhack/LeadBoots
/// use) - but that's a flat multiplier applied to ALL movement, including normal ground running, so
/// it also sped up ordinary walking/running, not just bhop-gained air speed - reported as wrong,
/// since the point was to stop capping bhop speed, not hand out a general speed boost. Switched to
/// raising IMoveData.MaxSpeed/ClientMaxSpeed from inside AirAccelerate.Pre instead - that hook only
/// ever fires during actual air-strafing (never for ground movement), so it structurally cannot
/// affect normal running speed, only the ceiling on speed gained while airborne.
///
/// Movement Unlocker patch: on top of all of the above, OnEnabled/OnDisabled additionally
/// apply/revert a binary patch (see resources/gamedata/signatures.jsonc + patches.jsonc, key
/// "MovementUnlocker") that NOPs out CS2's own native anti-bunnyhop landing-speed-cap check. This
/// is the "real" server-side fix for the same problem the AirAccelerate.Pre multiplier above is
/// patched around from userland - it doesn't touch jump timing at all, so genuine bhop skill
/// (jump + air-strafe timing) is still required to build up any speed from it; it only stops the
/// game clamping that speed back down. Bug fix: the bytes originally ported from
/// Fallen-Networks/CS2-MovementUnlocker failed to resolve at all after a CS2 update - replaced
/// with Source2ZE/CS2Fixes's actively-maintained equivalent (see signatures.jsonc's comment). The
/// patch has zero per-player scoping (it's a single process-wide binary edit), so for as long as
/// Bhop is active it's in effect for every player on the server that round, not just whoever rolled it -
/// wrapped in try/catch since a CS2 update shifting the underlying bytes would otherwise crash
/// modifier activation instead of just failing to find the signature.
/// </summary>
public sealed class GameModifierBhop : GameModifierBase
{
    private const string MovementUnlockerPatch = "MovementUnlocker";

    private readonly Dictionary<int, bool> _isHoldingSpace = [];

    public GameModifierBhop()
    {
        Name = "Bhop";
        Description = "Hold jump to bunny-hop automatically, with no landing speed penalty";
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
        Core.Event.OnClientKeyStateChanged += OnClientKeyStateChanged;
        Core.GameHooks.Movement.AirAccelerate.Pre += OnAirAccelerate;
        Core.Event.OnTick += OnGameTick;

        try
        {
            Core.GameData.ApplyPatch(MovementUnlockerPatch);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[CSRoll] Bhop: failed to apply MovementUnlocker patch - signature may be out of date for this CS2 build.");
        }
    }

    protected override void OnDisabled()
    {
        Core.Event.OnClientKeyStateChanged -= OnClientKeyStateChanged;
        Core.GameHooks.Movement.AirAccelerate.Pre -= OnAirAccelerate;
        Core.Event.OnTick -= OnGameTick;
        _isHoldingSpace.Clear();

        try
        {
            Core.GameData.RevertPatch(MovementUnlockerPatch);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[CSRoll] Bhop: failed to revert MovementUnlocker patch.");
        }
    }

    private void OnClientKeyStateChanged(IOnClientKeyStateChangedEvent @event)
    {
        if (@event.Key != KeyKind.Space || !IsAssignedTo(@event.PlayerId))
        {
            return;
        }

        _isHoldingSpace[@event.PlayerId] = @event.Pressed;
    }

    private void OnAirAccelerate(ref AirAccelerateMovementPreContext ctx)
    {
        var player = ctx.Params.Player;
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return;
        }

        var moveData = ctx.Params.MoveData;
        moveData.MaxSpeed *= Runtime.Config.Bhop.SpeedMultiplier;
        moveData.ClientMaxSpeed *= Runtime.Config.Bhop.SpeedMultiplier;
    }

    private void OnGameTick()
    {
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (!IsAssignedTo(player.Slot) || !player.IsAlive)
            {
                continue;
            }

            if (player.PlayerPawn?.MovementServices is { } movementServices)
            {
                movementServices.Stamina = 0f;
                movementServices.StaminaUpdated();
            }

            TryAutoJump(player);
        }
    }

    private void TryAutoJump(IPlayer player)
    {
        if (!_isHoldingSpace.GetValueOrDefault(player.Slot, false))
        {
            return;
        }

        if (player.PlayerPawn is not { } pawn || pawn.GroundEntity.Value is null)
        {
            // Airborne (or no pawn) - nothing to relaunch; a real jump or existing arc runs untouched.
            return;
        }

        var velocity = pawn.AbsVelocity;
        player.Teleport(velocity: new Vector(velocity.X, velocity.Y, Runtime.Config.Bhop.JumpVelocityZ));

        if (Runtime.DebugMode)
        {
            Core.Logger.LogInformation("[CSRoll] Bhop ({Slot}): auto-jumped, set VelocityZ={VelZ}", player.Slot, Runtime.Config.Bhop.JumpVelocityZ);
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _isHoldingSpace.Remove(@event.PlayerId);
    }
}
