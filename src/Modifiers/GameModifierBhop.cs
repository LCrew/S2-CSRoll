using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;

using Microsoft.Extensions.Logging;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// EXPERIMENTAL: a previous Bhop attempt was removed entirely per explicit request - the
/// landing-penalty-removal half worked, but the auto-jump-without-repressing half was attempted via
/// ProcessMovement.Pre velocity injection (the same fragile "doesn't survive the native code that
/// runs right after it" mechanism SuperJump's own jump-height fix also failed with on its first try)
/// and never worked reliably. This retries the auto-jump half via a completely different, more direct
/// hook point: CCSPlayer_MovementServices::CheckJumpButtonLegacy/Modern (Core.GameHooks.Movement.
/// CheckJumpButtonLegacy/Modern) - the actual native functions that decide whether the jump button,
/// combined with sv_autobunnyhopping's server-wide debounce logic, should result in a jump this tick.
/// Hooking Pre and unconditionally applying our own jump velocity (then CancelOriginal to skip the
/// native's own decision entirely) for the assigned player replicates what that cvar does internally,
/// but scoped to one player instead of the whole server. Hold-detection uses
/// Core.Event.OnClientKeyStateChanged (confirmed working for Jetpack's own hold-to-thrust) rather than
/// polling IPlayer.PressedButtons.
///
/// Known caveat, not yet resolved by live testing: CancelOriginal skips the ENTIRE native function
/// body for that call, not just its debounce check - so whatever else it normally does (jump sound,
/// player_jump game event, animation state) does not fire for our own auto-triggered jumps. Only the
/// very first, real jump (which a normal, non-debounced CheckJumpButton call already lets through
/// before our override matters) gets the native's full treatment.
///
/// Landing-penalty removal: CS2's own anti-bhop mechanic reduces max speed based on
/// CCSPlayer_MovementServices.Stamina (a "fatigue" value that rises on each jump and decays over
/// time) - zeroed every tick for the assigned player via OnGameTick so it never accumulates.
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
        Core.GameHooks.Movement.CheckJumpButtonLegacy.Pre += OnCheckJumpButtonLegacy;
        Core.GameHooks.Movement.CheckJumpButtonModern.Pre += OnCheckJumpButtonModern;
        Core.Event.OnTick += OnGameTick;
    }

    protected override void OnDisabled()
    {
        Core.Event.OnClientKeyStateChanged -= OnClientKeyStateChanged;
        Core.GameHooks.Movement.CheckJumpButtonLegacy.Pre -= OnCheckJumpButtonLegacy;
        Core.GameHooks.Movement.CheckJumpButtonModern.Pre -= OnCheckJumpButtonModern;
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

    private void OnCheckJumpButtonLegacy(ref CheckJumpButtonLegacyMovementPreContext ctx)
    {
        if (TryAutoJump(ctx.Params.Player, ctx.Params.MoveData))
        {
            ctx.SetHookResult(HookResult.CancelOriginal);
        }
    }

    private void OnCheckJumpButtonModern(ref CheckJumpButtonModernMovementPreContext ctx)
    {
        if (TryAutoJump(ctx.Params.Player, ctx.Params.MoveData))
        {
            ctx.SetHookResult(HookResult.CancelOriginal);
        }
    }

    private bool TryAutoJump(IPlayer? player, IMoveData moveData)
    {
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return false;
        }

        if (!_isHoldingSpace.GetValueOrDefault(player.Slot, false))
        {
            return false;
        }

        // Only override while grounded - airborne, there's nothing to debounce, and the native
        // function should just run normally (in particular, it must not be cancelled mid-air, or a
        // real single jump would never actually resolve while a later re-check is pending).
        if (player.PlayerPawn?.GroundEntity.Value is null)
        {
            return false;
        }

        moveData.Velocity = new Vector(moveData.Velocity.X, moveData.Velocity.Y, Runtime.Config.Bhop.JumpVelocityZ);

        if (Runtime.DebugMode)
        {
            Core.Logger.LogInformation("[CSRoll] Bhop ({Slot}): auto-jumped, set VelocityZ={VelZ}", player.Slot, Runtime.Config.Bhop.JumpVelocityZ);
        }

        return true;
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
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _isHoldingSpace.Remove(@event.PlayerId);
    }
}
