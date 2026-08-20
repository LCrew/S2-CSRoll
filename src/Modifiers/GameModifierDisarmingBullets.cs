using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Each bullet hit has a chance to disarm the player that was hit - same weapon-drop mechanism as
/// GameModifierButterfingers (WeaponServices.DropWeapon), just triggered on landing a hit rather than
/// missing one. Gated/excludes self/team damage the same way GameModifierFlashingBullets does, and
/// only triggers for genuine gunfire (DMG_BULLET/DMG_BUCKSHOT).
///
/// The chance itself is rolled once per activation (fresh each time this modifier is applied to a
/// player - a new random round, an admin re-roll, etc.) from Config.DisarmingBullets.Min/MaxChancePercent,
/// not re-rolled per bullet hit - so different activations get different odds within that range, but
/// the odds stay fixed for the life of one activation.
///
/// DynamicPercentText exposes the actual rolled percent for the current activation (or the
/// configured range if never yet activated, e.g. for !rolllist before this has ever been
/// rolled) - CSRollUtils.GetModifierDescription substitutes it into any "{rand%}" token found in
/// either a translations/en.jsonc override or the hardcoded Description fallback below, so the
/// wording is freely customizable there while the value always tracks the live roll.
/// </summary>
public sealed class GameModifierDisarmingBullets : GameModifierBase
{
    private float? _rolledChancePercent;

    private string RollText => _rolledChancePercent is { } percent
        ? $"{percent:0.#}%"
        : $"{Runtime.Config.DisarmingBullets.MinChancePercent:0.#}-{Runtime.Config.DisarmingBullets.MaxChancePercent:0.#}%";

    public override IReadOnlyDictionary<string, string>? DynamicTextTokens => new Dictionary<string, string> { ["rand%"] = RollText };

    public override string Description => $"{RollText} chance to disarm an enemy hit by your bullets";

    public GameModifierDisarmingBullets()
    {
        Name = "DisarmingBullets";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        var min = Runtime.Config.DisarmingBullets.MinChancePercent;
        var max = Runtime.Config.DisarmingBullets.MaxChancePercent;
        _rolledChancePercent = min + (float)(Random.Shared.NextDouble() * (max - min));

        Core.GameHooks.Entities.TakeDamage.Pre += OnTakeDamage;
    }

    protected override void OnDisabled()
    {
        Core.GameHooks.Entities.TakeDamage.Pre -= OnTakeDamage;
        _rolledChancePercent = null;
    }

    private void OnTakeDamage(ref TakeDamageEntityPreContext ctx)
    {
        if (!CSRollUtils.IsBulletDamage(ctx.Params.Info.DamageType))
        {
            return;
        }

        var attacker = CSRollUtils.GetPlayerFromEntityHandle(Core, ctx.Params.Info.Attacker);
        if (attacker is not { IsValid: true } || !IsAssignedTo(attacker.Slot))
        {
            return;
        }

        var victim = Core.PlayerManager.GetPlayerFromPawn(ctx.Params.Entity.As<CBasePlayerPawn>());
        if (victim is not { IsValid: true, IsAlive: true } || victim.PlayerPawn is not { } pawn)
        {
            return;
        }

        // Bug fix: Controller used to be dereferenced (.Team) with no null-check - a valid attacker/
        // victim can still have a null/invalid Controller mid-spawn/death transition, which threw an
        // NRE on this per-hit hot path. Also switched the self-hit check from SteamID to Slot (via
        // IsSamePlayer) - bot SteamID is fixed at 0, so two different bots hitting each other used to
        // be misread as a self-hit and silently excluded.
        if (CSRollUtils.IsSamePlayer(attacker, victim) ||
            attacker.Controller is not { IsValid: true } attackerController ||
            victim.Controller is not { IsValid: true } victimController ||
            attackerController.Team == victimController.Team)
        {
            return;
        }

        if (Random.Shared.NextDouble() * 100 >= (_rolledChancePercent ?? 0f))
        {
            return;
        }

        if (pawn.WeaponServices is not { } weaponServices || weaponServices.ActiveWeapon.Value is not { } weapon)
        {
            return;
        }

        weaponServices.DropWeapon(weapon);
    }
}
