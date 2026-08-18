using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;

namespace CSRoll.Modifiers;

/// <summary>
/// The assigned player doesn't see thrown smoke grenades at all - reuses the exact per-viewer
/// IPlayer.ShouldBlockTransmitEntity mechanic already proven for Xray's relay/glow props this
/// session, applied to the smoke projectile entity itself instead. Everyone else still sees the
/// smoke normally, since the block is per-viewer, not global.
/// </summary>
public sealed class GameModifierSmokeImmunity : GameModifierBase
{
    private readonly HashSet<int> _blockedEntityIndices = [];
    private Guid _detonateHookId;
    private Guid _expiredHookId;

    public GameModifierSmokeImmunity()
    {
        Name = "SmokeImmunity";
        Description = "Smokes don't render for you at all";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        _detonateHookId = Core.GameEvent.HookPost<EventSmokegrenadeDetonate>(OnSmokegrenadeDetonate);
        _expiredHookId = Core.GameEvent.HookPost<EventSmokegrenadeExpired>(OnSmokegrenadeExpired);
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_detonateHookId);
        Core.GameEvent.Unhook(_expiredHookId);

        foreach (var entityIndex in _blockedEntityIndices)
        {
            UnblockForAssignedViewers(entityIndex);
        }

        _blockedEntityIndices.Clear();
    }

    private HookResult OnSmokegrenadeDetonate(EventSmokegrenadeDetonate @event)
    {
        var entityIndex = (int)@event.EntityID;
        _blockedEntityIndices.Add(entityIndex);

        foreach (var viewer in GetAssignedPlayers())
        {
            viewer.ShouldBlockTransmitEntity(entityIndex, true);
        }

        return HookResult.Continue;
    }

    private HookResult OnSmokegrenadeExpired(EventSmokegrenadeExpired @event)
    {
        var entityIndex = (int)@event.EntityID;
        if (_blockedEntityIndices.Remove(entityIndex))
        {
            UnblockForAssignedViewers(entityIndex);
        }

        return HookResult.Continue;
    }

    private void UnblockForAssignedViewers(int entityIndex)
    {
        foreach (var viewer in GetAssignedPlayers())
        {
            viewer.ShouldBlockTransmitEntity(entityIndex, false);
        }
    }
}
