using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Config;

namespace CSRoll.Hud;

/// <summary>
/// Owns the <c>custom_hud_layout</c> entity and is the only code in this plugin that touches it.
///
/// See <see cref="ICSRollHudService"/> for the rationale behind that containment and for the
/// main-thread precondition on every method here.
/// </summary>
public sealed class CSRollHudService : ICSRollHudService
{
    private const string DesignerName = "custom_hud_layout";

    /// <summary>
    /// Scope key for values set globally rather than per-player. Player slots are always &gt;= 0, so -1
    /// can never collide with one.
    /// </summary>
    private const int GlobalScope = -1;

    /// <summary>
    /// How many consecutive creation failures before the service stops trying for the rest of the map.
    /// Without a circuit breaker a misconfigured layout path would retry forever, once per interval,
    /// logging on every attempt.
    /// </summary>
    private const int MaxCreateAttempts = 5;

    /// <summary>
    /// How far a recomputed deadline may drift from a running one before it is treated as a NEW
    /// countdown rather than the same one seen again.
    ///
    /// Comfortably above tick jitter and float error - an unchanged cooldown re-derives to within a
    /// few milliseconds of itself - and far below the smallest gap between two genuinely different
    /// cooldowns, which differ by whole seconds.
    /// </summary>
    private const float DeadlineResyncToleranceSeconds = 0.35f;

    /// <summary>
    /// How often a running bar is stepped, in seconds.
    ///
    /// Ten times a second against a 1% ladder is what makes a cooldown read as moving rather than
    /// ticking. Faster buys nothing visible; slower starts to show as steps on a short cooldown.
    /// </summary>
    private const float BarStepIntervalSeconds = 0.1f;

    private readonly ISwiftlyCore _core;

    private CSRollConfig _config;
    private CCSCustomHudLayout? _layout;

    private bool _installed;
    private int _createAttempts;
    private bool _creationAbandoned;
    private float _nextCreateAttemptAt;
    private string? _lastCreateError;

    private float _lastCountdownPumpTime;

    // Dirty-state caches. These are not an optimisation - the tracker runs off OnTick, so without them
    // every panel of every player would be rewritten across the network several times a second even
    // when nothing changed. With them, a player staring at a static tracker costs zero writes.
    private readonly Dictionary<(int Scope, string Panel, string Variable), string> _textState = [];
    private readonly Dictionary<(int Scope, string Panel, string Class), bool> _classState = [];
    private readonly Dictionary<(int Scope, string Panel, string Group), string> _groupState = [];
    private readonly Dictionary<(int Slot, string FillA), int> _barStarts = [];
    private readonly Dictionary<(int Slot, string FillA), float> _barEndsAt = [];
    private readonly Dictionary<(int Slot, string Panel, string Variable), float> _countdowns = [];

    /// <summary>
    /// Bars currently counting down: key -> (the bar, when it ends, how long it ran for).
    ///
    /// Stepped by <see cref="Pump"/> rather than handed to a CSS transition. See the fill ladder in
    /// csroll_hud.css for why - transitions driven by server class writes are not dependable here.
    /// </summary>
    private readonly Dictionary<(int Slot, string FillA), (HudBar Bar, float EndsAt, float Total, bool FillUp)> _barRuns = [];

    private float _lastBarPumpTime;

    /// <summary>Slot -> when its notice should disappear. Cleared by the same pump that drives countdowns.</summary>
    private readonly Dictionary<int, float> _noticeUntil = [];

    public CSRollHudService(ISwiftlyCore core, CSRollConfig config)
    {
        _core = core;
        _config = config;
    }

    private CustomHudConfig Cfg => _config.CustomHud;

    public bool Available => Cfg.Enabled && _layout is { IsValid: true };

    // -------------------------------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------------------------------

    public void Install()
    {
        // Load() in this plugin is documented to sometimes run twice for one logical load and self-heals
        // by calling Unload() first, but Install() must survive being called twice regardless - two live
        // layout entities would render two overlapping copies of the entire HUD.
        if (_installed)
        {
            Uninstall();
        }

        if (!Cfg.Enabled)
        {
            _core.Logger.LogInformation("[CSRoll][HUD] Custom HUD is disabled in config - no entity will be created and all HUD code paths stay inert.");
            return;
        }

        _installed = true;

        _core.Logger.LogInformation(
            "[CSRoll][HUD] Custom HUD enabled (layout={Layout}). This requires the CSRoll HUD Workshop addon to be mounted on the server AND downloaded by players - see tools/HUD_SETUP.md. Without it players see nothing from the HUD.",
            Cfg.LayoutPath);

        SweepOrphans();

        _core.Event.OnMapLoad += OnMapLoad;
        _core.Event.OnMapUnload += OnMapUnload;
        _core.Event.OnEntityDeleted += OnEntityDeleted;
        _core.Event.OnClientDisconnected += OnClientDisconnected;
        _core.Event.OnTick += Pump;

        // Covers a mid-map hot reload, where the entity system is already live and no OnMapLoad is
        // coming. On a cold server boot this attempt fails harmlessly and the OnMapLoad path takes over.
        _core.Scheduler.DelayBySeconds(1f, EnsureEntity);
    }

    public void Uninstall()
    {
        if (!_installed)
        {
            return;
        }

        _installed = false;

        // Unsubscribe before despawning, or Pump could recreate the entity mid-teardown.
        _core.Event.OnMapLoad -= OnMapLoad;
        _core.Event.OnMapUnload -= OnMapUnload;
        _core.Event.OnEntityDeleted -= OnEntityDeleted;
        _core.Event.OnClientDisconnected -= OnClientDisconnected;
        _core.Event.OnTick -= Pump;

        ResetAll();
        DespawnLayout();
    }

    public void OnConfigReloaded(CSRollConfig config)
    {
        var wasEnabled = Cfg.Enabled;
        _config = config;

        if (wasEnabled == Cfg.Enabled)
        {
            return;
        }

        // Toggling the master switch at runtime has to actually take effect, in both directions.
        if (Cfg.Enabled)
        {
            Install();
        }
        else
        {
            Uninstall();
        }
    }

    public string DescribeStatus()
    {
        if (!Cfg.Enabled)
        {
            return "disabled in config (CustomHud.Enabled = false)";
        }

        if (_creationAbandoned)
        {
            return $"ENTITY CREATION FAILED after {MaxCreateAttempts} attempts - {_lastCreateError ?? "no detail"}; layout={Cfg.LayoutPath}";
        }

        if (_layout is not { IsValid: true } layout)
        {
            return $"enabled, but no live layout entity right now (attempt {_createAttempts}/{MaxCreateAttempts}); layout={Cfg.LayoutPath}";
        }

        return $"live: entity #{layout.Index}, layout={Cfg.LayoutPath}, replaceCenterHtml={Cfg.ReplaceCenterHtml}, tracker={Cfg.ShowTracker}. " +
               "A live entity does NOT prove clients have the Workshop addon - confirm visually in-game.";
    }

    // -------------------------------------------------------------------------------------------------
    // Entity management
    // -------------------------------------------------------------------------------------------------

    private void OnMapLoad(IOnMapLoadEvent @event)
    {
        // The old entity died with the old map. Drop every cached value with it: GlobalVars.CurrentTime
        // is map-relative and restarts near zero, so timestamps carried across would sit far in the
        // future and freeze every throttle and countdown for the rest of the session.
        ResetAll();
        _layout = null;
        _createAttempts = 0;
        _creationAbandoned = false;
        _lastCreateError = null;

        _core.Scheduler.DelayBySeconds(1f, EnsureEntity);
    }

    private void OnMapUnload(IOnMapUnloadEvent @event)
    {
        ResetAll();
        _layout = null;
    }

    private void OnEntityDeleted(IOnEntityDeletedEvent @event)
    {
        if (_layout is { IsValid: true } layout && @event.Entity.Index == layout.Index)
        {
            _layout = null;
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
        => ResetPlayer(@event.PlayerId);

    /// <summary>
    /// Creates the layout entity if it is missing, rate-limited and eventually giving up.
    ///
    /// Called both from a scheduled delay after map load and from every <see cref="Pump"/> tick, because
    /// neither alone is enough: OnEntityDeleted catches an explicit kill, but an entity can also be
    /// invalidated without one, and the scheduled attempt can land before the entity system is ready.
    /// </summary>
    private void EnsureEntity()
    {
        if (!_installed || !Cfg.Enabled || _creationAbandoned || _layout is { IsValid: true })
        {
            return;
        }

        var now = _core.Engine.GlobalVars.CurrentTime;

        // The "now >= _nextCreateAttemptAt" half is the map-change clock guard used throughout this
        // codebase: a stamp from the previous map sits in the future and would otherwise gate retries
        // off permanently.
        if (now < _nextCreateAttemptAt && now >= _nextCreateAttemptAt - Cfg.EntityRetryIntervalSeconds)
        {
            return;
        }

        _nextCreateAttemptAt = now + Math.Max(0.5f, Cfg.EntityRetryIntervalSeconds);
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

            _core.Logger.LogInformation(
                "[CSRoll][HUD] Spawned {DesignerName} #{Index} with layout {Layout}.",
                DesignerName, entity.Index, Cfg.LayoutPath);

            OnLayoutReady();
        }
        catch (Exception ex)
        {
            _lastCreateError = ex.Message;

            if (_createAttempts >= MaxCreateAttempts)
            {
                _creationAbandoned = true;
                _core.Logger.LogWarning(
                    ex,
                    "[CSRoll][HUD] Giving up on creating {DesignerName} after {Attempts} attempts. The custom HUD stays unavailable for this map; center-HTML output is unaffected.",
                    DesignerName, _createAttempts);
            }
        }
    }

    /// <summary>Writes the state that is constant for the life of an entity, once, on creation.</summary>
    private void OnLayoutReady()
    {
        SetText(HudPanelIds.Version, HudPanelIds.VarName, Cfg.VersionStamp);
        SetText(HudPanelIds.TrackTitle, HudPanelIds.VarName, "ACTIVE MODIFIERS");
        Show(HudPanelIds.Spin, false);
        Show(HudPanelIds.Reveal, false);
        Show(HudPanelIds.Self, false);
        Show(HudPanelIds.Spectator, false);
        Show(HudPanelIds.Track, Cfg.ShowTracker);
    }

    /// <summary>
    /// Kills any layout entity of ours left behind by a previous load.
    ///
    /// Two stacked layouts render two overlapping copies of the whole HUD, and the plugin documents
    /// Load() firing twice for a single logical load as a real, observed occurrence - so this is not
    /// paranoia. Only entities pointing at OUR configured layout are touched, so a custom workshop map
    /// that spawns its own custom_hud_layout is left strictly alone.
    /// </summary>
    private void SweepOrphans()
    {
        try
        {
            foreach (var existing in _core.EntitySystem.GetAllEntitiesByDesignerName<CCSCustomHudLayout>(DesignerName))
            {
                if (existing is { IsValid: true } && existing.StrLayout == Cfg.LayoutPath)
                {
                    _core.Logger.LogWarning("[CSRoll][HUD] Removing orphaned {DesignerName} #{Index} left over from a previous load.", DesignerName, existing.Index);
                    existing.Despawn();
                }
            }
        }
        catch (Exception ex)
        {
            // Entity system not ready yet (cold boot) is the normal case here, not an error.
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
    }

    // -------------------------------------------------------------------------------------------------
    // Pump
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// One OnTick loop for the whole service: keeps the entity alive and advances every countdown.
    ///
    /// Countdowns live here rather than each growing its own scheduler chain so that they share a single
    /// throttle and are all cancelled at once by a map change or a player disconnect.
    /// </summary>
    private void Pump()
    {
        EnsureEntity();

        if (!Available)
        {
            return;
        }

        ExpireNotices();
        StepBars();

        if (_countdowns.Count == 0)
        {
            return;
        }

        var now = _core.Engine.GlobalVars.CurrentTime;

        // Same throttle idiom as ModifierRuntime: the "now >= last" half is the map-change clock guard.
        if (now >= _lastCountdownPumpTime && now - _lastCountdownPumpTime < Cfg.CountdownRefreshIntervalSeconds)
        {
            return;
        }

        _lastCountdownPumpTime = now;

        foreach (var (key, endsAt) in _countdowns.ToList())
        {
            var remaining = endsAt - now;

            // A countdown whose end is further away than when it was set means the map clock restarted
            // underneath it - drop it rather than display a nonsense value for the rest of the session.
            if (remaining < 0f)
            {
                remaining = 0f;
            }

            SetTextFor(key.Slot, key.Panel, key.Variable, FormatCountdown(remaining));

            if (remaining <= 0f)
            {
                _countdowns.Remove(key);
            }
        }
    }

    /// <summary>
    /// Advances every running bar one step.
    ///
    /// This is the animation. Stepping at 1% granularity ten times a second reads as continuous
    /// movement, and dirty tracking means a write only leaves the server when the bucket changes - so a
    /// 20 second cooldown costs about a hundred writes spread over its whole life, not one per tick.
    /// </summary>
    private void StepBars()
    {
        if (_barRuns.Count == 0)
        {
            return;
        }

        var now = _core.Engine.GlobalVars.CurrentTime;

        // Same throttle idiom as everywhere else; the "now >= last" half guards a restarted map clock.
        if (now >= _lastBarPumpTime && now - _lastBarPumpTime < BarStepIntervalSeconds)
        {
            return;
        }

        _lastBarPumpTime = now;

        foreach (var (key, run) in _barRuns.ToList())
        {
            var remaining = run.EndsAt - now;

            // A deadline further out than the run was ever long means the map clock restarted beneath
            // it - drop the bar rather than leave it stuck full for the rest of the session.
            if (remaining > run.Total + 1f)
            {
                _barRuns.Remove(key);
                continue;
            }

            if (remaining <= 0f)
            {
                // A cooldown that has finished sits FULL - it is ready. A duration that has finished sits
                // empty - it is over.
                SetClassGroupFor(key.Slot, run.Bar.FillA, HudClasses.GroupWidth, HudClasses.Width(run.FillUp ? 1f : 0f));
                _barRuns.Remove(key);
                continue;
            }

            var elapsed = 1f - (remaining / run.Total);
            SetClassGroupFor(key.Slot, run.Bar.FillA, HudClasses.GroupWidth,
                             HudClasses.Width(run.FillUp ? elapsed : remaining / run.Total));
        }
    }

    /// <summary>Retires notices whose time is up. Runs every tick; the dictionary is almost always empty.</summary>
    private void ExpireNotices()
    {
        if (_noticeUntil.Count == 0)
        {
            return;
        }

        var now = _core.Engine.GlobalVars.CurrentTime;

        foreach (var (slot, until) in _noticeUntil.ToList())
        {
            // "now < until - lifetime" would be a stale deadline from the previous map, whose clock ran
            // higher - same guard as everywhere else in this file.
            if (now >= until || now < until - 60f)
            {
                ClearNoticeFor(slot);
            }
        }
    }

    /// <summary>
    /// Whole seconds while there is time to spare, one decimal in the last few, then a word.
    ///
    /// The point is the interaction with dirty tracking: at whole-second granularity a 20-second cooldown
    /// emits about 20 network writes over its whole life instead of one per pump tick. The comma decimal
    /// separator matches how every other timer in this plugin is already rendered.
    /// </summary>
    private static string FormatCountdown(float remaining)
        => remaining <= 0f ? "READY"
         : remaining > 5f ? $"{(int)Math.Ceiling(remaining)}s"
         : $"{remaining:0.0}s".Replace('.', ',');

    // -------------------------------------------------------------------------------------------------
    // Text
    // -------------------------------------------------------------------------------------------------

    public void SetText(string panelId, string variable, string value)
    {
        if (!Available || !TrySetDirty(_textState, (GlobalScope, panelId, variable), value))
        {
            return;
        }

        Guard(panelId, () => _layout!.SetDialogVariableString(panelId, variable, value));
    }

    public void SetTextFor(int slot, string panelId, string variable, string value)
    {
        if (!Available || !TrySetDirty(_textState, (slot, panelId, variable), value))
        {
            return;
        }

        Guard(panelId, () => _layout!.SetDialogVariableStringForPlayer(slot, panelId, variable, value));
    }

    // -------------------------------------------------------------------------------------------------
    // Classes
    // -------------------------------------------------------------------------------------------------

    public void SetClass(string panelId, string className, bool on)
    {
        if (!Available || !TrySetDirty(_classState, (GlobalScope, panelId, className), on))
        {
            return;
        }

        var status = on
            ? EHudPanelClassStatus_t.k_eHudPanelClassStatus_HasClass
            : EHudPanelClassStatus_t.k_eHudPanelClassStatus_DoesNotHaveClass;

        Guard(panelId, () => _layout!.SetHasClass(panelId, className, status));
    }

    public void SetClassFor(int slot, string panelId, string className, bool on)
    {
        if (!Available || !TrySetDirty(_classState, (slot, panelId, className), on))
        {
            return;
        }

        var status = on
            ? EHudPanelClassStatus_t.k_eHudPanelClassStatus_HasClass
            : EHudPanelClassStatus_t.k_eHudPanelClassStatus_DoesNotHaveClass;

        Guard(panelId, () => _layout!.SetHasClassForPlayer(slot, panelId, className, status));
    }

    public void SetClassGroup(string panelId, string groupKey, string? className)
        => SetClassGroupScoped(GlobalScope, panelId, groupKey, className);

    public void SetClassGroupFor(int slot, string panelId, string groupKey, string? className)
        => SetClassGroupScoped(slot, panelId, groupKey, className);

    private void SetClassGroupScoped(int scope, string panelId, string groupKey, string? className)
    {
        if (!Available)
        {
            return;
        }

        var key = (scope, panelId, groupKey);
        _groupState.TryGetValue(key, out var previous);

        if (previous == className)
        {
            return;
        }

        // Remove only the member that was actually applied. Clearing the whole group instead would mean
        // 47 network writes to change one icon.
        if (!string.IsNullOrEmpty(previous))
        {
            ApplyClass(scope, panelId, previous, on: false);
        }

        if (!string.IsNullOrEmpty(className))
        {
            ApplyClass(scope, panelId, className, on: true);
            _groupState[key] = className;
        }
        else
        {
            _groupState.Remove(key);
        }
    }

    private void ApplyClass(int scope, string panelId, string className, bool on)
    {
        if (scope == GlobalScope)
        {
            SetClass(panelId, className, on);
        }
        else
        {
            SetClassFor(scope, panelId, className, on);
        }
    }

    // -------------------------------------------------------------------------------------------------
    // Visibility
    // -------------------------------------------------------------------------------------------------

    public void Show(string panelId, bool visible)
        => SetClass(panelId, HudClasses.Hidden, !visible);

    public void ShowFor(int slot, string panelId, bool visible)
        => SetClassFor(slot, panelId, HudClasses.Hidden, !visible);

    // -------------------------------------------------------------------------------------------------
    // Bars
    // -------------------------------------------------------------------------------------------------

    public void StartBarFor(int slot, in HudBar bar, float seconds, bool fillUp = false)
    {
        if (!Available)
        {
            return;
        }

        var key = (slot, bar.FillA);
        var total = Math.Max(0.01f, seconds);

        ShowFor(slot, bar.FillB, false);
        ShowFor(slot, bar.FillA, true);

        // `drain` is a transition target and this bar is not driven by one; clearing it makes the width
        // class the only thing deciding how full the bar looks.
        SetClassFor(slot, bar.FillA, HudClasses.Drain, false);
        SetClassGroupFor(slot, bar.FillA, HudClasses.GroupDuration, HudClasses.DurationInstant);
        SetClassGroupFor(slot, bar.FillA, HudClasses.GroupWidth, HudClasses.Width(fillUp ? 0f : 1f));

        _barRuns[key] = (bar, _core.Engine.GlobalVars.CurrentTime + total, total, fillUp);
        _barEndsAt[key] = _core.Engine.GlobalVars.CurrentTime + total;
        _barStarts[key] = _barStarts.GetValueOrDefault(key) + 1;
    }

    /// <summary>
    /// Starts a countdown bar only if it is not already running towards the same deadline.
    ///
    /// This exists because the tracker re-reads every modifier's state several times a second, and a
    /// bar is not a value you can idempotently re-assert: calling <see cref="StartBarFor"/> on each
    /// refresh would restart the transition four times a second and the bar would never visibly move.
    ///
    /// A still-running countdown is recognised by its deadline rather than its remaining time - as the
    /// remaining time ticks down, "now + remaining" stays put, so an unchanged cooldown lands within a
    /// hair of the stored value while a genuinely new one jumps by whole seconds.
    /// </summary>
    public void SyncBarFor(int slot, in HudBar bar, float secondsRemaining, bool fillUp = false)
    {
        if (!Available)
        {
            return;
        }

        var endsAt = _core.Engine.GlobalVars.CurrentTime + Math.Max(0f, secondsRemaining);

        if (_barEndsAt.TryGetValue((slot, bar.FillA), out var running) &&
            Math.Abs(running - endsAt) <= DeadlineResyncToleranceSeconds)
        {
            return;
        }

        StartBarFor(slot, bar, secondsRemaining, fillUp);
    }

    /// <summary>
    /// Countdown text and its bar together, with the same "is this the same countdown?" guard as
    /// <see cref="SyncBarFor"/>. The refresh-safe call the tracker uses.
    /// </summary>
    public void SyncCountdownFor(int slot, string panelId, string variable, in HudBar bar, float secondsRemaining)
    {
        if (!Available)
        {
            return;
        }

        var endsAt = _core.Engine.GlobalVars.CurrentTime + Math.Max(0f, secondsRemaining);
        var key = (slot, panelId, variable);

        if (!_countdowns.TryGetValue(key, out var running) ||
            Math.Abs(running - endsAt) > DeadlineResyncToleranceSeconds)
        {
            _countdowns[key] = endsAt;
            SetTextFor(slot, panelId, variable, FormatCountdown(secondsRemaining));
        }

        SyncBarFor(slot, bar, secondsRemaining);
    }

    public void SetBarFor(int slot, in HudBar bar, float fraction)
    {
        if (!Available)
        {
            return;
        }

        // A gauge is not a countdown - it moves in both directions - so it uses the quantised fill
        // ladder rather than a transition, and only emits when the 5% bucket actually changes.
        //
        // ORDER MATTERS. The duration has to drop to instant BEFORE the drain target is cleared,
        // otherwise a bar arriving here straight off a countdown still carries that countdown's
        // dur-N, and clearing drain animates it slowly back to full - which reads as a bar still
        // filling up long after the cooldown it represents has finished.
        ShowFor(slot, bar.FillA, true);
        ShowFor(slot, bar.FillB, false);
        SetClassGroupFor(slot, bar.FillA, HudClasses.GroupDuration, HudClasses.DurationInstant);
        SetClassFor(slot, bar.FillA, HudClasses.Drain, false);
        SetClassGroupFor(slot, bar.FillA, HudClasses.GroupWidth, HudClasses.Width(fraction));

        // The other fill may still be mid-transition from a countdown that just ended; park it.
        SetClassGroupFor(slot, bar.FillB, HudClasses.GroupDuration, HudClasses.DurationInstant);
        SetClassFor(slot, bar.FillB, HudClasses.Drain, false);

        // A gauge is not a countdown, so nothing is "running" any more - drop the deadline, or a later
        // countdown that happened to end at the same moment would be mistaken for one already animating.
        _barEndsAt.Remove((slot, bar.FillA));
        _barRuns.Remove((slot, bar.FillA));
    }

    public void StopBarFor(int slot, in HudBar bar)
    {
        if (!Available)
        {
            return;
        }

        ShowFor(slot, bar.FillA, false);
        ShowFor(slot, bar.FillB, false);
        SetClassFor(slot, bar.FillA, HudClasses.Drain, false);
        SetClassFor(slot, bar.FillB, HudClasses.Drain, false);
        SetClassGroupFor(slot, bar.FillA, HudClasses.GroupWidth, null);
        _barStarts.Remove((slot, bar.FillA));
        _barEndsAt.Remove((slot, bar.FillA));
        _barRuns.Remove((slot, bar.FillA));
    }

    // -------------------------------------------------------------------------------------------------
    // Countdowns
    // -------------------------------------------------------------------------------------------------

    public void StartCountdownFor(int slot, string panelId, string variable, float seconds)
    {
        if (!Available)
        {
            return;
        }

        _countdowns[(slot, panelId, variable)] = _core.Engine.GlobalVars.CurrentTime + Math.Max(0f, seconds);
        SetTextFor(slot, panelId, variable, FormatCountdown(seconds));
    }

    public void StopCountdownFor(int slot, string panelId, string variable)
        => _countdowns.Remove((slot, panelId, variable));

    // -------------------------------------------------------------------------------------------------
    // Notices
    // -------------------------------------------------------------------------------------------------

    public void ShowNoticeFor(int slot, string message, float seconds = 2.5f)
    {
        if (!Available || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        // The text write is dirty-cached, so re-showing the same message costs nothing; the deadline is
        // refreshed regardless, which is what makes a notice fired repeatedly behave as one that simply
        // stays up rather than one that flickers.
        SetTextFor(slot, HudPanelIds.SelfLine(0), HudPanelIds.VarName, message);
        ShowFor(slot, HudPanelIds.Self, true);
        SetClassFor(slot, HudPanelIds.Self, HudClasses.Active, true);

        _noticeUntil[slot] = _core.Engine.GlobalVars.CurrentTime + Math.Max(0.1f, seconds);
    }

    public bool IsClassSetFor(int slot, string panelId, string className)
        => _classState.TryGetValue((slot, panelId, className), out var on) && on;

    public void ClearNoticeFor(int slot)
    {
        _noticeUntil.Remove(slot);

        if (!Available)
        {
            return;
        }

        SetClassFor(slot, HudPanelIds.Self, HudClasses.Active, false);
        ShowFor(slot, HudPanelIds.Self, false);
    }

    // -------------------------------------------------------------------------------------------------
    // Reset
    // -------------------------------------------------------------------------------------------------

    public void ResetPlayer(int slot)
    {
        if (Available)
        {
            foreach (var key in _textState.Keys.Where(k => k.Scope == slot).ToList())
            {
                Guard(key.Panel, () => _layout!.RemoveDialogVariableStringForPlayer(slot, key.Panel, key.Variable));
            }

            foreach (var key in _classState.Keys.Where(k => k.Scope == slot).ToList())
            {
                // Undefined, not DoesNotHaveClass: this is dropping an override so the panel follows
                // whatever the global state says, rather than pinning it off for the next occupant.
                Guard(key.Panel, () => _layout!.SetHasClassForPlayer(slot, key.Panel, key.Class, EHudPanelClassStatus_t.k_eHudPanelClassStatus_Undefined));
            }
        }

        // Always drop the local bookkeeping, even when the entity is already gone.
        RemoveWhere(_textState, k => k.Scope == slot);
        RemoveWhere(_classState, k => k.Scope == slot);
        RemoveWhere(_groupState, k => k.Scope == slot);
        RemoveWhere(_barStarts, k => k.Slot == slot);
        RemoveWhere(_barEndsAt, k => k.Slot == slot);
        RemoveWhere(_barRuns, k => k.Slot == slot);
        RemoveWhere(_countdowns, k => k.Slot == slot);
        _noticeUntil.Remove(slot);
    }

    public void ResetAll()
    {
        // Deliberately does NOT touch the entity. This runs on map change, where the entity is already
        // gone - issuing per-player removals against a dead handle is the most likely way to crash here.
        _textState.Clear();
        _classState.Clear();
        _groupState.Clear();
        _barStarts.Clear();
        _barEndsAt.Clear();
        _barRuns.Clear();
        _lastBarPumpTime = 0f;
        _countdowns.Clear();
        _noticeUntil.Clear();
        _lastCountdownPumpTime = 0f;
        _nextCreateAttemptAt = 0f;
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>Records a value and reports whether it actually changed. The whole dirty-tracking scheme.</summary>
    private static bool TrySetDirty<TKey, TValue>(Dictionary<TKey, TValue> state, TKey key, TValue value)
        where TKey : notnull
    {
        if (state.TryGetValue(key, out var previous) && EqualityComparer<TValue>.Default.Equals(previous, value))
        {
            return false;
        }

        state[key] = value;
        return true;
    }

    private static void RemoveWhere<TKey, TValue>(Dictionary<TKey, TValue> state, Func<TKey, bool> predicate)
        where TKey : notnull
    {
        foreach (var key in state.Keys.Where(predicate).ToList())
        {
            state.Remove(key);
        }
    }

    /// <summary>
    /// Runs one entity call, swallowing and logging a failure rather than letting it escape into a game
    /// event handler. A transient failure against one panel must not take the whole surface down, so
    /// <see cref="Available"/> is deliberately left alone.
    /// </summary>
    private void Guard(string panelId, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _core.Logger.LogWarning(ex, "[CSRoll][HUD] Entity call failed for panel {PanelId}.", panelId);
        }
    }
}
