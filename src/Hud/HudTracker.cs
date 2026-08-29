using SwiftlyS2.Shared;

using CSRoll.Config;
using CSRoll.Core;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

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
public sealed partial class HudTracker
{
    private readonly ISwiftlyCore _core;
    private readonly ModifierRuntime _runtime;
    private readonly ICSRollHudService _hud;

    private float _lastRefreshTime;

    /// <summary>
    /// The last subject each viewer successfully resolved.
    ///
    /// The observer lookup is intermittent - it reads as null on some ticks even while spectating the
    /// same player continuously. The center-HTML spectator HUD never noticed because it simply skips a
    /// failed tick and leaves its previous message up; this tracker actively hides on failure, which
    /// turned the same flicker into a panel that appeared about half the time. Remembering the last
    /// good subject and reusing it while the viewer is still dead makes it as resilient as the old one.
    /// </summary>
    private readonly Dictionary<int, int> _lastSubject = [];

    /// <summary>
    /// When each viewer's subject was last resolved for real, so a held subject can EXPIRE.
    ///
    /// Holding across a failed lookup guards against flicker, but holding indefinitely is worse than the
    /// flicker: a genuine target switch whose first lookups happen to fail would be masked by the
    /// previous target until one succeeded, which reads as "switching does nothing until I spam the
    /// key". Bounded to <see cref="SubjectHoldSeconds"/> - long enough to bridge a dropped tick, far too
    /// short to hide a real switch.
    /// </summary>
    private readonly Dictionary<int, float> _lastSubjectAt = [];

    /// <summary>How long a resolved subject may be reused after the lookup stops returning it.</summary>
    private const float SubjectHoldSeconds = 0.5f;

    /// <summary>
    /// The subject each viewer's HUD is currently DRAWN for, as opposed to the one just resolved.
    ///
    /// Needed because the roll animation is mirrored to spectators: switching target mid-reveal means
    /// the old player's remaining frames stop being written to you, which would strand a half-finished
    /// reveal card on screen with reveal-active still set - and reveal-active hides the tracker, so the
    /// switch would cost you both panels at once.
    /// </summary>
    private readonly Dictionary<int, int> _shownSubject = [];

    /// <summary>When each viewer was last drawn. Purely so !hudstatus can tell "the refresh loop is not
    /// reaching you" apart from "it reached you and drew the wrong thing" - which look identical.</summary>
    private readonly Dictionary<int, float> _lastDrawnAt = [];

    /// <summary>
    /// Which branch RefreshPlayer took for each viewer, last time.
    ///
    /// There are three ways out of that method and two of them write nothing, which from outside is
    /// indistinguishable from "it never ran" - and the previous diagnostics could show the loop
    /// reaching a viewer while the rows stayed stale, without saying why. This records the answer
    /// instead of leaving it to be deduced.
    /// </summary>
    private readonly Dictionary<int, string> _lastOutcome = [];

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

        // GetAllPlayers + our own validity check, rather than GetAllValidPlayers: what that call counts
        // as "valid" is undocumented, and a dead spectator being filtered out of it would look exactly
        // like this bug - a tracker drawn once and then never updated again.
        foreach (var player in _core.PlayerManager.GetAllPlayers())
        {
            if (player is { IsValid: true })
            {
                RefreshPlayer(player);
            }
        }
    }

    /// <summary>
    /// Whoever this player's HUD should be describing: the player they are watching if they are
    /// spectating, otherwise themselves.
    ///
    /// IPlayer.PlayerPawn is specifically the alive game pawn and is gone once dead, so the observer
    /// services live on IPlayer.Pawn - the general pawn, whichever is currently active. That
    /// distinction is the same one ModifierRuntime.RefreshSpectatorHud documents, and getting it wrong
    /// means the lookup silently never matches.
    /// </summary>
    private (int Slot, string? SpectatingName) ResolveSubject(IPlayer viewer)
    {
        var targetEntity = viewer.Pawn?.ObserverServices?.ObserverTarget.Value;
        var target = targetEntity is null
            ? null
            : _core.PlayerManager.GetPlayerFromPawn(targetEntity.As<CBasePlayerPawn>());

        if (target is { IsValid: true, Controller: { IsValid: true } controller } && target.Slot != viewer.Slot)
        {
            _lastSubject[viewer.Slot] = target.Slot;
            _lastSubjectAt[viewer.Slot] = _core.Engine.GlobalVars.CurrentTime;
            return (target.Slot, controller.PlayerName);
        }

        // Lookup failed this tick. While the viewer is still dead they are almost certainly still
        // spectating someone, so hold the last known subject rather than snapping back to their own
        // (usually empty) modifiers and hiding the panel. Only a living player genuinely means "self".
        if (!viewer.IsAlive && _lastSubject.TryGetValue(viewer.Slot, out var remembered))
        {
            var heldAt = _lastSubjectAt.GetValueOrDefault(viewer.Slot, float.NegativeInfinity);
            var age = _core.Engine.GlobalVars.CurrentTime - heldAt;

            // The "age >= 0" half is the map-clock guard used throughout this codebase: a stamp from the
            // previous map sits in the future and would otherwise hold a subject forever.
            if (age >= 0f && age < SubjectHoldSeconds &&
                _core.PlayerManager.GetPlayer(remembered) is { IsValid: true, Controller: { IsValid: true } heldController })
            {
                return (remembered, heldController.PlayerName);
            }
        }

        _lastSubject.Remove(viewer.Slot);
        _lastSubjectAt.Remove(viewer.Slot);
        return (viewer.Slot, null);
    }

    private void RefreshPlayer(IPlayer viewer)
    {
        var slot = viewer.Slot;

        if (Cfg.SpectatorFallbackCenterHtml && IsOnSpectatorTeam(viewer))
        {
            // One clearing pass, on the transition. It is issued the moment they are seen on the
            // spectator team, which is the last point at which a write to them might still land - and
            // if it does not, the panels were already beyond reach and nothing here made it worse.
            // Repeating it every refresh would just be writes into the void.
            if (_clearedForSpectatorTeam.Add(slot))
            {
                BlankRows(slot);
                ClearMirroredReveal(slot);
                _hud.ShowFor(slot, HudPanelIds.Track, false);
                _hud.ShowFor(slot, HudPanelIds.Help, false);
            }

            _shownSubject.Remove(slot);
            _lastOutcome[slot] = "spectator team - center-HTML has this viewer";
            return;
        }

        _clearedForSpectatorTeam.Remove(slot);

        _lastDrawnAt[slot] = _core.Engine.GlobalVars.CurrentTime;

        var (subject, spectatingName) = ResolveSubject(viewer);
        var spectating = spectatingName is not null;

        // Rows describe the SUBJECT, but are written to the VIEWER's own per-player overrides - which is
        // what lets one layout entity show a different list to every player watching a different person.
        // A change of subject invalidates anything mirrored from the previous one.
        if (_shownSubject.TryGetValue(slot, out var previous) && previous != subject)
        {
            // Not while a roll owns the card: switching onto someone who is mid-spin should join their
            // animation, not wipe it. The sequencer recomputes its viewer set on every write, so the
            // new watcher is already being written to by the time this runs.
            if (_hud.RevealOwnerOf(slot) != HudRevealOwner.Roll)
            {
                ClearMirroredReveal(slot);
            }

            // Reset the per-row TIMERS, then fall through and draw the new subject in this same pass.
            //
            // This used to blank every row and skip a tick, to defeat a dirty cache that was believed to
            // be swallowing writes. It was not - the viewers whose rows never updated were spectators,
            // who receive no HUD state at all, and no cache behaviour was ever involved. All the blank
            // tick bought was a frame of empty rows on every target switch.
            //
            // What genuinely does need clearing is the bars: SyncBarFor deliberately ignores a countdown
            // it is already animating, so a row moving from one player's 10-second cooldown to another's
            // 25-second one would keep animating the first. Stopping them makes the next Sync* a fresh
            // start.
            ResetRowTimers(slot);
        }

        _shownSubject[slot] = subject;

        var modifiers = _runtime.GetModifiersForSlot(subject);
        var budget = RowBudget;

        if (modifiers.Count == 0 || budget == 0)
        {
            ClearRowsFrom(slot, 0);
            _hud.ShowFor(slot, HudPanelIds.Track, false);
            _hud.ShowFor(slot, HudPanelIds.Help, false);
            _lastOutcome[slot] = $"hidden (subject {subject} has {modifiers.Count} modifiers, budget {budget})";
            return;
        }

        _hud.ShowFor(slot, HudPanelIds.Track, true);

        // Named while spectating, so the list is not mistaken for your own. Hidden otherwise - your own
        // modifiers need no label.
        if (spectating)
        {
            _hud.SetTextFor(slot, HudPanelIds.TrackTitle, HudPanelIds.VarName, $"SPECTATING: {spectatingName}");
        }

        _hud.ShowFor(slot, HudPanelIds.TrackTitle, spectating);

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
        _lastOutcome[slot] = $"drew {listedCount} row(s) for subject {subject}";

        // Spectators get the modifier card drawn from scratch, every refresh, rather than inheriting one
        // mirrored from the roll.
        //
        // Mirroring only ever worked if you happened to be watching that player AT THE MOMENT they
        // rolled - switch to them a second later and there was no card, because nothing recreates one
        // after the fact. Drawing it here makes it a property of who you are watching rather than of
        // when you started watching, which is what "always visible" actually requires.
        var owner = _hud.RevealOwnerOf(slot);

        if (owner == HudRevealOwner.Roll)
        {
            // A roll is animating on these exact panels. Leave every one of them alone - the sequencer
            // is mid-sequence and anything written here fights it.
            //
            // This is what was killing the description card. The old test was "is the reveal panel
            // visible", which is true during a roll as much as it is for a held spectator card, so the
            // branch below fired on the refresh immediately after the card appeared and tore it down
            // within a tenth of a second - for the rolling player just as much as for a spectator.
        }
        else if (spectating)
        {
            DrawSpectatorCard(slot, ordered);
            _hud.ClaimReveal(slot, HudRevealOwner.Spectator);
        }
        else if (owner == HudRevealOwner.Spectator)
        {
            // Respawned, or switched back to themselves, while a card this tracker was holding is still
            // up. It never closes on its own, so something has to close it - and only the writer that
            // opened it may.
            ClearMirroredReveal(slot);
        }

        // Drawn while spectating too - it describes the subject's ability and how long until they can use
        // it, which is exactly what someone watching them wants.
        //
        // It stands down for any OWNED reveal card, live roll or held spectator card alike: both occupy
        // the helper's slot, so drawing them together would stack two panels on the same pixels.
        var revealStillUp = _hud.RevealOwnerOf(slot) != HudRevealOwner.None;

        if (revealStillUp)
        {
            _hud.ShowFor(slot, HudPanelIds.Help, false);
            _hud.StopBarFor(slot, HudPanelIds.HelpBarPair());
        }
        else
        {
            DrawHelper(slot, ordered);
        }
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
        var helper = ordered.FirstOrDefault(entry =>
            !string.IsNullOrEmpty(entry.Timer?.Prompt) || !string.IsNullOrEmpty(entry.Timer?.HelpTop));

        if (helper.Modifier is null || helper.Timer is not { } live)
        {
            _hud.ShowFor(slot, HudPanelIds.Help, false);
            _hud.StopBarFor(slot, HudPanelIds.HelpBarPair());
            return;
        }

        var bar = HudPanelIds.HelpBarPair();
        var tone = HudClasses.Tone(live.Tone);

        _hud.ShowFor(slot, HudPanelIds.Help, true);
        _hud.SetClassGroupFor(slot, HudPanelIds.Help, HudClasses.GroupAccent,
            _runtime.HudPresentation.For(helper.Modifier.Name).AccentClass);

        // Each line appears only if the modifier filled that slot, and the card sizes itself to what is
        // left - which is what lets one layout read FUEL-then-bar for a gauge and bar-then-PRESS-F for
        // an ability.
        // A cooling ability says so, and says how long. Leaving the prompt alone while the bar was the
        // only hint that anything was happening was actively misleading - "PRESS F TO FLANK" reads as an
        // invitation, so it has to be paired with the state rather than standing on its own.
        var cooling = live.Kind == HudTimerKind.Cooldown && live.SecondsRemaining > 0f;

        var topLine = live.HelpTop
                   ?? (live.Kind == HudTimerKind.Cooldown
                        ? (cooling ? FormatCooldown(live.SecondsRemaining) : "READY")
                        : null);

        var topTone = live.HelpTop is not null ? tone
                    : live.Kind == HudTimerKind.Cooldown ? HudClasses.Tone(cooling ? HudTone.Warn : HudTone.Good)
                    : tone;

        WriteHelpLine(slot, HudPanelIds.HelpTop, topLine, topTone);
        WriteHelpLine(slot, HudPanelIds.HelpBottom, live.Prompt, cooling ? null : HudClasses.Tone(HudTone.Good));

        _hud.ShowFor(slot, HudPanelIds.HelpBar, true);
        _hud.SetClassGroupFor(slot, bar.FillA, HudClasses.GroupTone, topTone);

        switch (live.Kind)
        {
            case HudTimerKind.Gauge:
                _hud.SetBarFor(slot, bar, live.Fraction);
                break;

            // Fills towards ready. The remaining time alone cannot say how far along a cooldown is, which
            // is why HudTimer.Cooldown carries the total as well.
            case HudTimerKind.Cooldown when live.SecondsRemaining > 0f:
                _hud.SyncBarFor(slot, bar, live.SecondsRemaining, fillUp: true);
                break;

            case HudTimerKind.Cooldown:
                _hud.SetBarFor(slot, bar, 1f);
                break;

            case HudTimerKind.Countdown when live.SecondsRemaining > 0f:
                _hud.SyncBarFor(slot, bar, live.SecondsRemaining);
                break;

            default:
                _hud.SetBarFor(slot, bar, live.Fraction);
                break;
        }
    }

    /// <summary>Whole seconds above five, one decimal below - matching every other timer in this plugin,
    /// including the comma decimal separator.</summary>
    private static string FormatCooldown(float remaining)
        => remaining > 5f ? $"{(int)Math.Ceiling(remaining)}s" : $"{remaining:0.0}s".Replace('.', ',');

    /// <summary>One of the helper's two text slots. Empty hides it, and the card shrinks to suit.</summary>
    private void WriteHelpLine(int slot, string panelId, string? text, string? tone)
    {
        if (string.IsNullOrEmpty(text))
        {
            _hud.ShowFor(slot, panelId, false);
            return;
        }

        _hud.SetTextFor(slot, panelId, HudPanelIds.VarName, text);
        _hud.SetClassGroupFor(slot, panelId, HudClasses.GroupTone, tone);
        _hud.ShowFor(slot, panelId, true);
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

            case HudTimerKind.Cooldown when live.SecondsRemaining > 0f:
                _hud.SyncCountdownFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime, bar, live.SecondsRemaining);
                _hud.SyncBarFor(slot, bar, live.SecondsRemaining, fillUp: true);
                break;

            case HudTimerKind.Cooldown:
                _hud.StopCountdownFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime);
                _hud.SetTextFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime, live.Status ?? "READY");
                _hud.SetBarFor(slot, bar, 1f);
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

    /// <summary>
    /// Fills and holds the reveal card with the spectated player's modifiers.
    ///
    /// Uses the same panels the roll's own reveal uses, so a spectator watching a live roll sees it
    /// animate and then simply keeps the finished card - there is no second card and no handover.
    /// </summary>
    private void DrawSpectatorCard(int slot, List<(GameModifierBase Modifier, HudTimer? Timer)> ordered)
    {
        var shown = Math.Min(ordered.Count, HudPanelIds.Cards);

        _hud.SetTextFor(slot, HudPanelIds.RevealTitle, HudPanelIds.VarName, "SPECTATING");

        for (var card = 0; card < HudPanelIds.Cards; card++)
        {
            if (card >= shown)
            {
                _hud.ShowFor(slot, HudPanelIds.Card(card), false);
                continue;
            }

            var modifier = ordered[card].Modifier;
            var presentation = _runtime.HudPresentation.For(modifier.Name);

            _hud.ShowFor(slot, HudPanelIds.Card(card), true);
            _hud.SetTextFor(slot, HudPanelIds.CardName(card), HudPanelIds.VarName, CSRollUtils.GetModifierDisplayName(_core, modifier));
            _hud.SetTextFor(slot, HudPanelIds.CardIcon(card), HudPanelIds.VarName, presentation.Glyph);
            _hud.SetClassGroupFor(slot, HudPanelIds.CardIcon(card), HudClasses.GroupAccent, presentation.AccentClass);
            _hud.SetClassGroupFor(slot, HudPanelIds.Card(card), HudClasses.GroupAccent, presentation.AccentClass);

            // Chat colour tokens would render literally in a Panorama label - same strip the sequencer does.
            var description = CSRollUtils.PlainTextFromChatColors(CSRollUtils.GetModifierDescription(_core, modifier));
            _hud.SetTextFor(slot, HudPanelIds.CardDesc(card), HudPanelIds.VarDesc, description);
        }

        var hidden = ordered.Count - shown;
        _hud.ShowFor(slot, HudPanelIds.CardOverflow, hidden > 0);
        if (hidden > 0)
        {
            _hud.SetTextFor(slot, HudPanelIds.CardOverflow, HudPanelIds.VarName, $"+{hidden} more");
        }

        // Full height, never collapsed, never faded out - the spectator's card does not close.
        _hud.SetClassFor(slot, HudPanelIds.Reveal, HudClasses.Spinning, false);
        _hud.SetClassFor(slot, HudPanelIds.Reveal, HudClasses.RevealOut, false);
        _hud.SetClassFor(slot, HudPanelIds.Reveal, HudClasses.RevealIn, true);
        _hud.ShowFor(slot, HudPanelIds.Reveal, true);
    }

    /// <summary>
    /// Stops every row's bar and countdown, without touching the text.
    ///
    /// Used when a viewer changes subject: text is dirty-tracked and corrects itself on the same pass,
    /// but a bar is stateful - see the call site.
    /// </summary>
    private void ResetRowTimers(int slot)
    {
        for (var row = 0; row < HudPanelIds.Rows; row++)
        {
            _hud.StopCountdownFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime);
            _hud.StopBarFor(slot, HudPanelIds.RowBarPair(row));
        }

        _hud.StopBarFor(slot, HudPanelIds.HelpBarPair());
    }

    /// <summary>
    /// Empties every tracker row and the helper card for one viewer. Used when the tracker hands a
    /// viewer over to another surface entirely - see the spectator-team branch in RefreshPlayer.
    /// </summary>
    private void BlankRows(int slot)
    {
        for (var row = 0; row < HudPanelIds.Rows; row++)
        {
            _hud.SetTextFor(slot, HudPanelIds.RowName(row), HudPanelIds.VarName, string.Empty);
            _hud.SetTextFor(slot, HudPanelIds.RowIcon(row), HudPanelIds.VarName, string.Empty);
            _hud.SetTextFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime, string.Empty);
            _hud.SetTextFor(slot, HudPanelIds.RowDetail(row), HudPanelIds.VarName, string.Empty);
            _hud.ShowFor(slot, HudPanelIds.Row(row), false);
            _hud.ShowFor(slot, HudPanelIds.RowDetail(row), false);
            _hud.StopCountdownFor(slot, HudPanelIds.RowTime(row), HudPanelIds.VarTime);
            _hud.StopBarFor(slot, HudPanelIds.RowBarPair(row));
        }

        _hud.SetTextFor(slot, HudPanelIds.HelpTop, HudPanelIds.VarName, string.Empty);
        _hud.SetTextFor(slot, HudPanelIds.HelpBottom, HudPanelIds.VarName, string.Empty);
        _hud.ShowFor(slot, HudPanelIds.Help, false);
        _hud.StopBarFor(slot, HudPanelIds.HelpBarPair());

        _rowsInUse[slot] = 0;
    }

    /// <summary>
    /// Drops a reveal that was mirrored from a player this viewer is no longer watching.
    ///
    /// reveal-active is cleared last and unconditionally: it is the class that hides the tracker, so
    /// leaving it set would mean switching target silently cost you the tracker for the rest of the
    /// round - the same failure the sequencer's generation guard used to cause.
    /// </summary>
    private void ClearMirroredReveal(int slot)
    {
        _hud.ShowFor(slot, HudPanelIds.Reveal, false);
        _hud.ShowFor(slot, HudPanelIds.Spin, false);
        _hud.SetClassFor(slot, HudPanelIds.Reveal, HudClasses.RevealIn, false);
        _hud.SetClassFor(slot, HudPanelIds.Reveal, HudClasses.RevealOut, false);
        _hud.SetClassFor(slot, HudPanelIds.Reveal, HudClasses.Spinning, false);
        _hud.SetClassFor(slot, HudPanelIds.Root, HudClasses.RevealActive, false);
        _hud.ClaimReveal(slot, HudRevealOwner.None);
    }

    /// <summary>
    /// Every player whose HUD should currently be showing <paramref name="subjectSlot"/>'s state: the
    /// player themselves, plus anyone spectating them.
    ///
    /// This is what makes a spectator see the same roll, reveal and helper card as the player they are
    /// watching. Per-player dialog variables are addressed by VIEWER, so mirroring is simply writing the
    /// same values to more than one slot - the subject's own HUD and every spectator's are independent
    /// copies of the same content.
    ///
    /// Reads the subject cache the refresh loop already maintains rather than re-walking observer
    /// services, so it is cheap enough to call per animation frame.
    /// </summary>
    public IEnumerable<int> ViewersOf(int subjectSlot)
    {
        yield return subjectSlot;

        foreach (var (viewer, subject) in _lastSubject)
        {
            if (subject != subjectSlot || viewer == subjectSlot)
            {
                continue;
            }

            // Skip viewers the custom HUD cannot reach - see IsOnSpectatorTeam. Mirroring a roll to them
            // writes ~50 per-player overrides per reveal that the client will never apply, and every one
            // of them is lasting entity state.
            if (_clearedForSpectatorTeam.Contains(viewer))
            {
                continue;
            }

            yield return viewer;
        }
    }

    /// <summary>Forgets a slot's row bookkeeping. The HUD service clears the actual per-player state.</summary>
    public void ForgetPlayer(int slot)
    {
        _rowsInUse.Remove(slot);
        _lastSubject.Remove(slot);
        _lastSubjectAt.Remove(slot);
        _shownSubject.Remove(slot);
        _lastDrawnAt.Remove(slot);
        _lastOutcome.Remove(slot);

        // Also drop this slot as anyone else's remembered subject - a spectator watching someone who
        // disconnects must not keep showing their modifiers to a slot the next joiner will occupy.
        foreach (var viewer in _lastSubject.Where(entry => entry.Value == slot).Select(entry => entry.Key).ToList())
        {
            _lastSubject.Remove(viewer);
        }
    }

    /// <summary>Forgets all row bookkeeping, on map change or a full modifier clear.</summary>
    public void Reset()
    {
        _rowsInUse.Clear();
        _lastSubject.Clear();
        _lastSubjectAt.Clear();
        _shownSubject.Clear();
        _lastDrawnAt.Clear();
        _lastOutcome.Clear();
        _lastRefreshTime = 0f;
    }
}
