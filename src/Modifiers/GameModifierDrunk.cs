using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace CSRoll.Modifiers;

/// <summary>
/// Mirrors A/D (left/right strafe) and nothing else: pressing A moves the player right and vice versa.
/// Forward/back, jumping and mouse look are untouched. Uses GameHooks.Movement.RunCommand (not the
/// older Events.OnMovementServicesRunCommandHook, which SwiftlyS2 marks obsolete in favor of this) -
/// same Pre/Post-context pattern as GameModifierDamage.cs's TakeDamage hook.
///
/// Bug fix: this used to negate only the usercmd's analog Leftmove. A CS2 usercmd describes the same
/// strafe input three ways - that analog value, the IN_MOVELEFT/IN_MOVERIGHT bits in the button
/// state, and the subtick move steps that record exactly when inside the tick a key went down or up
/// (with an analog delta of their own) - and the movement code reads all of them. Flipping just one
/// left the mirrored analog value disagreeing with unmirrored buttons and subtick steps, which in the
/// air cancelled the strafe out entirely ("can't air strafe with neither of the buttons"). All three
/// are now mirrored together, so the server sees one consistent, mirrored input - A behaves exactly
/// like vanilla D, including for air-strafing.
///
/// The player's own client still predicts its movement from the keys actually pressed - nothing a
/// server can send flips a client's bindings - so a strafe shows a small correction as the server's
/// mirrored result arrives. That is inherent to mirroring server-side, not something left unfixed.
/// </summary>
public sealed class GameModifierDrunk : GameModifierBase
{
    private const ulong MoveLeft = (ulong)InputBitMask_t.IN_MOVELEFT;
    private const ulong MoveRight = (ulong)InputBitMask_t.IN_MOVERIGHT;

    public GameModifierDrunk()
    {
        Name = "Drunk";
        Description = "Left and right movement (A/D) is mirrored";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        Core.GameHooks.Movement.RunCommand.Pre += OnRunCommand;
    }

    protected override void OnDisabled()
    {
        Core.GameHooks.Movement.RunCommand.Pre -= OnRunCommand;
    }

    private void OnRunCommand(ref RunCommandMovementPreContext ctx)
    {
        if (ctx.Params.Player is not { IsValid: true } player || !IsAssignedTo(player.Slot))
        {
            return;
        }

        var userCmd = ctx.Params.UserCmd;
        var baseCmd = userCmd.CSGOUserCmd.Base;

        baseCmd.Leftmove = -baseCmd.Leftmove;

        var subtickMoves = baseCmd.SubtickMoves;
        for (var i = 0; i < subtickMoves.Count; i++)
        {
            var step = subtickMoves.Get(i);
            step.Button = MirrorStrafe(step.Button);
            step.AnalogLeftDelta = -step.AnalogLeftDelta;
        }

        var buttons = userCmd.ButtonState;
        buttons.ButtonPressed = (GameButtonFlags)MirrorStrafe((ulong)buttons.ButtonPressed);
        buttons.ButtonChanged = (GameButtonFlags)MirrorStrafe((ulong)buttons.ButtonChanged);
        buttons.ButtonScroll = (GameButtonFlags)MirrorStrafe((ulong)buttons.ButtonScroll);

        // The protobuf copy of the same button state, kept in agreement with the native one above in
        // case anything downstream re-reads it.
        var buttonsPb = baseCmd.ButtonsPb;
        buttonsPb.Buttonstate1 = MirrorStrafe(buttonsPb.Buttonstate1);
        buttonsPb.Buttonstate2 = MirrorStrafe(buttonsPb.Buttonstate2);
        buttonsPb.Buttonstate3 = MirrorStrafe(buttonsPb.Buttonstate3);
    }

    /// <summary>Swaps the IN_MOVELEFT and IN_MOVERIGHT bits, leaving every other button as it was.</summary>
    private static ulong MirrorStrafe(ulong buttons)
    {
        var mirrored = buttons & ~(MoveLeft | MoveRight);
        if ((buttons & MoveLeft) != 0)
        {
            mirrored |= MoveRight;
        }

        if ((buttons & MoveRight) != 0)
        {
            mirrored |= MoveLeft;
        }

        return mirrored;
    }
}
