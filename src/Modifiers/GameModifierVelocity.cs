using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

namespace CSRoll.Modifiers;

/// <summary>
/// Scales every player's movement speed. The engine recalculates/resets the velocity modifier
/// far more often than a 0.2s timer can keep up with - most visibly during air-strafing, where
/// movement code re-derives speed every tick - so this re-applies on every server tick instead.
///
/// Bug fix: holding walk (+speed, bound to Shift by default - GameButtonFlags.Shift, cross-checked
/// against CounterStrikeSharp's InputBitMask_t.IN_SPEED = 65536, the same value) used to still get
/// the full VelocityModifier scaling applied on top, same as running. CS2's silent-walk mechanic
/// works by dropping actual movement speed low enough that the engine treats it as walking rather
/// than running; Speedhack's multiplier pushed that scaled-up "walk" speed back above the
/// running-speed threshold, so the engine (and its footstep sounds) treated it as running again -
/// shift-walking was no longer actually silent. Skipping the multiplier entirely while the walk
/// button is held keeps walk speed (and its silence) exactly as vanilla, regardless of any
/// otherwise-active speed modifier - only running speed is ever scaled.
/// </summary>
public abstract class GameModifierVelocity : GameModifierBase
{
    private readonly Dictionary<int, float> _lastDebugLogTime = [];

    protected abstract float GetSpeedMultiplier();

    /// <summary>
    /// Whether to zero CCSPlayer_MovementServices.Stamina every tick. Opt-in per subclass rather than
    /// on by default: Stamina is CS2's jump/land fatigue value (rises on jump and landing, reduces
    /// max speed until it decays), so clearing it removes the "jumping strips the speed bonus away"
    /// effect - desirable for Speedhack, but it would quietly buff HeavyBoots, whose whole point is
    /// being slow. Same mechanism GameModifierBunnyHop already uses to defeat CS2's anti-bhop slowdown.
    /// </summary>
    protected virtual bool ShouldRemoveJumpStaminaPenalty() => false;

    protected override void OnEnabled()
    {
        Core.Event.OnTick += ApplyToAllPlayers;
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= ApplyToAllPlayers;

        foreach (var player in GetAssignedPlayers())
        {
            SetSpeedMultiplier(player, 1.0f);
        }
    }

    private void ApplyToAllPlayers()
    {
        var runMultiplier = GetSpeedMultiplier();
        var removeStaminaPenalty = ShouldRemoveJumpStaminaPenalty();
        foreach (var player in GetAssignedPlayers())
        {
            if (player.PlayerPawn is { } pawn)
            {
                // Walking (IN_SPEED held) is left at 1.0 - see the bug-fix note above.
                var multiplier = player.PressedButtons.HasFlag(GameButtonFlags.Shift) ? 1.0f : runMultiplier;

                // Diagnostic: read the value BEFORE we overwrite it, so if something else (native
                // engine code, another modifier, a jump/landing transition) is resetting
                // VelocityModifier back to 1.0 between our own ticks, this catches it directly -
                // rather than only ever seeing our own just-written value reflected back.
                if (Runtime.DebugMode)
                {
                    var now = Core.Engine.GlobalVars.CurrentTime;
                    if (!_lastDebugLogTime.TryGetValue(player.Slot, out var lastLog) || now - lastLog >= 1f)
                    {
                        _lastDebugLogTime[player.Slot] = now;
                        Core.Logger.LogInformation("[CSRoll] {Modifier} ({Slot}): VelocityModifier before write={Before} target={Target}",
                            Name, player.Slot, pawn.VelocityModifier, multiplier);
                    }
                }

                SetSpeedMultiplier(player, multiplier);

                // Jumping otherwise strips the speed bonus away: Stamina rises on every jump and
                // landing and reduces max speed until it decays. Zeroed per tick so it can never
                // accumulate - the same fix GameModifierBunnyHop already uses against the identical
                // mechanic. Skipped while walking, so shift-walking keeps vanilla fatigue for the
                // same reason the multiplier itself is skipped there.
                if (removeStaminaPenalty && multiplier != 1.0f && pawn.MovementServices is { } movementServices)
                {
                    movementServices.Stamina = 0f;
                    movementServices.StaminaUpdated();
                }
            }
        }
    }

    private static void SetSpeedMultiplier(SwiftlyS2.Shared.Players.IPlayer player, float multiplier)
    {
        if (player.PlayerPawn is not { } pawn)
        {
            return;
        }

        pawn.VelocityModifier = multiplier;
        pawn.VelocityModifierUpdated();
    }
}

/// <summary>
/// Formerly two separate modifiers (GameModifierLightweight here, plus a GameModifierSpeedhack
/// with an extra jump-momentum ProcessMovement hook) - consolidated into just this one, kept simple.
/// </summary>
public sealed class GameModifierSpeedhack : GameModifierVelocity
{
    public GameModifierSpeedhack()
    {
        Name = "Speedhack";
        Description = "Max movement speed is much faster";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["HeavyBoots"];
    }

    protected override float GetSpeedMultiplier() => Runtime.Config.Speedhack.SpeedMultiplier;

    protected override bool ShouldRemoveJumpStaminaPenalty() => Runtime.Config.Speedhack.RemoveJumpStaminaPenalty;
}

/// <summary>
/// Bug fix: this used to be a resources/ConVarModifiers/HeavyBootsModifier.cfg entry driving
/// sv_maxspeed/sv_jump_impulse - both server-wide cvars, so it slowed down every player instead of
/// just whoever rolled it. Rewritten as a proper per-player modifier using the same VelocityModifier
/// mechanism as Speedhack (just a fraction instead of a multiple) so it now only affects the
/// assigned player(s). The jump-impulse-reduction half of the original .cfg wasn't carried over -
/// there's no confirmed per-player schema field for jump power, and doing that part globally would
/// have defeated the whole point of this fix.
///
/// Also grants full armor+helmet and bonus health (on activation, and again on every spawn since
/// both reset each life) - a tradeoff for the reduced mobility, using CBaseEntity.ArmorValue and
/// CCSPlayer_ItemServices.HasHelmet (both confirmed settable schema fields with matching Updated()
/// methods) plus a flat health bonus on top of the normal spawn health.
///
/// Bug fix: OnDisabled used to only reset the speed multiplier (via base.OnDisabled()) - the armor,
/// helmet, and bonus health were never reverted, so a player whose HeavyBoots wore off mid-round kept
/// all three permanently for that life with none of the compensating slowdown anymore. Now caches
/// the pre-grant armor/helmet state per slot (freshly on every grant, since a fresh spawn naturally
/// resets both anyway) and reverts it on disable; the bonus health is subtracted back out rather
/// than reset to a stale cached value, clamped to a minimum of 1 so removing the bonus can never
/// itself be a death sentence.
/// </summary>
public sealed class GameModifierHeavyBoots : GameModifierVelocity
{
    private readonly Dictionary<int, int> _cachedOriginalArmor = [];
    private readonly Dictionary<int, bool> _cachedOriginalHasHelmet = [];
    private Guid _spawnHookId;

    public GameModifierHeavyBoots()
    {
        Name = "HeavyBoots";
        Description = "Movement speed is much slower - grants armor, a helmet and bonus health to compensate";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["Speedhack"];
    }

    protected override float GetSpeedMultiplier() => Runtime.Config.HeavyBoots.SpeedMultiplier;

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
        base.OnEnabled();

        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        foreach (var player in Core.PlayerManager.GetAlive())
        {
            if (IsAssignedTo(player.Slot))
            {
                GrantArmorAndHealth(player);
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_spawnHookId);

        foreach (var slot in _cachedOriginalArmor.Keys.ToList())
        {
            if (Core.PlayerManager.GetPlayer(slot) is { IsValid: true } player)
            {
                RevertArmorAndHealth(player);
            }
        }

        _cachedOriginalArmor.Clear();
        _cachedOriginalHasHelmet.Clear();

        base.OnDisabled();
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            GrantArmorAndHealth(player);
        }

        return HookResult.Continue;
    }

    private void GrantArmorAndHealth(IPlayer player)
    {
        if (player.PlayerPawn is not { } pawn)
        {
            return;
        }

        _cachedOriginalArmor[player.Slot] = pawn.ArmorValue;
        _cachedOriginalHasHelmet[player.Slot] = pawn.ItemServices?.HasHelmet ?? false;

        pawn.ArmorValue = Runtime.Config.HeavyBoots.ArmorValue;
        pawn.ArmorValueUpdated();

        if (pawn.ItemServices is { } itemServices)
        {
            itemServices.HasHelmet = true;
            itemServices.HasHelmetUpdated();
        }

        pawn.Health += Runtime.Config.HeavyBoots.BonusHealth;
        pawn.HealthUpdated();
    }

    private void RevertArmorAndHealth(IPlayer player)
    {
        if (player.PlayerPawn is not { } pawn)
        {
            return;
        }

        pawn.ArmorValue = _cachedOriginalArmor.GetValueOrDefault(player.Slot);
        pawn.ArmorValueUpdated();

        if (pawn.ItemServices is { } itemServices)
        {
            itemServices.HasHelmet = _cachedOriginalHasHelmet.GetValueOrDefault(player.Slot);
            itemServices.HasHelmetUpdated();
        }

        pawn.Health = Math.Max(1, pawn.Health - Runtime.Config.HeavyBoots.BonusHealth);
        pawn.HealthUpdated();
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _cachedOriginalArmor.Remove(@event.PlayerId);
        _cachedOriginalHasHelmet.Remove(@event.PlayerId);
    }
}
