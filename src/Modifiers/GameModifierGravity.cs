using Microsoft.Extensions.Logging;

namespace CSRoll.Modifiers;

/// <summary>
/// Scales a player's fall speed via the per-entity CBaseEntity.GravityScale schema field (confirmed
/// via SwiftlyS2.CS2.dll metadata inspection: a ref float property with a matching
/// GravityScaleUpdated() network-state-changed method) - unlike the sv_gravity console variable
/// these two modifiers used to drive, GravityScale is per-entity, so it can actually be scoped to
/// just the assigned player(s) instead of affecting the whole server. Re-applies every tick, same
/// rationale as GameModifierVelocity: the engine can reset a schema field more often than a slower
/// timer would keep up with.
///
/// Live testing reported this "doesn't work at all", unlike VelocityModifier (same OnTick-reapply
/// pattern, confirmed working for Speedhack/LeadBoots). CBaseEntity also exposes a separate
/// ActualGravityScale (read-only getter, no matching Updated()/setter - looks like a computed
/// "effective" value derived from GravityScale rather than something to write directly) and
/// GravityDisabled/GravityActuallyDisabled flags. Added throttled !debug logging of GravityScale AND
/// ActualGravityScale around each write to see whether the write is failing to stick at all, or
/// sticking on GravityScale but never reflected in ActualGravityScale (which would mean the physics
/// simulation isn't actually consuming GravityScale live the way movement code consumes
/// VelocityModifier) - rather than guess further at a fix blind.
/// </summary>
public abstract class GameModifierGravity : GameModifierBase
{
    private readonly Dictionary<int, float> _lastDebugLogTime = [];

    protected abstract float GetGravityMultiplier();

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
                SetGravityMultiplier(player, 1.0f);
            }
        }
    }

    private void ApplyToAllPlayers()
    {
        var multiplier = GetGravityMultiplier();
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot) && player.PlayerPawn is { } pawn)
            {
                if (Runtime.DebugMode)
                {
                    var now = Core.Engine.GlobalVars.CurrentTime;
                    if (!_lastDebugLogTime.TryGetValue(player.Slot, out var lastLog) || now - lastLog >= 1f)
                    {
                        _lastDebugLogTime[player.Slot] = now;
                        Core.Logger.LogInformation("[CSRoll] {Modifier} ({Slot}): before write GravityScale={GravityScale} ActualGravityScale={Actual} target={Target}",
                            Name, player.Slot, pawn.GravityScale, pawn.ActualGravityScale, multiplier);
                    }
                }

                SetGravityMultiplier(player, multiplier);
            }
        }
    }

    private static void SetGravityMultiplier(SwiftlyS2.Shared.Players.IPlayer player, float multiplier)
    {
        if (player.PlayerPawn is not { } pawn)
        {
            return;
        }

        pawn.GravityScale = multiplier;
        pawn.GravityScaleUpdated();
    }
}

/// <summary>
/// Bug fix: this used to be resources/ConVarModifiers/HighGravityModifier.cfg driving the
/// server-wide sv_gravity cvar - Source has no per-client gravity via cvar, so it affected everyone
/// regardless of who rolled it. Rewritten to scale just the assigned player's own GravityScale.
/// </summary>
public sealed class GameModifierHighGravity : GameModifierGravity
{
    public GameModifierHighGravity()
    {
        Name = "HighGravity";
        Description = "Gravity is much stronger";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["LowGravity"];
    }

    protected override float GetGravityMultiplier() => Runtime.Config.Gravity.HighGravityMultiplier;
}

/// <summary>Per-player counterpart to GameModifierHighGravity - see its remarks for the same sv_gravity-to-GravityScale bug fix.</summary>
public sealed class GameModifierLowGravity : GameModifierGravity
{
    public GameModifierLowGravity()
    {
        Name = "LowGravity";
        Description = "Gravity is much weaker";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["HighGravity"];
    }

    protected override float GetGravityMultiplier() => Runtime.Config.Gravity.LowGravityMultiplier;
}
