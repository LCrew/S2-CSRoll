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

    protected override void OnEnabled()
    {
        Core.Event.OnTick += ApplyToAllPlayers;
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= ApplyToAllPlayers;

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot))
            {
                SetSpeedMultiplier(player, 1.0f);
            }
        }
    }

    private void ApplyToAllPlayers()
    {
        var runMultiplier = GetSpeedMultiplier();
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot) && player.PlayerPawn is { } pawn)
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
        IncompatibleModifiers = ["LeadBoots"];
    }

    protected override float GetSpeedMultiplier() => Runtime.Config.Speedhack.SpeedMultiplier;
}

/// <summary>
/// Bug fix: this used to be a resources/ConVarModifiers/LeadBootsModifier.cfg entry driving
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
/// </summary>
public sealed class GameModifierLeadBoots : GameModifierVelocity
{
    private Guid _spawnHookId;

    public GameModifierLeadBoots()
    {
        Name = "LeadBoots";
        Description = "Movement speed is much slower - grants armor, a helmet and bonus health to compensate";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["Speedhack"];
    }

    protected override float GetSpeedMultiplier() => Runtime.Config.LeadBoots.SpeedMultiplier;

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

        pawn.ArmorValue = Runtime.Config.LeadBoots.ArmorValue;
        pawn.ArmorValueUpdated();

        if (pawn.ItemServices is { } itemServices)
        {
            itemServices.HasHelmet = true;
            itemServices.HasHelmetUpdated();
        }

        pawn.Health += Runtime.Config.LeadBoots.BonusHealth;
        pawn.HealthUpdated();
    }
}
