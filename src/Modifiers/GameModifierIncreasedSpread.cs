using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Bug fix: this used to be resources/ConVarModifiers/IncreasedSpread.cfg, driving
/// weapon_accuracy_forcespread - a server-wide cvar, so it made everyone's bullets go wide instead
/// of just whoever rolled it. Rewritten per-player: CCSWeaponBase.AccuracyPenalty (a per-weapon-
/// instance schema field, not a cvar) is forced up every tick for the assigned player's currently
/// held weapon, then reset back to 0 on disable so it doesn't linger.
/// </summary>
public sealed class GameModifierIncreasedSpread : GameModifierBase
{
    public GameModifierIncreasedSpread()
    {
        Name = "IncreasedSpread";
        Description = "Your bullets go where they want now";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["NoSpread"];
    }

    protected override void OnEnabled()
    {
        Core.Event.OnTick += ApplyToAllPlayers;
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= ApplyToAllPlayers;

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot) && player.PlayerPawn?.WeaponServices?.ActiveWeapon.Value is { } weapon)
            {
                var csWeapon = weapon.As<CCSWeaponBase>();
                csWeapon.AccuracyPenalty = 0f;
                csWeapon.AccuracyPenaltyUpdated();
            }
        }
    }

    private void ApplyToAllPlayers()
    {
        var penalty = Runtime.Config.IncreasedSpread.AccuracyPenalty;
        foreach (var player in Core.PlayerManager.GetAlive())
        {
            if (IsAssignedTo(player.Slot) && player.PlayerPawn?.WeaponServices?.ActiveWeapon.Value is { } weapon)
            {
                var csWeapon = weapon.As<CCSWeaponBase>();
                csWeapon.AccuracyPenalty = penalty;
                csWeapon.AccuracyPenaltyUpdated();
            }
        }
    }
}
