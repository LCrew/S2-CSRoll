using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Config;
using CSRoll.Core;
using CSRoll.Modifiers;

namespace CSRoll.Hud;

/// <summary>
/// The Panorama HUD: one custom_hud_layout entity (hud/panorama/layout/custom_game/csroll_hud.xml)
/// driven per player - the roll carousel and reveal card at the centre, the active-modifier list under
/// the radar, and floating gauges above the bottom HUD.
///
/// What the server can do to a layout is narrow: write a text variable, or toggle a class, on a panel
/// by id, globally or for one player. Everything here reduces to those two, and every write goes
/// through a per-player cache so an unchanged value is never resent - the state travels as entity
/// netvar diffs, and the refresh below runs ten times a second.
///
/// Lessons carried over from the first HUD attempt (the feature/custom-hud branch), each of which cost
/// a live debugging session:
/// - A per-player call takes a player slot. Anything else (an entity index) indexes past the engine's
///   player array - memory corruption, not an exception. IsAddressable gates every per-player write.
/// - Clients on the spectator team receive no HUD state at all, so they stay on center-HTML.
/// - A CSS animation or transition started by a server class write does not run in game (seen live:
///   the reel sat still at its end position). So nothing animates client-side: the reel is moved by
///   the server through a position class per tick, and bars step 1% at a time every tick.
/// - Turning a class off writes Undefined rather than DoesNotHave, which drops the override instead of
///   piling up hundreds of them over a round.
/// - The entity can disappear (round restart, map change) and is recreated on the next tick; every
///   cache is dropped with it, since the new entity starts blank.
/// </summary>
public sealed class CustomHudService
{
    private const string DesignerName = "custom_hud_layout";
    private const float RefreshIntervalSeconds = 0.1f;
    private const float CreateRetrySeconds = 2f;
    private const int MaxCreateAttempts = 10;

    private const EHudPanelClassStatus_t HasClass = EHudPanelClassStatus_t.k_eHudPanelClassStatus_HasClass;
    private const EHudPanelClassStatus_t Unset = EHudPanelClassStatus_t.k_eHudPanelClassStatus_Undefined;

    private sealed class RollState
    {
        public required GameModifierBase Primary { get; init; }
        public required int Count { get; init; }
        public Action? OnLanded { get; set; }
        public float StartedAt { get; init; }
        public int NextTick { get; set; }
        public float LastTickSoundAt { get; set; } = float.MinValue;
        public bool Landed { get; set; }
        public float LandedAt { get; set; }
        public float HideAt { get; set; }
    }

    private sealed class PlayerState
    {
        public readonly Dictionary<(string Panel, string Variable), string> Text = [];
        public readonly Dictionary<(string Panel, string Group), string> Exclusive = [];
        public readonly HashSet<(string Panel, string Class)> Flags = [];
        public float NextRefresh;
        public float PromptUntil;
        public bool OptOutHintShown;
        public RollState? Roll;
    }

    private readonly ISwiftlyCore _core;
    private readonly ModifierRuntime _runtime;
    private readonly HudPreferences _preferences;
    private readonly Dictionary<int, PlayerState> _players = [];
    private readonly Dictionary<(string Panel, string Group), string> _globalExclusive = [];

    /// <summary>When each tile boundary crosses the marker, in seconds from the start of a spin - the same curve the reel moves along, so every tick lands on a tile.</summary>
    private static readonly float[] TickTimes = BuildTickTimes();

    /// <summary>Tick sounds closer together than this are skipped: at the start of a spin tiles cross faster than the sound can play distinctly.</summary>
    private const float MinTickSoundGapSeconds = 0.05f;

    private CCSCustomHudLayout? _layout;
    private bool _installed;
    private int _createAttempts;
    private float _nextCreateAt;
    private string? _lastCreateError;
    private int? _playerCap;
    private float _stripFilledAt = float.MinValue;
    private Guid _spawnHookId;

    public CustomHudService(ISwiftlyCore core, ModifierRuntime runtime)
    {
        _core = core;
        _runtime = runtime;
        _preferences = new HudPreferences(core.PluginDataDirectory, core.Logger);
    }

    private CustomHudConfig Cfg => _runtime.Config.CustomHud;

    private bool EveryoneMode => string.Equals(Cfg.Mode, "Everyone", StringComparison.OrdinalIgnoreCase);

    private bool Live => _installed && Cfg.Enabled && _layout is { IsValid: true };

    private float Now => _core.Engine.GlobalVars.CurrentTime;

    public void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        _preferences.Load();

        _core.Event.OnTick += OnTick;
        _core.Event.OnClientDisconnected += OnClientDisconnected;
        _core.Event.OnMapLoad += OnMapLoad;
        _core.Event.OnMapUnload += OnMapUnload;
        _spawnHookId = _core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        // A reload mid-map leaves the previous load's entity behind; two layouts would draw the whole
        // HUD twice. Only ours (same layout path) is touched, so a map's own HUD is left alone.
        SweepOrphans();
    }

    public void Uninstall()
    {
        if (!_installed)
        {
            return;
        }

        _core.Event.OnTick -= OnTick;
        _core.Event.OnClientDisconnected -= OnClientDisconnected;
        _core.Event.OnMapLoad -= OnMapLoad;
        _core.Event.OnMapUnload -= OnMapUnload;
        _core.GameEvent.Unhook(_spawnHookId);

        LandPendingRolls();
        DespawnLayout();
        _installed = false;
    }

    // -------------------------------------------------------------------------------------------------
    // Who sees it
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether this player is on the custom HUD right now. Every center-HTML surface asks this and
    /// stands aside when it is true, so nobody gets both. False for bots, for anyone still choosing a
    /// team, for the spectator team when ShowToSpectatorTeam is off, and for anyone who hasn't chosen
    /// the HUD - unless Mode is "Everyone", where only an explicit !hud opt-out says no.
    ///
    /// The spectator team used to be excluded outright on the first HUD attempt's finding that CS2
    /// sends those clients no HUD state. That finding is unconfirmed, so it's a config switch now: if
    /// spectators see nothing with it on, turning it off puts them back on the center-HTML panel.
    /// </summary>
    public bool UsesCustomHud(int slot)
    {
        if (!Live || _core.PlayerManager.GetPlayer(slot) is not { IsValid: true, IsFakeClient: false } player)
        {
            return false;
        }

        if (player.Controller is not { IsValid: true } controller)
        {
            return false;
        }

        var onTeam = controller.Team is Team.T or Team.CT;
        if (!onTeam && !(controller.Team == Team.Spectator && Cfg.ShowToSpectatorTeam))
        {
            return false;
        }

        return _preferences.Get(player.SteamID) ?? EveryoneMode;
    }

    /// <summary>!hud - flips the caller between the custom HUD and center-HTML, remembered for next time. Returns the chat reply.</summary>
    public string Toggle(IPlayer player)
    {
        if (!Cfg.Enabled)
        {
            return "The custom HUD isn't enabled on this server.";
        }

        if (player.IsFakeClient || player.SteamID == 0)
        {
            return "Bots can't switch HUDs.";
        }

        var useCustom = !(_preferences.Get(player.SteamID) ?? EveryoneMode);
        _preferences.Set(player.SteamID, useCustom);

        // Takes effect on the next refresh; the prompt is gone either way now that they've chosen.
        if (_players.TryGetValue(player.Slot, out var state))
        {
            state.NextRefresh = 0f;
            state.PromptUntil = 0f;
        }

        return useCustom
            ? "Switched to the new HUD. If nothing shows up, your game doesn't have the HUD addon yet - type !hud again to switch back."
            : "Switched back to the centre-text HUD. Type !hud to use the new one.";
    }

    /// <summary>!hudstatus - one line an admin can read to tell "the server side works" apart from "clients have the addon".</summary>
    public string Status(IPlayer? caller)
    {
        if (!Cfg.Enabled)
        {
            return "Custom HUD: disabled in config (CustomHud.Enabled = false).";
        }

        var entity = _layout is { IsValid: true } layout
            ? $"entity #{layout.Index} live"
            : _createAttempts >= MaxCreateAttempts
                ? $"entity creation FAILED ({_lastCreateError ?? "no detail"})"
                : $"no entity yet (attempt {_createAttempts}/{MaxCreateAttempts})";

        var users = _core.PlayerManager.GetAllValidPlayers().Count(p => UsesCustomHud(p.Slot));
        var self = caller is { IsValid: true }
            ? $" You: {(UsesCustomHud(caller.Slot) ? "custom HUD" : "centre text")}."
            : "";

        return $"Custom HUD: {entity}, mode {Cfg.Mode}, layout {Cfg.LayoutPath}, {users} player(s) on it, {_preferences.Count} saved choice(s).{self}";
    }

    // -------------------------------------------------------------------------------------------------
    // Roll
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Plays the carousel for one player and lands it on the first of their modifiers after
    /// HudLayout.SpinSeconds, then invokes onLanded, which is the roll's own commit (ModifierRuntime's
    /// Reveal). If the HUD can't play it, the commit happens
    /// immediately instead: a modifier is never held back by the HUD.
    /// </summary>
    public void PlayRoll(int slot, IReadOnlyList<GameModifierBase> modifiers, Action? onLanded)
    {
        EnsureEntity();

        if (!Live || modifiers.Count == 0 || !IsAddressable(slot) || _core.PlayerManager.GetPlayer(slot) is not { IsValid: true })
        {
            onLanded?.Invoke();
            return;
        }

        var now = Now;
        var state = State(slot);

        // A roll that hadn't landed yet still owes its commit - pay it rather than drop it. The runtime's
        // own generation guard discards it if a newer roll superseded it.
        if (state.Roll is { Landed: false } unfinished)
        {
            InvokeSafely(unfinished.OnLanded);
        }

        FillStrip(now);

        var primary = modifiers[0];
        SetFlag(slot, state, HudLayout.Tile(HudLayout.WinTile), HudLayout.Won, false);
        SetFlag(slot, state, HudLayout.RollPanel, HudLayout.Landed, false);
        SetFlag(slot, state, HudLayout.Card, HudLayout.On, false);
        SetExclusive(slot, state, HudLayout.TileIcon(HudLayout.WinTile), "icon", HudLayout.IconClass(HudCatalog.Icon(primary)));
        SetExclusive(slot, state, HudLayout.Tile(HudLayout.WinTile), "cat", HudLayout.CategoryClass(HudCatalog.Category(primary)));

        var brand = Cfg.BrandText?.Trim() ?? "";
        SetText(slot, state, HudLayout.BrandText, HudLayout.VarBrand, brand.Length > 64 ? brand[..64] : brand);
        SetFlag(slot, state, HudLayout.RollPanel, HudLayout.Brand, brand.Length > 0);

        // Same tick as `on`, so the first frame the player sees is the fade-in's first.
        SetReelPosition(slot, state, 0f);
        SetExclusive(slot, state, HudLayout.RollPanel, "fx", HudLayout.InClass(0));
        SetFlag(slot, state, HudLayout.RollPanel, HudLayout.On, true);

        state.Roll = new RollState { Primary = primary, Count = modifiers.Count, OnLanded = onLanded, StartedAt = now };
    }

    private void AdvanceRoll(IPlayer player, PlayerState state, RollState roll, float now)
    {
        var slot = player.Slot;
        var elapsed = now - roll.StartedAt;

        // The map clock restarted under us - land it now rather than wait out a negative elapsed time.
        if (elapsed < 0f)
        {
            elapsed = HudLayout.SpinSeconds;
        }

        if (!roll.Landed)
        {
            // The server is the animation: the reel's position along the eased curve, every tick.
            SetReelPosition(slot, state, EaseProgress(Math.Clamp(elapsed / HudLayout.SpinSeconds, 0f, 1f)));
            SetExclusive(slot, state, HudLayout.RollPanel, "fx", HudLayout.InClass(Math.Min(Frame(elapsed), HudLayout.InFrames)));

            while (roll.NextTick < TickTimes.Length && elapsed >= TickTimes[roll.NextTick])
            {
                roll.NextTick++;
                if (now - roll.LastTickSoundAt >= MinTickSoundGapSeconds)
                {
                    roll.LastTickSoundAt = now;
                    CSRollUtils.PlaySoundToPlayer(player, _runtime.Config.SpinReveal.TickSoundEventName, _runtime.Config.SpinReveal.TickSoundVolume);
                }
            }

            if (elapsed < HudLayout.SpinSeconds)
            {
                return;
            }

            // Landing: the win timeline starts in the same tick as the ring, the card and `landed`.
            roll.Landed = true;
            roll.LandedAt = now;
            roll.HideAt = now + Math.Max((float)HudLayout.WinFrames / HudLayout.Fps + 0.5f, Cfg.RevealHoldSeconds);

            // Commit before drawing the card: activating is what rolls a modifier's chance or health,
            // so the card shows the real number rather than the configured range.
            var onLanded = roll.OnLanded;
            roll.OnLanded = null;
            InvokeSafely(onLanded);

            // The commit started a new roll for this player - that one owns the panels now.
            if (state.Roll != roll)
            {
                return;
            }

            ShowCard(slot, state, roll);
            SetFlag(slot, state, HudLayout.Tile(HudLayout.WinTile), HudLayout.Won, true);
            SetFlag(slot, state, HudLayout.RollPanel, HudLayout.Landed, true);
            SetWinFrame(slot, state, 0);

            // The list was held back for the spin; redraw it this tick so its rows arrive with frame 0.
            state.NextRefresh = now;
            return;
        }

        // The map clock restarted mid-hold: skip straight to the fade.
        if (now < roll.LandedAt)
        {
            roll.LandedAt = roll.HideAt = now;
        }

        if (now < roll.HideAt)
        {
            SetWinFrame(slot, state, Math.Min(Frame(now - roll.LandedAt), HudLayout.WinFrames));
            return;
        }

        var fade = Frame(now - roll.HideAt);
        if (fade < HudLayout.OutFrames)
        {
            SetExclusive(slot, state, HudLayout.RollPanel, "fx", HudLayout.OutClass(fade));
            return;
        }

        SetFlag(slot, state, HudLayout.Card, HudLayout.On, false);
        SetFlag(slot, state, HudLayout.RollPanel, HudLayout.On, false);
        state.Roll = null;
    }

    /// <summary>The landing timeline runs on the reel and the list together - the list's rows slide in on it.</summary>
    private void SetWinFrame(int slot, PlayerState state, int frame)
    {
        SetExclusive(slot, state, HudLayout.RollPanel, "fx", HudLayout.WinClass(frame));
        SetExclusive(slot, state, HudLayout.ListPanel, "fx", HudLayout.WinClass(frame));
    }

    /// <summary>Which timeline frame (HudLayout.Fps a second) a moment falls on. The nudge keeps float error from holding a frame back a tick.</summary>
    private static int Frame(float seconds) => Math.Max(0, (int)((seconds * HudLayout.Fps) + 0.01f));

    /// <summary>
    /// Moves the reel to a point (0-1) along its travel, in half-pixel steps: the strip jumps whole tiles
    /// and the track around it slides the remainder. Both land in the same tick's update, so the client
    /// never draws one without the other.
    /// </summary>
    private void SetReelPosition(int slot, PlayerState state, float progress)
    {
        var total = HudLayout.CoarseSteps * HudLayout.FineSteps;
        var steps = Math.Clamp((int)MathF.Round(progress * total), 0, total);
        SetExclusive(slot, state, HudLayout.Strip, "pos", HudLayout.CoarseClass(steps / HudLayout.FineSteps));
        SetExclusive(slot, state, HudLayout.Track, "pos", HudLayout.FineClass(steps % HudLayout.FineSteps));
    }

    private void ShowCard(int slot, PlayerState state, RollState roll)
    {
        var modifier = roll.Primary;
        var category = HudCatalog.Category(modifier);
        var label = HudCatalog.CategoryLabel(category);
        if (roll.Count > 1)
        {
            label += $"  ·  +{roll.Count - 1} more";
        }

        SetExclusive(slot, state, HudLayout.Card, "cat", HudLayout.CategoryClass(category));
        SetExclusive(slot, state, HudLayout.CardIcon, "icon", HudLayout.IconClass(HudCatalog.Icon(modifier)));
        SetText(slot, state, HudLayout.CardCategory, HudLayout.VarCategory, label);
        SetText(slot, state, HudLayout.CardName, HudLayout.VarName, CSRollUtils.GetModifierDisplayName(_core, modifier));
        SetText(slot, state, HudLayout.CardDescription, HudLayout.VarDescription, CSRollUtils.StripChatColors(CSRollUtils.GetModifierDescription(_core, modifier, slot)));
        SetFlag(slot, state, HudLayout.Card, HudLayout.On, true);
    }

    /// <summary>
    /// Fills every tile except the winner's with random icons - globally, once per wave of rolls, so a
    /// round start costs one write per tile for the whole server rather than one per player. Only the
    /// winning tile is per player.
    /// </summary>
    private void FillStrip(float now)
    {
        if (now >= _stripFilledAt && now - _stripFilledAt < 1f)
        {
            return;
        }

        _stripFilledAt = now;

        var pool = _runtime.RegisteredModifiers;
        GameModifierBase? previous = null;

        for (var i = 0; i < HudLayout.Tiles; i++)
        {
            if (i == HudLayout.WinTile || pool.Count == 0)
            {
                continue;
            }

            var pick = pool[Random.Shared.Next(pool.Count)];
            while (pool.Count > 1 && pick == previous)
            {
                pick = pool[Random.Shared.Next(pool.Count)];
            }

            previous = pick;
            SetGlobalExclusive(HudLayout.TileIcon(i), "icon", HudLayout.IconClass(HudCatalog.Icon(pick)));
            SetGlobalExclusive(HudLayout.Tile(i), "cat", HudLayout.CategoryClass(HudCatalog.Category(pick)));
        }
    }

    /// <summary>How far along the reel is (0-1) at a fraction of the spin's duration: an ease-out, 1 - (1 - t)^SpinPower.</summary>
    private static float EaseProgress(float timeFraction) =>
        1f - MathF.Pow(1f - timeFraction, HudLayout.SpinPower);

    /// <summary>
    /// For each tile boundary the marker passes between StartTile and WinTile, the moment the eased reel
    /// reaches it - EaseProgress solved for time - so every tick lands on a tile.
    /// </summary>
    private static float[] BuildTickTimes()
    {
        var crossings = HudLayout.WinTile - HudLayout.StartTile;
        var times = new float[crossings];

        for (var j = 0; j < crossings; j++)
        {
            var distance = (j + 0.5f) / crossings;
            times[j] = (1f - MathF.Pow(1f - distance, 1f / HudLayout.SpinPower)) * HudLayout.SpinSeconds;
        }

        return times;
    }

    /// <summary>The HUD is going away mid-roll (disabled, unloaded) - every roll still in flight commits now, so nobody's modifier is lost with it.</summary>
    private void LandPendingRolls()
    {
        foreach (var state in _players.Values)
        {
            if (state.Roll is { } roll)
            {
                state.Roll = null;
                InvokeSafely(roll.OnLanded);
            }
        }
    }

    private void InvokeSafely(Action? action)
    {
        if (action is null)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            _core.Logger.LogError(ex, "[CSRoll][HUD] A roll's landing callback threw.");
        }
    }

    // -------------------------------------------------------------------------------------------------
    // Refresh
    // -------------------------------------------------------------------------------------------------

    private void OnTick()
    {
        EnsureEntity();

        if (!Live)
        {
            LandPendingRolls();
            return;
        }

        var now = Now;
        foreach (var player in _core.PlayerManager.GetAllValidPlayers())
        {
            if (player.IsFakeClient || !IsAddressable(player.Slot))
            {
                continue;
            }

            var state = State(player.Slot);

            // Every tick, not on the refresh throttle: the reel position and tick sounds need the
            // precision, and gauges step 1% at a time - at ten updates a second a fast drain like
            // Jetpack fuel visibly jumps.
            if (state.Roll is { } roll)
            {
                AdvanceRoll(player, state, roll, now);
            }

            if (UsesCustomHud(player.Slot))
            {
                RefreshGauges(player.Slot, state, ResolveSubject(player));
            }

            // The "now >= NextRefresh - interval" half is the map-clock guard: a deadline from the
            // previous map sits in the future and would otherwise freeze the HUD.
            if (now < state.NextRefresh && now >= state.NextRefresh - RefreshIntervalSeconds)
            {
                continue;
            }

            state.NextRefresh = now + RefreshIntervalSeconds;
            Refresh(player, state, now);
        }
    }

    private void Refresh(IPlayer player, PlayerState state, float now)
    {
        var slot = player.Slot;
        var usesHud = UsesCustomHud(slot);

        SetFlag(slot, state, HudLayout.HudPanel, HudLayout.On, usesHud);
        SetFlag(slot, state, HudLayout.PromptPanel, HudLayout.On, !usesHud && ShouldPrompt(player, state, now));

        if (!usesHud)
        {
            return;
        }

        var subject = ResolveSubject(player);
        var title = subject.Slot == slot ? "Modifiers" : $"{DisplayName(subject)}'s Modifiers";
        var modifiers = RevealPending(slot, state, subject) ? [] : _runtime.GetModifiersForSlot(subject.Slot);

        // Outside a landing the list rests on the last win frame, which has no rules - so a landing cut
        // short (a new roll, the round restarting) can't leave rows stuck half-faded.
        if (state.Roll is not { Landed: true } landing || now >= landing.HideAt)
        {
            SetExclusive(slot, state, HudLayout.ListPanel, "fx", HudLayout.WinClass(HudLayout.WinFrames));
        }

        // While watching someone the title stays up even when they have nothing - "Rex's Modifiers"
        // over an empty list says more than a list that silently vanished.
        SetFlag(slot, state, HudLayout.ListPanel, HudLayout.On, modifiers.Count > 0 || subject.Slot != slot);
        SetText(slot, state, HudLayout.ListTitle, HudLayout.VarTitle, title);

        for (var i = 0; i < HudLayout.Rows; i++)
        {
            if (i >= modifiers.Count)
            {
                SetFlag(slot, state, HudLayout.Row(i), HudLayout.On, false);
                continue;
            }

            var modifier = modifiers[i];
            SetExclusive(slot, state, HudLayout.Row(i), "cat", HudLayout.CategoryClass(HudCatalog.Category(modifier)));
            SetExclusive(slot, state, HudLayout.RowIcon(i), "icon", HudLayout.IconClass(HudCatalog.Icon(modifier)));
            SetText(slot, state, HudLayout.RowName(i), HudLayout.VarName, CSRollUtils.GetModifierDisplayName(_core, modifier));
            SetText(slot, state, HudLayout.RowShort(i), HudLayout.VarShort, CSRollUtils.GetModifierShortDescription(_core, modifier, subject.Slot));
            SetFlag(slot, state, HudLayout.Row(i), HudLayout.On, true);
        }

    }

    private void RefreshGauges(int slot, PlayerState state, IPlayer subject)
    {
        var gauges = RevealPending(slot, state, subject) ? [] : _runtime.GetHudGauges(subject.Slot);
        for (var i = 0; i < HudLayout.Gauges; i++)
        {
            if (i >= gauges.Count)
            {
                SetFlag(slot, state, HudLayout.Gauge(i), HudLayout.On, false);
                continue;
            }

            var (owner, gauge) = gauges[i];
            var step = (int)MathF.Round(Math.Clamp(gauge.Fill, 0f, 1f) * HudLayout.FillSteps);
            SetExclusive(slot, state, HudLayout.Gauge(i), "cat", HudLayout.CategoryClass(HudCatalog.Category(owner)));
            SetExclusive(slot, state, HudLayout.GaugeIcon(i), "icon", HudLayout.IconClass(HudCatalog.Icon(owner)));
            SetText(slot, state, HudLayout.GaugeLabel(i), HudLayout.VarLabel, gauge.Label);
            SetText(slot, state, HudLayout.GaugeValue(i), HudLayout.VarValue, gauge.Value);
            SetExclusive(slot, state, HudLayout.Gauge(i), "fill", HudLayout.FillClass(step));
            SetFlag(slot, state, HudLayout.Gauge(i), HudLayout.Ready, gauge.Ready);
            SetFlag(slot, state, HudLayout.Gauge(i), HudLayout.On, true);
        }
    }

    /// <summary>
    /// Your own list and gauges wait for your reel to land. A global roll commits on the centre-text
    /// spin's schedule, which finishes before the carousel, and the list would give the result away.
    /// </summary>
    private static bool RevealPending(int slot, PlayerState state, IPlayer subject) =>
        subject.Slot == slot && state.Roll is { Landed: false };

    /// <summary>The prompt shows only to players who haven't chosen, for PromptSeconds after each spawn. Players without the addon get the write too, but have no layout to draw it with - which is the whole point.</summary>
    private bool ShouldPrompt(IPlayer player, PlayerState state, float now) =>
        !EveryoneMode &&
        _preferences.Get(player.SteamID) is null &&
        now < state.PromptUntil &&
        now + Cfg.PromptSeconds >= state.PromptUntil;

    /// <summary>Whose modifiers the list shows: your own, or - while dead and watching someone - theirs.</summary>
    private IPlayer ResolveSubject(IPlayer viewer)
    {
        if (viewer.IsAlive)
        {
            return viewer;
        }

        if (viewer.Pawn?.ObserverServices?.ObserverTarget.Value is { IsValid: true } target &&
            _core.PlayerManager.GetPlayerFromPawn(target.As<CBasePlayerPawn>()) is { IsValid: true } watched)
        {
            return watched;
        }

        return viewer;
    }

    private static string DisplayName(IPlayer player) =>
        player.Controller is { IsValid: true } controller ? controller.PlayerName : player.Name;

    // -------------------------------------------------------------------------------------------------
    // Entity
    // -------------------------------------------------------------------------------------------------

    private void EnsureEntity()
    {
        if (!_installed)
        {
            return;
        }

        if (!Cfg.Enabled)
        {
            // Switched off by a config reload while live.
            if (_layout is not null)
            {
                LandPendingRolls();
                DespawnLayout();
            }

            return;
        }

        if (_layout is { IsValid: true })
        {
            return;
        }

        if (_layout is not null)
        {
            // Gone without us (round restart, map change). Retry at once rather than wait out the
            // interval - a roll may be about to start.
            _layout = null;
            ResetEntityState();
            _nextCreateAt = 0f;
        }

        if (_createAttempts >= MaxCreateAttempts)
        {
            return;
        }

        var now = Now;
        if (now < _nextCreateAt && now >= _nextCreateAt - CreateRetrySeconds)
        {
            return;
        }

        _nextCreateAt = now + CreateRetrySeconds;
        _createAttempts++;

        try
        {
            var entity = _core.EntitySystem.CreateEntity<CCSCustomHudLayout>();
            entity.StrLayout = Cfg.LayoutPath;
            entity.StrLayoutUpdated();
            entity.DispatchSpawn();

            _layout = entity;
            _createAttempts = 0;
            _lastCreateError = null;
            ResetEntityState();

            var offset = Math.Clamp(Cfg.ListOffset, 0, HudLayout.ListOffsets - 1);
            if (offset > 0)
            {
                Guard(HudLayout.ListPanel, () => entity.SetHasClass(HudLayout.ListPanel, HudLayout.ListOffsetClass(offset), HasClass));
            }

            _core.Logger.LogInformation("[CSRoll][HUD] Spawned {DesignerName} #{Index} with layout {Layout}.", DesignerName, entity.Index, Cfg.LayoutPath);
        }
        catch (Exception ex)
        {
            _lastCreateError = ex.Message;
            if (_createAttempts >= MaxCreateAttempts)
            {
                _core.Logger.LogWarning(ex, "[CSRoll][HUD] Gave up creating {DesignerName} after {Attempts} attempts - everyone stays on centre text this map.", DesignerName, _createAttempts);
            }
        }
    }

    /// <summary>A new entity starts blank, so every cache describing the old one has to go - otherwise the "unchanged, skip" check would never resend anything to it.</summary>
    private void ResetEntityState()
    {
        foreach (var state in _players.Values)
        {
            state.Text.Clear();
            state.Exclusive.Clear();
            state.Flags.Clear();
            state.NextRefresh = 0f;
        }

        _globalExclusive.Clear();
        _stripFilledAt = float.MinValue;
        _playerCap = null;
    }

    private void SweepOrphans()
    {
        try
        {
            foreach (var existing in _core.EntitySystem.GetAllEntitiesByDesignerName<CCSCustomHudLayout>(DesignerName))
            {
                if (existing is { IsValid: true } && existing.StrLayout == Cfg.LayoutPath)
                {
                    _core.Logger.LogWarning("[CSRoll][HUD] Removing {DesignerName} #{Index} left over from a previous load.", DesignerName, existing.Index);
                    existing.Despawn();
                }
            }
        }
        catch (Exception ex)
        {
            // No map yet (cold boot) is the normal case here, not an error.
            _core.Logger.LogDebug(ex, "[CSRoll][HUD] Orphan sweep skipped.");
        }
    }

    private void DespawnLayout()
    {
        if (_layout is { IsValid: true } layout)
        {
            try
            {
                layout.Despawn();
            }
            catch (Exception ex)
            {
                _core.Logger.LogWarning(ex, "[CSRoll][HUD] Failed to despawn the layout entity.");
            }
        }

        _layout = null;
        ResetEntityState();
    }

    private void OnMapLoad(IOnMapLoadEvent @event)
    {
        // The entity died with the old map, and GlobalVars.CurrentTime restarts near zero - drop every
        // timestamp along with it.
        _layout = null;
        _players.Clear();
        ResetEntityState();
        _createAttempts = 0;
        _nextCreateAt = 0f;
    }

    private void OnMapUnload(IOnMapUnloadEvent @event)
    {
        _layout = null;
        _players.Clear();
        ResetEntityState();
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true, IsFakeClient: false } player && IsAddressable(player.Slot))
        {
            var state = State(player.Slot);
            state.PromptUntil = Now + Math.Max(0f, Cfg.PromptSeconds);

            // Opt-out can't tell who has the addon, and a player without it would see no reveal at all -
            // so everyone who hasn't chosen is told once how to get the classic display back.
            if (Live && EveryoneMode && !state.OptOutHintShown && _preferences.Get(player.SteamID) is null)
            {
                state.OptOutHintShown = true;
                CSRollUtils.PrintTitleToChatColored(_core, player, "Modifiers now show on the new HUD. Can't see it? Type [green]!hud[default] for the classic display.");
            }
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// Slots are recycled, and the entity keeps per-player state by slot - the next player into this
    /// one would inherit it. Every override this player held is withdrawn before the slot is reused.
    /// </summary>
    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        var slot = @event.PlayerId;
        if (!_players.Remove(slot, out var state))
        {
            return;
        }

        if (_layout is not { IsValid: true } layout || !IsAddressable(slot))
        {
            return;
        }

        foreach (var (panel, variable) in state.Text.Keys)
        {
            Guard(panel, () => layout.RemoveDialogVariableStringForPlayer(slot, panel, variable));
        }

        foreach (var ((panel, _), cls) in state.Exclusive)
        {
            Guard(panel, () => layout.SetHasClassForPlayer(slot, panel, cls, Unset));
        }

        foreach (var (panel, cls) in state.Flags)
        {
            Guard(panel, () => layout.SetHasClassForPlayer(slot, panel, cls, Unset));
        }
    }

    // -------------------------------------------------------------------------------------------------
    // Writes
    // -------------------------------------------------------------------------------------------------

    private PlayerState State(int slot)
    {
        if (!_players.TryGetValue(slot, out var state))
        {
            state = new PlayerState();
            _players[slot] = state;
        }

        return state;
    }

    private void SetText(int slot, PlayerState state, string panel, string variable, string value)
    {
        if (state.Text.TryGetValue((panel, variable), out var current) && current == value)
        {
            return;
        }

        state.Text[(panel, variable)] = value;
        Guard(panel, () => _layout!.SetDialogVariableStringForPlayer(slot, panel, variable, value));
    }

    private void SetFlag(int slot, PlayerState state, string panel, string cls, bool on)
    {
        var key = (panel, cls);
        if (on == state.Flags.Contains(key))
        {
            return;
        }

        if (on)
        {
            state.Flags.Add(key);
        }
        else
        {
            state.Flags.Remove(key);
        }

        Guard(panel, () => _layout!.SetHasClassForPlayer(slot, panel, cls, on ? HasClass : Unset));
    }

    /// <summary>One class out of a group on a panel (an icon, a category, a fill step): the previous one is withdrawn and the new one set, in the same tick - different names, so both changes reach the client.</summary>
    private void SetExclusive(int slot, PlayerState state, string panel, string group, string cls)
    {
        var key = (panel, group);
        if (state.Exclusive.TryGetValue(key, out var current))
        {
            if (current == cls)
            {
                return;
            }

            Guard(panel, () => _layout!.SetHasClassForPlayer(slot, panel, current, Unset));
        }

        state.Exclusive[key] = cls;
        Guard(panel, () => _layout!.SetHasClassForPlayer(slot, panel, cls, HasClass));
    }

    private void SetGlobalExclusive(string panel, string group, string cls)
    {
        var key = (panel, group);
        if (_globalExclusive.TryGetValue(key, out var current))
        {
            if (current == cls)
            {
                return;
            }

            Guard(panel, () => _layout!.SetHasClass(panel, current, Unset));
        }

        _globalExclusive[key] = cls;
        Guard(panel, () => _layout!.SetHasClass(panel, cls, HasClass));
    }

    /// <summary>
    /// Whether a per-player id is one the engine can index. Every ...ForPlayer call indexes a fixed
    /// player array in native code; an out-of-range id is an out-of-bounds write in the game server,
    /// not an exception. Cached per entity - PlayerCap is an engine constant for the map.
    /// </summary>
    private bool IsAddressable(int slot)
    {
        _playerCap ??= _core.PlayerManager.PlayerCap;
        return slot >= 0 && slot < _playerCap;
    }

    private void Guard(string panel, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _core.Logger.LogWarning(ex, "[CSRoll][HUD] HUD write failed for panel {Panel}.", panel);
        }
    }
}
