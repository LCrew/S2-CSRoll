using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// When the assigned player dies, drops a config-tunable number of live HE grenades near their body
/// (scattered outward with a short toss, same technique as GameModifierClusterGrenades) that explode
/// for a config-tunable damage multiplier.
///
/// The multiplier is applied via Core.GameHooks.Entities.TakeDamage.Pre, matching
/// GameModifierBiggerExplosions - but identified differently: the player who owns these grenades is
/// already dead by the time they detonate, so resolving CTakeDamageInfo.Attacker back to a player
/// (as BiggerExplosions does) isn't reliable here. Instead this tracks the specific entity indices of
/// the grenades it spawns itself and checks CTakeDamageInfo.Inflictor (the actual damaging entity -
/// a separate field from Attacker, confirmed via metadata) against that set, so it works regardless
/// of whether the thrower is still alive. Tracked indices are removed once EventHegrenadeDetonate
/// confirms that specific grenade has gone off, so a much later, unrelated entity that happens to
/// reuse the same recycled index is never mistaken for one of ours.
///
/// Uses Core.Game.EmitHEGrenade(pos, angle, velocity, owner) - the same purpose-built factory method
/// ClusterGrenades already confirmed reliably creates a fully-armed, normal-fuse grenade (no manual
/// entity setup needed).
/// </summary>
public sealed class GameModifierKamikaze : GameModifierBase
{
    private const float ScatterSpeed = 150f;

    private readonly HashSet<uint> _kamikazeGrenadeIndices = [];
    private Guid _deathHookId;
    private Guid _detonateHookId;

    public override IReadOnlyDictionary<string, string>? DynamicTextTokens => new Dictionary<string, string>
    {
        ["count"] = Runtime.Config.Kamikaze.GrenadeCount.ToString(),
        ["mult"] = $"{Runtime.Config.Kamikaze.DamageMultiplier:0.##}x",
    };

    public override string Description =>
        $"On death, drops {Runtime.Config.Kamikaze.GrenadeCount} grenades near your body that explode for {Runtime.Config.Kamikaze.DamageMultiplier:0.##}x HE damage";

    public GameModifierKamikaze()
    {
        Name = "Kamikaze";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        _deathHookId = Core.GameEvent.HookPost<EventPlayerDeath>(OnPlayerDeath);
        _detonateHookId = Core.GameEvent.HookPost<EventHegrenadeDetonate>(OnHegrenadeDetonate);
        Core.GameHooks.Entities.TakeDamage.Pre += OnTakeDamage;
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_deathHookId);
        Core.GameEvent.Unhook(_detonateHookId);
        Core.GameHooks.Entities.TakeDamage.Pre -= OnTakeDamage;
        _kamikazeGrenadeIndices.Clear();
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event)
    {
        var victim = @event.UserIdPlayer;
        if (victim is not { IsValid: true } || !IsAssignedTo(victim.Slot) || @event.UserIdPawn is not { } pawn || pawn.AbsOrigin is not { } position)
        {
            return HookResult.Continue;
        }

        var count = Math.Max(0, Runtime.Config.Kamikaze.GrenadeCount);
        for (var i = 0; i < count; i++)
        {
            var angleRadians = Random.Shared.NextSingle() * MathF.Tau;
            var velocity = new Vector(MathF.Cos(angleRadians) * ScatterSpeed, MathF.Sin(angleRadians) * ScatterSpeed, ScatterSpeed * 0.4f);
            var angle = velocity.ToQAngles();

            _kamikazeGrenadeIndices.Add(Core.Game.EmitHEGrenade(position, angle, velocity, pawn).Index);
        }

        return HookResult.Continue;
    }

    private HookResult OnHegrenadeDetonate(EventHegrenadeDetonate @event)
    {
        _kamikazeGrenadeIndices.Remove((uint)@event.EntityID);
        return HookResult.Continue;
    }

    private void OnTakeDamage(ref TakeDamageEntityPreContext ctx)
    {
        if ((ctx.Params.Info.DamageType & DamageTypes_t.DMG_BLAST) == 0 ||
            ctx.Params.Info.Inflictor.Value is not { } inflictor || !_kamikazeGrenadeIndices.Contains((uint)inflictor.Index))
        {
            return;
        }

        ctx.Params.Info.Damage *= Runtime.Config.Kamikaze.DamageMultiplier;
    }
}
