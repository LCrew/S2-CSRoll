using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Each bullet hit has a chance to blind the player that was hit for a short duration. Gated on the
/// shooter's slot (like GameModifierVampire), excludes self/team damage with the same guard, and
/// only triggers for genuine gunfire (DMG_BULLET/DMG_BUCKSHOT) - not knife slashes or explosive
/// damage.
///
/// The chance is rolled once per activation (fresh each time this modifier is applied to a player)
/// from Config.FlashingBullets.Min/MaxBlindChancePercent, not re-rolled per bullet hit - so
/// different activations get different odds within that range, but the odds stay fixed for the
/// life of one activation.
///
/// Bug fix (two earlier attempts didn't produce a visible effect): GameModifierLongerFlashes only
/// ever needs to set FlashDuration/BlindUntilTime because it EXTENDS an already-real flashbang's
/// blind - the engine's own native flash processing has already set the other blind-related fields
/// on the pawn before that hook runs. FlashingBullets has no real flash event at all, so those
/// other fields were left at their stale/zero defaults, which plausibly rendered the effect at
/// zero visible intensity even with BlindUntilTime set correctly. CCSPlayerPawnBase actually has
/// four blind-related fields, not two: FlashDuration, BlindUntilTime, BlindStartTime (the epoch the
/// engine measures elapsed blind time from - left stale, "elapsed since blind" is nonsense) and
/// FlashMaxAlpha (peak whiteout intensity - left at 0, i.e. an invisible flash). All four are set
/// here now. A prior attempt also tried firing a synthetic EventPlayerBlind via Core.GameEvent.Fire
/// to trigger the reaction - dropped, since SwiftlyS2's own docs specifically warn that many Source
/// 2 game events (especially ones a plugin synthesizes rather than hooks) are unreliable/obsolete;
/// direct schema writes are the safer, more deterministic mechanism.
/// </summary>
public sealed class GameModifierFlashingBullets : GameModifierBase
{
    private const float FlashMaxAlpha = 255f;

    private float? _rolledBlindChancePercent;

    private string RollText => _rolledBlindChancePercent is { } percent
        ? $"{percent:0.#}%"
        : $"{Runtime.Config.FlashingBullets.MinBlindChancePercent:0.#}-{Runtime.Config.FlashingBullets.MaxBlindChancePercent:0.#}%";

    public override IReadOnlyDictionary<string, string>? DynamicTextTokens => new Dictionary<string, string> { ["rand%"] = RollText };

    public override string Description => $"{RollText} chance for a bullet hit to blind the enemy";

    public GameModifierFlashingBullets()
    {
        Name = "FlashingBullets";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        var min = Runtime.Config.FlashingBullets.MinBlindChancePercent;
        var max = Runtime.Config.FlashingBullets.MaxBlindChancePercent;
        _rolledBlindChancePercent = min + (float)(Random.Shared.NextDouble() * (max - min));

        Core.GameHooks.Entities.TakeDamage.Pre += OnTakeDamage;
    }

    protected override void OnDisabled()
    {
        Core.GameHooks.Entities.TakeDamage.Pre -= OnTakeDamage;
        _rolledBlindChancePercent = null;
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

        if (attacker.SteamID == victim.SteamID || attacker.Controller.Team == victim.Controller.Team)
        {
            return;
        }

        if (Random.Shared.NextDouble() * 100 >= (_rolledBlindChancePercent ?? 0f))
        {
            return;
        }

        var duration = Runtime.Config.FlashingBullets.BlindDurationSeconds;
        var now = Core.Engine.GlobalVars.CurrentTime;

        pawn.FlashDuration = duration;
        pawn.FlashDurationUpdated();

        pawn.FlashMaxAlpha = FlashMaxAlpha;
        pawn.FlashMaxAlphaUpdated();

        pawn.BlindStartTime.Value = now;
        pawn.BlindUntilTime.Value = now + duration;
    }
}
