using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Killing an enemy gives a chance to revive one random dead teammate. Reacts after the kill
/// (EventPlayerDeath Post) rather than intercepting anything - unlike Revive, there's nothing to
/// prevent here. Gated on the killer's slot and excludes suicide/team-kills, mirroring
/// GameModifierVampire's guard exactly.
///
/// The chance is rolled once per activation (fresh each time this modifier is applied to a player)
/// from Config.Saint.Min/MaxRevivePercent, not re-rolled per kill - so different activations get
/// different odds within that range, but the odds stay fixed for the life of one activation.
/// </summary>
public sealed class GameModifierSaint : GameModifierBase
{
    private float? _rolledRevivePercent;
    private Guid _deathHookId;

    private string RollText => _rolledRevivePercent is { } percent
        ? $"{percent:0.#}%"
        : $"{Runtime.Config.Saint.MinRevivePercent:0.#}-{Runtime.Config.Saint.MaxRevivePercent:0.#}%";

    public override IReadOnlyDictionary<string, string>? DynamicTextTokens => new Dictionary<string, string> { ["rand%"] = RollText };

    public override string Description => $"{RollText} chance for a kill to revive a dead teammate";

    public GameModifierSaint()
    {
        Name = "Saint";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        var min = Runtime.Config.Saint.MinRevivePercent;
        var max = Runtime.Config.Saint.MaxRevivePercent;
        _rolledRevivePercent = min + (float)(Random.Shared.NextDouble() * (max - min));

        _deathHookId = Core.GameEvent.HookPost<EventPlayerDeath>(OnPlayerDeath);
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_deathHookId);
        _rolledRevivePercent = null;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event)
    {
        var attacker = @event.AttackerPlayer;
        var victim = @event.UserIdPlayer;

        if (attacker is not { IsValid: true } || !IsAssignedTo(attacker.Slot) || victim is not { IsValid: true })
        {
            return HookResult.Continue;
        }

        if (attacker.SteamID == victim.SteamID || attacker.Controller.Team == victim.Controller.Team)
        {
            return HookResult.Continue;
        }

        if (Random.Shared.NextDouble() * 100 >= (_rolledRevivePercent ?? 0f))
        {
            return HookResult.Continue;
        }

        CSRollUtils.GetRandomDeadTeammate(Core, attacker.Controller.Team)?.Respawn();

        return HookResult.Continue;
    }
}
