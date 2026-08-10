using SwiftlyS2.Shared.EntitySystem;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// EXPERIMENTAL test fork of GameModifierXrayAll/GameModifierXrayBase (see GameModifierXray.cs,
/// backed up verbatim as GameModifierXray.cs.bak before this fork was made) - deliberately NOT
/// wired into random rounds (SupportsRandomRounds/SupportsPerPlayerRandomization both false), only
/// reachable via manual "!addmodifier Xray2" / "!memodifier Xray2" for A/B testing against the
/// original "Wallhack".
///
/// Hypothesis under test: the original Wallhack can't see ConditionalInvisibility/FullInvisibility
/// targets at all (reported live), because its glow prop is attached to the real player pawn via a
/// two-hop FollowEntity relay chain - and entities using FollowEntity (MOVETYPE_FOLLOW) appear to
/// inherit their followed parent's per-client transmission state in Source 2. So when invisibility
/// calls ShouldBlockTransmitEntity(pawnId, true) against a given alive viewer, that same block
/// cascades down through the follow-chain to the relay and glow too, silently overriding this
/// modifier's own explicit unblock for x-ray-enabled viewers.
///
/// Fix under test: drop FollowEntity entirely. A single glow prop (no relay hop needed once nothing
/// is "following" anything at the engine level) is repositioned every tick via a direct Teleport()
/// call that copies the target pawn's current AbsOrigin/AbsRotation. A plain position copy has zero
/// parent/follow relationship to the real pawn, so the glow's transmission should be governed
/// purely by its own ApplyTransmitStateForAllViewers calls - unaffected by whatever invisibility is
/// doing to the real pawn's transmission for that viewer. Untested against the original single-hop
/// "vanishes after ~1s" rendering bug that motivated the relay chain in the first place - since that
/// bug was tied to FollowEntity-attaching directly to a complex animated entity, and this fork uses
/// no FollowEntity attachment at all (just a plain per-tick Teleport), it's expected not to
/// reproduce, but needs in-game verification like the rest of this Xray system.
/// </summary>
public abstract class GameModifierXrayBase2 : GameModifierBase
{
    private static readonly Color TerroristGlowColor = new(255, 165, 0);
    private static readonly Color CounterTerroristGlowColor = new(135, 206, 235);
    private static readonly Color GlowPropRenderColor = new(1, 255, 255, 255);

    protected readonly HashSet<int> CachedXrayEnabledSlots = [];
    private readonly Dictionary<int, uint> _glowPropEntityIndex = [];

    private Guid _spawnHookId;
    private Guid _deathHookId;

    protected override void OnRegistered()
    {
        Core.Event.OnClientConnected += OnClientConnected;
        Core.Event.OnClientDisconnected += OnClientDisconnected;
    }

    protected override void OnUnregistered()
    {
        Core.Event.OnClientConnected -= OnClientConnected;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
    }

    protected override void OnEnabled()
    {
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawnEvent);
        _deathHookId = Core.GameEvent.HookPost<EventPlayerDeath>(OnPlayerDeathEvent);
        Core.Event.OnTick += OnGameTick;

        SetupXray();
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_spawnHookId);
        Core.GameEvent.Unhook(_deathHookId);
        Core.Event.OnTick -= OnGameTick;

        foreach (var slot in _glowPropEntityIndex.Keys.ToList())
        {
            RemoveXrayFromSlot(slot);
        }

        CachedXrayEnabledSlots.Clear();
    }

    protected virtual void SetupXray()
    {
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (CheckEnableXray(player))
            {
                CachedXrayEnabledSlots.Add(player.Slot);
            }
        }

        foreach (var player in Core.PlayerManager.GetAlive())
        {
            ApplyXrayToPlayer(player);
        }
    }

    protected virtual bool CheckEnableXray(IPlayer viewer) => false;

    protected void ApplyXrayToPlayer(IPlayer target)
    {
        RemoveXrayFromSlot(target.Slot);

        var targetSlot = target.Slot;
        Core.Scheduler.NextWorldUpdate(() =>
        {
            var currentTarget = Core.PlayerManager.GetPlayer(targetSlot);
            if (currentTarget is not { IsValid: true, IsAlive: true } || currentTarget.PlayerPawn is not { } pawn)
            {
                return;
            }

            var modelName = pawn.GetModel();
            if (string.IsNullOrEmpty(modelName))
            {
                return;
            }

            var glow = CreateGlowProp(modelName);

            glow.Render = GlowPropRenderColor;
            glow.RenderUpdated();
            glow.Glow.GlowColorOverride = currentTarget.Controller?.Team == Team.T ? TerroristGlowColor : CounterTerroristGlowColor;
            glow.Glow.GlowColorOverrideUpdated();
            glow.Glow.GlowRange = 5000;
            glow.Glow.GlowRangeUpdated();
            glow.Glow.GlowRangeMin = 20;
            glow.Glow.GlowRangeMinUpdated();
            glow.Glow.GlowTeam = -1;
            glow.Glow.GlowTeamUpdated();
            glow.Glow.GlowType = 3;
            glow.GlowUpdated();

            if (pawn.AbsOrigin is { } position)
            {
                glow.Teleport(position, pawn.AbsRotation, null);
            }

            _glowPropEntityIndex[targetSlot] = glow.Index;

            ApplyTransmitStateForAllViewers((int)glow.Index);
        });
    }

    /// <summary>Same spawn/model/collision setup GameModifierXray.cs's CreateGlowChainProp uses - minus the relay-specific bits (Spawnflags/identity-flag clear are kept since they're what made the glow itself render reliably, not specific to the relay hop).</summary>
    private CDynamicProp CreateGlowProp(string modelName)
    {
        var prop = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>("prop_dynamic");

        prop.Spawnflags = 256u;
        prop.SpawnflagsUpdated();

        using var keyValues = new CEntityKeyValues();
        prop.DispatchSpawn(keyValues);

        prop.SetModel(modelName);

        if (prop.Identity is { } identity)
        {
            identity.Flags &= ~(uint)(1 << 2);
        }

        prop.Collision.CollisionGroup = (byte)CollisionGroup.Nonphysical;
        prop.Collision.CollisionGroupUpdated();
        prop.Collision.SolidFlags = 4; // FSOLID_NOT_SOLID
        prop.Collision.SolidFlagsUpdated();
        prop.Collision.SolidType = SolidType_t.SOLID_NONE;
        prop.Collision.SolidTypeUpdated();

        return prop;
    }

    private void ApplyTransmitStateForAllViewers(int entityIndex)
    {
        foreach (var viewer in Core.PlayerManager.GetAllValidPlayers())
        {
            viewer.ShouldBlockTransmitEntity(entityIndex, !CachedXrayEnabledSlots.Contains(viewer.Slot));
        }
    }

    protected void RemoveXrayFromSlot(int slot)
    {
        if (_glowPropEntityIndex.Remove(slot, out var glowIndex) && Core.EntitySystem.GetEntityByIndex(glowIndex) is { IsValid: true } glowEntity)
        {
            glowEntity.Despawn();
        }
    }

    /// <summary>The core of this fork: glues each glow prop's position/angle to its real target every tick via a plain Teleport() copy - never an engine FollowEntity relationship - so the prop's own transmission state stays fully independent of whatever the real pawn's transmission is doing for a given viewer.</summary>
    private void OnGameTick()
    {
        foreach (var (slot, glowIndex) in _glowPropEntityIndex.ToList())
        {
            var target = Core.PlayerManager.GetPlayer(slot);
            if (target is not { IsValid: true, IsAlive: true } || target.PlayerPawn is not { } pawn || pawn.AbsOrigin is not { } position)
            {
                RemoveXrayFromSlot(slot);
                continue;
            }

            if (Core.EntitySystem.GetEntityByIndex(glowIndex) is not CDynamicProp { IsValid: true } glow)
            {
                continue;
            }

            glow.Teleport(position, pawn.AbsRotation, null);
        }
    }

    private HookResult OnPlayerSpawnEvent(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            ApplyXrayToPlayer(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDeathEvent(EventPlayerDeath @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            RemoveXrayFromSlot(player.Slot);
        }

        return HookResult.Continue;
    }

    private void OnClientConnected(IOnClientConnectedEvent @event)
    {
        if (!IsActive)
        {
            return;
        }

        var viewer = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (viewer is not { IsValid: true })
        {
            return;
        }

        var isXrayEnabled = CachedXrayEnabledSlots.Contains(viewer.Slot);
        foreach (var entityIndex in _glowPropEntityIndex.Values)
        {
            viewer.ShouldBlockTransmitEntity((int)entityIndex, !isXrayEnabled);
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        CachedXrayEnabledSlots.Remove(@event.PlayerId);
        _glowPropEntityIndex.Remove(@event.PlayerId);
    }
}

public sealed class GameModifierXrayAll2 : GameModifierXrayBase2
{
    public GameModifierXrayAll2()
    {
        Name = "Xray2";
        Description = "[TEST] You can see everyone through walls, including invisible players";

        // Deliberately excluded from both random-round pools - this is a manual A/B test fork
        // ("!addmodifier Xray2" / "!memodifier Xray2"), not a real rollable modifier.
        SupportsRandomRounds = false;
        SupportsPerPlayerRandomization = false;
    }

    protected override bool CheckEnableXray(IPlayer viewer) => IsAssignedTo(viewer.Slot);
}
