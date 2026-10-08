using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;

using CSRoll.Config;
using CSRoll.Core;
using CSRoll.Modifiers;

namespace CSRoll.Hud;

/// <summary>
/// The admin panel (!rolladmin): a clickable window in the same layout as the HUD (csr_adm, the last
/// child of the screen, so it paints over everything), shown to one admin at a time by the `on` class
/// and given a mouse cursor with input capture.
///
/// It's drawn the way the HUD is: DrawAdmin writes every panel's text and classes from the live
/// values through the per-player caches, which drop anything unchanged. It runs on open, in the same
/// tick as every click, and ten times a second while open - which is what picks up changes made
/// elsewhere (another admin, a console convar, a config reload) and expires armed buttons and messages.
///
/// Every click is re-checked: the layout is ours, the panel is open for that slot, and the player still
/// has the admin permission - any client can send any button id.
/// </summary>
public sealed partial class CustomHudService
{
    private const string AdminPrefix = "csr_adm_";
    private const float AdminIdleCloseSeconds = 180f;
    private const float AdminConfirmSeconds = 3f;
    private const float AdminMessageSeconds = 3f;
    private const float AdminWarnSeconds = 4f;
    private const float AdminUntilNextClick = float.MaxValue;
    private const int AdminMaxRounds = 10;
    private const int AdminMaxCooldown = 10;
    private const float AdminMaxWeight = 100f;

    /// <summary>The buttons that ask twice: the first click arms them, a second within AdminConfirmSeconds runs them.</summary>
    private static readonly string[] AdminArmable =
        [HudLayout.AdminReload, HudLayout.AdminAllOff, HudLayout.AdminOverrideReset, HudLayout.AdminReroll, HudLayout.AdminClear];

    private sealed class AdminView
    {
        public string Page = "gen";
        public string Filter = "all";
        public int ModPage;

        /// <summary>Slot -> modifier name, as last drawn. A click acts on the row the admin saw, even if the list changed underneath.</summary>
        public readonly string?[] Drawn = new string?[HudLayout.AdminSlots];

        public string? Armed;
        public float ArmedUntil;
        public string? Message;
        public string MessageKind = HudLayout.StatusOk;
        public float MessageUntil;
        public float LastInput;
        public float NextDraw;
        public bool Clicked;

        /// <summary>The entity the panel was last put on screen for - a new entity starts blank, input capture included.</summary>
        public int ShownOn = -1;
    }

    private readonly Dictionary<int, AdminView> _admins = [];

    /// <summary>Where each admin left the panel, so a reopen comes back to the same page.</summary>
    private readonly Dictionary<ulong, (string Page, string Filter)> _adminLastPage = [];

    /// <summary>Bumped on every entity spawn - an open panel compares it to know it has to be shown again.</summary>
    private int _entityGeneration;

    private bool _adminSlotMismatchLogged;

    /// <summary>Admins who've had the long how-to-close hint this session - later opens get a short line.</summary>
    private readonly HashSet<ulong> _adminHintShown = [];

    /// <summary>The last draw failure logged, so a fault that repeats ten times a second is logged once.</summary>
    private string? _adminDrawError;

    /// <summary>One instance of every known modifier, sorted by display name - registered ones are swapped for the live instance when drawn.</summary>
    private List<GameModifierBase>? _adminCatalog;

    /// <summary>Short lines for modifiers that aren't registered: their text tokens read runtime state they don't have, so it's worked out once and kept.</summary>
    private readonly Dictionary<string, string> _adminShortCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The plugin side: baseline, Save, Reload and the convars. The panel won't open without it.</summary>
    public IAdminPanelHost? AdminHost { get; set; }

    // -------------------------------------------------------------------------------------------------
    // Open / close
    // -------------------------------------------------------------------------------------------------

    /// <summary>!rolladmin - opens the panel for this admin, or closes it if it's open. Returns the chat reply.</summary>
    public string ToggleAdmin(IPlayer player)
    {
        if (_admins.ContainsKey(player.Slot))
        {
            CloseAdmin(player.Slot);
            return "Admin panel closed.";
        }

        if (!_installed || AdminHost is null)
        {
            return "The admin panel isn't available right now - use !rollmenu.";
        }

        if (player.IsFakeClient || !IsAddressable(player.Slot))
        {
            return "Only an in-game player can open the admin panel.";
        }

        var view = new AdminView { LastInput = Now };
        if (_adminLastPage.TryGetValue(player.SteamID, out var last))
        {
            (view.Page, view.Filter) = last;
        }

        _admins[player.Slot] = view;

        // The panel lives in the HUD's layout, so this creates the entity even with the HUD switched off.
        EnsureEntity();
        if (_layout is not { IsValid: true } && _createAttempts >= MaxCreateAttempts)
        {
            _admins.Remove(player.Slot);
            return $"The HUD entity couldn't be created this map ({_lastCreateError ?? "no detail"}) - use !rollmenu.";
        }

        // Until the entity exists, the tick shows it as soon as it does.
        ShowAdmin(player.Slot, view);
        return _adminHintShown.Add(player.SteamID)
            ? "Admin panel open - click X, type !rolladmin or press your bind (bind F6 rolladmin) to close it. Nothing on screen? You need the CSRoll HUD addon - use !rollmenu instead."
            : "Admin panel open.";
    }

    public void CloseAdmin(int slot)
    {
        if (!_admins.Remove(slot, out var view))
        {
            return;
        }

        if (_core.PlayerManager.GetPlayer(slot) is { IsValid: true } player)
        {
            _adminLastPage[player.SteamID] = (view.Page, view.Filter);
        }

        if (_layout is not { IsValid: true } layout || !IsAddressable(slot))
        {
            return;
        }

        // The other writes stay cached: the panel is collapsed, they cost nothing, and a reopen only sends what changed.
        SetFlag(slot, State(slot), HudLayout.AdminPanel, HudLayout.On, false);
        Guard(HudLayout.AdminPanel, () => layout.SetInputCaptureEnabledForPlayer(slot, false));
    }

    /// <summary>Puts the panel on screen - every text first, then `on` and the cursor, all in one tick. A no-op once shown on this entity.</summary>
    private void ShowAdmin(int slot, AdminView view)
    {
        if (_layout is not { IsValid: true } layout || view.ShownOn == _entityGeneration)
        {
            return;
        }

        var now = Now;
        var state = State(slot);
        DrawAdmin(slot, state, view, now);
        SetFlag(slot, state, HudLayout.AdminPanel, HudLayout.On, true);
        Guard(HudLayout.AdminPanel, () => layout.SetInputCaptureEnabledForPlayer(slot, true));
        view.ShownOn = _entityGeneration;
        view.NextDraw = now + RefreshIntervalSeconds;
    }

    /// <summary>Every tick: shows panels on a new entity, closes idle or no-longer-admin ones, and redraws the rest ten times a second.</summary>
    private void TickAdmins(float now)
    {
        if (_admins.Count == 0)
        {
            return;
        }

        foreach (var (slot, view) in _admins.ToArray())
        {
            if (view.ShownOn != _entityGeneration)
            {
                ShowAdmin(slot, view);
                continue;
            }

            if (now < view.NextDraw && now >= view.NextDraw - RefreshIntervalSeconds)
            {
                continue;
            }

            view.NextDraw = now + RefreshIntervalSeconds;

            if (!IsAdmin(slot))
            {
                CloseAdmin(slot);
                continue;
            }

            if (now - view.LastInput > AdminIdleCloseSeconds)
            {
                CloseAdmin(slot);
                CSRollUtils.PrintTitleToChat(_core, _core.PlayerManager.GetPlayer(slot), "Admin panel closed after 3 minutes without a click.");
                continue;
            }

            DrawAdmin(slot, State(slot), view, now);
        }
    }

    private bool IsAdmin(int slot) =>
        _core.PlayerManager.GetPlayer(slot) is { IsValid: true, IsFakeClient: false } player &&
        _core.Permission.PlayerHasPermission(player.SteamID, CSRollUtils.AdminPermission);

    // -------------------------------------------------------------------------------------------------
    // Clicks
    // -------------------------------------------------------------------------------------------------

    private void OnCustomHudClicked(IOnCustomHudClickedEvent @event)
    {
        // Copied out first: the event object belongs to this callback, and the work hops a tick.
        var slot = @event.PlayerId;
        var buttonId = @event.ButtonId ?? "";
        var layoutIndex = @event.CustomHudLayout?.Index;
        if (!buttonId.StartsWith(AdminPrefix, StringComparison.Ordinal))
        {
            return;
        }

        // Main thread, like !rollmenu's actions: disabling a modifier deactivates it, a re-roll activates.
        _core.Scheduler.NextWorldUpdate(() => HandleAdminClick(slot, layoutIndex, buttonId));
    }

    private void HandleAdminClick(int slot, uint? layoutIndex, string buttonId)
    {
        if (_layout is not { IsValid: true } layout || layoutIndex != layout.Index)
        {
            return;
        }

        if (!_admins.TryGetValue(slot, out var view))
        {
            // The click's player id should be the slot the panel was opened on. If it ever isn't, this says so.
            if (_admins.Count > 0 && !_adminSlotMismatchLogged)
            {
                _adminSlotMismatchLogged = true;
                _core.Logger.LogWarning("[CSRoll][Admin] Click on {Button} from player id {PlayerId}, which has no panel open (open on: {Open}).", buttonId, slot, string.Join(", ", _admins.Keys));
            }

            return;
        }

        if (!IsAdmin(slot))
        {
            CloseAdmin(slot);
            return;
        }

        if (!view.Clicked)
        {
            view.Clicked = true;
            _core.Logger.LogInformation("[CSRoll][Admin] Panel clicks are arriving (slot {Slot}, first button {Button}).", slot, buttonId);
        }

        var now = Now;
        view.LastInput = now;
        if (view.Armed != buttonId)
        {
            view.Armed = null;
        }

        view.Message = null;

        try
        {
            RunAdminAction(slot, view, buttonId, now);
        }
        catch (Exception ex)
        {
            _core.Logger.LogError(ex, "[CSRoll][Admin] {Button} failed.", buttonId);
            Say(view, HudLayout.StatusError, $"That didn't work: {ex.Message}", now, AdminUntilNextClick);
        }

        // Same tick, so the click's result leaves with this tick's update.
        if (_admins.TryGetValue(slot, out var open) && open == view && view.ShownOn == _entityGeneration)
        {
            view.NextDraw = now + RefreshIntervalSeconds;
            DrawAdmin(slot, State(slot), view, now);
        }
    }

    private void RunAdminAction(int slot, AdminView view, string id, float now)
    {
        var host = AdminHost!;
        var config = _runtime.Config;

        switch (id)
        {
            case HudLayout.AdminClose:
                CloseAdmin(slot);
                return;

            case HudLayout.AdminSave:
            {
                var live = CaptureAdminLive();
                var count = live.Diff(host.Baseline ?? live, AdminDisplayName).Count;
                if (count == 0)
                {
                    Say(view, HudLayout.StatusOk, "Nothing to save - config.jsonc already matches", now);
                }
                else if (host.Save(live, out var error))
                {
                    Say(view, HudLayout.StatusOk, $"Saved {Plural(count, "change")} to config.jsonc", now);
                }
                else
                {
                    Say(view, HudLayout.StatusError, $"Couldn't save: {error} - changes are still live", now, AdminUntilNextClick);
                }

                return;
            }

            case HudLayout.AdminReload:
            {
                var live = CaptureAdminLive();
                var count = live.Diff(host.Baseline ?? live, AdminDisplayName).Count;
                if (count > 0 && !Confirm(view, id, now, $"Click RELOAD again to discard {Plural(count, "unsaved change")}"))
                {
                    return;
                }

                host.ReloadFromDisk();
                Say(view, HudLayout.StatusOk, "Reloaded config.jsonc from disk", now);
                return;
            }

            case HudLayout.AdminPrev:
                view.ModPage = Math.Max(0, view.ModPage - 1);
                return;

            case HudLayout.AdminNext:
                view.ModPage++;   // clamped when drawn
                return;

            case HudLayout.AdminAllOn:
                SetAllInFilter(view, enable: true, now);
                return;

            case HudLayout.AdminAllOff:
                if (Confirm(view, id, now, $"Click ALL OFF again to disable all {FilterNoun(view.Filter)}"))
                {
                    SetAllInFilter(view, enable: false, now);
                }

                return;

            case HudLayout.AdminOverrideReset:
                if (Confirm(view, id, now, "Click RESET TIERS again to put every modifier back in its built-in tier"))
                {
                    config.Rarity.Overrides = new Dictionary<string, string>();
                    ModifierRarity.Invalidate();
                    Say(view, HudLayout.StatusOk, "Every modifier is back in its built-in tier", now);
                }

                return;

            case HudLayout.AdminReroll:
                if (!_runtime.RandomRoundsEnabled)
                {
                    Say(view, HudLayout.StatusError, "Random rounds are off - switch them on to re-roll", now, AdminUntilNextClick);
                }
                else if (_runtime.RegisteredModifiers.Count == 0)
                {
                    Say(view, HudLayout.StatusError, "No modifiers are enabled - enable some first", now, AdminUntilNextClick);
                }
                else if (Confirm(view, id, now, "Click RE-ROLL again to strip every active modifier and roll again"))
                {
                    // Exactly !rollmenu's Re-roll: the roll is stashed and committed when the reveal lands.
                    _runtime.RemoveAllModifiers();
                    _runtime.ApplyRandomRoundsForRound(showBanner: false);
                    _runtime.PlaySpinThenRevealActiveModifiersBanner();
                    Say(view, HudLayout.StatusOk, "Re-rolled - the reveal is playing", now);
                }

                return;

            case HudLayout.AdminClear:
                if (Confirm(view, id, now, "Click REMOVE ALL again to strip every active modifier"))
                {
                    _runtime.RemoveAllModifiers();
                    Say(view, HudLayout.StatusOk, "Removed every active modifier", now);
                }

                return;
        }

        if (TryStrip(id, "csr_adm_tab_", out var page))
        {
            if (HudLayout.AdminPages.Contains(page))
            {
                view.Page = page;
            }
        }
        else if (TryStrip(id, "csr_adm_flt_", out var filter))
        {
            if (HudLayout.AdminFilters.Contains(filter))
            {
                view.Filter = filter;
                view.ModPage = 0;
            }
        }
        else if (TryStrip(id, "csr_adm_s_", out var setting))
        {
            ApplySetting(view, setting, now);
        }
        else if (TryStrip(id, "csr_adm_w_", out var weight))
        {
            ApplyWeight(weight);
        }
        else if (TryParseSlotButton(id, out var index, out var action) && view.Drawn[index] is { } name)
        {
            if (action == "tog")
            {
                ToggleModifier(view, name, now);
            }
            else if (action == "tier")
            {
                CycleTier(view, name, now);
            }
        }
        else
        {
            _core.Logger.LogDebug("[CSRoll][Admin] Ignored unknown button {Button}.", id);
        }
    }

    /// <summary>A setting row's button: "{row}_{option}", e.g. "mode_team", "min_inc".</summary>
    private void ApplySetting(AdminView view, string setting, float now)
    {
        var cut = setting.LastIndexOf('_');
        if (cut <= 0)
        {
            return;
        }

        var host = AdminHost!;
        var config = _runtime.Config;
        var (key, option) = (setting[..cut], setting[(cut + 1)..]);
        var on = option == "on";

        switch (key)
        {
            case "rr":
                if (on == _runtime.RandomRoundsEnabled)
                {
                    return;
                }

                if (on && _runtime.RegisteredModifiers.Count == 0)
                {
                    Say(view, HudLayout.StatusError, "No modifiers are enabled - enable some first", now, AdminUntilNextClick);
                    return;
                }

                // Through the toggle, so it's announced like !randomrounds.
                _runtime.ToggleRandomRounds();
                host.SyncConVar("csr_randomrounds");
                return;

            case "mode":
                if (!CSRollConfig.TryParseRollMode(option, out var mode) || mode == _runtime.RollMode)
                {
                    return;
                }

                config.RollMode = mode.ToString();
                config.RandomizePlayers = null;
                host.AnnounceRollMode(mode);
                host.SyncConVar("csr_rollmode");
                return;

            case "min":
                _runtime.MinRandomRounds = Step(_runtime.MinRandomRounds, option, 0, _runtime.MaxRandomRounds);
                host.SyncConVar("csr_minrounds");
                return;

            case "max":
                _runtime.MaxRandomRounds = Step(_runtime.MaxRandomRounds, option, _runtime.MinRandomRounds, Math.Max(AdminMaxRounds, _runtime.MaxRandomRounds));
                host.SyncConVar("csr_maxrounds");
                return;

            case "cd":
                config.PerPlayerRepeatCooldownRounds = Step(config.PerPlayerRepeatCooldownRounds, option, 0, Math.Max(AdminMaxCooldown, config.PerPlayerRepeatCooldownRounds));
                return;

            case "rep":
                config.CanRepeat = on;
                return;

            case "warm":
                config.DisableRandomRoundsInWarmup = !on;
                return;

            case "reveal":
                config.ShowCentreMsg = on;
                return;

            case "spin":
                config.SpinReveal.Enabled = on;
                return;

            case "spec":
                config.SpectatorHud.Enabled = on;
                return;

            case "hud":
                config.CustomHud.Enabled = on;
                host.SyncConVar("csr_hud");
                return;

            case "hudmode":
                config.CustomHud.Mode = option == "everyone" ? "Everyone" : "OptIn";
                host.SyncConVar("csr_hud_mode");
                return;

            case "hudspec":
                config.CustomHud.ShowToSpectatorTeam = on;
                return;

            case "listy":
                config.CustomHud.ListOffset = Step(Math.Clamp(config.CustomHud.ListOffset, 0, HudLayout.ListOffsets - 1), option, 0, HudLayout.ListOffsets - 1);
                return;

            case "rar":
                config.Rarity.Enabled = on;
                host.SyncConVar("csr_rarity");
                return;
        }
    }

    /// <summary>A tier weight button: "{tier}_{dec5|dec|inc|inc5}".</summary>
    private void ApplyWeight(string text)
    {
        var cut = text.LastIndexOf('_');
        if (cut <= 0)
        {
            return;
        }

        var key = text[..cut];
        var delta = text[(cut + 1)..] switch { "dec5" => -5f, "dec" => -1f, "inc" => 1f, "inc5" => 5f, _ => 0f };
        foreach (var tier in ModifierRarity.Tiers)
        {
            if (ModifierRarity.CssKey(tier) != key || delta == 0f)
            {
                continue;
            }

            var rarity = _runtime.Config.Rarity;
            var current = AdminSnapshot.RawWeight(tier, rarity);
            var whole = float.IsFinite(current) ? MathF.Round(current, MidpointRounding.AwayFromZero) : 0f;
            AdminSnapshot.SetWeight(tier, rarity, Math.Clamp(whole + delta, 0f, AdminMaxWeight));
        }
    }

    private void ToggleModifier(AdminView view, string name, float now)
    {
        var display = AdminDisplayName(name);
        if (_runtime.IsModifierRegisteredByName(name))
        {
            var wasActive = _runtime.IsModifierActiveByName(name);
            if (!_runtime.DisableModifierByName(name, out var message))
            {
                Say(view, HudLayout.StatusError, message, now, AdminUntilNextClick);
                return;
            }

            Say(view, HudLayout.StatusOk, wasActive ? $"{display} disabled and removed from everyone holding it" : $"{display} disabled", now);
        }
        else
        {
            var enabled = _runtime.EnableModifierByName(name, out var message);
            Say(view, enabled ? HudLayout.StatusOk : HudLayout.StatusError, enabled ? $"{display} can roll again" : message, now);
        }
    }

    /// <summary>One tier up, Gold wrapping to Mil-Spec. Back at the built-in tier the override is removed, so the file only lists real moves.</summary>
    private void CycleTier(AdminView view, string name, float now)
    {
        var rarity = _runtime.Config.Rarity;
        var next = (ModifierTier)(((int)ModifierRarity.Resolve(name, rarity) + 1) % ModifierRarity.Tiers.Length);

        if (rarity.Overrides is null)
        {
            rarity.Overrides = new Dictionary<string, string>();
        }

        foreach (var key in rarity.Overrides.Keys.Where(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            rarity.Overrides.Remove(key);
        }

        if (next != ModifierRarity.BuiltInTier(name))
        {
            rarity.Overrides[name] = next.ToString();
        }

        // The tier cache is keyed on the config object, which this edit doesn't replace.
        ModifierRarity.Invalidate();
        Say(view, HudLayout.StatusOk, $"{AdminDisplayName(name)} moved to {ModifierRarity.Label(next)}", now);
    }

    /// <summary>All on / All off: every modifier in the current filter - exactly what the count above the list describes.</summary>
    private void SetAllInFilter(AdminView view, bool enable, float now)
    {
        var changed = 0;
        foreach (var modifier in FilteredModifiers(view.Filter))
        {
            var registered = _runtime.IsModifierRegisteredByName(modifier.Name);
            if (enable && !registered && _runtime.EnableModifierByName(modifier.Name, out _))
            {
                changed++;
            }
            else if (!enable && registered && _runtime.DisableModifierByName(modifier.Name, out _))
            {
                changed++;
            }
        }

        if (!enable && _runtime.RegisteredModifiers.Count == 0)
        {
            Say(view, HudLayout.StatusWarn, "Every modifier is disabled - random rounds have nothing to roll", now, AdminWarnSeconds);
            return;
        }

        Say(view, HudLayout.StatusOk, changed == 0
            ? $"All {FilterNoun(view.Filter)} were already {(enable ? "on" : "off")}"
            : $"{(enable ? "Enabled" : "Disabled")} {Plural(changed, "modifier")}", now);
    }

    /// <summary>The first click arms the button and returns false; a second click on it within AdminConfirmSeconds returns true.</summary>
    private static bool Confirm(AdminView view, string id, float now, string warning)
    {
        if (view.Armed == id && now < view.ArmedUntil)
        {
            view.Armed = null;
            return true;
        }

        view.Armed = id;
        view.ArmedUntil = now + AdminConfirmSeconds;
        Say(view, HudLayout.StatusWarn, warning, now, AdminConfirmSeconds);
        return false;
    }

    private static void Say(AdminView view, string kind, string text, float now, float seconds = AdminMessageSeconds)
    {
        view.Message = text;
        view.MessageKind = kind;
        view.MessageUntil = seconds == AdminUntilNextClick ? AdminUntilNextClick : now + seconds;
    }

    private static int Step(int value, string direction, int min, int max) =>
        Math.Clamp(value + (direction == "inc" ? 1 : direction == "dec" ? -1 : 0), min, Math.Max(min, max));

    private static bool TryStrip(string id, string prefix, out string rest)
    {
        rest = id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : "";
        return rest.Length > 0;
    }

    /// <summary>"csr_adm_m{i}_{tog|tier}" - a modifier slot's switch or tier chip.</summary>
    private static bool TryParseSlotButton(string id, out int index, out string action)
    {
        index = -1;
        action = "";
        if (!TryStrip(id, "csr_adm_m", out var rest))
        {
            return false;
        }

        var cut = rest.IndexOf('_');
        if (cut <= 0 || !int.TryParse(rest[..cut], out index) || index < 0 || index >= HudLayout.AdminSlots)
        {
            return false;
        }

        action = rest[(cut + 1)..];
        return true;
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string FilterNoun(string filter) => filter == "all" ? "modifiers" : HudCatalog.CategoryLabel(filter);

    // -------------------------------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------------------------------

    /// <summary>Draws the whole panel. A fault in it is logged and swallowed - it runs inside the HUD's tick, which every player's HUD depends on.</summary>
    private void DrawAdmin(int slot, PlayerState s, AdminView v, float now)
    {
        try
        {
            DrawAdminCore(slot, s, v, now);
        }
        catch (Exception ex)
        {
            if (_adminDrawError != ex.Message)
            {
                _adminDrawError = ex.Message;
                _core.Logger.LogError(ex, "[CSRoll][Admin] Drawing the admin panel failed.");
            }
        }
    }

    private void DrawAdminCore(int slot, PlayerState s, AdminView v, float now)
    {
        if (AdminHost is not { } host)
        {
            return;
        }

        if (v.Armed is not null && now >= v.ArmedUntil)
        {
            v.Armed = null;
        }

        if (v.Message is not null && now >= v.MessageUntil)
        {
            v.Message = null;
        }

        var live = CaptureAdminLive();
        var changes = live.Diff(host.Baseline ?? live, AdminDisplayName);
        var dirty = new HashSet<string>(changes.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
        var modifiers = AdminModifiers();
        var enabled = modifiers.Count(m => !live.Disabled.Contains(m.Name));

        SetText(slot, s, HudLayout.AdminVersion, HudLayout.VarValue, "v" + host.Version);
        SetText(slot, s, HudLayout.AdminUnsaved, HudLayout.VarValue, $"{changes.Count} unsaved");
        SetText(slot, s, HudLayout.AdminTabSub("mod"), HudLayout.VarValue, $"{enabled} / {modifiers.Count} enabled");
        SetText(slot, s, HudLayout.AdminTabSub("cfg"), HudLayout.VarValue, changes.Count > 0 ? $"{changes.Count} unsaved" : "All saved");
        foreach (var page in HudLayout.AdminPages)
        {
            SetFlag(slot, s, HudLayout.AdminTab(page), HudLayout.Dirty, changes.Any(c => c.Page == page));
        }

        if (v.Message is { } message)
        {
            SetText(slot, s, HudLayout.AdminStatus, HudLayout.VarValue, message);
            SetExclusive(slot, s, HudLayout.AdminStatus, "st", v.MessageKind);
        }
        else
        {
            SetText(slot, s, HudLayout.AdminStatus, HudLayout.VarValue, changes.Count > 0
                ? $"{Plural(changes.Count, "unsaved change")} - live now, reverts on plugin reload unless saved"
                : "All changes saved to config.jsonc");
            ClearExclusive(slot, s, HudLayout.AdminStatus, "st");
        }

        foreach (var id in AdminArmable)
        {
            SetFlag(slot, s, id, HudLayout.Armed, v.Armed == id);
        }

        DrawAdminSettings(slot, s, live, dirty);
        DrawAdminModifiers(slot, s, v, live, dirty, modifiers);
        DrawAdminRarity(slot, s, live, dirty, modifiers);
        DrawAdminConfig(slot, s, changes, host);

        // The root's classes last: they reveal the page and the dirty markers the writes above filled in.
        SetExclusive(slot, s, HudLayout.AdminPanel, "pg", HudLayout.AdminPageClass(v.Page));
        SetFlag(slot, s, HudLayout.AdminPanel, HudLayout.Dirty, changes.Count > 0);
        SetFlag(slot, s, HudLayout.AdminPanel, HudLayout.RarityClass("off"), !live.Rarity);
    }

    private void DrawAdminSettings(int slot, PlayerState s, AdminSnapshot live, HashSet<string> dirty)
    {
        void Choice(string row, string key, int option, bool na = false)
        {
            SetExclusive(slot, s, row, "v", HudLayout.ChoiceClass(option));
            SetFlag(slot, s, row, HudLayout.Dirty, dirty.Contains(key));
            SetFlag(slot, s, row, HudLayout.NotApplicable, na);
        }

        void Stepper(string row, string key, string text, bool atMin, bool atMax, bool na = false)
        {
            SetText(slot, s, row + "_val", HudLayout.VarValue, text);
            SetFlag(slot, s, row, HudLayout.AtMin, atMin);
            SetFlag(slot, s, row, HudLayout.AtMax, atMax);
            SetFlag(slot, s, row, HudLayout.Dirty, dirty.Contains(key));
            SetFlag(slot, s, row, HudLayout.NotApplicable, na);
        }

        var game = live.RollMode == ModifierRollMode.Game;

        Choice(HudLayout.SetRr, "rr", live.RandomRounds ? 0 : 1);
        Choice(HudLayout.SetMode, "mode", (int)live.RollMode);
        Stepper(HudLayout.SetMin, "min", $"{live.MinRounds}", live.MinRounds <= 0, live.MinRounds >= live.MaxRounds);
        Stepper(HudLayout.SetMax, "max", $"{live.MaxRounds}", live.MaxRounds <= live.MinRounds, live.MaxRounds >= AdminMaxRounds);
        Stepper(HudLayout.SetCd, "cd", live.RepeatCooldown <= 0 ? "Off" : $"{live.RepeatCooldown}", live.RepeatCooldown <= 0, live.RepeatCooldown >= AdminMaxCooldown, na: game);
        Choice(HudLayout.SetRep, "rep", live.CanRepeat ? 0 : 1, na: !game);
        Choice(HudLayout.SetWarm, "warm", live.RollInWarmup ? 0 : 1);

        Choice(HudLayout.SetReveal, "reveal", live.Reveal ? 0 : 1);
        Choice(HudLayout.SetSpin, "spin", live.Spin ? 0 : 1, na: !live.Reveal);
        Choice(HudLayout.SetSpec, "spec", live.SpectatorPanel ? 0 : 1);
        Choice(HudLayout.SetHud, "hud", live.Hud ? 0 : 1);
        Choice(HudLayout.SetHudmode, "hudmode", live.HudEveryone ? 1 : 0, na: !live.Hud);
        Choice(HudLayout.SetHudspec, "hudspec", live.HudSpectators ? 0 : 1, na: !live.Hud);
        Stepper(HudLayout.SetListy, "listy", $"{live.ListOffset}", live.ListOffset <= 0, live.ListOffset >= HudLayout.ListOffsets - 1, na: !live.Hud);

        var brand = Cfg.BrandText?.Trim() ?? "";
        SetText(slot, s, HudLayout.SetBrandValue, HudLayout.VarValue, brand.Length == 0 ? "(empty - the bar is hidden)" : brand.Length > 64 ? brand[..64] : brand);

        Choice(HudLayout.SetRar, "rar", live.Rarity ? 0 : 1);
    }

    private void DrawAdminModifiers(int slot, PlayerState s, AdminView v, AdminSnapshot live, HashSet<string> dirty, List<GameModifierBase> all)
    {
        var list = v.Filter == "all" ? all : all.Where(m => HudCatalog.Category(m) == v.Filter).ToList();
        var pages = Math.Max(1, (list.Count + HudLayout.AdminSlots - 1) / HudLayout.AdminSlots);
        v.ModPage = Math.Clamp(v.ModPage, 0, pages - 1);

        var enabled = list.Count(m => !live.Disabled.Contains(m.Name));
        SetExclusive(slot, s, HudLayout.AdminFilter, "flt", v.Filter == "all" ? HudLayout.FilterAll : HudLayout.CategoryClass(v.Filter));
        SetText(slot, s, HudLayout.AdminModCount, HudLayout.VarValue, v.Filter == "all"
            ? $"{enabled} / {list.Count} enabled"
            : $"{HudCatalog.CategoryLabel(v.Filter)}  ·  {enabled} / {list.Count} enabled");
        SetText(slot, s, HudLayout.AdminPageInfo, HudLayout.VarValue, $"Page {v.ModPage + 1} / {pages}");
        SetFlag(slot, s, HudLayout.AdminPager, HudLayout.AtMin, v.ModPage == 0);
        SetFlag(slot, s, HudLayout.AdminPager, HudLayout.AtMax, v.ModPage >= pages - 1);

        // Slots fill column-major (0-6 down the left, 7-13 down the right), so the sorted list reads down, then across.
        for (var i = 0; i < HudLayout.AdminSlots; i++)
        {
            var panel = HudLayout.AdminMod(i);
            var index = v.ModPage * HudLayout.AdminSlots + i;
            if (index >= list.Count)
            {
                v.Drawn[i] = null;
                SetFlag(slot, s, panel, HudLayout.Empty, true);
                continue;
            }

            var modifier = list[index];
            var name = modifier.Name;
            var tier = live.Tiers.TryGetValue(name, out var resolved) ? resolved : ModifierRarity.Resolve(modifier, Rarity);
            var off = live.Disabled.Contains(name);
            v.Drawn[i] = name;

            SetExclusive(slot, s, panel, "cat", HudLayout.CategoryClass(HudCatalog.Category(modifier)));
            SetExclusive(slot, s, panel, "rar", HudLayout.RarityClass(ModifierRarity.CssKey(tier)));
            SetExclusive(slot, s, HudLayout.AdminModIcon(i), "icon", HudLayout.IconClass(HudCatalog.Icon(modifier)));
            SetText(slot, s, HudLayout.AdminModName(i), HudLayout.VarName, CSRollUtils.GetModifierDisplayName(_core, modifier));
            SetText(slot, s, HudLayout.AdminModShort(i), HudLayout.VarShort, AdminShort(modifier));
            SetText(slot, s, HudLayout.AdminModTierText(i), HudLayout.VarRarity, ModifierRarity.Label(tier));
            SetFlag(slot, s, panel, HudLayout.Off, off);
            SetFlag(slot, s, panel, HudLayout.Live, !off && _runtime.IsModifierActive(modifier));
            SetFlag(slot, s, panel, HudLayout.Overridden, tier != ModifierRarity.BuiltInTier(name));
            SetFlag(slot, s, panel, HudLayout.Dirty, dirty.Contains($"m:{name}") || dirty.Contains($"t:{name}"));
            SetFlag(slot, s, panel, HudLayout.Empty, false);
        }
    }

    /// <summary>
    /// Each tier's share of a pick, the way ModifierRarity.PickWeighted works it out over today's pool:
    /// weight over the total of the tiers that have an enabled modifier. Per-roller exclusions shift it a
    /// little per player; this is the server-wide picture.
    /// </summary>
    private void DrawAdminRarity(int slot, PlayerState s, AdminSnapshot live, HashSet<string> dirty, List<GameModifierBase> all)
    {
        var tiers = ModifierRarity.Tiers;
        var counts = new int[tiers.Length];
        foreach (var modifier in all)
        {
            if (!live.Disabled.Contains(modifier.Name) && live.Tiers.TryGetValue(modifier.Name, out var tier))
            {
                counts[(int)tier]++;
            }
        }

        var enabled = counts.Sum();
        var weights = tiers.Select(t => counts[(int)t] > 0 && float.IsFinite(live.Weights[(int)t]) ? Math.Max(0f, live.Weights[(int)t]) : 0f).ToArray();
        var total = weights.Sum();

        // With every weight at 0 a pick is uniform, so a tier's share is its head count.
        var shares = tiers.Select(t => total > 0f ? weights[(int)t] / total * 100f : enabled > 0 ? counts[(int)t] * 100f / enabled : 0f).ToArray();
        var strip = LargestRemainder(shares);

        foreach (var tier in tiers)
        {
            var k = (int)tier;
            var key = ModifierRarity.CssKey(tier);
            var row = HudLayout.AdminWeight(key);
            var weight = live.Weights[k];
            var count = counts[k];

            var info = !live.Rarity ? $"{count} enabled  ·  unused while rarity is off"
                : count == 0 ? "0 enabled  ·  never rolls"
                : shares[k] <= 0f ? $"{count} enabled  ·  weight 0, never rolls"
                : $"{count} enabled  ·  {shares[k] / count:0.0}% each";

            SetText(slot, s, row + "_val", HudLayout.VarValue, AdminSnapshot.FormatWeight(weight));
            SetText(slot, s, row + "_info", HudLayout.VarValue, info);
            SetText(slot, s, row + "_pct", HudLayout.VarValue, count == 0 ? "–" : $"{shares[k]:0.0}%");
            SetExclusive(slot, s, row, "f", HudLayout.FillClass(Math.Clamp((int)MathF.Round(shares[k]), 0, HudLayout.FillSteps)));
            SetFlag(slot, s, row, HudLayout.AtMin, !(weight > 0f));
            SetFlag(slot, s, row, HudLayout.AtMax, weight >= AdminMaxWeight);
            SetFlag(slot, s, row, HudLayout.Dirty, dirty.Contains($"w:{key}"));
            SetFlag(slot, s, row, HudLayout.NotApplicable, count == 0);
            SetExclusive(slot, s, HudLayout.AdminDist(key), "f", HudLayout.FillClass(strip[k]));
        }

        var moved = live.Tiers.Count(kv => kv.Value != ModifierRarity.BuiltInTier(kv.Key));
        SetText(slot, s, HudLayout.AdminOverrideInfo, HudLayout.VarValue, moved == 0
            ? "Every modifier is in its built-in tier"
            : $"{Plural(moved, "modifier")} moved off {(moved == 1 ? "its" : "their")} built-in tier (Rarity.Overrides)");
    }

    private void DrawAdminConfig(int slot, PlayerState s, List<AdminChange> changes, IAdminPanelHost host)
    {
        SetText(slot, s, HudLayout.AdminConfigCount, HudLayout.VarValue, changes.Count == 0 ? "None" : Plural(changes.Count, "change"));

        for (var i = 0; i < HudLayout.AdminConfigLines; i++)
        {
            string? text;
            if (changes.Count == 0)
            {
                text = i == 0 ? "Nothing to save - config.jsonc matches what is live" : null;
            }
            else if (changes.Count > HudLayout.AdminConfigLines && i == HudLayout.AdminConfigLines - 1)
            {
                text = $"+ {changes.Count - i} more";
            }
            else
            {
                text = i < changes.Count ? changes[i].Text : null;
            }

            var line = HudLayout.AdminConfigLine(i);
            SetText(slot, s, line, HudLayout.VarValue, text ?? "");
            SetFlag(slot, s, line, HudLayout.Empty, text is null);
            SetFlag(slot, s, line, HudLayout.NotApplicable, changes.Count == 0 && i == 0);
        }

        var holders = _core.PlayerManager.GetAllValidPlayers().Count(p => _runtime.GetModifiersForSlot(p.Slot).Count > 0);
        SetText(slot, s, HudLayout.AdminConfigLive, HudLayout.VarValue,
            $"{Plural(_runtime.ActiveModifiers.Count, "modifier")} active on {Plural(holders, "player")}  ·  random rounds {(_runtime.RandomRoundsEnabled ? "on" : "off")}  ·  {_runtime.RollMode} mode");

        var path = host.ConfigPath.Replace('\\', '/');
        var addons = path.IndexOf("addons/", StringComparison.OrdinalIgnoreCase);
        SetText(slot, s, HudLayout.AdminConfigPath, HudLayout.VarValue, addons >= 0 ? path[addons..] : path);

        var note = host.ConVarNote;
        SetText(slot, s, HudLayout.AdminConfigConVar, HudLayout.VarValue, note ?? "");
        SetFlag(slot, s, HudLayout.AdminConfigConVar, HudLayout.Empty, note is null);
    }

    /// <summary>Whole percentages that add up to exactly 100 (or all 0): floors, then the largest remainders get the rest.</summary>
    private static int[] LargestRemainder(float[] shares)
    {
        var floors = shares.Select(v => (int)MathF.Floor(Math.Max(0f, v))).ToArray();
        if (shares.Sum() <= 0f)
        {
            return floors;
        }

        var left = 100 - floors.Sum();
        foreach (var i in Enumerable.Range(0, shares.Length).OrderByDescending(i => shares[i] - floors[i]).Take(Math.Max(0, left)))
        {
            floors[i]++;
        }

        return floors;
    }

    // -------------------------------------------------------------------------------------------------
    // Data
    // -------------------------------------------------------------------------------------------------

    private AdminSnapshot CaptureAdminLive() =>
        AdminSnapshot.Capture(_runtime, _runtime.RandomRoundsEnabled,
            _runtime.KnownModifierNames.Where(name => !_runtime.IsModifierRegisteredByName(name)).ToList());

    /// <summary>Every known modifier, sorted by display name, as its live instance where it's registered.</summary>
    private List<GameModifierBase> AdminModifiers()
    {
        _adminCatalog ??= [.. _runtime.GetAllKnownModifiers().OrderBy(m => CSRollUtils.GetModifierDisplayName(_core, m), StringComparer.OrdinalIgnoreCase)];

        var list = new List<GameModifierBase>(_adminCatalog.Count);
        foreach (var modifier in _adminCatalog)
        {
            list.Add(_runtime.GetRegisteredModifierByName(modifier.Name) ?? modifier);
        }

        return list;
    }

    private IEnumerable<GameModifierBase> FilteredModifiers(string filter) =>
        AdminModifiers().Where(m => filter == "all" || HudCatalog.Category(m) == filter);

    private string AdminDisplayName(string name) =>
        _runtime.GetRegisteredModifierByName(name) is { } registered ? CSRollUtils.GetModifierDisplayName(_core, registered)
        : _adminCatalog?.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } known ? CSRollUtils.GetModifierDisplayName(_core, known)
        : name;

    /// <summary>
    /// The short line under a slot's name. A registered modifier's is read live (its numbers follow the
    /// config). One that isn't registered can't fill in tokens that read runtime state, so its line is
    /// worked out once - with the raw tokens blanked if it throws.
    /// </summary>
    private string AdminShort(GameModifierBase modifier)
    {
        if (_runtime.IsModifierRegistered(modifier))
        {
            try
            {
                return CSRollUtils.GetModifierShortDescription(_core, modifier);
            }
            catch (Exception)
            {
                return "";
            }
        }

        if (!_adminShortCache.TryGetValue(modifier.Name, out var text))
        {
            try
            {
                text = CSRollUtils.GetModifierShortDescription(_core, modifier);
            }
            catch (Exception)
            {
                text = "Switched off";
            }

            _adminShortCache[modifier.Name] = text;
        }

        return text;
    }
}
