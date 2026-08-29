using SwiftlyS2.Shared;

using CSRoll.Config;
using CSRoll.Core;
using CSRoll.Modifiers;

namespace CSRoll.Hud;

/// <summary>
/// Draws the persistent "what's rolled on you right now" panel: one row per active modifier, with its
/// icon, name, and whatever live timer the modifier chooses to expose.
///
/// Every row is a per-player override, so all players share one layout entity while each sees only
/// their own modifiers.
///
/// This sits BESIDE the nine modifiers that draw center-HTML gauges rather than replacing them. Doing
/// both at once - introducing a brand-new rendering surface that cannot be verified on this machine,
/// while simultaneously rewriting the nine things that currently work - would make a failure impossible
/// to attribute. The <see cref="GameModifierBase.GetHudTimer"/> hook is the migration path; the
/// center-HTML gauges are untouched.
/// </summary>
public sealed class HudTracker
{
    private readonly ISwiftlyCore _core;
    private readonly ModifierRuntime _runtime;
    private readonly ICSRollHudService _hud;

    private float _lastRefreshTime;

    /// <summary>Rows currently showing something, per slot - so a row that empties gets cleared exactly
    /// once instead of being rewritten as blank on every refresh.</summary>
    private readonly Dictionary<int, int> _rowsInUse = [];

    public HudTracker(ISwiftlyCore core, ModifierRuntime runtime, ICSRollHudService hud)
    {
        _core = core;
        _runtime = runtime;
        _hud = hud;
    }

    private CustomHudConfig Cfg => _runtime.Config.CustomHud;

    /// <summary>Row budget: config, clamped to what the published layout actually contains. Asking for
    /// more rows than the addon has does nothing until the addon is rebuilt and republished.</summary>
    private int RowBudget => Math.Clamp(Cfg.TrackerRowCount, 0, HudPanelIds.Rows);

    /// <summary>OnTick entry point. Throttled; does nothing at all when the HUD is unavailable.</summary>
    public void Refresh()
    {
        if (!_hud.Available || !Cfg.ShowTracker)
        {
            return;
        }

        var now = _core.Engine.GlobalVars.CurrentTime;

        // Same throttle idiom used throughout this codebase - the "now >= last" half is the guard
        // against a map-relative clock that restarted underneath a stale timestamp.
        if (now >= _lastRefreshTime && now - _lastRefreshTime < Cfg.TrackerRefreshIntervalSeconds)
        {
            return;
        }

        _lastRefreshTime = now;

        foreach (var player in _core.PlayerManager.GetAllValidPlayers())
        {
            RefreshPlayer(player.Slot);
        }
    }

    private void RefreshPlayer(int slot)
    {
        var modifiers = _runtime.GetModifiersForSlot(slot);
        var budget = RowBudget;

        if (modifiers.Count == 0 || budget == 0)
        {
            ClearRowsFrom(slot, 0);
            _hud.ShowFor(slot, HudPanelIds.Track, false);
            _hud.ShowFor(slot, HudPanelIds.Help, false);
            return;
        }

        _hud.ShowFor(slot, HudPanelIds.Track, true);

        // Ordered by name and NOTHING else. Sorting timered modifiers to the top seemed friendlier, but
        // "has a timer" changes as cooldowns start and finish, so rows reshuffled mid-round - and a
        // modifier landing on a different row inherits that row's bar, which restarts the transition.
        // On screen that reads as a countdown running down and then abruptly jumping back up. Row
        // identity has to be stable for the whole round, so the only safe sort key is one that cannot
        // change while the round is running.
        var ordered = modifiers
            .Select(modifier => (Modifier: modifier, Timer: SafeTimer(modifier, slot)))
            .OrderBy(entry => entry.Modifier.Name, StringComparer.Ordinal)
            .ToList();

        // When the list is longer than the budget, the last usable row becomes a "+N more" counter -
        // the player is told something is hidden rather than it silently vanishing.
        var overflowing = ordered.Count > budget;
        var listedCount = overflowing ? budget - 1 : ordered.Count;

        for (var row = 0; row < listedCount; row++)
        {
            DrawRow(slot, row, ordered[row].Modifier, ordered[row].Timer);
        }

        if (overflowing)
        {
            DrawOverflowRow(slot, listedCount, ordered.Count - listedCount);
            ClearRowsFrom(slot, listedCount + 1);
        }
        else
        {
            ClearRowsFrom(slot, listedCount);
        }

        _rowsInUse[slot] = overflowing ? listedCount + 1 : listedCount;

        DrawHelper(slot, ordered);
    }

    /// <summary>
    /// The helper card: the one modifier the player has to actively DO something with.
    ///
    /// Only modifiers that supply a Prompt opt in, which is the same set that used to draw center-HTML
    /// gauges - the ability modifiers. If more than one qualifies, the first by name wins and holds the
    /// card for the round; a card that swapped between two abilities mid-fight would be worse than
    /// showing one consistently, since the player would have to re-read it every time it changed.
    /// </summary>
    private void DrawHelper(int slot, List<(GameModifierBase Modifier, HudTimer? Timer)> ordered)
    {
        var helper = ordered.FirstOrDefault(entry => !string.IsNullOrEmpty(entry.Timer?.Prompt));

        if (helper.Modifier is null || helper.Timer is not { } live)
        {
            _hud.ShowFor(slot, HudPanelIds.Help, false);
            _hud.StopCountdownFor(slot, HudPanelIds.HelpTime, HudPanelIds.VarTime);
            _hud.StopBarFor(slot, HudPanelIds.HelpBarPair());
            return;
        }

        var presentation = _runtime.HudPresentation.For(helper.Modifier.Name);
        var bar = HudPanelIds.HelpBarPair();

        _hud.ShowFor(slot, HudPanelIds.Help, true);
        _hud.SetTextFor(slot, HudPanelIds.HelpIcon, HudPanelIds.VarName, presentation.Glyph);
        _hud.SetClassGroupFor(slot, HudPanelIds.HelpIcon, HudClasses.GroupAccent, presentation.AccentClass);
        _hud.SetClassGroupFor(slot, HudPanelIds.Help, HudClasses.GroupAccent, presentation.AccentClass);
        _hud.SetTextFor(slot, HudPanelIds.HelpName, HudPanelIds.VarName, CSRollUtils.GetModifierDisplayName(_core, helper.Modifier));
        _hud.SetTextFor(slot, HudPanelIds.HelpPrompt, HudPanelIds.VarName, live.Prompt!);

        _hud.ShowFor(slot, HudPanelIds.HelpBar, true);

        switch (live.Kind)
        {
            case HudTimerKind.Gauge:
                _hud.StopCountdownFor(slot, HudPanelIds.HelpTime, HudPanelIds.VarTime);
                _hud.SetTextFor(slot, HudPanelIds.HelpTime, HudPanelIds.VarTime, live.Status ?? string.Empty);
                _hud.SetBarFor(slot, bar, live.Fraction);
                break;

            case HudTimerKind.Countdown when live.SecondsRemaining > 0f && live.Status is { } busy:
                _hud.StopCountdownFor(slot, HudPanelIds.HelpTime, HudPanelIds.VarTime);
                _hud.SetTextFor(slot, HudPanelIds.HelpTime, HudPanelIds.VarTime, busy);
                _hud.SyncBarFor(slot, bar, live.SecondsRemaining);
                break;

            case HudTimerKind.Countdown when live.SecondsRemaining > 0f:
                _hud.SyncCountdownFor(slot, HudPanelIds.HelpTime, HudPanelIds.VarTime, bar, live.SecondsRemaining);
                break;

            default:
                _hud.StopCountdownFor(slot, HudPanelIds.HelpTime, HudPanelIds.VarTime);
                _hud.SetTextFor(slot, HudPanelIds.HelpTime, HudPanelIds.VarTime, live.Status ?? "READY");
                _hud.SetBarFor(slot, bar, live.Fraction);
                break;
        }
    }

    /// <summary>
    /// A modifier's timer must never be able to break the tracker. These are per-modifier overrides
    /// reading live per-slot dictionaries, and a bad one throwing here would run inside OnTick.
    /// </summary>
    private HudTimer? SafeTimer(GameModifierBase modifier, int slot)
    {
        try
        {
            return modifier.GetHudTimer(slot);
        }
        catch
        {
            return null;
        }
    }

    private void DrawRow(int slot, int row, GameModifierBase modifier, HudTimer? timer)
    {
        var presentation = _runtime.HudPresentation.For(modifier.Name);
        var bar = HudPanelIds.RowBarPair(row);

        _hud.ShowFor(slot, HudPanelIds.Row(row), true);
        _hud.SetClassFor(slot, HudPanelIds.Row(row), HudClasses.Active, true);
        _hud.SetClassFor(slot, HudPanelIds.Row(row), HudClasses.Overflow, false);
        _hud.SetTextFor(slot, HudPanelIds.RowName(row), HudPanelIds.VarName, CSRollUtils.GetModifierDisplayName(_core, modifier));

        _hud.SetTextFor(slot, HudPanelIds.RowIcon(row), HudPanelIds.VarName, presentation.Glyph);
        _hud.SetClassGroupFor(slot, HudPanelIds.RowIcon(row), HudClasses.GroupAccent, presentation.AccentClass);
        _hud.SetClassGroupFor(slot, HudPanelIds.Row(row), HudClasses.GroupAccent, presentation.AccentClass);

        DrawDetail(slot, row, timer);

        if (timer is not { } live)
        {
            // A passive modifier: name and icon only, no bar, no timer text.
            _hud.StopCountdownFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime);
            _hud.SetTextFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime, string.Empty);
            _hud.ShowFor(slot, HudPanelIds.RowBar(row), false);
            _hud.StopBarFor(slot, bar);
            return;
        }

        _hud.ShowFor(slot, HudPanelIds.RowBar(row), true);

        // Every branch below uses the Sync* calls rather than Start*. This method runs on a poll, and a
        // bar is not idempotent: re-issuing a start on each refresh would restart the CSS transition
        // several times a second and the bar would never visibly move. Sync* recognises a countdown it
        // is already animating and leaves it alone.
        switch (live.Kind)
        {
            case HudTimerKind.Gauge:
                _hud.StopCountdownFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime);
                _hud.SetTextFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime, live.Status ?? string.Empty);
                _hud.SetBarFor(slot, bar, live.Fraction);
                break;

            case HudTimerKind.Countdown when live.SecondsRemaining > 0f && live.Status is { } busy:
                // Counting down, but showing a word instead of the number ("ACTIVE" while a Vanish is
                // running). The bar still animates the remaining time.
                _hud.StopCountdownFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime);
                _hud.SetTextFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime, busy);
                _hud.SyncBarFor(slot, bar, live.SecondsRemaining);
                break;

            case HudTimerKind.Countdown when live.SecondsRemaining > 0f:
                // The numeric readout is handed to the service's pump, which owns it from here.
                _hud.SyncCountdownFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime, bar, live.SecondsRemaining);
                break;

            default:
                // Nothing counting - ready, or a finished countdown.
                _hud.StopCountdownFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime);
                _hud.SetTextFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime, live.Status ?? string.Empty);
                _hud.SetBarFor(slot, bar, live.Fraction);
                break;
        }
    }

    /// <summary>
    /// The row's optional second line. Hidden unless the modifier supplies one, so a passive modifier's
    /// row stays a single compact line.
    /// </summary>
    private void DrawDetail(int slot, int row, HudTimer? timer)
    {
        var detail = timer?.Detail;

        if (string.IsNullOrEmpty(detail))
        {
            _hud.ShowFor(slot, HudPanelIds.RowDetail(row), false);
            return;
        }

        _hud.SetTextFor(slot, HudPanelIds.RowDetail(row), HudPanelIds.VarName, detail);
        _hud.SetClassGroupFor(slot, HudPanelIds.RowDetail(row), HudClasses.GroupTone, HudClasses.Tone(timer!.Value.Tone));
        _hud.ShowFor(slot, HudPanelIds.RowDetail(row), true);
    }

    private void DrawOverflowRow(int slot, int row, int hiddenCount)
    {
        var bar = HudPanelIds.RowBarPair(row);

        _hud.ShowFor(slot, HudPanelIds.Row(row), true);
        _hud.SetClassFor(slot, HudPanelIds.Row(row), HudClasses.Active, true);
        _hud.SetClassFor(slot, HudPanelIds.Row(row), HudClasses.Overflow, true);
        _hud.SetTextFor(slot, HudPanelIds.RowName(row), HudPanelIds.VarName, $"+{hiddenCount} more");
        _hud.SetTextFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime, string.Empty);
        _hud.SetTextFor(slot, HudPanelIds.RowIcon(row), HudPanelIds.VarName, "…");
        _hud.SetClassGroupFor(slot, HudPanelIds.RowIcon(row), HudClasses.GroupAccent, HudClasses.AccentFallback);
        _hud.ShowFor(slot, HudPanelIds.RowBar(row), false);
        _hud.ShowFor(slot, HudPanelIds.RowDetail(row), false);
        _hud.StopCountdownFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime);
        _hud.StopBarFor(slot, bar);
    }

    /// <summary>Hides every row from <paramref name="firstUnused"/> onward that was previously drawn.</summary>
    private void ClearRowsFrom(int slot, int firstUnused)
    {
        _rowsInUse.TryGetValue(slot, out var previouslyUsed);

        for (var row = firstUnused; row < previouslyUsed; row++)
        {
            _hud.ShowFor(slot, HudPanelIds.Row(row), false);
            _hud.ShowFor(slot, HudPanelIds.RowDetail(row), false);
            _hud.SetClassFor(slot, HudPanelIds.Row(row), HudClasses.Active, false);
            _hud.StopCountdownFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime);
            _hud.StopBarFor(slot, HudPanelIds.RowBarPair(row));
        }

        _rowsInUse[slot] = firstUnused;
    }

    /// <summary>Forgets a slot's row bookkeeping. The HUD service clears the actual per-player state.</summary>
    public void ForgetPlayer(int slot) => _rowsInUse.Remove(slot);

    /// <summary>Forgets all row bookkeeping, on map change or a full modifier clear.</summary>
    public void Reset()
    {
        _rowsInUse.Clear();
        _lastRefreshTime = 0f;
    }
}
