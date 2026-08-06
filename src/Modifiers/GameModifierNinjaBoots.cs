using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Silent footsteps: cancels EventPlayerFootstep on Pre for the assigned player.
/// SwiftlyS2.Shared.Misc.HookResult.Stop "cancels the executions of following hooks AND the
/// original function" (per its own doc comment) - the original function here is what actually
/// drives the native footstep sound, not just other plugins' handlers, so this genuinely silences
/// it rather than just suppressing a notification. Same technique long used by SourceMod/CS:GO
/// "silent steps" plugins for the equivalent event.
///
/// Landing-thud caveat: there's no distinct "player landed" event to hook the same way.
/// EventPlayerFalldamage only fires above the fall-damage threshold, and it's unconfirmed whether
/// cancelling it would ALSO cancel the fall damage itself (Stop cancels "the original function",
/// which for that event may include applying the damage, not just playing a sound) - granting free
/// fall-damage immunity as an unrequested side effect of muting one landing thud isn't an acceptable
/// trade, so EventPlayerFalldamage is deliberately left alone. A normal (non-damaging) landing likely
/// reuses the same footstep-sound system already silenced above; a hard landing's distinct thud may
/// not be - needs live confirmation either way.
///
/// Auto-equips a defuse kit on spawn if the assigned player is CT - a silent CT approaching the
/// bombsite with no defuser would be a strange, half-finished "ninja" fantasy.
/// </summary>
public sealed class GameModifierNinjaBoots : GameModifierBase
{
    private Guid _footstepHookId;
    private Guid _spawnHookId;

    public GameModifierNinjaBoots()
    {
        Name = "NinjaBoots";
        Description = "No footstep sounds - move in complete silence (CTs also get a free defuse kit)";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        _footstepHookId = Core.GameEvent.HookPre<EventPlayerFootstep>(OnPlayerFootstep);
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot))
            {
                GiveDefuserIfCT(player);
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_footstepHookId);
        Core.GameEvent.Unhook(_spawnHookId);
    }

    private HookResult OnPlayerFootstep(EventPlayerFootstep @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            return HookResult.Stop;
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            GiveDefuserIfCT(player);
        }

        return HookResult.Continue;
    }

    private static void GiveDefuserIfCT(IPlayer player)
    {
        if (player.Controller is not { IsValid: true, Team: Team.CT } || player.PlayerPawn?.ItemServices is not { } itemServices)
        {
            return;
        }

        itemServices.HasDefuser = true;
        itemServices.HasDefuserUpdated();
    }
}
