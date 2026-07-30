using SwiftlyS2.Shared.GameHooks;

namespace CSRoll.Modifiers;

/// <summary>
/// Mirrors A/D (left/right strafe): negates the incoming usercmd's analog Leftmove value every
/// tick, before the engine derives wish-direction from it, so pressing A moves the player right
/// and vice versa. Forward/back and mouse look are left untouched. Uses GameHooks.Movement.RunCommand
/// (not the older Events.OnMovementServicesRunCommandHook, which SwiftlyS2 marks obsolete in favor
/// of this) - same Pre/Post-context pattern as GameModifierDamage.cs's TakeDamage hook.
/// </summary>
public sealed class GameModifierDrunk : GameModifierBase
{
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
        if (!IsAssignedTo(ctx.Params.Player.Slot))
        {
            return;
        }

        var baseCmd = ctx.Params.UserCmd.CSGOUserCmd.Base;
        baseCmd.Leftmove = -baseCmd.Leftmove;
    }
}
