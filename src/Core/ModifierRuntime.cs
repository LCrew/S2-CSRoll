using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Config;
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
    /// privately. When toggled on via !debug, that "who got what" breakdown is sent to connected
    /// admins only, never broadcast to the whole server.
    /// </summary>
    public bool DebugMode { get; set; }

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

    public IReadOnlyList<GameModifierBase> RegisteredModifiers => _registeredModifiers;
    public IReadOnlyList<GameModifierBase> ActiveModifiers => _activeModifiers;

    public ModifierRuntime(ISwiftlyCore core, CSRollConfig config, ICvarRollbackService cvarService)
    {
        _core = core;
        Config = config;
        _cvarService = cvarService;
        MinRandomRounds = config.MinRandomRounds;
        MaxRandomRounds = config.MaxRandomRounds;
    }

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
    }

    private void InitialiseModifiers(IEnumerable<Func<GameModifierBase>> factories)
    {
        _registeredModifiers.Clear();

        foreach (var factory in factories)
        {
            var modifier = factory();
            if (modifier.IsRegistered && !Config.DisabledModifiers.Any(x => x.Equals(modifier.Name, StringComparison.OrdinalIgnoreCase)))
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
            if (handle.ModifierName is null || Config.DisabledModifiers.Contains(handle.ModifierName, StringComparer.OrdinalIgnoreCase))
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

        RemoveAllModifiers();

        foreach (var modifier in _registeredModifiers)
        {
            modifier.Unregister();
        }

        _lastActiveModifiers.Clear();
        _registeredModifiers.Clear();
        _lastRoundAssignedPerPlayer.Clear();
        _roundNumber = 0;
        _lastSpectatorHudUpdateTime.Clear();
    }

    /// <summary>
    /// Persistent HUD for spectators: whoever a player is currently observing (CBasePlayerPawn's
    /// ObserverServices/ObserverTarget - non-null only while actually in observer mode, e.g. dead or
    /// a true spectator, so this is a no-op for anyone currently alive and playing), listing that
    /// target's active modifiers. Re-sent on a short throttled interval (same persistent-HUD
    /// convention as FullInvisibility/Jetpack/FlankTeleport) rather than once, so switching spectate
    /// targets or the target's modifiers changing are both picked up promptly.
    /// </summary>
    private void RefreshSpectatorHud()
    {
        if (!Config.SpectatorHud.Enabled || _activeModifiers.Count == 0)
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
        // added so ConditionalInvisibility/FullInvisibility (which used to opt out of per-player
        // randomization, picking their own random target internally instead of using the runtime's
        // assignment) still got a chance while RandomizePlayers was on. That secondary global roll is
        // removed entirely per explicit instruction: no automatic global/shared activation happens
        // alongside the per-player roll anymore. ConditionalInvisibility/FullInvisibility now support
        // per-player randomization directly instead (see their own files), so they lose nothing by
        // this removal - only modifiers that are still genuinely global-only (PlantAnywhere, etc.)
        // are excluded from the automatic rotation now; they're still fully usable via an explicit
        // admin !addmodifier.
        if (Config.RandomizePlayers)
        {
            appliedAnything = AssignRandomModifiersPerPlayer(showBanner);
        }
        else
        {
            var count = random.Next(MinRandomRounds, MaxRandomRounds);
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
    /// player Y from getting LeadBoots in the same roll; incompatibility is a same-player constraint,
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
            player.SendChat($"• {CSRollUtils.GetModifierDisplayName(_core, modifier)} - {CSRollUtils.GetModifierDescription(_core, modifier)}");
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
        var count = random.Next(MinRandomRounds, MaxRandomRounds);
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
    /// be incompatible with many others (e.g. ConditionalInvisibility/FullInvisibility listing each
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

            _core.PlayerManager.GetPlayer(slot)?.SendCenterHTML(CSRollUtils.BuildActivatingModifiersHtml(_core, modifiers), 6000);
        }
    }

    public bool ToggleModifier(GameModifierBase? modifier, out string message)
    {
        if (modifier is null)
        {
            message = "Modifier is null?";
            return false;
        }

        return IsModifierActive(modifier) ? RemoveModifier(modifier, out message) : AddModifier(modifier, out message);
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
                player.SendCenterHTML(CSRollUtils.BuildActivatingModifiersHtml(_core, [modifier]), 6000);
            }
        }

        message = $"Applied {CSRollUtils.GetModifierDisplayName(_core, modifier)} to just you.";
        return true;
    }

    /// <summary>Empty AssignedSlots means global/"everyone" (the same convention GameModifierBase.IsAssignedTo uses internally) - this is the equivalent check from outside the modifier.</summary>
    private static bool ModifierAppliesToSlot(GameModifierBase modifier, int slot) =>
        modifier.AssignedSlots.Count == 0 || modifier.AssignedSlots.Contains(slot);

    /// <summary>
    /// Permanently removes a modifier from the registered pool for the rest of this session (the
    /// !disablemodifier command) - a stronger version of RemoveModifierByName, which only deactivates
    /// a currently-active modifier but leaves it eligible to be rolled/added again immediately
    /// afterward. Deactivates it first if active, then Unregister()s it (so its own OnUnregistered
    /// cleanup runs - e.g. dropping any OnClientConnected/OnClientDisconnected subscriptions) and
    /// drops it from RegisteredModifiers, so !addmodifier/!memodifier/the random pools can no longer
    /// select it at all.
    ///
    /// Also appends the name to Config.DisabledModifiers in memory, so a later !reloadmodifiers
    /// (without an intervening !reloadconfig) still respects the disable instead of silently bringing
    /// the modifier back. This does NOT write back to config.jsonc on disk, though - an explicit
    /// !reloadconfig re-reads the file fresh and will re-enable this modifier unless it's also been
    /// added to DisabledModifiers there by hand.
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

        message = $"Disabled {CSRollUtils.GetModifierDisplayName(_core, modifier)} - it can no longer be added/rolled until config.jsonc's DisabledModifiers is updated and modifiers are reloaded.";
        return true;
    }

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

        for (var i = _activeModifiers.Count - 1; i >= 0; i--)
        {
            _activeModifiers[i].Deactivate();
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

        // Bug fix: this shared/global roll had no !debug visibility at all, unlike the per-player
        // roll's admin listing - live testing found modifiers activated here (e.g. PlantAnywhere -
        // anything that doesn't support per-player randomization, including the
        // ConditionalInvisibility/FullInvisibility supplementary roll) reported as "hidden": nobody
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

    /// <summary>Immediate activation with an immediate banner/chat announcement - used by callers with no separate reveal animation following them (manual !addmodifier/!addrandommodifier(s)/!reroll). The round-start automatic roll defers activation instead - see PlaySpinThenRevealActiveModifiersBanner.</summary>
    private void ActivateModifiers(List<GameModifierBase> modifiers)
    {
        if (modifiers.Count == 0)
        {
            return;
        }

        if (Config.ShowCentreMsg)
        {
            CSRollUtils.ShowMessageCentreAll(_core, CSRollUtils.BuildActivatingModifiersHtml(_core, modifiers), 6000);
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
        // !addmodifier on something that doesn't support per-player randomization like
        // PlantAnywhere) - live testing confirmed these were taking effect completely
        // silently under RandomizePlayers=true: no chat, no spin, not even in the !debug listing,
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
                    modifier.Activate();
                    _activeModifiers.Add(modifier);
                }

                _lastActiveModifiers = _activeModifiers.ToList();
            }

            foreach (var player in _core.PlayerManager.GetAllValidPlayers())
            {
                SendOwnModifiersChatMessage(player, modifiers);
            }
        }

        if (Config.ShowCentreMsg)
        {
            PlaySpinThenRevealAll(CSRollUtils.BuildActivatingModifiersHtml(_core, modifiers), Reveal);
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

                SendOwnModifiersChatMessage(_core.PlayerManager.GetPlayer(slot), modifiers);
            }

            if (Config.ShowCentreMsg)
            {
                PlaySpinThenReveal(slot, CSRollUtils.BuildActivatingModifiersHtml(_core, modifiers), Reveal);
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
    /// easing from a fast to a slow tick rate, then shows finalHtml for RevealDurationSeconds and
    /// invokes onRevealed. Implemented as a self-rescheduling chain of Core.Scheduler.DelayBySeconds
    /// calls (the same primitive ScheduleFreezeTimeBanner already uses successfully) rather than
    /// DelayAndRepeatBySeconds with a 0-second initial delay - that combination turned out not to
    /// fire at all on a live server, so this sticks to the one delay primitive already confirmed
    /// working in this codebase. Re-fetches the player by slot every frame (not a captured IPlayer
    /// reference) since a delayed scheduler callback can easily outlive a disconnecting player.
    /// </summary>
    private void PlaySpinThenReveal(int slot, string finalHtml, Action onRevealed)
    {
        if (!Config.SpinReveal.Enabled || _registeredModifiers.Count == 0)
        {
            _core.PlayerManager.GetPlayer(slot)?.SendCenterHTML(finalHtml, (int)(Config.SpinReveal.RevealDurationSeconds * 1000));
            onRevealed();
            return;
        }

        PlayNextSpinFrame(slot, 0, Config.SpinReveal.SpinCount, finalHtml, onRevealed);
    }

    private void PlayNextSpinFrame(int slot, int frameIndex, int totalFrames, string finalHtml, Action onRevealed)
    {
        var current = _core.PlayerManager.GetPlayer(slot);
        if (current is not { IsValid: true })
        {
            return;
        }

        if (frameIndex >= totalFrames)
        {
            current.SendCenterHTML(finalHtml, (int)(Config.SpinReveal.RevealDurationSeconds * 1000));
            onRevealed();
            return;
        }

        var randomName = CSRollUtils.GetModifierDisplayName(_core, _registeredModifiers[Random.Shared.Next(_registeredModifiers.Count)]);
        var interval = GetSpinFrameIntervalSeconds(frameIndex, totalFrames);
        current.SendCenterHTML(CSRollUtils.BuildSpinFrameHtml(randomName), (int)(interval * 1000) + 50);
        CSRollUtils.PlaySoundToPlayer(_core, current, Config.SpinReveal.TickSoundEventName, Config.SpinReveal.TickSoundVolume, debugMode: DebugMode);

        _core.Scheduler.DelayBySeconds(interval, () => PlayNextSpinFrame(slot, frameIndex + 1, totalFrames, finalHtml, onRevealed));
    }

    /// <summary>Broadcast counterpart to PlaySpinThenReveal, used for the shared/global (non-RandomizePlayers) activation path where every player sees the same spin land on the same result.</summary>
    private void PlaySpinThenRevealAll(string finalHtml, Action onRevealed)
    {
        if (!Config.SpinReveal.Enabled || _registeredModifiers.Count == 0)
        {
            CSRollUtils.ShowMessageCentreAll(_core, finalHtml, (int)(Config.SpinReveal.RevealDurationSeconds * 1000));
            onRevealed();
            return;
        }

        PlayNextSpinFrameAll(0, Config.SpinReveal.SpinCount, finalHtml, onRevealed);
    }

    private void PlayNextSpinFrameAll(int frameIndex, int totalFrames, string finalHtml, Action onRevealed)
    {
        if (frameIndex >= totalFrames)
        {
            CSRollUtils.ShowMessageCentreAll(_core, finalHtml, (int)(Config.SpinReveal.RevealDurationSeconds * 1000));
            onRevealed();
            return;
        }

        var randomName = CSRollUtils.GetModifierDisplayName(_core, _registeredModifiers[Random.Shared.Next(_registeredModifiers.Count)]);
        var interval = GetSpinFrameIntervalSeconds(frameIndex, totalFrames);
        CSRollUtils.ShowMessageCentreAll(_core, CSRollUtils.BuildSpinFrameHtml(randomName), (int)(interval * 1000) + 50);
        CSRollUtils.PlaySoundToAll(_core, Config.SpinReveal.TickSoundEventName, Config.SpinReveal.TickSoundVolume, debugMode: DebugMode);

        _core.Scheduler.DelayBySeconds(interval, () => PlayNextSpinFrameAll(frameIndex + 1, totalFrames, finalHtml, onRevealed));
    }
}
