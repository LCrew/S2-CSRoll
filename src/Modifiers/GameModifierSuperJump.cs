using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Bug fix: this used to be resources/ConVarModifiers/SuperJumpModifier.cfg, driving
/// sv_jump_impulse/sv_falldamage_scale - both server-wide, so every player jumped higher and took no
/// fall damage instead of just whoever rolled it. Core.GameHooks.Entities.TakeDamage.Pre zeroes
/// DMG_FALL damage for this player specifically (the same hook mechanism HardHead/IronBody/Revive
/// already use for other per-player damage exceptions) - that part was fine from the start.
///
/// Bug fix 2 (the jump-height half): this went through two failed designs. First it tried to detect
/// "a jump is already in progress" via moveData.Velocity.Z &gt; 0 in ProcessMovement.Pre, but Pre fires
/// before native jump code runs, so that was never true on the tick that mattered. Second, it copied
/// Bhop's Pre-hook velocity injection - live testing ("couldn't tell, nothing seemed to change")
/// suggests a directly-injected Pre-hook velocity for a discrete event like a jump doesn't reliably
/// survive the native code that runs right after it, unlike a passively-respected cap (MaxSpeed).
/// Switched to IGameHookMovement.OnJumpLegacy/OnJumpModern (Post) instead - these fire AFTER the
/// engine's own native jump already applied its normal velocity, so overwriting
/// IMoveData.Velocity.Z here is the last write for that tick rather than something native code can
/// still clobber afterward. Both variants are hooked since it's unconfirmed which of CS2's two
/// parallel jump-input systems ("legacy" vs "modern" subtick) a given server/client uses.
///
/// Reverted from the Jetpack rework (fuel-limited hold-to-thrust + air-strafe boost) back to this
/// original, simpler design per explicit instruction: across several iterations the sustained-thrust
/// half never worked reliably (holding jump produced no felt effect even while the fuel gauge
/// correctly drained, and the initial-jump boost could still be spammed indefinitely once fuel hit
/// zero, since it was only rate-limited by a cooldown rather than gated on fuel) - see git history on
/// GameModifierJetpack.cs for the full record of what was tried. Plain higher-jump + no-fall-damage
/// has no such reliability problem, since it's a single one-shot velocity override per jump rather
/// than something that has to keep winning against native physics integration every tick.
/// </summary>
public sealed class GameModifierSuperJump : GameModifierBase
{
    public GameModifierSuperJump()
    {
        Name = "SuperJump";
        Description = "Jumping is much higher, no fall damage";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        Core.GameHooks.Movement.OnJumpLegacy.Post += OnJumpLegacy;
        Core.GameHooks.Movement.OnJumpModern.Post += OnJumpModern;
        Core.GameHooks.Entities.TakeDamage.Pre += OnTakeDamage;
    }

    protected override void OnDisabled()
    {
        Core.GameHooks.Movement.OnJumpLegacy.Post -= OnJumpLegacy;
        Core.GameHooks.Movement.OnJumpModern.Post -= OnJumpModern;
        Core.GameHooks.Entities.TakeDamage.Pre -= OnTakeDamage;
    }

    private void OnJumpLegacy(ref OnJumpLegacyMovementPostContext ctx) => BoostJumpVelocity(ctx.Params.Player, ctx.Params.MoveData, "legacy");

    private void OnJumpModern(ref OnJumpModernMovementPostContext ctx) => BoostJumpVelocity(ctx.Params.Player, ctx.Params.MoveData, "modern");

    private void BoostJumpVelocity(IPlayer? player, IMoveData moveData, string variant)
    {
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return;
        }

        moveData.Velocity = new Vector(moveData.Velocity.X, moveData.Velocity.Y, Runtime.Config.SuperJump.JumpVelocityZ);

        if (Runtime.DebugMode)
        {
            Core.Logger.LogInformation("[CSRoll] SuperJump ({Slot}): {Variant} jump boosted, set VelocityZ={VelZ}", player.Slot, variant, Runtime.Config.SuperJump.JumpVelocityZ);
        }
    }

    private void OnTakeDamage(ref TakeDamageEntityPreContext ctx)
    {
        if ((ctx.Params.Info.DamageType & DamageTypes_t.DMG_FALL) == 0)
        {
            return;
        }

        var victim = Core.PlayerManager.GetPlayerFromPawn(ctx.Params.Entity.As<CBasePlayerPawn>());
        if (victim is not { IsValid: true } || !IsAssignedTo(victim.Slot))
        {
            return;
        }

        ctx.Params.Info.Damage = 0;
    }
}
