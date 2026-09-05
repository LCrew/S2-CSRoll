using System.Linq;

using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;
using CSRoll.Services.Interfaces;

namespace CSRoll.Modifiers;

/// <summary>
/// Base class for every toggleable gameplay modifier.
/// Lifecycle: Register -> (Activate -> Deactivate)* -> Unregister.
/// </summary>
public abstract class GameModifierBase
{
    public virtual string Name { get; protected set; } = "Unnamed";
    public virtual string Description { get; protected set; } = "";

    /// <summary>
    /// Overridden by modifiers whose description needs to show a live value instead of fixed text -
    /// a chance rolled fresh per activation (DisarmingBullets, FlashingBullets, Revive, Saint - token
    /// "rand%") or a live config value (PlantAnywhere - tokens "delay"/"timer"). Keys are the bare
    /// token name (no braces). CSRollUtils.GetModifierDescription substitutes each "{key}" it finds
    /// in the resolved description (translation override or hardcoded fallback) with its value - so
    /// translations/en.jsonc can freely reword these modifiers' text while the actual values always
    /// track what's live. Null for modifiers with nothing dynamic to show.
    /// </summary>
    public virtual IReadOnlyDictionary<string, string>? DynamicTextTokens => null;
    public virtual bool SupportsRandomRounds { get; protected set; } = false;
    public virtual bool IsRegistered { get; protected set; } = true;
    public bool IsActive { get; private set; }
    public virtual HashSet<string> IncompatibleModifiers { get; protected set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Opt-in flag for ModifierRuntime.AssignRandomModifiersPerPlayer() (RandomizePlayers=true).
    /// ConVar-driven modifiers and any modifier whose semantics don't map to "one owning player"
    /// leave this false.
    /// </summary>
    public virtual bool SupportsPerPlayerRandomization { get; protected set; } = false;

    private readonly HashSet<int> _assignedSlots = [];

    /// <summary>
    /// Empty means "unscoped" - applies globally, today's behavior. Non-empty means this modifier
    /// instance is only in effect for these specific player slots (per-player random rounds).
    /// Exposed publicly (read-only) so ModifierRuntime can build per-player banners/chat.
    /// </summary>
    public IReadOnlySet<int> AssignedSlots => _assignedSlots;

    protected bool IsAssignedTo(int slot) => _assignedSlots.Count == 0 || _assignedSlots.Contains(slot);

    /// <summary>
    /// Global-scope-safe iteration: every currently valid player this modifier instance is in effect
    /// for (all of them when AssignedSlots is empty/unscoped, only the assigned ones otherwise). This
    /// is the same "IsAssignedTo(player.Slot) filter over GetAllValidPlayers()" idiom several modifiers
    /// already hand-rolled independently - centralized here so future modifiers can't reintroduce the
    /// "empty AssignedSlots means everyone" footgun by forgetting the filter.
    /// </summary>
    protected IEnumerable<IPlayer> GetAssignedPlayers() =>
        Core.PlayerManager.GetAllValidPlayers().Where(p => IsAssignedTo(p.Slot));

    /// <summary>
    /// Shared TakeDamage.Pre victim-resolution pattern, duplicated near-identically across several
    /// modifiers (HardHead, SteelBody, Jetpack): resolve the pawn being damaged to its owning IPlayer,
    /// then bail unless it's valid and in scope for this modifier instance.
    /// </summary>
    protected bool TryGetAssignedTakeDamageVictim(ref TakeDamageEntityPreContext ctx, out IPlayer victim)
    {
        var resolved = Core.PlayerManager.GetPlayerFromPawn(ctx.Params.Entity.As<CBasePlayerPawn>());
        if (resolved is not { IsValid: true } || !IsAssignedTo(resolved.Slot))
        {
            victim = null!;
            return false;
        }

        victim = resolved;
        return true;
    }

    protected ISwiftlyCore Core { get; private set; } = null!;
    protected ModifierRuntime Runtime { get; private set; } = null!;
    protected ICvarConfigHandle? CvarConfig { get; private set; }

    internal void Register(ISwiftlyCore core, ModifierRuntime runtime, ICvarRollbackService cvarService)
    {
        Core = core;
        Runtime = runtime;
        CvarConfig = cvarService.TryLoadBoltOnConfig(Name);
        OnRegistered();
    }

    internal void Unregister()
    {
        OnUnregistered();
        Core = null!;
        Runtime = null!;
        CvarConfig = null;
    }

    internal void Activate(IEnumerable<int>? slots = null)
    {
        if (slots is not null)
        {
            _assignedSlots.UnionWith(slots);
        }

        IsActive = true;
        CvarConfig?.Apply();
        OnEnabled();
    }

    /// <summary>Adds more owning slots to an already-active per-player modifier (e.g. a second player rolls the same modifier this round) without re-running OnEnabled().</summary>
    internal void AddAssignedSlots(IEnumerable<int> slots)
    {
        var added = slots.Where(_assignedSlots.Add).ToList();
        if (added.Count > 0)
        {
            OnSlotsAdded(added);
        }
    }

    /// <summary>
    /// Called when slots are added to an ALREADY-ACTIVE modifier (e.g. !memodifier on a modifier
    /// someone else already has), which deliberately does not re-run OnEnabled.
    ///
    /// Bug fix: any per-player state OnEnabled seeds was silently never seeded for these players.
    /// Vanish seeds its activation cooldown there, and its readiness check defaults a missing entry
    /// to "ready now" - so a player handed the modifier mid-round could use it on the very next tick,
    /// skipping RoundStartCooldownSeconds entirely.
    /// </summary>
    protected virtual void OnSlotsAdded(IReadOnlyCollection<int> slots)
    {
    }

    /// <summary>
    /// Bug fix: player slots are small, reused indices - if an assigned player disconnects while
    /// this modifier is still active and a new player connects into that freed slot before the
    /// modifier deactivates, IsAssignedTo(slot) had no way to know the slot changed hands, so the
    /// newcomer silently inherited the effect. ModifierRuntime calls this for every active modifier
    /// on every disconnect so a freed slot is never still "owned" by anyone.
    ///
    /// </summary>
    internal void RemoveAssignedSlot(int slot) => _assignedSlots.Remove(slot);

    /// <summary>
    /// Bug fix: true when `slot` is the ONLY slot this modifier is currently assigned to - i.e.
    /// removing it would leave a previously-SCOPED modifier with an empty AssignedSlots, which
    /// IsAssignedTo reads as "applies to everyone". A per-player modifier assigned to exactly one
    /// player therefore silently WIDENED from "scoped to that player" to "server-wide" the moment
    /// they disconnected, instead of ending: ConditionalInvisibility rolled for one player who then
    /// disconnects mid-round started hiding every player on the server, Vanish started
    /// stripping everyone's weapons, MoreDamage buffed everyone. ModifierRuntime tests this BEFORE
    /// removing the slot and deactivates instead - deliberately in that order, because Deactivate()
    /// runs OnDisabled() before clearing the set, and several modifiers' OnDisabled iterates by
    /// IsAssignedTo to undo their effect (Vampire heals its assigned players back to full, etc.).
    /// Removing the slot first would leave that cleanup loop reading an already-empty set as
    /// "everyone" and applying itself to every player on the server.
    ///
    /// A Count == 0 set returns false: that's a genuinely global activation (!rolltoggle), which has
    /// no owning slot to lose and must not be deactivated by an unrelated player's disconnect.
    /// </summary>
    internal bool IsOnlyAssignedSlot(int slot) => _assignedSlots.Count == 1 && _assignedSlots.Contains(slot);

    internal void Deactivate()
    {
        OnDisabled();
        CvarConfig?.Remove();
        IsActive = false;
        _assignedSlots.Clear();
    }

    protected virtual void OnRegistered() { }
    protected virtual void OnUnregistered() { }
    protected virtual void OnEnabled() { }
    protected virtual void OnDisabled() { }

    public bool CheckIfIncompatible(GameModifierBase? other)
    {
        return other is not null && IncompatibleModifiers.Contains(other.Name);
    }

    public bool CheckIfIncompatibleByName(string modifierName)
    {
        return IncompatibleModifiers.Contains(modifierName);
    }
}
