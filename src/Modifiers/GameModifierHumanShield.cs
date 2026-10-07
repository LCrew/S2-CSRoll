using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.EntitySystem;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Config;
using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// The player carries a hostage on their back, the way CTs do on hostage maps: it slows them to
/// RunSpeedMultiplier (the same VelocityModifier mechanism as HeavyBoots), and hits from behind -
/// where the hostage hangs - deal BackDamageReduction less.
///
/// No real hostage is involved, so no rescue, kill or round rules apply and it works for both teams
/// on any map. What's spawned is the game's own carried-hostage prop (hostage_carriable_prop with
/// the hostage_carry model), bone-merged onto the player with FollowEntity and set as the pawn's
/// HostageServices.CarriedHostageProp - the same two things the game does itself when a hostage is
/// picked up, so the client draws it the way it draws a real carry.
///
/// The prop is maintained, not just placed once: twice a second every living assigned player is
/// checked, and one whose prop has gone (a death, a respawn, the engine cleaning it up) gets a new
/// one, while dead players lose theirs. That covers spawns and deaths without hooking either.
/// </summary>
public sealed class GameModifierHumanShield : GameModifierVelocity
{
    private const string CarryPropDesignerName = "hostage_carriable_prop";
    private const string CarryModel = "models/hostage/hostage_carry.vmdl";
    private const float MaintainIntervalSeconds = 0.5f;

    /// <summary>A hit counts as "from behind" when the attacker stands within about 70 degrees of straight behind the player.</summary>
    private const float BehindCosine = -0.34f;

    private readonly Dictionary<int, CHandle<CHostageCarriableProp>> _props = [];
    private float _nextMaintainAt;

    public GameModifierHumanShield()
    {
        Name = "HumanShield";
        Description = "You carry a hostage - slower, but hits from behind deal less damage";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;

        // Speedhack and HeavyBoots write the same VelocityModifier every tick; an invisible player
        // with a hostage hanging off their back gives themselves away.
        IncompatibleModifiers = ["Speedhack", "HeavyBoots", "ConditionalInvisibility", "Vanish"];
    }

    private HumanShieldConfig Cfg => Runtime.Config.HumanShield;

    public override IReadOnlyDictionary<string, string>? DynamicTextTokens => new Dictionary<string, string>
    {
        ["slow"] = $"{(1f - Cfg.RunSpeedMultiplier) * 100f:0}%",
        ["block"] = $"{Cfg.BackDamageReduction * 100f:0}%",
    };

    protected override float GetSpeedMultiplier() => Cfg.RunSpeedMultiplier;

    protected override void OnRegistered()
    {
        Core.Event.OnPrecacheResource += OnPrecacheResource;
        Core.Event.OnClientDisconnected += OnClientDisconnected;
    }

    protected override void OnUnregistered()
    {
        Core.Event.OnPrecacheResource -= OnPrecacheResource;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
    }

    protected override void OnEnabled()
    {
        base.OnEnabled();
        Core.Event.OnTick += OnTick;
        Core.GameHooks.Entities.TakeDamage.Pre += OnTakeDamage;
        _nextMaintainAt = 0f;
    }

    protected override void OnSlotsRemoved(IReadOnlyCollection<int> slots)
    {
        base.OnSlotsRemoved(slots);
        foreach (var slot in slots)
        {
            Detach(slot);
        }
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= OnTick;
        Core.GameHooks.Entities.TakeDamage.Pre -= OnTakeDamage;

        foreach (var slot in _props.Keys.ToList())
        {
            Detach(slot);
        }

        base.OnDisabled();
    }

    private void OnPrecacheResource(IOnPrecacheResourceEvent @event)
    {
        @event.AddItem(CarryModel);
    }

    private void OnTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;

        // The map clock restarts on a map change, so a deadline from before it would sit far ahead.
        if (now < _nextMaintainAt && now >= _nextMaintainAt - MaintainIntervalSeconds)
        {
            return;
        }

        _nextMaintainAt = now + MaintainIntervalSeconds;

        foreach (var player in GetAssignedPlayers())
        {
            if (!player.IsAlive)
            {
                Detach(player.Slot);
            }
            else if (!HasProp(player.Slot))
            {
                Attach(player);
            }
        }
    }

    private bool HasProp(int slot) =>
        _props.TryGetValue(slot, out var handle) && handle.IsValid && handle.Value is { } prop &&
        CSRollUtils.IsUsableHandle(prop) && prop.DesignerName == CarryPropDesignerName;

    private void Attach(IPlayer player)
    {
        if (player.PlayerPawn is not { } pawn || !CSRollUtils.IsUsableHandle(pawn) || pawn.AbsOrigin is not { } origin ||
            pawn.HostageServices is not { } services || !CSRollUtils.IsUsableHandle(services))
        {
            return;
        }

        Detach(player.Slot);

        var prop = Core.EntitySystem.CreateEntityByDesignerName<CHostageCarriableProp>(CarryPropDesignerName);
        using (var keyValues = new CEntityKeyValues())
        {
            keyValues.SetString("model", CarryModel);
            keyValues.SetVector("origin", origin);
            prop.DispatchSpawn(keyValues);
        }

        if (!CSRollUtils.IsUsableHandle(prop))
        {
            Core.Logger.LogWarning("[CSRoll] HumanShield: the hostage prop for slot {Slot} was destroyed during DispatchSpawn.", player.Slot);
            return;
        }

        // Riding on the player, it must never collide with anyone.
        if (prop.Collision.IsValid)
        {
            prop.Collision.CollisionGroup = (byte)CollisionGroup.Nonphysical;
            prop.Collision.CollisionGroupUpdated();
            prop.Collision.SolidFlags = 4; // FSOLID_NOT_SOLID
            prop.Collision.SolidFlagsUpdated();
            prop.Collision.SolidType = SolidType_t.SOLID_NONE;
            prop.Collision.SolidTypeUpdated();
        }

        prop.AcceptInput("FollowEntity", "!activator", pawn, pawn);

        var handle = Core.EntitySystem.GetRefEHandle(prop);
        services.CarriedHostageProp = new CHandle<CBaseEntity>(handle.Raw);
        services.CarriedHostagePropUpdated();

        _props[player.Slot] = handle;
        Core.Logger.LogInformation("[CSRoll] HumanShield: hostage #{Index} on slot {Slot}.", prop.Index, player.Slot);
    }

    /// <summary>Removes the slot's hostage prop, and clears the pawn's carried-hostage field if it still points at it.</summary>
    private void Detach(int slot)
    {
        if (!_props.Remove(slot, out var handle))
        {
            return;
        }

        if (Core.PlayerManager.GetPlayer(slot)?.PlayerPawn is { } pawn && CSRollUtils.IsUsableHandle(pawn) &&
            pawn.HostageServices is { } services && CSRollUtils.IsUsableHandle(services) && services.CarriedHostageProp.Raw == handle.Raw)
        {
            services.CarriedHostageProp = CHandle<CBaseEntity>.Invalid;
            services.CarriedHostagePropUpdated();
        }

        if (handle.IsValid && handle.Value is { } prop && CSRollUtils.IsUsableHandle(prop) && prop.DesignerName == CarryPropDesignerName)
        {
            prop.Despawn();
        }
    }

    /// <summary>Hits from another player standing behind the carrier land on the hostage first.</summary>
    private void OnTakeDamage(ref TakeDamageEntityPreContext ctx)
    {
        if (!TryGetAssignedTakeDamageVictim(ref ctx, out var victim) || !victim.IsAlive || !HasProp(victim.Slot) ||
            victim.PlayerPawn is not { } pawn || pawn.AbsOrigin is not { } at)
        {
            return;
        }

        if (ctx.Params.Info.Attacker.Value is not { IsValid: true } attacker || attacker.Index == pawn.Index ||
            attacker.As<CBaseEntity>().AbsOrigin is not { } from)
        {
            return;
        }

        var dx = from.X - at.X;
        var dy = from.Y - at.Y;
        var length = MathF.Sqrt((dx * dx) + (dy * dy));
        if (length < 1f)
        {
            return;
        }

        var yaw = pawn.EyeAngles.Yaw * MathF.PI / 180f;
        if (((MathF.Cos(yaw) * dx) + (MathF.Sin(yaw) * dy)) / length > BehindCosine)
        {
            return;
        }

        ctx.Params.Info.Damage *= 1f - Math.Clamp(Cfg.BackDamageReduction, 0f, 1f);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        // The pawn is gone with the player; only the prop needs removing.
        if (_props.Remove(@event.PlayerId, out var handle) &&
            handle.IsValid && handle.Value is { } prop && CSRollUtils.IsUsableHandle(prop) && prop.DesignerName == CarryPropDesignerName)
        {
            prop.Despawn();
        }
    }
}
