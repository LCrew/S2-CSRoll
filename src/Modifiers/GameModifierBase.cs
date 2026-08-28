using System.Linq;

using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;
using CSRoll.Hud;
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
    internal void RemoveAssignedSlot(int slot)
    {
        if (!_assignedSlots.Remove(slot))
        {
            return;
        }

        // Bug fix: Deactivate() clears this modifier's HUD blocks for everyone, but that only runs
        // when the LAST owner loses it. Un-scoping one player off a modifier that other players still
        // have takes this path instead, which left that player's block published forever - nothing
        // ever refreshed it (the modifier no longer counts them as assigned) and nothing ever
        // retracted it. Reachable as soon as one modifier can grant another: ButterflyEffect re-rolls
        // a granted Jetpack away from its carrier while an unrelated player also has Jetpack, and the
        // carrier keeps a frozen fuel gauge on screen for the rest of the round.
        Runtime.ClearHudSection(this, slot);

        OnSlotsRemoved([slot]);
    }

    /// <summary>
    /// Called when a slot is un-scoped from an ALREADY-ACTIVE modifier, which (like OnSlotsAdded, its
    /// mirror) deliberately does not run OnDisabled.
    ///
    /// Bug fix: this is what makes it safe for one modifier to grant another. ButterflyEffect and
    /// Mimic hand out other registered modifiers per-slot; if THEY are themselves granted to someone
    /// and later revoked (Mimic steals ButterflyEffect, then steals something else off the next kill),
    /// they stop being assigned to that player - but whatever they had handed out was still scoped
    /// onto them, with no OnDisabled to clean it up, so it was stranded on that player until the round
    /// ended. Implementers release per-slot state here.
    /// </summary>
    protected virtual void OnSlotsRemoved(IReadOnlyCollection<int> slots)
    {
    }

    /// <summary>Publishes this modifier's persistent HUD block for one player. See ModifierRuntime._hudSections for why modifiers must not call SendCenterHTML directly for this.</summary>
    protected void SetHud(int slot, string html, int priority = 0) => Runtime.SetHudSection(this, slot, html, priority);

    /// <summary>Whether another modifier is currently drawing its own HUD block for this player - see ModifierRuntime.HasHudSection.</summary>
    protected bool HasHud(GameModifierBase other, int slot) => Runtime.HasHudSection(other, slot);

    /// <summary>Retracts this modifier's HUD block for one player (e.g. while they're dead).</summary>
    protected void ClearHud(int slot) => Runtime.ClearHudSection(this, slot);

    /// <summary>
    /// Optional live readout for this modifier's row on the custom HUD tracker: a cooldown winding down,
    /// a duration burning off, a gauge draining.
    ///
    /// Returning null - the default, and what all but a handful of modifiers do - simply means the
    /// tracker row shows the modifier's icon and name with no timer, which is correct for the passive
    /// ones. This is deliberately additive: it does not replace <see cref="SetHud"/>, and a modifier
    /// that implements it keeps its existing center-HTML gauge untouched, so the fallback path for
    /// servers without the HUD addon is unaffected either way.
    /// </summary>
    public virtual HudTimer? GetHudTimer(int slot) => null;

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

        // Unconditional, so no modifier can leave a stale block on a player's HUD after it ends -
        // this is cleanup every HUD-drawing modifier would otherwise have to remember in its own
        // OnDisabled, and forgetting it leaves text on screen with nothing behind it.
        Runtime.ClearHudSections(this);

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
