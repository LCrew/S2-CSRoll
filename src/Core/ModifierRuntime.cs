using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Config;
using CSRoll.Hud;
using CSRoll.Modifiers;
using CSRoll.Services.Interfaces;

namespace CSRoll.Core;

/// <summary>
/// Owns the registered/active modifier lists and all add/remove/toggle/random-round business
/// logic. Direct port of CSRollCore's non-command, non-game-event methods.
/// </summary>
public sealed class ModifierRuntime
{
    private readonly ISwiftlyCore _core;
    private readonly ICvarRollbackService _cvarService;

    public CSRollConfig Config { get; set; }
    public bool RandomRoundsEnabled { get; private set; }
    public int MinRandomRounds { get; set; }
    public int MaxRandomRounds { get; set; }

    /// <summary>
    /// Off by default. When off, per-player random-round assignments ("who got which modifier")
    /// are never sent to chat at all - each player's own center-HTML banner already tells them
    /// privately. When toggled on via !rolldebug, that "who got what" breakdown is sent to connected
    /// admins only, never broadcast to the whole server.
    /// </summary>
    public bool DebugMode { get; set; }

    /// <summary>
    /// Every modifier CSRoll knows how to construct, by name - both classic modifiers and cvar-file
    /// modifiers, keyed on the name each produces. A superset of _registeredModifiers: this also
    /// includes anything currently disabled (via Config.DisabledModifiers), so !rollmenu's
    /// enable/disable list can show and re-register a disabled modifier without a full plugin reload.
    /// Built once in Initialise() and never mutated afterward (Initialise itself only ever runs once
    /// per plugin load - see the removed !reloadmodifiers command's history).
    /// </summary>
    private readonly Dictionary<string, Func<GameModifierBase>> _allModifierFactories = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<GameModifierBase> _registeredModifiers = [];
    private readonly List<GameModifierBase> _activeModifiers = [];
    private List<GameModifierBase> _lastActiveModifiers = [];

    /// <summary>
    /// Round-start silent roll (showBanner:false) results that have been selected but not yet
    /// Activate()'d - see the bug-fix note on PlaySpinThenRevealActiveModifiersBanner for why
    /// activation is deferred until each reveal actually lands instead of happening immediately.
    /// Exactly one of _pendingGlobalModifiers/_pendingModifiersByPlayerSlot is populated at a time,
    /// mirroring the RandomizePlayers on/off branches in ApplyRandomRoundsForRound.
    /// </summary>
    private List<GameModifierBase>? _pendingGlobalModifiers;
    private Dictionary<GameModifierBase, List<int>>? _pendingAssignedSlotsByModifier;
    private Dictionary<int, List<GameModifierBase>>? _pendingModifiersByPlayerSlot;

    /// <summary>
    /// Backs Config.PerPlayerRepeatCooldownRounds: keyed by (the specific player's stable
    /// IPlayer.SessionId, modifier name) rather than Slot, so a new player connecting into a
    /// recently-vacated slot doesn't inherit a stranger's cooldown history - bots all share
    /// SteamID 0, but each still gets its own distinct SessionId, so this works uniformly for both.
    /// Value is the round number (_roundNumber) they were assigned it in.
    /// </summary>
    private readonly Dictionary<(ulong SessionId, string ModifierName), int> _lastRoundAssignedPerPlayer = [];
    private int _roundNumber;

    /// <summary>
    /// Bumped every time RemoveAllModifiers() runs. CS2 fires EventRoundStart twice in a row during
    /// a warmup-to-live-match transition (a well-known engine quirk around mp_restartgame-style
    /// transitions) - each firing calls RemoveAllModifiers() then rolls and defers a fresh
    /// spin-then-reveal. That reveal can take several seconds (SpinReveal animation), so the FIRST
    /// firing's reveal can still be mid-flight when the SECOND firing's RemoveAllModifiers() runs and
    /// starts its own roll. Without this guard, the first roll's Reveal() closure would still fire
    /// later and Activate() its own modifier - even though it was already superseded - leaving two
    /// independently-rolled modifiers active for the same player at once. Each reveal closure captures
    /// the generation it was dispatched under and refuses to activate/announce if the generation has
    /// since moved on.
    /// </summary>
    private int _rollGeneration;

    /// <summary>Backs the spectator HUD's refresh throttle - keyed by the spectating player's own slot.</summary>
    private readonly Dictionary<int, float> _lastSpectatorHudUpdateTime = [];

    private float _modifierHudSuppressedUntil;

    /// <summary>
    /// True while the roll's own center-HTML (spin, description wipe, and the reveal it lands on) owns
    /// the screen. Every modifier that draws a persistent center-HTML HUD checks this and stays quiet
    /// until it clears.
    ///
    /// Center-HTML is a single shared surface - the newest message simply replaces whatever was there.
    /// So a modifier refreshing its own HUD several times a second (Vanish, Flanker, Jetpack,
    /// ConditionalInvisibility, Regeneration, WeaponRoulette all do) will fight the reveal for it the
    /// instant that modifier activates, which is exactly when the reveal is showing. The result is
    /// both flickering. Suppressing the HUDs for the reveal's own lifetime is the fix.
    /// </summary>
    public bool IsModifierHudSuppressed => _core.Engine.GlobalVars.CurrentTime < _modifierHudSuppressedUntil;

    /// <summary>
    /// How long the fully-resolved reveal is held. When the description wipes in, the wipe is part of
    /// the description's screen time rather than extra on top of it - so RevealDurationSeconds stays a
    /// straight "how long the description is up for" budget and the whole popup keeps fitting inside
    /// freeze time. Floored so a long scramble can never leave the resolved text with no hold at all.
    /// </summary>
    private int RevealHoldMs()
    {
        var seconds = Config.SpinReveal.RevealDurationSeconds;
        if (Config.SpinReveal.ShowDescription && Config.SpinReveal.DescriptionScrambleEnabled)
        {
            seconds -= Config.SpinReveal.DescriptionScrambleDurationSeconds;
        }

        return (int)(Math.Max(1f, seconds) * 1000);
    }

    /// <summary>
    /// Clears state holding a GlobalVars.CurrentTime value, which is map-relative and restarts near
    /// zero on a map change - see CSRoll.OnMapLoad for the full explanation. A stale future deadline
    /// would otherwise keep every modifier HUD suppressed until the new map's clock caught up to it.
    /// </summary>
    public void ResetMapRelativeTimeState()
    {
        _modifierHudSuppressedUntil = 0f;
        _lastSpectatorHudUpdateTime.Clear();
        _lastModifierHudUpdateTime.Clear();

        // The custom HUD caches map-relative deadlines of its own (countdowns, entity retry backoff),
        // so it has to be cleared through the same single reset point rather than growing a second one.
        Hud.ResetAll();
    }

    /// <summary>Extends the HUD blackout to at least <paramref name="seconds"/> from now - never shortens an existing one, so overlapping reveals can't cut each other short.</summary>
    private void SuppressModifierHudFor(float seconds)
    {
        var until = _core.Engine.GlobalVars.CurrentTime + seconds;
        if (until > _modifierHudSuppressedUntil)
        {
            _modifierHudSuppressedUntil = until;
        }
    }

    /// <summary>
    /// How long the whole animation runs before the reveal even lands - the eased spin frames plus the
    /// description wipe. Summed rather than approximated because GetSpinFrameIntervalSeconds is
    /// quadratically eased, so frame count times average interval would be well off.
    /// </summary>
    private float EstimateRevealAnimationSeconds()
    {
        if (!Config.SpinReveal.Enabled)
        {
            return 0f;
        }

        var total = 0f;
        for (var i = 0; i < Config.SpinReveal.SpinCount; i++)
        {
            total += GetSpinFrameIntervalSeconds(i, Config.SpinReveal.SpinCount);
        }

        if (Config.SpinReveal.ShowDescription && Config.SpinReveal.DescriptionScrambleEnabled)
        {
            total += Config.SpinReveal.DescriptionScrambleDurationSeconds;
        }

        return total;
    }

    /// <summary>
    /// Just the eased spin frames, without the description wipe that <see cref="EstimateRevealAnimationSeconds"/>
    /// adds on top.
    ///
    /// The custom HUD needs this separately because it does not push the description wipe as server
    /// frames at all - a CSS mask sweep does that client-side - so its spin lasts exactly as long as the
    /// name reel is travelling. Sharing the same eased sum keeps both surfaces on the identical timing
    /// budget documented on SpinRevealConfig.SpinCount.
    /// </summary>
    internal float SpinAnimationSeconds()
    {
        if (!Config.SpinReveal.Enabled)
        {
            return 0f;
        }

        var total = 0f;
        for (var i = 0; i < Config.SpinReveal.SpinCount; i++)
        {
            total += GetSpinFrameIntervalSeconds(i, Config.SpinReveal.SpinCount);
        }

        return total;
    }

    /// <summary>Per-frame interval of the eased spin, shared with the custom HUD's tick-sound chain.</summary>
    internal float SpinFrameIntervalSeconds(int frameIndex, int totalFrames)
        => GetSpinFrameIntervalSeconds(frameIndex, totalFrames);

    /// <summary>How long a landed reveal is held on screen, shared with the custom HUD.</summary>
    internal int RevealHoldMilliseconds() => RevealHoldMs();

    public IReadOnlyList<GameModifierBase> RegisteredModifiers => _registeredModifiers;
    public IReadOnlyList<GameModifierBase> ActiveModifiers => _activeModifiers;

    /// <summary>
    /// The custom-HUD surface. Exposed rather than kept private so modifiers can reach it through
    /// <see cref="GameModifierBase"/> without a second DI hop; every caller must gate on
    /// <see cref="ICSRollHudService.Available"/>, which is false whenever the HUD is off or its layout
    /// entity is missing.
    /// </summary>
    public ICSRollHudService Hud { get; }

    private readonly HudTracker _hudTracker;
    private readonly HudSequencer _hudSequencer;

    /// <summary>Icon/accent lookup for drawing a modifier on the custom HUD.</summary>
    public IHudPresentationCatalog HudPresentation { get; }

    /// <summary>
    /// Bumped by RemoveAllModifiers to invalidate reveals still animating from a superseded roll.
    /// Exposed so HudSequencer can apply the same guard the center-HTML path already does.
    /// </summary>
    internal int RollGeneration => _rollGeneration;

    public ModifierRuntime(
        ISwiftlyCore core,
        CSRollConfig config,
        ICvarRollbackService cvarService,
        ICSRollHudService hudService,
        IHudPresentationCatalog hudPresentation)
    {
        _core = core;
        Config = config;
        _cvarService = cvarService;
        Hud = hudService;
        HudPresentation = hudPresentation;
        _hudTracker = new HudTracker(core, this, hudService);
        _hudSequencer = new HudSequencer(core, this, hudService);
        MinRandomRounds = config.MinRandomRounds;
        MaxRandomRounds = config.MaxRandomRounds;
    }

    /// <summary>
    /// Bug fix: Random.Next(min, max) throws ArgumentOutOfRangeException whenever min > max, and
    /// Min/MaxRandomRounds come straight from config.jsonc (hand-editable, hot-reloaded) with nothing
    /// else validating them - the !minrandomrounds/!maxrandomrounds commands that used to reject a
    /// Min > Max pair were removed when these became config-only, so this clamp is now the ONLY
    /// defense. A bad config value degrades to "roll exactly Min" instead of crashing round-start.
    ///
    /// Bug fix: Max is treated as INCLUSIVE (hence Max + 1). Random.Next's own upper bound is
    /// exclusive, so "MinRandomRounds: 1, MaxRandomRounds: 3" could previously only ever roll 1 or 2 -
    /// the configured maximum was unreachable, which is not what the field name implies (and the
    /// Max == Min case was already effectively inclusive, so the two boundaries disagreed with
    /// each other).
    /// </summary>
    private int RollRandomRoundCount(Random random) =>
        MaxRandomRounds > MinRandomRounds ? random.Next(MinRandomRounds, MaxRandomRounds + 1) : MinRandomRounds;

    public void Initialise(IEnumerable<Func<GameModifierBase>> factories)
    {
        InitialiseModifiers(factories);
        InitialiseCvarModifiers();

        var seenNames = new List<string>();
        foreach (var modifier in _registeredModifiers)
        {
            modifier.Register(_core, this, _cvarService);

            if (seenNames.Contains(modifier.Name, StringComparer.OrdinalIgnoreCase))
            {
                _core.Logger.LogWarning("[CSRoll] Duplicate modifier name {Name} - all modifier names should be unique!", modifier.Name);
                continue;
            }

            seenNames.Add(modifier.Name);
        }

        if (Config.RandomRoundsEnabledByDefault)
        {
            if (_registeredModifiers.Count == 0)
            {
                _core.Logger.LogWarning("[CSRoll] No modifiers are registered! Cannot activate random rounds by default.");
            }
            else
            {
                RandomRoundsEnabled = true;
            }
        }

        _core.Event.OnTick += RefreshSpectatorHud;
        _core.Event.OnTick += RefreshModifierHud;
        _core.Event.OnTick += _hudTracker.Refresh;
        _core.Event.OnClientDisconnected += OnClientDisconnected;
    }

    private void InitialiseModifiers(IEnumerable<Func<GameModifierBase>> factories)
    {
        _registeredModifiers.Clear();
        _allModifierFactories.Clear();

        foreach (var factory in factories)
        {
            var modifier = factory();
            if (!modifier.IsRegistered)
            {
                // Hard-coded off (not a config-driven disable) - never offered as togglable at all,
                // by any command or the !rollmenu enable/disable list.
                _core.Logger.LogInformation("[CSRoll] Disabled modifier: {Name}", modifier.Name);
                continue;
            }

            // Bug fix: the factory is remembered here regardless of whether this modifier starts
            // disabled, so DisableModifierByName's effect can later be reversed (EnableModifierByName)
            // without a full plugin reload - previously there was no way back short of restarting.
            _allModifierFactories[modifier.Name] = factory;

            if (!Config.DisabledModifiers.Any(x => x.Equals(modifier.Name, StringComparison.OrdinalIgnoreCase)))
            {
                _registeredModifiers.Add(modifier);
            }
            else
            {
                _core.Logger.LogInformation("[CSRoll] Disabled modifier: {Name}", modifier.Name);
            }
        }
    }

    private void InitialiseCvarModifiers()
    {
        foreach (var file in _cvarService.FindCvarModifierFiles())
        {
            var handle = _cvarService.ParseCvarModifierFile(file);
            if (handle.ModifierName is null)
            {
                continue;
            }

            // Same reasoning as InitialiseModifiers - keep a factory around so a disabled cvar
            // modifier can be re-enabled later without re-reading the whole registered-modifier list
            // from scratch. Re-parses the file fresh on each call rather than capturing `handle`
            // directly, since ParseCvarModifierFile is presumably cheap (a small local file) and this
            // avoids ever handing out a second live reference to the same mutable handle instance.
            _allModifierFactories[handle.ModifierName] = () => new GameModifierCvar(_cvarService.ParseCvarModifierFile(file));

            if (Config.DisabledModifiers.Contains(handle.ModifierName, StringComparer.OrdinalIgnoreCase))
            {
                _core.Logger.LogInformation("[CSRoll] Disabled cvar modifier config: {File}", file);
                continue;
            }

            _registeredModifiers.Add(new GameModifierCvar(handle));
            _core.Logger.LogInformation("[CSRoll] Registered cvar modifier config: {File}", file);
        }
    }

    public void Unregister()
    {
        _core.Event.OnTick -= RefreshSpectatorHud;
        _core.Event.OnTick -= RefreshModifierHud;
        _core.Event.OnTick -= _hudTracker.Refresh;
        _core.Event.OnClientDisconnected -= OnClientDisconnected;

        _hudTracker.Reset();

        RemoveAllModifiers();

        foreach (var modifier in _registeredModifiers)
        {
            modifier.Unregister();
        }

        _lastActiveModifiers.Clear();
        _registeredModifiers.Clear();
        _lastSpectatorHudUpdateTime.Clear();
        _lastModifierHudUpdateTime.Clear();

        // Bug fix: this used to also clear _lastRoundAssignedPerPlayer and reset _roundNumber to 0 -
        // meaning any Unregister()-then-Initialise() registry reload (e.g. after a config change)
        // silently wiped every player's
        // PerPlayerRepeatCooldownRounds history too, letting a modifier they'd just rolled repeat
        // immediately regardless of how many real rounds had actually passed. Reported as "got Jetpack
        // again with only a single round in between" - a reload between those rounds is the most
        // likely explanation. Round-cooldown history is player-facing fairness state tied to the
        // ongoing match's round sequence, not modifier-registry bookkeeping, so it has no business
        // being reset by a registry reload - only a genuine plugin unload (full session end, e.g. map
        // change) should clear it, which already happens naturally since ModifierRuntime itself is
        // recreated from scratch then.
    }

    /// <summary>
    /// Bug fix: player slots are small, reused indices - without this, a disconnecting per-player-
    /// assigned player's slot stayed "owned" by that modifier until it deactivated, so a new player
    /// connecting into the freed slot before then silently inherited the effect (every modifier's
    /// IsAssignedTo(slot) check has no way to know the slot changed hands on its own).
    ///
    /// Also opportunistically prunes _lastRoundAssignedPerPlayer for the departed session - that
    /// dictionary is deliberately never cleared by Unregister() (see its own comment above), but a
    /// SessionId becomes permanently unusable once the connection ends anyway, so any entry left
    /// behind is pure unbounded growth on a server with high player churn, never a real future
    /// lookup hit.
    /// </summary>
    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        // Slots are recycled by the next player to join, so any HUD blocks published for this one
        // have to go with them or the newcomer inherits a stale panel.
        _hudSections.Remove(@event.PlayerId);
        _lastModifierHudUpdateTime.Remove(@event.PlayerId);

        // Same reasoning for the custom HUD's per-player row bookkeeping. The HUD service clears its own
        // per-player overrides from its own disconnect subscription, so ordering between the two here
        // does not matter.
        _hudTracker.ForgetPlayer(@event.PlayerId);

        // Iterating a copy: an orphaned modifier is removed from _activeModifiers inside this loop.
        foreach (var modifier in _activeModifiers.ToList())
        {
            // Bug fix: a per-player modifier whose LAST assigned player just disconnected would
            // otherwise be left with an empty AssignedSlots, which IsAssignedTo reads as "applies to
            // everyone" - silently widening it from one player to the whole server instead of ending
            // it. See GameModifierBase.IsOnlyAssignedSlot for the full write-up, including why this
            // must be checked BEFORE the slot is removed rather than after. Deactivating (rather than
            // just dropping it from the list) runs the modifier's own OnDisabled cleanup.
            if (modifier.IsOnlyAssignedSlot(@event.PlayerId))
            {
                _core.Logger.LogInformation("[CSRoll] Deactivating {Name} - its last assigned player (slot {Slot}) disconnected.", modifier.Name, @event.PlayerId);
                modifier.Deactivate();
                _activeModifiers.Remove(modifier);
                continue;
            }

            modifier.RemoveAssignedSlot(@event.PlayerId);
        }

        if (_core.PlayerManager.GetPlayer(@event.PlayerId) is { } player)
        {
            var staleKeys = _lastRoundAssignedPerPlayer.Keys.Where(key => key.SessionId == player.SessionId).ToList();
            foreach (var key in staleKeys)
            {
                _lastRoundAssignedPerPlayer.Remove(key);
            }
        }
    }

    /// <summary>
    /// Persistent HUD for spectators: whoever a player is currently observing (CBasePlayerPawn's
    /// ObserverServices/ObserverTarget - non-null only while actually in observer mode, e.g. dead or
    /// a true spectator, so this is a no-op for anyone currently alive and playing), listing that
    /// target's active modifiers. Re-sent on a short throttled interval (same persistent-HUD
    /// convention as Vanish/Jetpack/Flanker) rather than once, so switching spectate
    /// targets or the target's modifiers changing are both picked up promptly.
    /// </summary>
    private void RefreshSpectatorHud()
    {
        if (!Config.SpectatorHud.Enabled || _activeModifiers.Count == 0)
        {
            return;
        }

        // The custom HUD's tracker follows the spectated player itself once it is driving the reveal, so
        // running this as well would put the same information on screen twice, in two different places
        // and two different styles.
        if (Hud.Available && Config.CustomHud.ReplaceCenterHtml && Config.CustomHud.ShowTracker)
        {
            return;
        }

        var now = _core.Engine.GlobalVars.CurrentTime;

        foreach (var player in _core.PlayerManager.GetAllValidPlayers())
        {
            // Bug fix: IPlayer.PlayerPawn is specifically the CCSPlayerPawn (alive game pawn), which
            // is gone once dead/spectating - ObserverServices only ever shows up on IPlayer.Pawn (the
            // general CBasePlayerPawn, whichever concrete pawn - game or observer - is currently
            // active), confirmed via SwiftlyS2's own IPlayer.cs doc comments distinguishing the two.
            if (player.Pawn?.ObserverServices?.ObserverTarget.Value is not { } targetEntity)
            {
                continue;
            }

            var slot = player.Slot;
            if (_lastSpectatorHudUpdateTime.TryGetValue(slot, out var lastUpdate) && now - lastUpdate < Config.SpectatorHud.RefreshIntervalSeconds)
            {
                continue;
            }

            // Bug fix: this was the one persistent HUD missing the suppression gate every modifier
            // HUD has. Refreshing 5x a second by default, it overwrote the roll's own spin and reveal
            // for anyone dead or spectating during freeze time - which is most of the server at round
            // start - so they never saw what was rolled. Exactly the flicker-fight the gate exists to
            // stop; it just never got applied here.
            if (IsModifierHudSuppressed)
            {
                continue;
            }

            var targetPlayer = _core.PlayerManager.GetPlayerFromPawn(targetEntity.As<CBasePlayerPawn>());
            if (targetPlayer is not { IsValid: true, Controller: { IsValid: true } targetController })
            {
                continue;
            }

            _lastSpectatorHudUpdateTime[slot] = now;

            var modifiers = _activeModifiers.Where(m => ModifierAppliesToSlot(m, targetPlayer.Slot)).ToList();
            var html = CSRollUtils.BuildSpectatorHudHtml(_core, targetController.PlayerName, modifiers);
            player.SendCenterHTML(html, (int)((Config.SpectatorHud.RefreshIntervalSeconds * 1000) + 100));
        }
    }

    public GameModifierBase? GetRegisteredModifierByName(string modifierName) =>
        _registeredModifiers.FirstOrDefault(m => string.Equals(m.Name, modifierName, StringComparison.OrdinalIgnoreCase));

    public GameModifierBase? GetActiveModifierByName(string modifierName) =>
        _activeModifiers.FirstOrDefault(m => string.Equals(m.Name, modifierName, StringComparison.OrdinalIgnoreCase));

    public bool AnyModifiersActive() => _activeModifiers.Count > 0;

    public bool IsModifierActive(GameModifierBase? modifier) => modifier is not null && _activeModifiers.Contains(modifier);

    public bool IsModifierActiveByName(string modifierName) => GetActiveModifierByName(modifierName) is not null;

    public bool IsModifierRegistered(GameModifierBase? modifier) => modifier is not null && _registeredModifiers.Contains(modifier);

    public bool IsModifierRegisteredByName(string modifierName) => GetRegisteredModifierByName(modifierName) is not null;

    public void ToggleRandomRounds()
    {
        RandomRoundsEnabled = !RandomRoundsEnabled;
        if (!RandomRoundsEnabled)
        {
            RemoveAllModifiers();
        }

        CSRollUtils.PrintTitleToChatAll(_core, RandomRoundsEnabled ? "Random rounds enabled for next round!" : "Random rounds disabled!");
        CSRollUtils.ShowMessageCentreAll(_core, CSRollUtils.BuildRandomRoundsToggleHtml(RandomRoundsEnabled), 4000);
    }

    public void ApplyRandomRoundsForRound(bool showBanner = true)
    {
        var random = new Random();
        var appliedAnything = false;

        // Round counter backing Config.PerPlayerRepeatCooldownRounds - incremented once per call
        // (once per round from OnRoundStart, or again on an explicit !randomroundsreroll, which
        // reasonably counts as "this round's roll" too rather than a free do-over of the cooldown).
        _roundNumber++;

        // Bug fix: this used to also run a supplementary global-only roll every round, originally
        // added so ConditionalInvisibility/Vanish (which used to opt out of per-player
        // randomization, picking their own random target internally instead of using the runtime's
        // assignment) still got a chance while RandomizePlayers was on. That secondary global roll is
        // removed entirely per explicit instruction: no automatic global/shared activation happens
        // alongside the per-player roll anymore. ConditionalInvisibility/Vanish now support
        // per-player randomization directly instead (see their own files), so they lose nothing by
        // this removal - only modifiers that are still genuinely global-only (PlantAnywhere, etc.)
        // are excluded from the automatic rotation now; they're still fully usable via an explicit
        // admin !rolltoggle.
        if (Config.RandomizePlayers)
        {
            appliedAnything = AssignRandomModifiersPerPlayer(showBanner);
        }
        else
        {
            var count = RollRandomRoundCount(random);
            if (AddRandomModifiers(count, out _, showBanner))
            {
                // When showBanner is false, AddRandomModifiers only selects the modifiers and stashes
                // them as _pendingGlobalModifiers - _activeModifiers isn't updated until
                // PlaySpinThenRevealActiveModifiersBanner commits them alongside the reveal, so
                // _lastActiveModifiers must wait until then too (see that method's global commit path).
                if (showBanner)
                {
                    _lastActiveModifiers = _activeModifiers.ToList();
                }

                appliedAnything = true;
            }
        }

        if (!appliedAnything)
        {
            CSRollUtils.PrintTitleToChatAll(_core, "Failed to apply random modifiers! Skipping random round...");
        }
    }

    /// <summary>
    /// Per-player counterpart to AddRandomModifiers: instead of one shared set applied to
    /// everyone, each connected player independently rolls their own Min..MaxRandomRounds
    /// modifiers from the SupportsPerPlayerRandomization pool. Two players CAN roll the same
    /// modifier - the single shared instance just accumulates both slots into AssignedSlots.
    ///
    /// IncompatibleModifiers is only ever checked WITHIN one player's own picks here (see
    /// PickCompatibleRandomModifiers, called fresh per player from PickRandomModifiersForPlayer with
    /// its own local `picked` list) - it does not, and must not, prevent two DIFFERENT players from
    /// independently holding a mutually-incompatible pair. Player X having Speedhack never stops
    /// player Y from getting HeavyBoots in the same roll; incompatibility is a same-player constraint,
    /// not a whole-server one. The whole-server check in AddModifier/AddRandomModifiers below is
    /// correct there for a different reason: those activate with an EMPTY AssignedSlots (global/
    /// "everyone"), so a new global modifier genuinely does overlap every existing active modifier's
    /// slots, whoever they belong to.
    /// </summary>
    public bool AssignRandomModifiersPerPlayer(bool showBanner = true)
    {
        var pool = _registeredModifiers.Where(m => m.SupportsPerPlayerRandomization).ToList();
        // Bug fix: GetAllValidPlayers() includes spectators, so a spectator could get assigned (and
        // shown, via their own "Your modifiers:" chat/banner) a gameplay modifier they were never
        // actually playing to receive. Only T/CT team members are eligible for the roll.
        var players = _core.PlayerManager.GetT().Concat(_core.PlayerManager.GetCT()).ToList();

        if (pool.Count == 0 || players.Count == 0)
        {
            return false;
        }

        var random = new Random();
        var assignedSlotsByModifier = new Dictionary<GameModifierBase, List<int>>();
        var modifiersByPlayerSlot = new Dictionary<int, List<GameModifierBase>>();

        foreach (var player in players)
        {
            var picked = PickRandomModifiersForPlayer(pool, random, player);
            if (picked.Count == 0)
            {
                continue;
            }

            modifiersByPlayerSlot[player.Slot] = picked;
            foreach (var modifier in picked)
            {
                if (!assignedSlotsByModifier.TryGetValue(modifier, out var slots))
                {
                    slots = [];
                    assignedSlotsByModifier[modifier] = slots;
                }

                slots.Add(player.Slot);

                // Backs Config.PerPlayerRepeatCooldownRounds - recorded at selection time (this
                // round's roll), not at commit/activation time, since the roll itself is what should
                // start the cooldown regardless of when the mechanical effect actually kicks in.
                _lastRoundAssignedPerPlayer[(player.SessionId, modifier.Name)] = _roundNumber;
            }
        }

        if (assignedSlotsByModifier.Count == 0)
        {
            return false;
        }

        if (DebugMode)
        {
            CSRollUtils.PrintTitleToAdminsOnly(_core, "Rolled modifiers (randomized per player):");
            foreach (var (slot, modifiers) in modifiersByPlayerSlot)
            {
                var player = _core.PlayerManager.GetPlayer(slot);
                var playerName = player?.Controller is { IsValid: true } controller ? controller.PlayerName : $"Player {slot}";
                foreach (var modifier in modifiers)
                {
                    CSRollUtils.PrintToAdminsOnly(_core, $"• {playerName}: {CSRollUtils.GetModifierDisplayName(_core, modifier)} - [{CSRollUtils.GetModifierDescription(_core, modifier)}]");
                }
            }
        }

        if (showBanner)
        {
            // Immediate path (e.g. !reroll): no separate reveal animation follows this call, so
            // activate right away - there's nothing to defer to.
            CommitPerPlayerModifiers(assignedSlotsByModifier);

            if (Config.ShowCentreMsg)
            {
                ShowPerPlayerModifiersBanner(modifiersByPlayerSlot);
            }

            foreach (var (slot, modifiers) in modifiersByPlayerSlot)
            {
                SendOwnModifiersChatMessage(_core.PlayerManager.GetPlayer(slot), modifiers);
            }
        }
        else
        {
            // Bug fix: modifiers used to be Activate()'d right here, immediately at round start -
            // up to several seconds before ScheduleFreezeTimeBanner's spin-then-reveal ever told the
            // player what they'd gotten. Weapons vanished, players turned invisible, etc. with no
            // explanation on screen yet. Stash the roll instead; PlaySpinThenRevealActiveModifiersBanner
            // commits it exactly when each player's reveal lands.
            _pendingAssignedSlotsByModifier = assignedSlotsByModifier;
            _pendingModifiersByPlayerSlot = modifiersByPlayerSlot;
        }

        return true;
    }

    /// <summary>Activates a full per-player roll in one shot - each modifier's complete slot set at once, so OnEnabled() sees every owning player immediately rather than one at a time.</summary>
    private void CommitPerPlayerModifiers(Dictionary<GameModifierBase, List<int>> assignedSlotsByModifier)
    {
        foreach (var (modifier, slots) in assignedSlotsByModifier)
        {
            if (_activeModifiers.Contains(modifier))
            {
                modifier.AddAssignedSlots(slots);
            }
            else
            {
                modifier.Activate(slots);
                _activeModifiers.Add(modifier);
            }
        }
    }

    /// <summary>
    /// Persistent, scrollback-able "Your modifiers:" chat confirmation for a player's own current
    /// assignment - unlike the transient center-HTML banner, this stays in chat history and isn't
    /// gated by DebugMode (that flag only hides *other* players' assignments from non-admins; a
    /// player seeing their own modifiers is never a privacy concern).
    /// </summary>
    private void SendOwnModifiersChatMessage(IPlayer? player, IReadOnlyCollection<GameModifierBase> modifiers)
    {
        if (modifiers.Count == 0 || player is not { IsValid: true })
        {
            return;
        }

        CSRollUtils.PrintTitleToChatColored(_core, player, "[gold]Your modifiers:[default]");
        foreach (var modifier in modifiers)
        {
            // Bug fix: the description was sent raw while only the display name went through
            // Helper.Colored(), so any "[green]"/"[default]" token inside a description printed as
            // literal text in chat instead of coloring it.
            player.SendChat(SwiftlyS2.Shared.Helper.Colored($"• {CSRollUtils.GetModifierDisplayName(_core, modifier)} - {CSRollUtils.GetModifierDescription(_core, modifier)}"));
        }
    }

    /// <summary>Modifiers listed in Config.RequiresMultiplePlayersPerTeam (e.g. Saint) are excluded unless the relevant team has at least 2 players - no point rolling a "revive a dead teammate" modifier in a 1v1 where there's never a teammate to revive.</summary>
    private bool MeetsTeamSizeRequirement(GameModifierBase modifier, int teamSize)
    {
        return !Config.RequiresMultiplePlayersPerTeam.Contains(modifier.Name, StringComparer.OrdinalIgnoreCase) || teamSize >= 2;
    }

    /// <summary>Backs Config.PerPlayerRepeatCooldownRounds - true if THIS player rolled THIS modifier too recently to roll it again.</summary>
    private bool IsOnPlayerCooldown(IPlayer player, GameModifierBase modifier)
    {
        if (Config.PerPlayerRepeatCooldownRounds <= 0)
        {
            return false;
        }

        return _lastRoundAssignedPerPlayer.TryGetValue((player.SessionId, modifier.Name), out var lastRound) &&
            _roundNumber - lastRound < Config.PerPlayerRepeatCooldownRounds;
    }

    private List<GameModifierBase> PickRandomModifiersForPlayer(List<GameModifierBase> pool, Random random, IPlayer player)
    {
        var count = RollRandomRoundCount(random);
        if (count <= 0)
        {
            return [];
        }

        var teamSize = player.Controller is { IsValid: true } controller ? _core.PlayerManager.GetInTeam(controller.Team).Count() : 0;
        var eligiblePool = pool.Where(m => MeetsTeamSizeRequirement(m, teamSize) && !IsOnPlayerCooldown(player, m)).ToList();

        return PickCompatibleRandomModifiers(eligiblePool, count, random);
    }

    /// <summary>
    /// Picks up to `count` mutually-compatible modifiers from eligiblePool at random.
    ///
    /// Bug fix: the old approach pre-resolved every incompatible PAIR in the whole pool up front -
    /// for every two modifiers in eligiblePool that were incompatible with each other, it flipped a
    /// coin and permanently removed one, before selection even started. A modifier that happens to
    /// be incompatible with many others (e.g. ConditionalInvisibility/Vanish listing each
    /// other plus several weapon-restricting modifiers) accumulated many independent chances to get
    /// coin-flipped away
    /// - surviving all of them got exponentially less likely the more incompatibilities it had,
    /// regardless of whether it would even have been selected. That's confirmed as the reason some
    /// modifiers were seen 5-10x live while others were never seen at all.
    ///
    /// Fix: shuffle first (so every modifier gets an equal starting chance), then greedily walk the
    /// shuffled order and only skip a candidate if it conflicts with something ALREADY picked - a far
    /// rarer event than "conflicts with anything anywhere in the whole pool".
    /// </summary>
    private static List<GameModifierBase> PickCompatibleRandomModifiers(List<GameModifierBase> eligiblePool, int count, Random random)
    {
        if (count <= 0 || eligiblePool.Count == 0)
        {
            return [];
        }

        var shuffled = new List<GameModifierBase>(eligiblePool);
        for (var i = shuffled.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        var picked = new List<GameModifierBase>();
        foreach (var candidate in shuffled)
        {
            if (picked.Count >= count)
            {
                break;
            }

            if (picked.Any(p => p.CheckIfIncompatible(candidate) || candidate.CheckIfIncompatible(p)))
            {
                continue;
            }

            picked.Add(candidate);
        }

        return picked;
    }

    private void ShowPerPlayerModifiersBanner(Dictionary<int, List<GameModifierBase>> modifiersByPlayerSlot)
    {
        foreach (var (slot, modifiers) in modifiersByPlayerSlot)
        {
            if (modifiers.Count == 0)
            {
                continue;
            }

            _core.PlayerManager.GetPlayer(slot)?.SendCenterHTML(CSRollUtils.BuildActivatingModifiersHtml(_core, modifiers, Config.SpinReveal), 6000);
        }
    }

    public bool ToggleModifierByName(string modifierName, out string message)
    {
        if (!IsModifierRegisteredByName(modifierName))
        {
            message = $"{modifierName} modifier is not registered.";
            return false;
        }

        if (IsModifierActiveByName(modifierName))
        {
            RemoveModifierByName(modifierName, out message);
            return true;
        }

        return AddModifierByName(modifierName, out message);
    }

    public bool AddModifierByName(string modifierName, out string message)
    {
        if (_registeredModifiers.Count == 0)
        {
            message = "No modifiers are registered.";
            return false;
        }

        var modifier = GetRegisteredModifierByName(modifierName);
        if (modifier is not null)
        {
            return AddModifier(modifier, out message);
        }

        message = $"{modifierName} modifier is not registered!";
        return false;
    }

    public bool AddModifier(GameModifierBase? modifier, out string message)
    {
        if (modifier is null)
        {
            message = "Modifier is null?";
            return false;
        }

        var blockingNames = _activeModifiers
            .Where(active => active.CheckIfIncompatible(modifier) || modifier.CheckIfIncompatible(active))
            .Select(active => CSRollUtils.GetModifierDisplayName(_core, active))
            .ToList();

        if (blockingNames.Count > 0)
        {
            message = $"{CSRollUtils.GetModifierDisplayName(_core, modifier)} modifier is blocked by: " + string.Join(", ", blockingNames);
            return false;
        }

        if (_activeModifiers.Contains(modifier))
        {
            message = $"{CSRollUtils.GetModifierDisplayName(_core, modifier)} modifier is already active.";
            return false;
        }

        ActivateModifiers([modifier]);
        message = $"Successfully added {CSRollUtils.GetModifierDisplayName(_core, modifier)} modifier.";
        return true;
    }

    /// <summary>
    /// Applies a modifier scoped to just one player's own slot (the !memodifier command), rather
    /// than everyone (AddModifier/AddModifierByName). If the modifier is already active globally or
    /// already covers this slot, there's nothing further to do. If it's active for OTHER slots only,
    /// this slot is added to its existing AssignedSlots instead of reactivating it. Note this only
    /// actually scopes the effect to one player for modifiers whose own implementation checks
    /// IsAssignedTo - some modifiers (PlantAnywhere, etc.) drive genuinely server-wide cvars and
    /// will still affect everyone regardless of AssignedSlots, since that's an engine limitation
    /// those modifiers already document, not something this command can work around.
    /// </summary>
    public bool AddModifierToPlayer(string modifierName, int slot, out string message)
    {
        var modifier = GetRegisteredModifierByName(modifierName);
        if (modifier is null)
        {
            message = $"{modifierName} modifier is not registered!";
            return false;
        }

        var blockingNames = _activeModifiers
            .Where(active => active != modifier && ModifierAppliesToSlot(active, slot) &&
                (active.CheckIfIncompatible(modifier) || modifier.CheckIfIncompatible(active)))
            .Select(active => CSRollUtils.GetModifierDisplayName(_core, active))
            .ToList();

        if (blockingNames.Count > 0)
        {
            message = $"{CSRollUtils.GetModifierDisplayName(_core, modifier)} modifier is blocked by: " + string.Join(", ", blockingNames);
            return false;
        }

        if (_activeModifiers.Contains(modifier))
        {
            if (ModifierAppliesToSlot(modifier, slot))
            {
                message = $"{CSRollUtils.GetModifierDisplayName(_core, modifier)} already applies to you.";
                return false;
            }

            modifier.AddAssignedSlots([slot]);
        }
        else
        {
            modifier.Activate([slot]);
            _activeModifiers.Add(modifier);

            if (Config.ShowCentreMsg && _core.PlayerManager.GetPlayer(slot) is { IsValid: true } player)
            {
                player.SendCenterHTML(CSRollUtils.BuildActivatingModifiersHtml(_core, [modifier], Config.SpinReveal), 6000);
            }
        }

        message = $"Applied {CSRollUtils.GetModifierDisplayName(_core, modifier)} to just you.";
        return true;
    }

    /// <summary>How often the composed per-player modifier HUD is pushed. Matches what each modifier used to use individually when they still owned their own SendCenterHTML call.</summary>
    private const float ModifierHudRefreshIntervalSeconds = 0.1f;

    /// <summary>Panel lifetime per push - comfortably longer than the refresh interval, so the HUD reads as persistent rather than strobing between pushes.</summary>
    private const int ModifierHudDurationMs = 400;

    /// <summary>
    /// How long a published block stays live without being re-published.
    ///
    /// This is what preserves the semantics modifiers already depended on. Before the composer they
    /// each pushed center-HTML with a ~400ms lifetime and simply STOPPED pushing when their HUD no
    /// longer applied - dead player, no fuel, cooldown state gone - and the panel expired on its own.
    /// A section held until explicitly retracted would instead re-push that last frame forever, so a
    /// player who died mid-Vanish would keep a frozen gauge on screen for the rest of the round.
    /// Expiring on the same kind of short timer restores "stop drawing and it goes away" for every
    /// modifier, without each one having to remember an explicit ClearHud on every exit path.
    ///
    /// Comfortably longer than ModifierHudRefreshIntervalSeconds so a section that IS still being
    /// published every tick can never lapse between two composer passes.
    /// </summary>
    private const float HudSectionTtlSeconds = 0.5f;

    private sealed record HudSection(string Html, int Priority, float ExpiresAt);

    /// <summary>
    /// Per-slot, per-modifier HUD fragments, composed into ONE center-HTML push per player per
    /// refresh.
    ///
    /// Center-HTML is a single shared surface: every SendCenterHTML rebuilds the whole Panorama panel,
    /// so two modifiers drawing their own HUD for the same player don't stack - they overwrite each
    /// other, and the player sees whichever push landed last, flickering between them several times a
    /// second. That was already possible whenever two of the seven HUD-drawing modifiers happened to
    /// roll onto the same player, but Mimic and ButterflyEffect make it systematic: both exist
    /// specifically to put a SECOND modifier on someone who already has one.
    ///
    /// So modifiers no longer call SendCenterHTML for their own persistent HUD - they publish a
    /// fragment here and the runtime joins every fragment for a slot into one panel. Priority orders
    /// the blocks (higher first), which is how a granting modifier's header stays above the modifier
    /// it handed out.
    ///
    /// Transient, animation-driven HTML (WeaponRoulette's spin frames, the roll reveal itself) still
    /// goes direct - it owns the surface deliberately and briefly.
    /// </summary>
    private readonly Dictionary<int, Dictionary<GameModifierBase, HudSection>> _hudSections = [];

    private readonly Dictionary<int, float> _lastModifierHudUpdateTime = [];

    /// <summary>Publishes (or replaces) one modifier's HUD block for one player. Cheap enough to call every tick - composition and the actual send are throttled separately in RefreshModifierHud.</summary>
    public void SetHudSection(GameModifierBase owner, int slot, string html, int priority = 0)
    {
        if (!_hudSections.TryGetValue(slot, out var sections))
        {
            sections = [];
            _hudSections[slot] = sections;
        }

        sections[owner] = new HudSection(html, priority, _core.Engine.GlobalVars.CurrentTime + HudSectionTtlSeconds);
    }

    /// <summary>
    /// Whether a modifier currently has a live HUD block published for this player.
    ///
    /// Exists so a GRANTING modifier (ButterflyEffect, Mimic) can avoid printing the name of what it
    /// handed out when that modifier already draws its own block directly underneath - otherwise the
    /// composed panel repeats the same name on two adjacent lines ("Active: Jetpack" immediately
    /// above Jetpack's own header). Modifiers with no HUD of their own still need the name printed,
    /// which is why this is a question rather than a blanket rule.
    /// </summary>
    public bool HasHudSection(GameModifierBase owner, int slot) =>
        _hudSections.TryGetValue(slot, out var sections) &&
        sections.TryGetValue(owner, out var section) &&
        _core.Engine.GlobalVars.CurrentTime < section.ExpiresAt;

    /// <summary>Retracts one modifier's block for one player - e.g. Vanish's own HUD while the player is dead.</summary>
    public void ClearHudSection(GameModifierBase owner, int slot)
    {
        if (_hudSections.TryGetValue(slot, out var sections) && sections.Remove(owner) && sections.Count == 0)
        {
            _hudSections.Remove(slot);
        }
    }

    /// <summary>Retracts every block a modifier owns, for every player. Called automatically from GameModifierBase.Deactivate so no modifier can leave a stale block on screen after it ends.</summary>
    public void ClearHudSections(GameModifierBase owner)
    {
        foreach (var slot in _hudSections.Keys.ToList())
        {
            ClearHudSection(owner, slot);
        }
    }

    /// <summary>
    /// Joins every published fragment for each player into a single center-HTML push. Ordered by
    /// descending priority, then by modifier name so equal-priority blocks keep a stable order
    /// instead of shuffling with dictionary enumeration between frames.
    /// </summary>
    private void RefreshModifierHud()
    {
        if (_hudSections.Count == 0)
        {
            return;
        }

        var now = _core.Engine.GlobalVars.CurrentTime;

        foreach (var (slot, sections) in _hudSections.ToList())
        {
            // Bug fix shape shared with every other map-relative timestamp in this file: CurrentTime
            // restarts near zero on a map change, so expiries carried over from the previous map sit
            // far in the future. Treating "now is BEFORE the section was even published" as expired
            // clears them out instead of freezing every HUD for the rest of the session.
            foreach (var (owner, section) in sections.ToList())
            {
                if (now >= section.ExpiresAt || now + HudSectionTtlSeconds < section.ExpiresAt)
                {
                    sections.Remove(owner);
                }
            }

            if (sections.Count == 0)
            {
                _hudSections.Remove(slot);
                continue;
            }

            // Bug fix shape borrowed from the spectator HUD: CurrentTime is map-relative and restarts
            // near zero, so a timestamp carried over from the previous map sits in the future and
            // would gate every refresh off for the rest of the session.
            if (_lastModifierHudUpdateTime.TryGetValue(slot, out var lastUpdate) &&
                now >= lastUpdate && now - lastUpdate < ModifierHudRefreshIntervalSeconds)
            {
                continue;
            }

            // Same gate every modifier HUD used to apply individually - stay off the surface while
            // the roll's own spin/reveal owns it. See IsModifierHudSuppressed.
            if (IsModifierHudSuppressed)
            {
                continue;
            }

            var player = _core.PlayerManager.GetAllValidPlayers().FirstOrDefault(p => p.Slot == slot);
            if (player is not { IsValid: true })
            {
                continue;
            }

            _lastModifierHudUpdateTime[slot] = now;

            var html = string.Join("<br/>", sections
                .OrderByDescending(entry => entry.Value.Priority)
                .ThenBy(entry => entry.Key.Name, StringComparer.Ordinal)
                .Select(entry => entry.Value.Html));

            player.SendCenterHTML(html, ModifierHudDurationMs);
        }
    }

    /// <summary>
    /// Set while RemoveAllModifiers is walking _activeModifiers, so a granting modifier's own
    /// OnDisabled cleanup (Mimic/ButterflyEffect revoking what they handed out) can't mutate the list
    /// underneath that loop. Deactivate() runs OnDisabled synchronously, so without this a revoke
    /// triggered from teardown would List.Remove an entry the reverse loop hadn't reached yet and skip
    /// deactivating it entirely. Everything is being torn down anyway, so no-oping is the correct
    /// behaviour rather than merely the safe one.
    /// </summary>
    private bool _isRemovingAllModifiers;

    /// <summary>
    /// Empty AssignedSlots means global/"everyone" (the same convention GameModifierBase.IsAssignedTo
    /// uses internally) - this is the equivalent check from outside the modifier.
    ///
    /// Bug fix: the liveness test is what makes that convention safe to apply here. Deactivate()
    /// CLEARS AssignedSlots, so an inactive modifier and an active global one are indistinguishable by
    /// slots alone - both have an empty set. Without this check every registered-but-inactive modifier
    /// read as "already applies to everyone", which inverted GetGrantableModifiersForSlot: instead of
    /// offering the whole pool it offered only modifiers currently active on OTHER players, so
    /// ButterflyEffect on a server where nobody else had rolled anything had nothing to roll and sat
    /// on "&lt;none&gt;" all round. (Mimic hid the bug - it intersects with its victim's modifiers,
    /// which are active by definition.)
    /// </summary>
    private bool ModifierAppliesToSlot(GameModifierBase modifier, int slot) =>
        _activeModifiers.Contains(modifier) &&
        (modifier.AssignedSlots.Count == 0 || modifier.AssignedSlots.Contains(slot));

    /// <summary>Every currently-active modifier scoped to this player, including globally-scoped ones.</summary>
    public IReadOnlyList<GameModifierBase> GetModifiersForSlot(int slot) =>
        _activeModifiers.Where(m => ModifierAppliesToSlot(m, slot)).ToList();

    /// <summary>
    /// Modifiers a "steal/roll another modifier onto this player" effect (Mimic, ButterflyEffect) is
    /// allowed to hand out: registered, per-player-capable, not already on them, not the granting
    /// modifier itself, and compatible with everything they currently have.
    ///
    /// Globally-scoped ACTIVES are deliberately excluded as candidates - they apply to everyone
    /// already, so "granting" one to a single player is a no-op that would look like a broken steal.
    /// Inactive modifiers are the opposite case and must stay in: the pool this draws from is the full
    /// registered set, not "whatever happens to be running right now" - see ModifierAppliesToSlot for
    /// the bug that came of conflating the two.
    /// </summary>
    public IReadOnlyList<GameModifierBase> GetGrantableModifiersForSlot(int slot, GameModifierBase granter)
    {
        // Bug fix: this pool skipped the team-size rule that PickRandomModifiersForPlayer applies to
        // the normal per-player roll, so Config.RequiresMultiplePlayersPerTeam (Saint, SwapOnDeath,
        // SuicideBomber - modifiers that simply cannot function with one player per side) was
        // enforced for rolled modifiers but bypassed entirely for granted ones. In a 1v1, Mimic or
        // ButterflyEffect would happily hand out the exact modifiers that list exists to keep out.
        var teamSize = _core.PlayerManager.GetPlayer(slot) is { IsValid: true, Controller: { IsValid: true } controller }
            ? _core.PlayerManager.GetInTeam(controller.Team).Count()
            : 0;

        return _registeredModifiers
            .Where(m => m != granter && m.SupportsPerPlayerRandomization && !ModifierAppliesToSlot(m, slot))
            .Where(m => MeetsTeamSizeRequirement(m, teamSize))
            .Where(m => !GetModifiersForSlot(slot).Any(active => active.CheckIfIncompatible(m) || m.CheckIfIncompatible(active)))
            .ToList();
    }

    /// <summary>
    /// Scopes an already-active modifier onto one more player, activating it for them if this is the
    /// first owner. Counterpart to RevokeModifierFromSlot.
    /// </summary>
    public bool GrantModifierToSlot(GameModifierBase modifier, int slot)
    {
        if (_isRemovingAllModifiers)
        {
            return false;
        }

        if (ModifierAppliesToSlot(modifier, slot) && _activeModifiers.Contains(modifier))
        {
            return false;
        }

        if (_activeModifiers.Contains(modifier))
        {
            modifier.AddAssignedSlots([slot]);
        }
        else
        {
            modifier.Activate([slot]);
            _activeModifiers.Add(modifier);
        }

        return true;
    }

    /// <summary>
    /// Un-scopes a modifier from one player, fully deactivating it only if they were its last owner.
    /// Mirrors the disconnect path's IsOnlyAssignedSlot handling - dropping the slot from a modifier
    /// that still has other owners must not tear it down for them, and a modifier left with an empty
    /// AssignedSlots would silently widen to "everyone" rather than switching off.
    /// </summary>
    public void RevokeModifierFromSlot(GameModifierBase modifier, int slot)
    {
        if (_isRemovingAllModifiers || !_activeModifiers.Contains(modifier) || !modifier.AssignedSlots.Contains(slot))
        {
            return;
        }

        if (modifier.IsOnlyAssignedSlot(slot))
        {
            modifier.Deactivate();
            _activeModifiers.Remove(modifier);
            return;
        }

        modifier.RemoveAssignedSlot(slot);
    }

    /// <summary>
    /// Permanently removes a modifier from the registered pool for the rest of this session (the
    /// !disablemodifier command) - a stronger version of RemoveModifierByName, which only deactivates
    /// a currently-active modifier but leaves it eligible to be rolled/added again immediately
    /// afterward. Deactivates it first if active, then Unregister()s it (so its own OnUnregistered
    /// cleanup runs - e.g. dropping any OnClientConnected/OnClientDisconnected subscriptions) and
    /// drops it from RegisteredModifiers, so !rolltoggle/!memodifier/the random pools can no longer
    /// select it at all.
    ///
    /// Also appends the name to Config.DisabledModifiers in memory - this does NOT write back to
    /// config.jsonc on disk, though, so it only lasts for the rest of this session: an explicit
    /// !rollreload re-reads the file fresh and will re-enable this modifier unless it's also been
    /// added to DisabledModifiers there by hand, and so will the next full plugin reload (map
    /// change/restart) regardless, since that always re-reads config.jsonc from scratch too.
    /// </summary>
    public bool DisableModifierByName(string modifierName, out string message)
    {
        var modifier = GetRegisteredModifierByName(modifierName);
        if (modifier is null)
        {
            message = $"{modifierName} modifier is not registered.";
            return false;
        }

        if (_activeModifiers.Contains(modifier))
        {
            modifier.Deactivate();
            _activeModifiers.Remove(modifier);
        }

        modifier.Unregister();
        _registeredModifiers.Remove(modifier);

        if (!Config.DisabledModifiers.Contains(modifier.Name, StringComparer.OrdinalIgnoreCase))
        {
            Config.DisabledModifiers = [.. Config.DisabledModifiers, modifier.Name];
        }

        message = $"Disabled {CSRollUtils.GetModifierDisplayName(_core, modifier)} - it can no longer be added/rolled until re-enabled (!rollmenu) or the plugin reloads.";
        return true;
    }

    /// <summary>
    /// Reverses DisableModifierByName without a full plugin reload: constructs a fresh instance from
    /// the remembered factory (see _allModifierFactories), Register()s it, adds it back to
    /// RegisteredModifiers, and drops it from Config.DisabledModifiers in memory. The new instance
    /// starts with no active/assigned state - re-enabling only makes it eligible to be rolled/added
    /// again, it does not itself activate anything.
    /// </summary>
    public bool EnableModifierByName(string modifierName, out string message)
    {
        if (IsModifierRegisteredByName(modifierName))
        {
            message = $"{modifierName} is already enabled.";
            return false;
        }

        if (!_allModifierFactories.TryGetValue(modifierName, out var factory))
        {
            message = $"{modifierName} modifier is not known.";
            return false;
        }

        var modifier = factory();
        modifier.Register(_core, this, _cvarService);
        _registeredModifiers.Add(modifier);

        Config.DisabledModifiers = Config.DisabledModifiers.Where(x => !x.Equals(modifierName, StringComparison.OrdinalIgnoreCase)).ToArray();

        message = $"Enabled {CSRollUtils.GetModifierDisplayName(_core, modifier)} - it can be added/rolled again.";
        return true;
    }

    /// <summary>
    /// One GameModifierBase per name CSRoll knows about, for !rollmenu's enable/disable list - the
    /// live registered instance where one exists (so IsModifierActive/AssignedSlots etc. reflect
    /// reality), or a fresh throwaway instance from its factory otherwise (display purposes only -
    /// Name/Description - never Register()'d or Activate()'d, and discarded after the caller reads it).
    /// </summary>
    public IEnumerable<GameModifierBase> GetAllKnownModifiers() =>
        _allModifierFactories.Select(kv => GetRegisteredModifierByName(kv.Key) ?? kv.Value());

    public void RemoveModifierByName(string modifierName, out string message)
    {
        if (_activeModifiers.Count == 0)
        {
            message = "No modifiers are active.";
            return;
        }

        var modifier = _activeModifiers.FirstOrDefault(m => string.Equals(m.Name, modifierName, StringComparison.OrdinalIgnoreCase));
        if (modifier is not null)
        {
            RemoveModifier(modifier, out message);
            return;
        }

        message = $"{modifierName} modifier is not active.";
    }

    public bool RemoveModifier(GameModifierBase? modifier, out string message)
    {
        if (modifier is null)
        {
            message = "Modifier is null?";
            return false;
        }

        if (!_activeModifiers.Contains(modifier))
        {
            message = $"{CSRollUtils.GetModifierDisplayName(_core, modifier)} modifier is not active.";
            return true;
        }

        _core.Logger.LogInformation("[CSRoll] RemoveModifier: deactivating {Name} (slots=[{Slots}])", modifier.Name, string.Join(",", modifier.AssignedSlots));
        modifier.Deactivate();
        _activeModifiers.Remove(modifier);
        message = $"Removed {CSRollUtils.GetModifierDisplayName(_core, modifier)} modifier.";
        return true;
    }

    public void RemoveAllModifiers()
    {
        // Invalidates any in-flight reveal closures from a prior roll - see _rollGeneration's doc
        // comment for why this is needed on top of nulling the pending fields below.
        _rollGeneration++;

        // A roll may have been selected but not yet committed (still waiting on its
        // spin-then-reveal to land) - cancel it too, or it would still activate later once that
        // scheduled reveal fires despite everything having just been cleared.
        _pendingGlobalModifiers = null;
        _pendingAssignedSlotsByModifier = null;
        _pendingModifiersByPlayerSlot = null;

        if (_activeModifiers.Count == 0)
        {
            return;
        }

        _isRemovingAllModifiers = true;

        try
        {
            for (var i = _activeModifiers.Count - 1; i >= 0; i--)
            {
                _core.Logger.LogInformation("[CSRoll] RemoveAllModifiers: deactivating {Name} (slots=[{Slots}])", _activeModifiers[i].Name, string.Join(",", _activeModifiers[i].AssignedSlots));
                _activeModifiers[i].Deactivate();
            }
        }
        finally
        {
            _isRemovingAllModifiers = false;
        }

        _activeModifiers.Clear();
    }

    public bool AddRandomModifier(out GameModifierBase? addedModifier)
    {
        if (AddRandomModifiers(1, out var addedModifiers))
        {
            addedModifier = addedModifiers[0];
            return true;
        }

        addedModifier = null;
        return false;
    }

    public bool AddRandomModifiers(int modifierCount, out List<GameModifierBase> addedModifiers, bool showBanner = true)
    {
        addedModifiers = [];

        if (modifierCount <= 0)
        {
            return true;
        }

        if (_registeredModifiers.Count == 0)
        {
            return false;
        }

        // Global activation could end up mattering for either team (e.g. Saint procs off whichever
        // team's player gets a kill), so both teams need to independently qualify.
        var tCount = _core.PlayerManager.GetT().Count();
        var ctCount = _core.PlayerManager.GetCT().Count();

        var randomModifiersPool = _registeredModifiers
            .Where(m => m.SupportsRandomRounds && !_activeModifiers.Contains(m) && (Config.CanRepeat || !_lastActiveModifiers.Contains(m)) &&
                MeetsTeamSizeRequirement(m, tCount) && MeetsTeamSizeRequirement(m, ctCount) &&
                !_activeModifiers.Any(active => active.CheckIfIncompatible(m) || m.CheckIfIncompatible(active)))
            .ToList();

        addedModifiers = PickCompatibleRandomModifiers(randomModifiersPool, modifierCount, new Random());

        if (addedModifiers.Count == 0)
        {
            return false;
        }

        // Bug fix: this shared/global roll had no !rolldebug visibility at all, unlike the per-player
        // roll's admin listing - live testing found modifiers activated here (e.g. PlantAnywhere -
        // anything that doesn't support per-player randomization, including the
        // ConditionalInvisibility/Vanish supplementary roll) reported as "hidden": nobody
        // showed up as having received them in the debug output because there simply wasn't any.
        if (DebugMode)
        {
            CSRollUtils.PrintTitleToAdminsOnly(_core, "Activating modifiers (global/shared roll):");
            foreach (var modifier in addedModifiers)
            {
                CSRollUtils.PrintToAdminsOnly(_core, $"• {CSRollUtils.GetModifierDisplayName(_core, modifier)} - [{CSRollUtils.GetModifierDescription(_core, modifier)}]");
            }
        }

        if (showBanner)
        {
            // Immediate path (e.g. !addrandommodifier(s), !reroll): no separate reveal animation
            // follows this call, so activate right away - there's nothing to defer to.
            ActivateModifiers(addedModifiers);
        }
        else
        {
            // Bug fix: modifiers used to be Activate()'d right here, immediately at round start -
            // up to several seconds before ScheduleFreezeTimeBanner's spin-then-reveal ever told
            // players what was rolled. Stash the roll instead; PlaySpinThenRevealActiveModifiersBanner
            // commits it exactly when the reveal lands.
            _pendingGlobalModifiers = addedModifiers;
        }

        return true;
    }

    /// <summary>Immediate activation with an immediate banner/chat announcement - used by callers with no separate reveal animation following them (manual !rolltoggle/!addrandommodifier(s)/!reroll). The round-start automatic roll defers activation instead - see PlaySpinThenRevealActiveModifiersBanner.</summary>
    private void ActivateModifiers(List<GameModifierBase> modifiers)
    {
        if (modifiers.Count == 0)
        {
            return;
        }

        if (Config.ShowCentreMsg)
        {
            CSRollUtils.ShowMessageCentreAll(_core, CSRollUtils.BuildActivatingModifiersHtml(_core, modifiers, Config.SpinReveal), 6000);
        }

        foreach (var player in _core.PlayerManager.GetAllValidPlayers())
        {
            SendOwnModifiersChatMessage(player, modifiers);
        }

        foreach (var modifier in modifiers)
        {
            modifier.Activate();
            _activeModifiers.Add(modifier);
        }
    }

    /// <summary>
    /// Spin-then-reveal for the currently-active set, played exactly once per round via
    /// ScheduleFreezeTimeBanner: cycles through random modifier names in the center-HTML popup before
    /// landing on the real result, then fires each affected player's "Your modifiers:" chat message
    /// at the exact moment their own reveal completes (not before). Nothing re-displays this again
    /// later in the round - the reveal itself stays up for SpinReveal.RevealDurationSeconds.
    /// </summary>
    public void PlaySpinThenRevealActiveModifiersBanner()
    {
        // Bug fix: modifiers rolled for this round used to be Activate()'d immediately at round
        // start - weapons stripped, invisibility applied, speed/spread changed, etc. - up to several
        // seconds before this method's spin-then-reveal animation ever told the affected player what
        // they'd actually gotten. A pending roll (stashed by AssignRandomModifiersPerPlayer/
        // AddRandomModifiers when called with showBanner:false) is committed here instead, timed to
        // land at the exact moment each reveal does, so the mechanical effect and the reveal always
        // appear together - never before.
        var pendingGlobal = _pendingGlobalModifiers;
        var pendingAssignedSlotsByModifier = _pendingAssignedSlotsByModifier;
        var pendingModifiersByPlayerSlot = _pendingModifiersByPlayerSlot;
        _pendingGlobalModifiers = null;
        _pendingAssignedSlotsByModifier = null;
        _pendingModifiersByPlayerSlot = null;

        if (DebugMode)
        {
            _core.Logger.LogInformation(
                "[CSRoll] Reveal: pendingGlobal={Global} pendingPerPlayer={PerPlayer} active={Active} showCentre={ShowCentre} spin={Spin}",
                pendingGlobal?.Count ?? -1,
                pendingModifiersByPlayerSlot?.Count ?? -1,
                _activeModifiers.Count,
                Config.ShowCentreMsg,
                Config.SpinReveal.Enabled);
        }

        if (pendingGlobal is { Count: > 0 })
        {
            RevealGlobalModifiers(pendingGlobal, commitOnReveal: true);
            return;
        }

        if (pendingModifiersByPlayerSlot is { Count: > 0 })
        {
            RevealPerPlayerModifiers(pendingModifiersByPlayerSlot, pendingAssignedSlotsByModifier);
            return;
        }

        // No pending roll this round (RandomRoundsEnabled == false: OnRoundStart just re-Activated
        // whatever was already active, nothing new was rolled) - redisplay the already-active set
        // exactly as before this fix, with nothing left to commit.
        if (_activeModifiers.Count == 0)
        {
            return;
        }

        // Bug fix: any active modifier with an EMPTY AssignedSlots is global in scope (e.g. an admin
        // !rolltoggle on something that doesn't support per-player randomization like
        // PlantAnywhere) - live testing confirmed these were taking effect completely
        // silently under RandomizePlayers=true: no chat, no spin, not even in the !rolldebug listing,
        // since the per-player branch below only ever iterates each modifier's AssignedSlots, which
        // contributes nothing for a global-scope one. These now always get their own broadcast
        // reveal, regardless of RandomizePlayers.
        var globalModifiers = _activeModifiers.Where(m => m.AssignedSlots.Count == 0).ToList();
        if (globalModifiers.Count > 0)
        {
            RevealGlobalModifiers(globalModifiers, commitOnReveal: false);
        }

        if (Config.RandomizePlayers)
        {
            var modifiersByPlayerSlot = new Dictionary<int, List<GameModifierBase>>();
            foreach (var modifier in _activeModifiers)
            {
                foreach (var slot in modifier.AssignedSlots)
                {
                    if (!modifiersByPlayerSlot.TryGetValue(slot, out var modifiers))
                    {
                        modifiers = [];
                        modifiersByPlayerSlot[slot] = modifiers;
                    }

                    modifiers.Add(modifier);
                }
            }

            RevealPerPlayerModifiers(modifiersByPlayerSlot, assignedSlotsByModifier: null);
        }

        // Non-RandomizePlayers mode: everything is already global in scope, so globalModifiers above
        // already covers the whole active set - nothing further to reveal here.
    }

    /// <summary>Broadcast reveal for a global-scope set of modifiers. When commitOnReveal is true (a deferred round-start roll), activation happens at the exact moment the reveal lands rather than beforehand.</summary>
    private void RevealGlobalModifiers(List<GameModifierBase> modifiers, bool commitOnReveal)
    {
        var generation = _rollGeneration;

        void Reveal()
        {
            // Bug fix: a newer roll superseded this one while its spin animation was still
            // in flight (see _rollGeneration's doc comment) - drop it instead of activating a
            // modifier from a roll that's no longer current.
            if (_rollGeneration != generation)
            {
                return;
            }

            if (commitOnReveal)
            {
                foreach (var modifier in modifiers)
                {
                    // Bug fix: selection filters against _activeModifiers, but the commit happens
                    // seconds later when the reveal lands - an admin !rolltoggle in between could
                    // have activated the same instance already. Re-activating doubles every
                    // OnEnabled subscription (OnTick fires twice, and the second HookPost overwrites
                    // the stored Guid so the first hook can never be unhooked) while Deactivate only
                    // ever runs once. The per-player twin below already guards this way.
                    if (_activeModifiers.Contains(modifier))
                    {
                        continue;
                    }

                    modifier.Activate();
                    _activeModifiers.Add(modifier);
                }

                _lastActiveModifiers = _activeModifiers.ToList();
            }

            foreach (var player in _core.PlayerManager.GetAllValidPlayers())
            {
                SendOwnModifiersChatMessage(player, modifiers);
                CSRollUtils.SendRevealFade(_core, player, Config.SpinReveal);
            }
        }

        if (Config.ShowCentreMsg)
        {
            PlaySpinThenRevealAll(
                () => CSRollUtils.BuildActivatingModifiersHtml(_core, modifiers, Config.SpinReveal),
                Reveal,
                progress => CSRollUtils.BuildActivatingModifiersHtml(_core, modifiers, Config.SpinReveal, progress),
                modifiers);
        }
        else
        {
            Reveal();
        }
    }

    /// <summary>
    /// Per-player reveal for a slot-to-modifiers assignment. When assignedSlotsByModifier is
    /// non-null (a deferred round-start roll), each modifier is committed the first time any of its
    /// owning slots' reveal lands - using the FULL slot set already known from the roll, not just
    /// that one slot - so OnEnabled() sees every owner at once even though, in practice, every
    /// slot's reveal runs the identical spin schedule and lands the same tick anyway. Later slots
    /// sharing that modifier then just find it already active (AddAssignedSlots, no-op for OnEnabled).
    /// </summary>
    private void RevealPerPlayerModifiers(Dictionary<int, List<GameModifierBase>> modifiersByPlayerSlot, Dictionary<GameModifierBase, List<int>>? assignedSlotsByModifier)
    {
        var generation = _rollGeneration;

        foreach (var (slot, modifiers) in modifiersByPlayerSlot)
        {
            if (modifiers.Count == 0)
            {
                continue;
            }

            void Reveal()
            {
                // Bug fix: same generation guard as RevealGlobalModifiers - without it, a stale
                // reveal from a roll superseded mid-animation could still Activate() its modifier
                // after a newer roll already committed its own, leaving two modifiers active for
                // the same player at once (the "first round after warmup ends" bug: CS2 fires
                // EventRoundStart twice for that transition).
                if (_rollGeneration != generation)
                {
                    return;
                }

                if (assignedSlotsByModifier is not null)
                {
                    foreach (var modifier in modifiers)
                    {
                        if (!_activeModifiers.Contains(modifier))
                        {
                            modifier.Activate(assignedSlotsByModifier[modifier]);
                            _activeModifiers.Add(modifier);
                        }
                    }
                }

                // Re-resolved rather than captured: this runs from a scheduler continuation at the end
                // of the spin, by which point the player may have disconnected.
                if (_core.PlayerManager.GetPlayer(slot) is { IsValid: true } revealed)
                {
                    SendOwnModifiersChatMessage(revealed, modifiers);
                    CSRollUtils.SendRevealFade(_core, revealed, Config.SpinReveal);
                }
            }

            if (Config.ShowCentreMsg)
            {
                PlaySpinThenReveal(
                    slot,
                    () => CSRollUtils.BuildActivatingModifiersHtml(_core, modifiers, Config.SpinReveal),
                    Reveal,
                    progress => CSRollUtils.BuildActivatingModifiersHtml(_core, modifiers, Config.SpinReveal, progress),
                    modifiers);
            }
            else
            {
                Reveal();
            }
        }
    }

    /// <summary>
    /// Eases the per-frame delay from SpinReveal.StartIntervalSeconds (fast) up to
    /// EndIntervalSeconds (slow) as the spin approaches its last frame - a quadratic curve, so the
    /// interval grows slowly at first and then noticeably stretches out right before landing, giving
    /// the classic slot-machine "spin fast, then ease out" feel rather than a constant tick rate.
    /// </summary>
    private float GetSpinFrameIntervalSeconds(int frameIndex, int totalFrames)
    {
        var t = totalFrames <= 1 ? 1f : (float)frameIndex / (totalFrames - 1);
        var eased = t * t;
        return Config.SpinReveal.StartIntervalSeconds + ((Config.SpinReveal.EndIntervalSeconds - Config.SpinReveal.StartIntervalSeconds) * eased);
    }

    /// <summary>
    /// Cycles a given player's center-HTML through SpinReveal.SpinCount random modifier names,
    /// easing from a fast to a slow tick rate, then shows the built reveal for RevealDurationSeconds and
    /// invokes onRevealed. Implemented as a self-rescheduling chain of Core.Scheduler.DelayBySeconds
    /// calls (the same primitive ScheduleFreezeTimeBanner already uses successfully) rather than
    /// DelayAndRepeatBySeconds with a 0-second initial delay - that combination turned out not to
    /// fire at all on a live server, so this sticks to the one delay primitive already confirmed
    /// working in this codebase. Re-fetches the player by slot every frame (not a captured IPlayer
    /// reference) since a delayed scheduler callback can easily outlive a disconnecting player.
    /// </summary>
    private void PlaySpinThenReveal(int slot, Func<string> buildFinalHtml, Action onRevealed, Func<float, string>? buildDescriptionFrame = null, IReadOnlyList<GameModifierBase>? modifiers = null)
    {
        // Claim the center-HTML surface for the whole animation plus the reveal it lands on, so
        // modifier HUDs don't fight it - see IsModifierHudSuppressed. Kept even on the custom-HUD path:
        // the nine center-HTML modifier gauges still exist and still need to stand down for a reveal,
        // whichever surface that reveal is drawn on.
        SuppressModifierHudFor(EstimateRevealAnimationSeconds() + Config.SpinReveal.RevealDurationSeconds);

        // The custom HUD draws the whole spin and reveal itself, client-side. Running both surfaces at
        // once would put two competing animations on screen, and there is no way to detect per-player
        // whether a client has the Workshop addon - so this is an all-or-nothing server config choice.
        if (modifiers is { Count: > 0 } && _hudSequencer.HandlesReveal)
        {
            _hudSequencer.PlayReveal(slot, modifiers, onRevealed);
            return;
        }

        if (!Config.SpinReveal.Enabled || _registeredModifiers.Count == 0)
        {
            _core.PlayerManager.GetPlayer(slot)?.SendCenterHTML(buildFinalHtml(), RevealHoldMs());
            onRevealed();
            return;
        }

        PlayNextSpinFrame(slot, 0, Config.SpinReveal.SpinCount, buildFinalHtml, onRevealed, buildDescriptionFrame);
    }

    private void PlayNextSpinFrame(int slot, int frameIndex, int totalFrames, Func<string> buildFinalHtml, Action onRevealed, Func<float, string>? buildDescriptionFrame = null)
    {
        var current = _core.PlayerManager.GetPlayer(slot);
        if (current is not { IsValid: true })
        {
            return;
        }

        if (frameIndex >= totalFrames)
        {
            // The name has landed, so the roll is over - commit here rather than after the
            // description animation, keeping the mechanical effect simultaneous with the reveal
            // (the whole point of the deferred-activation fix) instead of trailing it by ~0.8s.
            onRevealed();

            if (TryGetDescriptionScrambleFrames(buildDescriptionFrame) is { } scrambleFrames)
            {
                PlayDescriptionScrambleFrame(slot, 0, scrambleFrames, buildDescriptionFrame!, buildFinalHtml);
            }
            else
            {
                // Re-armed at the exact moment the finished reveal goes up, so modifier HUDs
                // return precisely when it disappears rather than on the up-front estimate.
                SuppressModifierHudFor(RevealHoldMs() / 1000f);
                current.SendCenterHTML(buildFinalHtml(), RevealHoldMs());
            }

            return;
        }

        // Emptiness is only checked once when the chain starts, but !disablemodifier can empty the
        // list mid-spin - Random.Next(0) would then throw from inside a scheduler callback.
        if (_registeredModifiers.Count == 0)
        {
            return;
        }

        var randomName = CSRollUtils.GetModifierDisplayName(_core, _registeredModifiers[Random.Shared.Next(_registeredModifiers.Count)]);
        var interval = GetSpinFrameIntervalSeconds(frameIndex, totalFrames);
        current.SendCenterHTML(CSRollUtils.BuildSpinFrameHtml(randomName), (int)(interval * 1000) + 50);
        CSRollUtils.PlaySoundToPlayer(_core, current, Config.SpinReveal.TickSoundEventName, Config.SpinReveal.TickSoundVolume, debugMode: DebugMode);

        _core.Scheduler.DelayBySeconds(interval, () => PlayNextSpinFrame(slot, frameIndex + 1, totalFrames, buildFinalHtml, onRevealed, buildDescriptionFrame));
    }

    /// <summary>
    /// The scramble's frame count, or null when it shouldn't run at all (no frame builder supplied,
    /// disabled in config, or a nonsensical frame count) - in which case the caller just sends the
    /// finished reveal directly, exactly as it did before the animation existed.
    /// </summary>
    private int? TryGetDescriptionScrambleFrames(Func<float, string>? buildDescriptionFrame)
    {
        if (buildDescriptionFrame is null || !Config.SpinReveal.ShowDescription || !Config.SpinReveal.DescriptionScrambleEnabled)
        {
            return null;
        }

        var frames = Config.SpinReveal.DescriptionScrambleFrames;
        return frames > 0 && Config.SpinReveal.DescriptionScrambleDurationSeconds > 0f ? frames : null;
    }

    /// <summary>
    /// Wipes the description in one frame at a time once the name has already landed.
    ///
    /// Each frame's on-screen duration deliberately outlives the gap to the next one (interval +
    /// DescriptionHoldMs). Center-HTML frames can be silently swallowed when they arrive faster than
    /// the panel rebuilds, so the overlap means a dropped frame just leaves the previous one showing
    /// for a moment rather than blanking the popup - and the final resolved line is sent with the
    /// full reveal duration, so it can never be the frame that gets lost.
    /// </summary>
    private void PlayDescriptionScrambleFrame(int slot, int frameIndex, int totalFrames, Func<float, string> buildDescriptionFrame, Func<string> buildFinalHtml)
    {
        var current = _core.PlayerManager.GetPlayer(slot);
        if (current is not { IsValid: true })
        {
            return;
        }

        if (frameIndex >= totalFrames)
        {
            // Re-armed at the exact moment the finished reveal goes up, so modifier HUDs
            // return precisely when it disappears rather than on the up-front estimate.
            SuppressModifierHudFor(RevealHoldMs() / 1000f);
            current.SendCenterHTML(buildFinalHtml(), RevealHoldMs());
            return;
        }

        var interval = Config.SpinReveal.DescriptionScrambleDurationSeconds / totalFrames;
        current.SendCenterHTML(buildDescriptionFrame((float)frameIndex / totalFrames), (int)(interval * 1000) + Config.SpinReveal.DescriptionHoldMs);

        _core.Scheduler.DelayBySeconds(interval, () => PlayDescriptionScrambleFrame(slot, frameIndex + 1, totalFrames, buildDescriptionFrame, buildFinalHtml));
    }

    /// <summary>Broadcast counterpart to PlaySpinThenReveal, used for the shared/global (non-RandomizePlayers) activation path where every player sees the same spin land on the same result.</summary>
    private void PlaySpinThenRevealAll(Func<string> buildFinalHtml, Action onRevealed, Func<float, string>? buildDescriptionFrame = null, IReadOnlyList<GameModifierBase>? modifiers = null)
    {
        // Claim the center-HTML surface for the whole animation plus the reveal it lands on, so
        // modifier HUDs don't fight it - see IsModifierHudSuppressed.
        SuppressModifierHudFor(EstimateRevealAnimationSeconds() + Config.SpinReveal.RevealDurationSeconds);

        // See PlaySpinThenReveal. The broadcast path is where the custom HUD is cheapest: one write per
        // panel for the entire server, rather than one per player per frame.
        if (modifiers is { Count: > 0 } && _hudSequencer.HandlesReveal)
        {
            _hudSequencer.PlayRevealAll(modifiers, onRevealed);
            return;
        }

        if (!Config.SpinReveal.Enabled || _registeredModifiers.Count == 0)
        {
            // Re-armed at the exact moment the finished reveal goes up, so modifier HUDs
            // return precisely when it disappears rather than on the up-front estimate.
            SuppressModifierHudFor(RevealHoldMs() / 1000f);
            CSRollUtils.ShowMessageCentreAll(_core, buildFinalHtml(), RevealHoldMs());
            onRevealed();
            return;
        }

        PlayNextSpinFrameAll(0, Config.SpinReveal.SpinCount, buildFinalHtml, onRevealed, buildDescriptionFrame);
    }

    private void PlayNextSpinFrameAll(int frameIndex, int totalFrames, Func<string> buildFinalHtml, Action onRevealed, Func<float, string>? buildDescriptionFrame = null)
    {
        if (frameIndex >= totalFrames)
        {
            // See PlayNextSpinFrame: commit on the name landing, not after the description wipe.
            onRevealed();

            if (TryGetDescriptionScrambleFrames(buildDescriptionFrame) is { } scrambleFrames)
            {
                PlayDescriptionScrambleFrameAll(0, scrambleFrames, buildDescriptionFrame!, buildFinalHtml);
            }
            else
            {
                // Re-armed at the exact moment the finished reveal goes up, so modifier HUDs
                // return precisely when it disappears rather than on the up-front estimate.
                SuppressModifierHudFor(RevealHoldMs() / 1000f);
                CSRollUtils.ShowMessageCentreAll(_core, buildFinalHtml(), RevealHoldMs());
            }

            return;
        }

        // Emptiness is only checked once when the chain starts, but !disablemodifier can empty the
        // list mid-spin - Random.Next(0) would then throw from inside a scheduler callback.
        if (_registeredModifiers.Count == 0)
        {
            return;
        }

        var randomName = CSRollUtils.GetModifierDisplayName(_core, _registeredModifiers[Random.Shared.Next(_registeredModifiers.Count)]);
        var interval = GetSpinFrameIntervalSeconds(frameIndex, totalFrames);
        CSRollUtils.ShowMessageCentreAll(_core, CSRollUtils.BuildSpinFrameHtml(randomName), (int)(interval * 1000) + 50);
        CSRollUtils.PlaySoundToAll(_core, Config.SpinReveal.TickSoundEventName, Config.SpinReveal.TickSoundVolume, debugMode: DebugMode);

        _core.Scheduler.DelayBySeconds(interval, () => PlayNextSpinFrameAll(frameIndex + 1, totalFrames, buildFinalHtml, onRevealed, buildDescriptionFrame));
    }

    /// <summary>Broadcast counterpart to PlayDescriptionScrambleFrame - see there for the frame-overlap reasoning.</summary>
    private void PlayDescriptionScrambleFrameAll(int frameIndex, int totalFrames, Func<float, string> buildDescriptionFrame, Func<string> buildFinalHtml)
    {
        if (frameIndex >= totalFrames)
        {
            // Re-armed at the exact moment the finished reveal goes up, so modifier HUDs
            // return precisely when it disappears rather than on the up-front estimate.
            SuppressModifierHudFor(RevealHoldMs() / 1000f);
            CSRollUtils.ShowMessageCentreAll(_core, buildFinalHtml(), RevealHoldMs());
            return;
        }

        var interval = Config.SpinReveal.DescriptionScrambleDurationSeconds / totalFrames;
        CSRollUtils.ShowMessageCentreAll(_core, buildDescriptionFrame((float)frameIndex / totalFrames), (int)(interval * 1000) + Config.SpinReveal.DescriptionHoldMs);

        _core.Scheduler.DelayBySeconds(interval, () => PlayDescriptionScrambleFrameAll(frameIndex + 1, totalFrames, buildDescriptionFrame, buildFinalHtml));
    }
}
