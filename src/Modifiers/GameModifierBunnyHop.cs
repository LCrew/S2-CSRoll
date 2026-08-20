using SwiftlyS2.Shared.Convars;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;

using Microsoft.Extensions.Logging;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// EXPERIMENTAL: a previous BunnyHop attempt was removed entirely per explicit request - the
/// landing-penalty-removal half worked, but the auto-jump-without-repressing half was attempted via
/// ProcessMovement.Pre velocity injection and never worked reliably.
///
/// Second attempt: hooking CheckJumpButtonLegacy/Modern.Pre and writing moveData.Velocity.Z there,
/// then SetHookResult(CancelOriginal) to skip the native's own debounce logic. Live testing (debug
/// log confirmed the override fired every tick while grounded and holding jump) showed no actual
/// jump ever happened. Root cause: CancelOriginal only skips that one check-and-decide function, not
/// the rest of that tick's movement pipeline (friction/ground-move) that still runs immediately
/// afterward - if the native function normally also flips the player to "airborne" internally before
/// that later code runs, skipping it entirely means the rest of the tick still treats the player as
/// grounded and silently re-absorbs the velocity we just wrote before it ever becomes visible. The
/// exact same failure class as Jetpack's original ProcessMovement.Post floor, just one hook earlier
/// in the pipeline.
///
/// Third attempt: don't touch the movement-hook pipeline at all. Detect "grounded and holding jump"
/// from Core.Event.OnTick (outside any movement hook, so nothing downstream can re-process and
/// clobber the write within the same tick) and directly re-assert velocity via
/// IPlayer.Teleport(velocity: ...) - the exact technique that fixed Jetpack's own hold-to-thrust for
/// the identical underlying problem. A single real jump (first press) is left completely untouched -
/// the native CheckJumpButton functions run normally for that, unaffected, since nothing hooks them
/// anymore; this modifier only re-launches the player on ticks it detects them already grounded
/// again while still holding jump. Still current - this is the auto-jump half, unrelated to the
/// speed-cap-removal history below.
///
/// Hold-detection uses Core.Event.OnClientKeyStateChanged (confirmed working for both this and
/// Jetpack) rather than polling IPlayer.PressedButtons.
///
/// Landing-penalty removal: CS2's own anti-bhop mechanic reduces max speed based on
/// CCSPlayer_MovementServices.Stamina (a "fatigue" value that rises on each jump and decays over
/// time) - zeroed every tick for the assigned player so it never accumulates.
///
/// Speed cap history: removing the timing skill (auto-relaunch) and the stamina penalty still wasn't
/// enough to let bhop-gained speed actually show up - CS2's normal speed cap silently clamped it
/// away. First attempt used CCSPlayerPawn.VelocityModifier (the same mechanism Speedhack/HeavyBoots
/// use) - reported wrong, since that's a flat multiplier on ALL movement, including normal ground
/// running, not just bhop-gained air speed. Second attempt raised IMoveData.MaxSpeed/ClientMaxSpeed
/// from inside AirAccelerate.Pre instead (that hook only ever fires while air-strafing, so it
/// structurally couldn't affect ground speed) - functional, but still just a userland workaround.
/// Third attempt layered a binary patch on top (ported from Fallen-Networks/CS2-MovementUnlocker,
/// later re-pointed at Source2ZE/CS2Fixes's actively-maintained equivalent after the original bytes
/// went stale) that NOPed out the actual native anti-bunnyhop check - the "real" fix, but a single
/// process-wide edit with zero per-player scoping (in effect for the whole server for the round,
/// not just whoever rolled it) and permanently exposed to future CS2 updates shifting the bytes again.
///
/// Fourth attempt (current): SwiftlyS2's own developers pointed at the actual first-party mechanism -
/// sv_bunnyhopping is a replicated convar, so IConVar.ReplicateToClientAsString(slot, "1") tells just
/// the assigned player's client its value is 1 while the real server-wide convar (and every other
/// player's replicated view of it) is untouched. This is genuinely per-player scoped (unlike the
/// binary patch) and needs no gamedata/signature maintenance at all - replacing both the
/// AirAccelerate.Pre multiplier and the MovementUnlocker patch entirely. Reverted the same way
/// (replicate the real, captured server value) in OnDisabled, and reapplied on every spawn as a
/// safety net in case a fresh life resets whatever the client thinks a replicated convar's value
/// currently is.
///
/// Extended to the full standard bhop-server convar set (sv_enablebunnyhopping/autobunnyhopping,
/// sv_maxvelocity, sv_airaccelerate, and the sv_stamina* family) via the same per-player
/// ReplicateToClientAsString mechanism - each is only ever replicated to the assigned player's own
/// client, never actually written to the real server-wide convar, so other players' movement (and
/// this player's movement once the modifier ends) is unaffected.
/// </summary>
public sealed class GameModifierBunnyHop : GameModifierBase
{
    /// <summary>Every convar this modifier overrides per-player, and the value it's set to while active. Reverted to each convar's real (captured) server value in OnDisabled.</summary>
    private static readonly (string Name, string OnValue)[] BunnyhopConVars =
    [
        ("sv_bunnyhopping", "1"),
        ("sv_enablebunnyhopping", "1"),
        ("sv_autobunnyhopping", "1"),
        ("sv_maxvelocity", "7000"),
        ("sv_airaccelerate", "2000"),
        ("sv_staminamax", "0"),
        ("sv_staminalandcost", "0"),
        ("sv_staminajumpcost", "0"),
        ("sv_accelerate_use_weapon_speed", "0"),
        ("sv_staminarecoveryrate", "0"),
    ];

    private readonly Dictionary<int, bool> _isHoldingSpace = [];
    private readonly Dictionary<string, IConVar> _convars = [];
    private readonly Dictionary<string, string> _originalValues = [];

    private Guid _spawnHookId;

    public GameModifierBunnyHop()
    {
        Name = "BunnyHop";
        Description = "Hold jump to bunny-hop automatically, with no landing speed penalty";
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
        Core.Event.OnClientKeyStateChanged += OnClientKeyStateChanged;
        Core.Event.OnTick += OnGameTick;
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        SetBunnyhopConVarsForAssignedPlayers(enabled: true);
    }

    protected override void OnDisabled()
    {
        Core.Event.OnClientKeyStateChanged -= OnClientKeyStateChanged;
        Core.Event.OnTick -= OnGameTick;
        Core.GameEvent.Unhook(_spawnHookId);
        _isHoldingSpace.Clear();

        SetBunnyhopConVarsForAssignedPlayers(enabled: false);
    }

    /// <summary>Resolves (and caches) a convar by name the first time it's needed, capturing its real server-side value on first resolve so OnDisabled can replicate players back to the genuine value instead of a hardcoded guess.</summary>
    private IConVar? ResolveConVar(string name)
    {
        if (_convars.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var convar = Core.ConVar.FindAsString(name);
        if (convar is null)
        {
            Core.Logger.LogError("[CSRoll] BunnyHop: {ConVar} convar not found - cannot override it per-player.", name);
            return null;
        }

        _convars[name] = convar;
        _originalValues[name] = convar.ValueAsString;
        return convar;
    }

    private void SetBunnyhopConVarsForAssignedPlayers(bool enabled)
    {
        foreach (var (name, onValue) in BunnyhopConVars)
        {
            if (ResolveConVar(name) is not { } convar)
            {
                continue;
            }

            var value = enabled ? onValue : _originalValues.GetValueOrDefault(name, convar.DefaultValueAsString);
            foreach (var player in GetAssignedPlayers())
            {
                convar.ReplicateToClientAsString(player.Slot, value);
            }
        }
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            foreach (var (name, onValue) in BunnyhopConVars)
            {
                ResolveConVar(name)?.ReplicateToClientAsString(player.Slot, onValue);
            }
        }

        return HookResult.Continue;
    }

    private void OnClientKeyStateChanged(IOnClientKeyStateChangedEvent @event)
    {
        if (@event.Key != KeyKind.Space || !IsAssignedTo(@event.PlayerId))
        {
            return;
        }

        _isHoldingSpace[@event.PlayerId] = @event.Pressed;
    }

    private void OnGameTick()
    {
        foreach (var player in GetAssignedPlayers())
        {
            if (!player.IsAlive)
            {
                continue;
            }

            if (player.PlayerPawn?.MovementServices is { } movementServices)
            {
                movementServices.Stamina = 0f;
                movementServices.StaminaUpdated();
            }

            TryAutoJump(player);
        }
    }

    private void TryAutoJump(IPlayer player)
    {
        if (!_isHoldingSpace.GetValueOrDefault(player.Slot, false))
        {
            return;
        }

        if (player.PlayerPawn is not { } pawn || pawn.GroundEntity.Value is null)
        {
            // Airborne (or no pawn) - nothing to relaunch; a real jump or existing arc runs untouched.
            return;
        }

        var velocity = pawn.AbsVelocity;
        player.Teleport(velocity: new Vector(velocity.X, velocity.Y, Runtime.Config.BunnyHop.JumpVelocityZ));

        if (Runtime.DebugMode)
        {
            Core.Logger.LogInformation("[CSRoll] BunnyHop ({Slot}): auto-jumped, set VelocityZ={VelZ}", player.Slot, Runtime.Config.BunnyHop.JumpVelocityZ);
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _isHoldingSpace.Remove(@event.PlayerId);
    }
}
