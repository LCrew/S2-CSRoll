using SwiftlyS2.Shared.Events;
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
/// </summary>
public sealed class GameModifierBhop : GameModifierBase
{
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
        Core.Event.OnTick += OnGameTick;
    }

    protected override void OnDisabled()
    {
        Core.Event.OnClientKeyStateChanged -= OnClientKeyStateChanged;
        Core.Event.OnTick -= OnGameTick;
        _isHoldingSpace.Clear();
    }

    private void OnClientKeyStateChanged(IOnClientKeyStateChangedEvent @event)
    {
        if (@event.Key != KeyKind.Space || !IsAssignedTo(@event.PlayerId))
        {
            return;
        }

        _isHoldingSpace[@event.PlayerId] = @event.Pressed;
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
