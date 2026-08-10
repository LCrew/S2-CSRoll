using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>Heals the assigned player(s) over time, up to their current max health.</summary>
public sealed class GameModifierRegeneration : GameModifierBase
{
    private float _lastHealTime;

    public GameModifierRegeneration()
    {
        Name = "Regeneration";
        Description = "Slowly heals over time, up to your max health";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        _lastHealTime = Core.Engine.GlobalVars.CurrentTime;
        Core.Event.OnTick += OnGameTick;
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= OnGameTick;
    }

    private void OnGameTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;
        if (now - _lastHealTime < Runtime.Config.Regeneration.IntervalSeconds)
        {
            return;
        }

        _lastHealTime = now;

        foreach (var player in Core.PlayerManager.GetAlive())
        {
            if (!IsAssignedTo(player.Slot) || player.PlayerPawn is not { } pawn || pawn.Health >= pawn.MaxHealth)
            {
                continue;
            }

            pawn.Health = Math.Min(pawn.MaxHealth, pawn.Health + Runtime.Config.Regeneration.Amount);
            pawn.HealthUpdated();
        }
    }
}
