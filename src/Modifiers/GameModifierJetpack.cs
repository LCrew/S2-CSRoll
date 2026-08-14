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
/// mashing jump mid-air just re-launched them over and over. That was fixed with a
/// BigBoostCooldownSeconds elapsed-time debounce (Core.Engine.GlobalVars.CurrentTime, the same
/// mechanism already proven reliable for MasterZeus/FlankTeleport) - no dependency on any
/// movement-hook-internal ground-state field.
///
/// Bug fix (the hold-to-thrust half, previously abandoned back to plain SuperJump): the original
/// sustained-thrust design applied ThrustSpeed by writing IMoveData.Velocity.Z inside
/// ProcessMovement.Post, mirroring the (working) one-shot jump-boost mechanism above - but live
/// testing showed the fuel gauge correctly drained (confirming ground/button detection was fine) while
/// holding jump produced no felt lift at all. Reasoning: ProcessMovement.Post's IMoveData is a
/// transient struct for that one native movement call; CS2's subtick movement can run further physics
/// integration within the same tick after our Post hook returns, meaning a floor written there simply
/// doesn't survive to become the entity's actual real velocity for continuous/sustained application
/// the way it reliably does for a one-off discrete event like a jump. Confirmed via a public reference
/// implementation (T3Marius/SW2-RandomSkills' JetpackSkill) using a completely different, working
/// mechanism instead: read CBaseEntity.AbsVelocity directly and re-assert it every tick via
/// IPlayer.Teleport(null, null, velocity) - the same authoritative "last word" native call the
/// one-shot jump boost above already relies on, just invoked repeatedly from Core.Event.OnTick instead
/// of from inside a movement hook. Switched to that: OnGameTick now calls ApplyThrust() directly
/// instead of floors IMoveData inside a ProcessMovement.Post hook (removed entirely).
///
/// Hold-detection also switched from polling IPlayer.PressedButtons each tick to
/// Core.Event.OnClientKeyStateChanged (a dedicated press/release event) - not because the polling
/// approach was shown to be broken (the fuel gauge draining correctly proves it wasn't), but because
/// it's the purpose-built, lower-latency signal for exactly this and the reference implementation
/// uses it too.
///
/// Ground-state gating (only thrust while airborne) is kept via CBaseEntity.GroundEntity - a null
/// Value means airborne, the same handle-nullness pattern already proven reliable elsewhere in this
/// codebase (e.g. GameModifierKamikaze's Inflictor check) - unlike the reference implementation, which
/// doesn't gate on ground state at all. AirAccelerate.Pre separately boosts in-flight steering by
/// multiplying CS2's normal air-accelerate value. The gauge is shown continuously while the modifier
/// is active, matching FlankTeleport/ConditionalInvisibility/FullInvisibility's persistent-HUD
/// convention.
/// </summary>
public sealed class GameModifierJetpack : GameModifierBase
{
    private const int GaugeBarWidth = 20;

    private readonly Dictionary<int, float> _fuel = [];
    private readonly Dictionary<int, bool> _isHoldingSpace = [];
    private readonly Dictionary<int, float> _refillDelayRemaining = [];
    private readonly Dictionary<int, float> _nextGaugeUpdateTime = [];
    private readonly Dictionary<int, float> _lastBigBoostTime = [];

    private float _lastTickTime = -1f;
    private Guid _spawnHookId;

    public GameModifierJetpack()
    {
        Name = "Jetpack";
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
        Core.GameHooks.Movement.AirAccelerate.Pre += OnAirAccelerate;
        Core.GameHooks.Entities.TakeDamage.Pre += OnTakeDamage;
        Core.Event.OnClientKeyStateChanged += OnClientKeyStateChanged;
        Core.Event.OnTick += OnGameTick;
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        _lastTickTime = -1f;

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot))
            {
                _fuel[player.Slot] = Runtime.Config.Jetpack.MaxFuel;
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.GameHooks.Movement.OnJumpLegacy.Post -= OnJumpLegacy;
        Core.GameHooks.Movement.OnJumpModern.Post -= OnJumpModern;
        Core.GameHooks.Movement.AirAccelerate.Pre -= OnAirAccelerate;
        Core.GameHooks.Entities.TakeDamage.Pre -= OnTakeDamage;
        Core.Event.OnClientKeyStateChanged -= OnClientKeyStateChanged;
        Core.Event.OnTick -= OnGameTick;
        Core.GameEvent.Unhook(_spawnHookId);

        _fuel.Clear();
        _isHoldingSpace.Clear();
        _refillDelayRemaining.Clear();
        _nextGaugeUpdateTime.Clear();
        _lastBigBoostTime.Clear();
    }

    private void OnJumpLegacy(ref OnJumpLegacyMovementPostContext ctx) => BoostJumpVelocity(ctx.Params.Player, ctx.Params.MoveData, "legacy");

    private void OnJumpModern(ref OnJumpModernMovementPostContext ctx) => BoostJumpVelocity(ctx.Params.Player, ctx.Params.MoveData, "modern");

    private void BoostJumpVelocity(IPlayer? player, IMoveData moveData, string variant)
    {
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return;
        }

        var now = Core.Engine.GlobalVars.CurrentTime;
        if (_lastBigBoostTime.TryGetValue(player.Slot, out var lastBoostTime) &&
            now - lastBoostTime < Runtime.Config.Jetpack.BigBoostCooldownSeconds)
        {
            return;
        }

        _lastBigBoostTime[player.Slot] = now;

        moveData.Velocity = new Vector(moveData.Velocity.X, moveData.Velocity.Y, Runtime.Config.Jetpack.JumpVelocityZ);

        if (Runtime.DebugMode)
        {
            Core.Logger.LogInformation("[CSRoll] Jetpack ({Slot}): {Variant} jump boosted, set VelocityZ={VelZ}", player.Slot, variant, Runtime.Config.Jetpack.JumpVelocityZ);
        }
    }

    private void OnAirAccelerate(ref AirAccelerateMovementPreContext ctx)
    {
        var player = ctx.Params.Player;
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return;
        }

        ctx.Params.Acceleration *= Runtime.Config.Jetpack.AirStrafeMultiplier;
    }

    private void OnTakeDamage(ref TakeDamageEntityPreContext ctx)
    {
        if ((ctx.Params.Info.DamageType & DamageTypes_t.DMG_FALL) == 0)
        {
            return;
        }

        if (!TryGetAssignedTakeDamageVictim(ref ctx, out _))
        {
            return;
        }

        ctx.Params.Info.Damage = 0;
    }

    private void OnClientKeyStateChanged(IOnClientKeyStateChangedEvent @event)
    {
        if (@event.Key != KeyKind.Space || !IsAssignedTo(@event.PlayerId))
        {
            return;
        }

        _isHoldingSpace[@event.PlayerId] = @event.Pressed;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            _fuel[player.Slot] = Runtime.Config.Jetpack.MaxFuel;
            _refillDelayRemaining[player.Slot] = 0f;
            _isHoldingSpace[player.Slot] = false;
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// Once-per-server-tick: decides "is thrusting" from the button state OnClientKeyStateChanged
    /// last reported plus a fresh ground-state read, drains/regens fuel from that decision, applies
    /// the actual lift via ApplyThrust, and refreshes the gauge.
    /// </summary>
    private void OnGameTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;
        var deltaSeconds = _lastTickTime < 0f ? 0f : Math.Max(0f, now - _lastTickTime);
        _lastTickTime = now;

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (!IsAssignedTo(player.Slot) || !player.IsAlive)
            {
                continue;
            }

            var slot = player.Slot;
            var maxFuel = Runtime.Config.Jetpack.MaxFuel;
            var fuel = _fuel.GetValueOrDefault(slot, maxFuel);

            var isAirborne = player.PlayerPawn?.GroundEntity.Value is null;
            var isHoldingSpace = _isHoldingSpace.GetValueOrDefault(slot, false);
            var isThrusting = isAirborne && isHoldingSpace && fuel > 0f;

            if (isThrusting)
            {
                ApplyThrust(player);
                fuel = Math.Max(0f, fuel - (Runtime.Config.Jetpack.FuelDrainPerSecond * deltaSeconds));
                _refillDelayRemaining[slot] = Runtime.Config.Jetpack.RefillDelaySeconds;
            }
            else
            {
                var delayRemaining = _refillDelayRemaining.GetValueOrDefault(slot, 0f);
                if (delayRemaining > 0f)
                {
                    _refillDelayRemaining[slot] = Math.Max(0f, delayRemaining - deltaSeconds);
                }
                else
                {
                    fuel = Math.Min(maxFuel, fuel + (Runtime.Config.Jetpack.FuelRegenPerSecond * deltaSeconds));
                }
            }

            _fuel[slot] = fuel;

            if (Runtime.DebugMode)
            {
                Core.Logger.LogInformation("[CSRoll] Jetpack ({Slot}): airborne={Airborne} holdingSpace={Holding} fuel={Fuel:0.#} thrusting={Thrusting}",
                    slot, isAirborne, isHoldingSpace, fuel, isThrusting);
            }

            UpdateFuelGauge(player, fuel, maxFuel);
        }
    }

    /// <summary>
    /// Applies the actual lift: reads the pawn's real current velocity and, if its vertical component
    /// is below ThrustSpeed, re-asserts it via IPlayer.Teleport(null, null, velocity) - a direct,
    /// authoritative velocity overwrite on the entity itself, called every tick while thrusting,
    /// rather than a value written into a transient per-movement-call struct that native physics
    /// integration can silently overwrite before it ever takes visible effect. Never reduces an
    /// already-higher upward velocity (e.g. right after the initial jump boost), and never floors
    /// above MaxVerticalSpeed.
    /// </summary>
    private void ApplyThrust(IPlayer player)
    {
        if (player.PlayerPawn is not { } pawn)
        {
            return;
        }

        var velocity = pawn.AbsVelocity;
        var thrustSpeed = Math.Min(Runtime.Config.Jetpack.ThrustSpeed, Runtime.Config.Jetpack.MaxVerticalSpeed);
        if (velocity.Z >= thrustSpeed)
        {
            return;
        }

        player.Teleport(velocity: new Vector(velocity.X, velocity.Y, thrustSpeed));
    }

    /// <summary>Always shown while the modifier is active (not hidden at full/idle) - matching FlankTeleport/ConditionalInvisibility/FullInvisibility's persistent-HUD convention, so there's no ambiguity about whether it's rendering.</summary>
    private void UpdateFuelGauge(IPlayer player, float fuel, float maxFuel)
    {
        var now = Core.Engine.GlobalVars.CurrentTime;
        var interval = Runtime.Config.Jetpack.GaugeUpdateIntervalSeconds;
        if (_nextGaugeUpdateTime.TryGetValue(player.Slot, out var nextUpdate) && now < nextUpdate)
        {
            return;
        }

        _nextGaugeUpdateTime[player.Slot] = now + interval;
        player.SendCenterHTML(BuildFuelGaugeHtml(fuel, maxFuel), (int)((interval * 1000) + 100));
    }

    private static string BuildFuelGaugeHtml(float fuel, float maxFuel)
    {
        var ratio = maxFuel > 0f ? fuel / maxFuel : 0f;
        return CSRollUtils.BuildGaugeHtml("Jetpack Fuel", "gold", ratio, CSRollUtils.GetGaugeBarColor(ratio), GaugeBarWidth);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _fuel.Remove(@event.PlayerId);
        _isHoldingSpace.Remove(@event.PlayerId);
        _refillDelayRemaining.Remove(@event.PlayerId);
        _nextGaugeUpdateTime.Remove(@event.PlayerId);
        _lastBigBoostTime.Remove(@event.PlayerId);
    }
}
