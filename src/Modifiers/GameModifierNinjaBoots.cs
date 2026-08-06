using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// KNOWN LIMITATION, confirmed not working: cancelling EventPlayerFootstep on Pre does NOT actually
/// silence the footstep sound, contrary to this class's original assumption (carried over from
/// SourceMod/CS:GO-era "silent steps" plugins, a different engine/framework whose event-cancellation
/// semantics don't necessarily transfer to CS2/SwiftlyS2). Live testing confirms footsteps are still
/// audible with this modifier active. Investigated the alternative (a native EmitSound-level hook,
/// the mechanism third-party CS2 tools like AimTux actually use for this) and confirmed via SDK
/// inspection - both the currently-referenced SwiftlyS2.CS2 1.4.3 and the latest available 1.4.4 -
/// that no such hook is exposed anywhere in the public SwiftlyS2.Shared API; the only sound-related
/// surface is SwiftlyS2.Shared.Sounds.SoundEvent (for playing sounds yourself), and an internal-only
/// SwiftlyS2.Core.Natives.NativeSounds.GetClients exists but isn't reachable from plugin code. The
/// EventPlayerFootstep cancellation is left in place as a harmless no-op (in case a future SwiftlyS2
/// version routes this differently) rather than silently removed, but the "no footstep sounds" half
/// of this modifier does not currently work - only the CT auto-defuser half does.
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
