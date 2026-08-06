using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
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
/// Reworked into a jetpack per explicit request: this used to let a player keep re-triggering
/// OnJumpLegacy/OnJumpModern while already airborne (nothing checked whether they'd landed yet), so
/// mashing jump mid-air just re-launched them over and over - visually indistinguishable from flying,
/// but as an unintended repeated-jump exploit rather than a designed mechanic. Now: the initial jump
/// off the ground still gets the same height boost as before, but holding jump while airborne
/// (checked via IMoveData.InAir, not a ground-adjacency guess) instead applies a deliberate,
/// fuel-limited upward thrust via ProcessMovement.Post - the same "last write wins" Post-hook
/// mechanism the original jump-height fix already proved reliable, just applied every movement tick
/// instead of once on the jump edge. A capped floor on Velocity.Z (never lowering an already-faster
/// upward speed, e.g. right after the initial jump) keeps this idempotent regardless of how many
/// times ProcessMovement fires per server tick under CS2's subtick movement system, so there's no
/// runaway acceleration to guard against. AirAccelerate.Pre separately boosts in-flight steering by
/// multiplying CS2's normal air-accelerate value, satisfying the "add air-strafe" half of the
/// request without needing to fight the physics simulation with a hand-rolled WASD-to-velocity
/// mapping. Fuel drains only while actually thrusting and regenerates whenever not (grounded or just
/// coasting/falling), tracked once per server tick (not per ProcessMovement call, since that can fire
/// more than once per tick) via a per-slot "was thrusting this tick" flag set by the movement hook and
/// consumed/reset by OnTick. A center-HTML ASCII gauge mirrors the current fuel level back to the
/// player, throttled to GaugeUpdateIntervalSeconds so it doesn't spam a fresh popup every tick.
/// </summary>
public sealed class GameModifierSuperJump : GameModifierBase
{
    private const int GaugeBarWidth = 20;

    private readonly Dictionary<int, float> _fuel = [];
    private readonly Dictionary<int, bool> _isThrustingThisTick = [];
    private readonly Dictionary<int, float> _nextGaugeUpdateTime = [];

    private float _lastTickTime = -1f;
    private Guid _spawnHookId;

    public GameModifierSuperJump()
    {
        Name = "SuperJump";
        Description = "Jumping is much higher, no fall damage, and holding jump in the air fires a fuel-limited jetpack thrust with boosted air-strafe";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnRegistered()
    {
        Core.Event.OnClientDisconnected += OnClientDisconnected;
    }

    protected override void OnUnregistered()
    {
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
    }

    protected override void OnEnabled()
    {
        Core.GameHooks.Movement.OnJumpLegacy.Post += OnJumpLegacy;
        Core.GameHooks.Movement.OnJumpModern.Post += OnJumpModern;
        Core.GameHooks.Movement.ProcessMovement.Post += OnProcessMovement;
        Core.GameHooks.Movement.AirAccelerate.Pre += OnAirAccelerate;
        Core.GameHooks.Entities.TakeDamage.Pre += OnTakeDamage;
        Core.Event.OnTick += OnGameTick;
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        _lastTickTime = -1f;

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot))
            {
                _fuel[player.Slot] = Runtime.Config.SuperJump.MaxFuel;
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.GameHooks.Movement.OnJumpLegacy.Post -= OnJumpLegacy;
        Core.GameHooks.Movement.OnJumpModern.Post -= OnJumpModern;
        Core.GameHooks.Movement.ProcessMovement.Post -= OnProcessMovement;
        Core.GameHooks.Movement.AirAccelerate.Pre -= OnAirAccelerate;
        Core.GameHooks.Entities.TakeDamage.Pre -= OnTakeDamage;
        Core.Event.OnTick -= OnGameTick;
        Core.GameEvent.Unhook(_spawnHookId);

        _fuel.Clear();
        _isThrustingThisTick.Clear();
        _nextGaugeUpdateTime.Clear();
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

    private void OnProcessMovement(ref ProcessMovementMovementPostContext ctx)
    {
        var player = ctx.Params.Player;
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return;
        }

        var moveData = ctx.Params.MoveData;
        var isHoldingJump = player.PressedButtons.HasFlag(GameButtonFlags.Space);
        var fuel = _fuel.GetValueOrDefault(player.Slot, Runtime.Config.SuperJump.MaxFuel);

        if (!moveData.InAir || !isHoldingJump || fuel <= 0f)
        {
            return;
        }

        // Floor (never lower) the current vertical speed at ThrustSpeed rather than adding to it -
        // safe to call more than once per server tick (CS2's subtick movement can invoke
        // ProcessMovement several times per tick) without compounding into runaway acceleration, and
        // never undoes a higher upward speed from the initial jump boost above.
        var velocity = moveData.Velocity;
        if (velocity.Z < Runtime.Config.SuperJump.ThrustSpeed)
        {
            moveData.Velocity = new Vector(velocity.X, velocity.Y, Runtime.Config.SuperJump.ThrustSpeed);
        }

        _isThrustingThisTick[player.Slot] = true;
    }

    private void OnAirAccelerate(ref AirAccelerateMovementPreContext ctx)
    {
        var player = ctx.Params.Player;
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return;
        }

        ctx.Params.Acceleration *= Runtime.Config.SuperJump.AirStrafeMultiplier;
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

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            _fuel[player.Slot] = Runtime.Config.SuperJump.MaxFuel;
        }

        return HookResult.Continue;
    }

    /// <summary>Once-per-server-tick fuel drain/regen and gauge refresh - deliberately not done inside OnProcessMovement, which can fire more than once per tick and would otherwise drain/regen fuel faster than real time on those ticks.</summary>
    private void OnGameTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;
        var deltaSeconds = _lastTickTime < 0f ? 0f : Math.Max(0f, now - _lastTickTime);
        _lastTickTime = now;

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (!IsAssignedTo(player.Slot))
            {
                continue;
            }

            var slot = player.Slot;
            var maxFuel = Runtime.Config.SuperJump.MaxFuel;
            var fuel = _fuel.GetValueOrDefault(slot, maxFuel);
            var wasThrusting = _isThrustingThisTick.GetValueOrDefault(slot, false);
            _isThrustingThisTick[slot] = false;

            fuel = wasThrusting
                ? Math.Max(0f, fuel - (Runtime.Config.SuperJump.FuelDrainPerSecond * deltaSeconds))
                : Math.Min(maxFuel, fuel + (Runtime.Config.SuperJump.FuelRegenPerSecond * deltaSeconds));

            _fuel[slot] = fuel;

            UpdateFuelGauge(player, fuel, maxFuel, wasThrusting);
        }
    }

    private void UpdateFuelGauge(IPlayer player, float fuel, float maxFuel, bool wasThrusting)
    {
        if (!wasThrusting && fuel >= maxFuel)
        {
            // Fully topped up and not currently flying - nothing worth showing. Any previous gauge
            // popup simply expires on its own rather than being force-cleared.
            return;
        }

        var now = Core.Engine.GlobalVars.CurrentTime;
        var interval = Runtime.Config.SuperJump.GaugeUpdateIntervalSeconds;
        if (_nextGaugeUpdateTime.TryGetValue(player.Slot, out var nextUpdate) && now < nextUpdate)
        {
            return;
        }

        _nextGaugeUpdateTime[player.Slot] = now + interval;
        player.SendCenterHTML(BuildFuelGaugeHtml(fuel, maxFuel), (int)((interval * 1000) + 100));
    }

    private static string BuildFuelGaugeHtml(float fuel, float maxFuel)
    {
        var ratio = maxFuel > 0f ? Math.Clamp(fuel / maxFuel, 0f, 1f) : 0f;
        var filled = (int)Math.Round(ratio * GaugeBarWidth);
        var bar = new string('#', filled) + new string('-', GaugeBarWidth - filled);
        var percent = (int)Math.Round(ratio * 100f);
        var barColor = ratio switch
        {
            > 0.5f => "lime",
            > 0.2f => "orange",
            _ => "red",
        };

        return $"<span color=\"gold\" class=\"fontWeight-bold\">Jetpack Fuel</span><br/><span color=\"{barColor}\" class=\"fontWeight-bold\">[{bar}] {percent}%</span>";
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _fuel.Remove(@event.PlayerId);
        _isThrustingThisTick.Remove(@event.PlayerId);
        _nextGaugeUpdateTime.Remove(@event.PlayerId);
    }
}
