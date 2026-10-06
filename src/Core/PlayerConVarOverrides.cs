using System.Globalization;

using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Convars;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

namespace CSRoll.Core;

/// <summary>
/// Per-player convar overrides that the SERVER honours too, not just the player's own client.
///
/// Why this exists (BunnyHop "doesn't feel like real bhop, not smooth"): ReplicateToClient on its own
/// only changes what that one player's CLIENT believes a replicated convar is. The client predicts its
/// own movement with that value, while the server keeps simulating the very same player with the real
/// one - so BunnyHop ran two different physics every tick (autobhop on the client and off on the
/// server, 2000 air-accelerate on the client and 12 on the server, no stamina on the client and full
/// stamina on the server), and every disagreement came back as the server snapping the player to
/// where IT thought they were.
///
/// Fix: alongside the replication, the real convar is switched to the override value for exactly the
/// stretch of server code that simulates THAT player - their user commands, their movement and their
/// post-think (which is where weapons fire) - and switched straight back before anyone else is
/// simulated. Client and server now run the same code with the same values, so prediction agrees.
///
/// The switch goes through the typed IConVar&lt;T&gt;.SetInternal, never SetInternalAsString. Only the
/// typed path sets SwiftlyS2's bypassConvarCallbacks, which skips the engine's global change callbacks
/// - the ones that broadcast a replicated convar to EVERY client and print FCVAR_NOTIFY changes to
/// chat. The string path fires them, which here would mean several broadcasts per player per tick.
///
/// Scoping is one player at a time with a depth counter, because the three wrapped hooks may nest
/// (movement runs inside user-command simulation). A scope that is never closed - another plugin
/// cancelling the original between our Pre and Post skips the Post - is closed by the next player's
/// Pre, and at the latest by the next OnTick, so a leaked override can never outlive one player's
/// simulation.
///
/// Hooks are only installed while at least one override exists, so a server with nobody running an
/// override modifier pays nothing per tick.
/// </summary>
public sealed class PlayerConVarOverrides
{
    private abstract class ConVarHandle(string name)
    {
        public string Name { get; } = name;
        public bool Applied { get; protected set; }

        public abstract bool TryParse(string text, out object value);
        public abstract string Format(object value);
        public abstract void Apply(object value);
        public abstract void Restore();

        /// <summary>The genuine server value, formatted for replication - the saved one while an override is applied, since the live value is the override then.</summary>
        public abstract string RealValueText { get; }
    }

    private sealed class ConVarHandle<T>(string name, IConVar<T> convar, ConVarParser<T> parse, Func<T, string> format) : ConVarHandle(name)
        where T : struct
    {
        private T _real;

        public override bool TryParse(string text, out object value)
        {
            if (parse(text, out var parsed))
            {
                value = parsed;
                return true;
            }

            value = default(T);
            return false;
        }

        public override string Format(object value) => format((T)value);

        public override void Apply(object value)
        {
            if (!Applied)
            {
                _real = convar.Value;
                Applied = true;
            }

            convar.SetInternal((T)value);
        }

        public override void Restore()
        {
            if (!Applied)
            {
                return;
            }

            convar.SetInternal(_real);
            Applied = false;
        }

        public override string RealValueText => format(Applied ? _real : convar.Value);
    }

    private delegate bool ConVarParser<T>(string text, out T value);

    private readonly record struct Entry(ConVarHandle Handle, object Value, string Text);

    private readonly ISwiftlyCore _core;

    /// <summary>slot -> owner -> that owner's overrides for the slot. Several owners can override the same slot; on a clash for the same convar the most recently registered owner wins.</summary>
    private readonly Dictionary<int, Dictionary<object, Entry[]>> _bySlot = [];

    /// <summary>Resolved once per name and kept, including misses (null) so a missing convar is only reported once per load.</summary>
    private readonly Dictionary<string, ConVarHandle?> _handles = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ConVarHandle> _applied = [];
    private int _scopeSlot = -1;
    private int _scopeDepth;

    private bool _installed;
    private Guid _spawnHookId;

    public PlayerConVarOverrides(ISwiftlyCore core)
    {
        _core = core;
    }

    /// <summary>
    /// Overrides these convars for one player, on the server during their simulation and on their own
    /// client via replication. Calling it again for the same owner and slot replaces that owner's set.
    /// Values are plain convar text ("1", "0", "1000"); a convar that doesn't exist, or a value that
    /// doesn't parse as its type, is skipped with a warning rather than failing the rest.
    /// </summary>
    public void Set(object owner, int slot, IReadOnlyList<(string Name, string Value)> overrides)
    {
        var entries = new List<Entry>(overrides.Count);
        foreach (var (name, text) in overrides)
        {
            if (Resolve(name) is not { } handle)
            {
                continue;
            }

            if (!handle.TryParse(text, out var value))
            {
                _core.Logger.LogWarning("[CSRoll] Convar override {Name}={Value} doesn't parse as that convar's type - skipped.", name, text);
                continue;
            }

            entries.Add(new Entry(handle, value, handle.Format(value)));
        }

        if (entries.Count == 0)
        {
            return;
        }

        // The slot's values change underneath an open scope otherwise - close it so the next Pre
        // applies the new set from a clean real-value snapshot.
        if (_scopeSlot == slot)
        {
            CloseScope();
        }

        if (!_bySlot.TryGetValue(slot, out var owners))
        {
            owners = [];
            _bySlot[slot] = owners;
        }

        owners[owner] = [.. entries];
        Install();

        foreach (var entry in entries)
        {
            Replicate(slot, entry.Handle.Name, entry.Text);
        }
    }

    /// <summary>Drops one owner's overrides for one player and tells their client the real values again (or another owner's override, where one still covers the same convar).</summary>
    public void Remove(object owner, int slot)
    {
        if (!_bySlot.TryGetValue(slot, out var owners) || !owners.Remove(owner, out var removed))
        {
            return;
        }

        if (_scopeSlot == slot)
        {
            CloseScope();
        }

        if (owners.Count == 0)
        {
            _bySlot.Remove(slot);
        }

        foreach (var entry in removed)
        {
            var still = FindOverride(owners, entry.Handle);
            Replicate(slot, entry.Handle.Name, still?.Text ?? entry.Handle.RealValueText);
        }

        UninstallIfEmpty();
    }

    /// <summary>Remove() for every slot this owner covers - what a modifier's OnDisabled calls.</summary>
    public void RemoveAll(object owner)
    {
        foreach (var slot in _bySlot.Where(pair => pair.Value.ContainsKey(owner)).Select(pair => pair.Key).ToList())
        {
            Remove(owner, slot);
        }
    }

    /// <summary>Forgets everything without replicating anything back - for a plugin unload or registry reload, after every modifier's own OnDisabled has already removed (and replicated) its overrides.</summary>
    public void Clear()
    {
        CloseScope();
        _bySlot.Clear();
        _handles.Clear();
        UninstallIfEmpty();
    }

    private static Entry? FindOverride(Dictionary<object, Entry[]> owners, ConVarHandle handle)
    {
        Entry? found = null;
        foreach (var entries in owners.Values)
        {
            foreach (var entry in entries)
            {
                if (entry.Handle == handle)
                {
                    found = entry;
                }
            }
        }

        return found;
    }

    private ConVarHandle? Resolve(string name)
    {
        if (_handles.TryGetValue(name, out var cached))
        {
            return cached;
        }

        // IConVar<T> throws on a type mismatch rather than returning null, and nothing on the
        // non-generic IConVar exposes the type, so each supported type is simply tried in turn.
        var handle =
            TryCreate<bool>(name, TryParseBool, value => value ? "1" : "0") ??
            TryCreate<float>(name, TryParseFloat, value => value.ToString(CultureInfo.InvariantCulture)) ??
            TryCreate<int>(name, TryParseInt, value => value.ToString(CultureInfo.InvariantCulture));

        if (handle is null)
        {
            _core.Logger.LogWarning("[CSRoll] Convar {Name} not found (or not a bool/float/int convar) - its per-player override is skipped.", name);
        }

        _handles[name] = handle;
        return handle;
    }

    private ConVarHandle? TryCreate<T>(string name, ConVarParser<T> parse, Func<T, string> format) where T : struct
    {
        try
        {
            return _core.ConVar.Find<T>(name) is { } convar ? new ConVarHandle<T>(name, convar, parse, format) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryParseBool(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
                value = true;
                return true;
            case "0":
            case "false":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool TryParseFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Through IConVarService's string overload on purpose: the typed IConVar&lt;T&gt;.ReplicateToClient
    /// formats floats with the server's current culture, which on a comma-decimal locale would send
    /// "0,08" for 0.08 - every value here is already invariant text.
    /// </summary>
    private void Replicate(int slot, string name, string text)
    {
        if (_core.PlayerManager.GetPlayer(slot) is not { IsValid: true, IsFakeClient: false })
        {
            return;
        }

        _core.ConVar.ReplicateToClient(slot, name, text);
    }

    private void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        _core.GameHooks.Controller.SimulateUserCommands.Pre += OnSimulateUserCommandsPre;
        _core.GameHooks.Controller.SimulateUserCommands.Post += OnSimulateUserCommandsPost;
        _core.GameHooks.Movement.ProcessMovement.Pre += OnProcessMovementPre;
        _core.GameHooks.Movement.ProcessMovement.Post += OnProcessMovementPost;
        _core.GameHooks.Pawn.PostThink.Pre += OnPostThinkPre;
        _core.GameHooks.Pawn.PostThink.Post += OnPostThinkPost;
        _core.Event.OnTick += OnTick;
        _core.Event.OnClientDisconnected += OnClientDisconnected;
        _spawnHookId = _core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);
    }

    private void UninstallIfEmpty()
    {
        if (!_installed || _bySlot.Count > 0)
        {
            return;
        }

        CloseScope();

        _installed = false;
        _core.GameHooks.Controller.SimulateUserCommands.Pre -= OnSimulateUserCommandsPre;
        _core.GameHooks.Controller.SimulateUserCommands.Post -= OnSimulateUserCommandsPost;
        _core.GameHooks.Movement.ProcessMovement.Pre -= OnProcessMovementPre;
        _core.GameHooks.Movement.ProcessMovement.Post -= OnProcessMovementPost;
        _core.GameHooks.Pawn.PostThink.Pre -= OnPostThinkPre;
        _core.GameHooks.Pawn.PostThink.Post -= OnPostThinkPost;
        _core.Event.OnTick -= OnTick;
        _core.Event.OnClientDisconnected -= OnClientDisconnected;
        _core.GameEvent.Unhook(_spawnHookId);
    }

    private void OnSimulateUserCommandsPre(ref SimulateUserCommandsPreContext ctx) => EnterScope(ctx.Params.Player);
    private void OnSimulateUserCommandsPost(ref SimulateUserCommandsPostContext ctx) => ExitScope(ctx.Params.Player);
    private void OnProcessMovementPre(ref ProcessMovementMovementPreContext ctx) => EnterScope(ctx.Params.Player);
    private void OnProcessMovementPost(ref ProcessMovementMovementPostContext ctx) => ExitScope(ctx.Params.Player);
    private void OnPostThinkPre(ref PostThinkPawnPreContext ctx) => EnterScope(ctx.Params.Player);
    private void OnPostThinkPost(ref PostThinkPawnPostContext ctx) => ExitScope(ctx.Params.Player);

    private void EnterScope(IPlayer? player)
    {
        if (player is not { IsValid: true })
        {
            return;
        }

        var slot = player.Slot;
        if (_scopeSlot == slot)
        {
            _scopeDepth++;
            return;
        }

        // Whatever is still open belongs to someone else (or was never closed) - their values must
        // not leak into this player's simulation, overridden or not.
        if (_scopeSlot != -1)
        {
            CloseScope();
        }

        if (!_bySlot.TryGetValue(slot, out var owners))
        {
            return;
        }

        foreach (var entries in owners.Values)
        {
            foreach (var entry in entries)
            {
                if (!entry.Handle.Applied)
                {
                    _applied.Add(entry.Handle);
                }

                entry.Handle.Apply(entry.Value);
            }
        }

        _scopeSlot = slot;
        _scopeDepth = 1;
    }

    private void ExitScope(IPlayer? player)
    {
        if (_scopeSlot == -1 || player is not { IsValid: true } || player.Slot != _scopeSlot)
        {
            return;
        }

        if (--_scopeDepth > 0)
        {
            return;
        }

        CloseScope();
    }

    private void CloseScope()
    {
        foreach (var handle in _applied)
        {
            handle.Restore();
        }

        _applied.Clear();
        _scopeSlot = -1;
        _scopeDepth = 0;
    }

    /// <summary>Frame boundary - no player is mid-simulation here, so anything still applied is a scope whose Post never ran.</summary>
    private void OnTick()
    {
        if (_scopeSlot != -1)
        {
            CloseScope();
        }
    }

    /// <summary>Re-sends the overrides on every spawn as a safety net, the same one BunnyHop always had - nothing guarantees a client keeps a replicated value across a new life.</summary>
    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && _bySlot.TryGetValue(player.Slot, out var owners))
        {
            foreach (var entries in owners.Values)
            {
                foreach (var entry in entries)
                {
                    Replicate(player.Slot, entry.Handle.Name, entry.Text);
                }
            }
        }

        return HookResult.Continue;
    }

    /// <summary>Slots are recycled - the next player into this one must not inherit overrides that were never theirs. Nothing to replicate back to a client that is leaving.</summary>
    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        if (_scopeSlot == @event.PlayerId)
        {
            CloseScope();
        }

        if (_bySlot.Remove(@event.PlayerId))
        {
            UninstallIfEmpty();
        }
    }
}
