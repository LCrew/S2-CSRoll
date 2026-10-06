using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;
using CSRoll.Hud;

namespace CSRoll.Modifiers;

/// <summary>
/// Bug fix: this used to be resources/ConVarModifiers/SuperJumpModifier.cfg, driving
/// sv_jump_impulse/sv_falldamage_scale - both server-wide, so every player jumped higher and took no
/// fall damage instead of just whoever rolled it. Core.GameHooks.Entities.TakeDamage.Pre zeroes
/// DMG_FALL damage for this player specifically (the same hook mechanism HardHead/SteelBody/Revive
/// already use for other per-player damage exceptions) - that part was fine from the start.
///
/// Bug fix 2 (the jump-height half): this went through two failed designs. First it tried to detect
/// "a jump is already in progress" via moveData.Velocity.Z &gt; 0 in ProcessMovement.Pre, but Pre fires
/// before native jump code runs, so that was never true on the tick that mattered. Second, it copied
/// BunnyHop's Pre-hook velocity injection - live testing ("couldn't tell, nothing seemed to change")
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
/// mechanism already proven reliable for MasterZeus/Flanker) - no dependency on any
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
/// codebase (e.g. GameModifierSuicideBomber's Inflictor check) - unlike the reference implementation, which
/// doesn't gate on ground state at all. AirAccelerate.Pre separately boosts in-flight steering by
/// multiplying CS2's normal air-accelerate value. The gauge is shown continuously while the modifier
/// is active, matching Flanker/ConditionalInvisibility/Vanish's persistent-HUD
/// convention.
///
/// Rework (thrust ramps up instead of snapping): thrust used to floor vertical speed at a fixed
/// ThrustSpeed the instant jump was held, so pressing jump mid-fall turned a -600 u/s fall into a
/// +140 u/s climb in a single tick - it read as jumping again, not as a jetpack. On top of that, the
/// OnJumpLegacy/OnJumpModern.Post "big boost" also fired for jump presses in mid-air (the hooks fire
/// for presses that don't take off - the reason BigBoostCooldownSeconds existed), and once its cooldown
/// had passed it set vertical speed straight to JumpVelocityZ: a literal mid-air re-jump. Thrust is now
/// an ACCELERATION added to the pawn's real velocity every tick, building from ThrustAccelerationStart
/// to ThrustAccelerationMax over ThrustRampUpSeconds, so a press mid-fall first brakes the fall and
/// only then climbs. The big boost is gone entirely, which disables jumping as a launch: the ground
/// jump is CS2's own, untouched, and mid-air presses do nothing but thrust.
///
/// Tuning after live testing: the build-up and peak thrust were too strong (both lowered 20%), while
/// softening a fall was too weak - and tapping jump to "fan" a fall down did close to nothing, because
/// each tap restarted the ramp at its weakest and a quick tap could start and end between two server
/// ticks. Two additions: FallBrakeAcceleration is extra lift that only acts while falling (and only
/// up to stopping the fall, never into a climb), at full strength from the first tick; and every
/// mid-air press thrusts for at least TapPuffSeconds, so each tap lands as a puff.
/// </summary>
public sealed class GameModifierJetpack : GameModifierBase
{
    private const int GaugeBarWidth = 20;

    private readonly Dictionary<int, float> _fuel = [];
    private readonly Dictionary<int, bool> _isHoldingSpace = [];
    private readonly Dictionary<int, float> _refillDelayRemaining = [];
    private readonly Dictionary<int, float> _nextGaugeUpdateTime = [];

    /// <summary>When the current unbroken stretch of thrust began, per player - drives the ramp-up. Absent while not thrusting.</summary>
    private readonly Dictionary<int, float> _thrustStartTime = [];

    /// <summary>A mid-air jump press keeps thrust going until this time even once released - see JetpackConfig.TapPuffSeconds.</summary>
    private readonly Dictionary<int, float> _puffUntil = [];

    private float _lastTickTime = -1f;
    private Guid _spawnHookId;

    public GameModifierJetpack()
    {
        Name = "Jetpack";
        Description = "Hold jump in the air for a fuel-limited jetpack thrust that builds up gradually, with boosted air-strafe and no fall damage";
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
        Core.GameHooks.Movement.AirAccelerate.Pre += OnAirAccelerate;
        Core.GameHooks.Entities.TakeDamage.Pre += OnTakeDamage;
        Core.Event.OnClientKeyStateChanged += OnClientKeyStateChanged;
        Core.Event.OnTick += OnGameTick;
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        _lastTickTime = -1f;

        foreach (var player in GetAssignedPlayers())
        {
            _fuel[player.Slot] = Runtime.Config.Jetpack.MaxFuel;
        }
    }

    protected override void OnDisabled()
    {
        Core.GameHooks.Movement.AirAccelerate.Pre -= OnAirAccelerate;
        Core.GameHooks.Entities.TakeDamage.Pre -= OnTakeDamage;
        Core.Event.OnClientKeyStateChanged -= OnClientKeyStateChanged;
        Core.Event.OnTick -= OnGameTick;
        Core.GameEvent.Unhook(_spawnHookId);

        _fuel.Clear();
        _isHoldingSpace.Clear();
        _refillDelayRemaining.Clear();
        _nextGaugeUpdateTime.Clear();
        _thrustStartTime.Clear();
        _puffUntil.Clear();
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

        // Only mid-air presses puff - a press on the ground is CS2's own jump, left untouched.
        if (@event.Pressed &&
            Core.PlayerManager.GetPlayer(@event.PlayerId) is { IsValid: true, IsAlive: true } player &&
            player.PlayerPawn is { IsValid: true } pawn &&
            pawn.GroundEntity.Value is null)
        {
            _puffUntil[@event.PlayerId] = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.Jetpack.TapPuffSeconds;
        }
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            _fuel[player.Slot] = Runtime.Config.Jetpack.MaxFuel;
            _refillDelayRemaining[player.Slot] = 0f;
            _isHoldingSpace[player.Slot] = false;
            _thrustStartTime.Remove(player.Slot);
            _puffUntil.Remove(player.Slot);
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
        // Capped so a server hitch (or the first tick after a long pause) can't turn into one huge
        // velocity kick - thrust is an acceleration now, so it scales with this directly.
        var deltaSeconds = _lastTickTime < 0f ? 0f : Math.Clamp(now - _lastTickTime, 0f, 0.1f);
        _lastTickTime = now;

        foreach (var player in GetAssignedPlayers())
        {
            if (!player.IsAlive)
            {
                continue;
            }

            var slot = player.Slot;
            var maxFuel = Runtime.Config.Jetpack.MaxFuel;
            var fuel = _fuel.GetValueOrDefault(slot, maxFuel);

            var isAirborne = player.PlayerPawn?.GroundEntity.Value is null;
            var isHoldingSpace = _isHoldingSpace.GetValueOrDefault(slot, false) || now < _puffUntil.GetValueOrDefault(slot, 0f);
            var isThrusting = isAirborne && isHoldingSpace && fuel > 0f;

            if (isThrusting)
            {
                if (!_thrustStartTime.TryGetValue(slot, out var thrustStart))
                {
                    thrustStart = now;
                    _thrustStartTime[slot] = now;
                }

                ApplyThrust(player, now - thrustStart, deltaSeconds);
                fuel = Math.Max(0f, fuel - (Runtime.Config.Jetpack.FuelDrainPerSecond * deltaSeconds));
                _refillDelayRemaining[slot] = Runtime.Config.Jetpack.RefillDelaySeconds;
            }
            else
            {
                _thrustStartTime.Remove(slot);

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
    /// Applies the actual lift: adds this tick's share of the (ramping) thrust acceleration to the
    /// pawn's real current velocity and re-asserts it via IPlayer.Teleport(null, null, velocity) - the
    /// same authoritative overwrite the old fixed-speed floor used, called every tick while thrusting.
    /// Gravity is still applied by the engine on top, so the net effect is (thrust - gravity): a fall
    /// is braked first and only turns into a climb once thrust has outweighed it for long enough.
    ///
    /// While falling, FallBrakeAcceleration is added first and capped at zero vertical speed, so it
    /// can stop a fall but never adds to a climb. Never pushes past MaxVerticalSpeed, and never slows
    /// a climb that's already faster than that.
    /// </summary>
    private void ApplyThrust(IPlayer player, float thrustingFor, float deltaSeconds)
    {
        if (deltaSeconds <= 0f || player.PlayerPawn is not { IsValid: true } pawn)
        {
            return;
        }

        var config = Runtime.Config.Jetpack;
        var velocity = pawn.AbsVelocity;
        if (velocity.Z >= config.MaxVerticalSpeed)
        {
            return;
        }

        var newZ = velocity.Z;
        if (newZ < 0f)
        {
            newZ = Math.Min(newZ + (config.FallBrakeAcceleration * deltaSeconds), 0f);
        }

        var ramp = config.ThrustRampUpSeconds > 0f ? Math.Clamp(thrustingFor / config.ThrustRampUpSeconds, 0f, 1f) : 1f;
        var acceleration = config.ThrustAccelerationStart + ((config.ThrustAccelerationMax - config.ThrustAccelerationStart) * ramp);
        newZ = Math.Min(newZ + (acceleration * deltaSeconds), config.MaxVerticalSpeed);

        player.Teleport(velocity: new Vector(velocity.X, velocity.Y, newZ));
    }

    /// <summary>Always shown while the modifier is active (not hidden at full/idle) - matching Flanker/ConditionalInvisibility/Vanish's persistent-HUD convention, so there's no ambiguity about whether it's rendering.</summary>
    private void UpdateFuelGauge(IPlayer player, float fuel, float maxFuel)
    {
        var ratio = maxFuel > 0f ? fuel / maxFuel : 0f;
        SetGauge(player.Slot, new HudGauge("Jetpack fuel", $"{ratio * 100f:0}%", ratio));

        var now = Core.Engine.GlobalVars.CurrentTime;
        var interval = Runtime.Config.Jetpack.GaugeUpdateIntervalSeconds;
        if (_nextGaugeUpdateTime.TryGetValue(player.Slot, out var nextUpdate) && now < nextUpdate)
        {
            return;
        }

        // Stay off the center-HTML surface while the roll's own reveal owns it - see
        // ModifierRuntime.IsModifierHudSuppressed.
        if (Runtime.IsModifierHudSuppressed)
        {
            return;
        }

        _nextGaugeUpdateTime[player.Slot] = now + interval;
        SetHud(player.Slot, BuildFuelGaugeHtml(fuel, maxFuel));
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
        _thrustStartTime.Remove(@event.PlayerId);
        _puffUntil.Remove(@event.PlayerId);
    }
}
