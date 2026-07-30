using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;

namespace CSRoll.Modifiers;

/// <summary>Attacker heals for the damage they deal.</summary>
public sealed class GameModifierVampire : GameModifierBase
{
    private Guid _hurtHookId;

    public GameModifierVampire()
    {
        Name = "Vampire";
        Description = "You steal the damage you deal";
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

        // Bug fix vs. the CSS original: heal back to each player's CURRENT max health
        // (whatever another active modifier, e.g. Juggernaut, has set it to) instead of
        // a hardcoded 100, which would have silently undone any co-active health modifier.
        foreach (var player in Core.PlayerManager.GetAlive())
        {
            if (!IsAssignedTo(player.Slot) || player.PlayerPawn is not { } pawn)
            {
                continue;
            }

            pawn.Health = pawn.MaxHealth;
            pawn.HealthUpdated();
        }
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event)
    {
        var attacker = @event.AttackerPlayer;
        var damaged = @event.UserIdPlayer;

        if (attacker is not { IsValid: true } || damaged is not { IsValid: true, IsAlive: true } || !IsAssignedTo(attacker.Slot))
        {
            return HookResult.Continue;
        }

        if (attacker.SteamID == damaged.SteamID || attacker.Controller.Team == damaged.Controller.Team)
        {
            return HookResult.Continue;
        }

        if (attacker.PlayerPawn is not { } attackerPawn)
        {
            return HookResult.Continue;
        }

        // If the target was over-killed (health went negative), don't heal the excess overkill damage.
        var damagedHealth = damaged.PlayerPawn?.Health ?? 0;
        var increaseHealth = damagedHealth < 0 ? @event.ActualDmgHealth + damagedHealth : @event.ActualDmgHealth;

        attackerPawn.Health += increaseHealth;
        attackerPawn.HealthUpdated();

        return HookResult.Continue;
    }
}
