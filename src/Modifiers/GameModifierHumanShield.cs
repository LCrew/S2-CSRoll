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
/// on any map. The hostage is the game's hostage_carry model on a plain prop_dynamic, bone-merged
/// onto the player through Wallhack's relay chain: an invisible relay carrying the player's own model
/// is merged onto the pawn, and the hostage onto the relay. The game's own hostage_carriable_prop set
/// as the pawn's CarriedHostageProp was tried first and never showed up live - and a single prop
/// merged straight onto a pawn vanishes after about a second (see Wallhack), which is what the relay
/// is for. VisibleToCarrier decides whether the carrier sees it from their own camera too - it is the
/// third-person model seen from inside, not CS's first-person hostage arm.
///
/// The props are maintained, not just placed once: twice a second every living assigned player is
/// checked, and one whose hostage has gone (a death, a respawn, the engine cleaning it up) gets a new
/// chain, while dead players lose theirs. That covers spawns and deaths without hooking either.
/// </summary>
public sealed class GameModifierHumanShield : GameModifierVelocity
{
    private const string PropDesignerName = "prop_dynamic";
    private const string CarryModel = "models/hostage/hostage_carry.vmdl";
    private const float MaintainIntervalSeconds = 0.5f;

    /// <summary>A hit counts as "from behind" when the attacker stands within about 70 degrees of straight behind the player.</summary>
    private const float BehindCosine = -0.34f;

    private readonly Dictionary<int, (CHandle<CDynamicProp> Relay, CHandle<CDynamicProp> Hostage)> _props = [];
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

    private bool HasProp(int slot) => _props.TryGetValue(slot, out var chain) && Resolve(chain.Hostage) is not null;

    private static CDynamicProp? Resolve(CHandle<CDynamicProp> handle) =>
        handle.IsValid && handle.Value is { } prop && CSRollUtils.IsUsableHandle(prop) && prop.DesignerName == PropDesignerName ? prop : null;

    private void Attach(IPlayer player)
    {
        if (player.PlayerPawn is not { } pawn || !CSRollUtils.IsUsableHandle(pawn) || pawn.AbsOrigin is not { } origin ||
            pawn.GetModel() is not { Length: > 0 } playerModel)
        {
            return;
        }

        Detach(player.Slot);

        if (SpawnProp(player.Slot, playerModel, origin, "relay") is not { } relay)
        {
            return;
        }

        // The relay only carries the merged skeleton; it is never drawn.
        relay.RenderMode = RenderMode_t.kRenderNone;
        relay.RenderModeUpdated();

        if (SpawnProp(player.Slot, CarryModel, origin, "hostage") is not { } hostage)
        {
            relay.Despawn();
            return;
        }

        // FollowEntity is a bone merge: the relay takes the player's pose, the hostage the relay's.
        relay.AcceptInput("FollowEntity", "!activator", pawn, pawn, 0);
        hostage.AcceptInput("FollowEntity", "!activator", relay, relay, 0);

        // Off: the carrier's own client never receives the chain, so they only see it on others.
        if (!Cfg.VisibleToCarrier)
        {
            player.ShouldBlockTransmitEntity((int)hostage.Index, true);
            player.ShouldBlockTransmitEntity((int)relay.Index, true);
        }

        _props[player.Slot] = (Core.EntitySystem.GetRefEHandle(relay), Core.EntitySystem.GetRefEHandle(hostage));
        Core.Logger.LogInformation("[CSRoll] HumanShield: hostage #{Hostage} (relay #{Relay}) on slot {Slot}.", hostage.Index, relay.Index, player.Slot);
    }

    /// <summary>Wallhack's spawn recipe: model and origin as spawn keyvalues (a bare prop_dynamic given a model afterwards has no render bounds), non-solid.</summary>
    private CDynamicProp? SpawnProp(int slot, string model, Vector origin, string role)
    {
        var prop = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>(PropDesignerName);
        using (var keyValues = new CEntityKeyValues())
        {
            keyValues.SetString("model", model);
            keyValues.SetVector("origin", origin);
            prop.DispatchSpawn(keyValues);
        }

        if (!CSRollUtils.IsUsableHandle(prop))
        {
            Core.Logger.LogWarning("[CSRoll] HumanShield: the {Role} for slot {Slot} was destroyed during DispatchSpawn.", role, slot);
            return null;
        }

        if (prop.Collision.IsValid)
        {
            prop.Collision.CollisionGroup = (byte)CollisionGroup.Nonphysical;
            prop.Collision.CollisionGroupUpdated();
            prop.Collision.SolidFlags = 4; // FSOLID_NOT_SOLID
            prop.Collision.SolidFlagsUpdated();
            prop.Collision.SolidType = SolidType_t.SOLID_NONE;
            prop.Collision.SolidTypeUpdated();
        }

        return prop;
    }

    /// <summary>Removes the slot's chain. The carrier's transmit block is lifted first - indices get recycled, and a block left behind would hide whatever inherits the index.</summary>
    private void Detach(int slot)
    {
        if (!_props.Remove(slot, out var chain))
        {
            return;
        }

        var carrier = Core.PlayerManager.GetPlayer(slot);
        foreach (var prop in new[] { Resolve(chain.Hostage), Resolve(chain.Relay) })
        {
            if (prop is null)
            {
                continue;
            }

            if (carrier is { IsValid: true })
            {
                carrier.ShouldBlockTransmitEntity((int)prop.Index, false);
            }

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
        // The carrier is gone, so there's no transmit block left to lift - just the props.
        if (_props.Remove(@event.PlayerId, out var chain))
        {
            Resolve(chain.Hostage)?.Despawn();
            Resolve(chain.Relay)?.Despawn();
        }
    }
}
