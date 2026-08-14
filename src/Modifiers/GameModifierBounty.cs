using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Landing damage on an enemy grants the assigned player bonus money, scaled by the damage dealt
/// and a random multiplier (Config.Bounty.Min/MaxMultiplier) - a bigger hit is a bigger (if still
/// randomized) payout. Hooks EventPlayerHurt rather than a kill/death event, so every landed hit
/// pays out, not just the killing blow, and this fires uniformly regardless of damage source
/// (bullet, HE, molotov/incendiary) since CS2's own player_hurt event reports the attacker the same
/// way for all of them - no separate grenade-specific hook needed.
///
/// Scoped the same way Vampire/MoreDamage scope their attacker-side effect: only IsAssignedTo(attacker)
/// earns a payout from their own hits, not from anyone else's. Self-damage and friendly fire never pay out.
/// </summary>
public sealed class GameModifierBounty : GameModifierBase
{
    private const int MaxAccount = 16000;

    private Guid _hurtHookId;

    public GameModifierBounty()
    {
        Name = "Bounty";
        Description = "Damaging enemies grants bonus money";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        _hurtHookId = Core.GameEvent.HookPost<EventPlayerHurt>(OnPlayerHurt);
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_hurtHookId);
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event)
    {
        // Bug fix: self-hit check used to compare SteamID - bot SteamID is fixed at 0, so bot-vs-
        // different-bot hits were misread as self-hits and silently excluded from bounty payouts.
        if (@event.AttackerPlayer is not { IsValid: true } attacker || @event.UserIdPlayer is not { IsValid: true } victim ||
            CSRollUtils.IsSamePlayer(attacker, victim) || !IsAssignedTo(attacker.Slot) ||
            attacker.Controller is not { IsValid: true } attackerController || victim.Controller is not { IsValid: true } victimController ||
            attackerController.Team == victimController.Team)
        {
            return HookResult.Continue;
        }

        if (attackerController.InGameMoneyServices is not { } moneyServices)
        {
            return HookResult.Continue;
        }

        var min = Runtime.Config.Bounty.MinMultiplier;
        var max = Runtime.Config.Bounty.MaxMultiplier;
        var multiplier = min + (Random.Shared.NextSingle() * (max - min));
        var bonus = (int)(@event.ActualDmgHealth * multiplier);

        moneyServices.Account = Math.Min(MaxAccount, moneyServices.Account + bonus);
        moneyServices.AccountUpdated();

        return HookResult.Continue;
    }
}
