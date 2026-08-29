using SwiftlyS2.Shared;

using CSRoll.Config;
using CSRoll.Core;
using CSRoll.Modifiers;

namespace CSRoll.Hud;

/// <summary>
/// Drives the roll's spin-and-reveal on the custom HUD.
///
/// The interesting difference from the center-HTML path this mirrors is where the animation happens.
/// That path pushes ~52 spin frames plus ~20 description-scramble frames as individual center-HTML
/// messages, and the client rebuilds the whole panel per message - which is why SpinRevealConfig has
/// several fields whose entire purpose is working around frames being silently swallowed when they
/// arrive faster than the panel can be replaced.
///
/// Here the reel is filled once, and one duration class starts a CSS transition that travels the whole
/// strip client-side. The server sends roughly 50 writes for an entire reveal instead of 72 panel
/// rebuilds, and the animation runs at the client's framerate rather than at whatever rate messages
/// survive.
///
/// The timing is deliberately NOT re-derived: it reads the same SpinRevealConfig values and the same
/// eased-interval sum the center-HTML path uses, so the ~15s freeze-time budget documented on
/// SpinRevealConfig.SpinCount holds identically on both surfaces.
/// </summary>
public sealed class HudSequencer
{
    /// <summary>
    /// How long the reveal takes to collapse into a tracker chip, in seconds.
    ///
    /// Must match the reveal-out transition-duration in csroll_hud.css. The panel is hidden and the
    /// tracker un-dimmed only once this elapses, so that the chip appears at the exact moment the
    /// collapsing card reaches chip height - that coincidence is the whole effect. Shorten this below
    /// the CSS duration and the card vanishes mid-collapse; lengthen it and there is a visible pause
    /// on a collapsed card before the chip appears.
    /// </summary>
    private const float RevealFadeOutSeconds = 0.28f;

    /// <summary>
    /// Pause between the reveal finishing its collapse and the tracker coming back, in seconds.
    ///
    /// Without it the chip appears the instant the card has gone and the two read as one continuous
    /// object sliding sideways, which is confusing - the card is centred and the tracker is not. A beat
    /// of empty screen separates "here is what you rolled" from "here is what is active", so the second
    /// reads as a new thing arriving rather than the first one moving.
    /// </summary>
    private const float TrackerReturnDelaySeconds = 1.0f;

    private readonly ISwiftlyCore _core;
    private readonly ModifierRuntime _runtime;
    private readonly ICSRollHudService _hud;

    public HudSequencer(ISwiftlyCore core, ModifierRuntime runtime, ICSRollHudService hud)
    {
        _core = core;
        _runtime = runtime;
        _hud = hud;
    }

    private CSRollConfig Config => _runtime.Config;

    /// <summary>Whether the custom HUD should be handling this roll's reveal at all.</summary>
    public bool HandlesReveal
        => _hud.Available && Config.CustomHud.ReplaceCenterHtml && Config.CustomHud.ShowRevealCard;

    /// <summary>
    /// Runs the spin and reveal for one player.
    /// </summary>
    /// <param name="slot">The player's slot, re-resolved at every step rather than captured.</param>
    /// <param name="modifiers">What the roll landed on for them.</param>
    /// <param name="onRevealed">
    /// The runtime's commit callback. Invoked exactly once, at the moment the reel lands and before
    /// anything description-related - identical ordering to the center-HTML path, which commits on the
    /// landing frame specifically so the mechanical effect is simultaneous with the reveal rather than
    /// trailing it. It carries its own roll-generation guard, so a superseded roll still cannot commit.
    /// </param>
    public void PlayReveal(int slot, IReadOnlyList<GameModifierBase> modifiers, Action onRevealed)
        => Play(slot, modifiers, onRevealed, broadcast: false);

    /// <summary>
    /// Broadcast counterpart, for the shared (non-RandomizePlayers) path where everyone sees the same
    /// roll land on the same result. Uses the global setters, so the whole server costs what one player
    /// costs above rather than N times that.
    /// </summary>
    public void PlayRevealAll(IReadOnlyList<GameModifierBase> modifiers, Action onRevealed)
        => Play(slot: null, modifiers, onRevealed, broadcast: true);

    private void Play(int? slot, IReadOnlyList<GameModifierBase> modifiers, Action onRevealed, bool broadcast)
    {
        var generation = _runtime.RollGeneration;
        var spinSeconds = _runtime.SpinAnimationSeconds();
        var landing = modifiers.Count > 0 ? CSRollUtils.GetModifierDisplayName(_core, modifiers[0]) : string.Empty;

        if (!broadcast && !IsPlayerPresent(slot!.Value))
        {
            return;
        }

        SetClass(slot, broadcast, HudPanelIds.Root, HudClasses.RevealActive, true);
        HideReveal(slot, broadcast);

        if (!Config.SpinReveal.Enabled || spinSeconds <= 0f)
        {
            // Spin disabled: commit and show the result immediately, exactly as the center-HTML path does.
            onRevealed();
            ShowCard(slot, broadcast, modifiers, generation);
            return;
        }

        // The roll and the result are the SAME card, not a separate reel. It opens in its short form
        // showing only a cycling name and glyph, then grows to add the description once it lands.
        ShowCardChrome(slot, broadcast);
        SetClass(slot, broadcast, HudPanelIds.Reveal, HudClasses.Spinning, true);
        SetText(slot, broadcast, HudPanelIds.RevealTitle, HudPanelIds.VarName, "ROLLING MODIFIER");
        SetText(slot, broadcast, HudPanelIds.CardDesc(0), HudPanelIds.VarDesc, string.Empty);

        Show(slot, broadcast, HudPanelIds.Reveal, true);
        SetClass(slot, broadcast, HudPanelIds.Reveal, HudClasses.RevealOut, false);
        SetClass(slot, broadcast, HudPanelIds.Reveal, HudClasses.RevealIn, true);

        PlaySpinFrame(slot, broadcast, 0, Config.SpinReveal.SpinCount, modifiers, onRevealed, generation);
    }

    /// <summary>
    /// One frame of the roll: swap the card's name and glyph for a random modifier, and tick.
    ///
    /// Pushing text per frame rather than animating client-side is deliberate. Two attempts at a CSS
    /// animation - a rotary wheel, then a translating strip - both failed to render in game and neither
    /// was diagnosable from outside it. Dialog-variable writes are the one mechanism here proven to work
    /// every time. Two writes per frame against one panel is also far cheaper than the center-HTML path
    /// this replaces, which rebuilds an entire panel per message.
    ///
    /// The eased interval comes from the same GetSpinFrameIntervalSeconds the center-HTML spin uses, so
    /// the roll slows into its landing on an identical curve and the freeze-time budget is unchanged.
    /// </summary>
    private void PlaySpinFrame(int? slot, bool broadcast, int frameIndex, int totalFrames,
                               IReadOnlyList<GameModifierBase> modifiers, Action onRevealed, int generation)
    {
        if (_runtime.RollGeneration != generation)
        {
            return;
        }

        if (!broadcast && !IsPlayerPresent(slot!.Value))
        {
            // Matches the center-HTML path: a player who left mid-roll never commits.
            return;
        }

        if (frameIndex >= totalFrames)
        {
            // Commit FIRST, before any presentation, so the mechanical effect lands with the reveal.
            onRevealed();

            // Dropping `spinning` grows the card to its full height, which is what brings the
            // description into view - the reveal is the same panel finishing its move, not a new one.
            SetClass(slot, broadcast, HudPanelIds.Reveal, HudClasses.Spinning, false);
            ShowCard(slot, broadcast, modifiers, generation);
            return;
        }

        if (_runtime.RegisteredModifiers.Count > 0)
        {
            var candidate = _runtime.RegisteredModifiers[Random.Shared.Next(_runtime.RegisteredModifiers.Count)];
            var presentation = _runtime.HudPresentation.For(candidate.Name);

            SetText(slot, broadcast, HudPanelIds.CardName(0), HudPanelIds.VarName, CSRollUtils.GetModifierDisplayName(_core, candidate));
            SetText(slot, broadcast, HudPanelIds.CardIcon(0), HudPanelIds.VarName, presentation.Glyph);
            SetClassGroup(slot, broadcast, HudPanelIds.CardIcon(0), HudClasses.GroupAccent, presentation.AccentClass);
        }

        if (!string.IsNullOrEmpty(Config.SpinReveal.TickSoundEventName))
        {
            if (broadcast)
            {
                CSRollUtils.PlaySoundToAll(_core, Config.SpinReveal.TickSoundEventName, Config.SpinReveal.TickSoundVolume, debugMode: _runtime.DebugMode);
            }
            else if (_core.PlayerManager.GetPlayer(slot!.Value) is { IsValid: true } player)
            {
                CSRollUtils.PlaySoundToPlayer(_core, player, Config.SpinReveal.TickSoundEventName, Config.SpinReveal.TickSoundVolume, debugMode: _runtime.DebugMode);
            }
        }

        var interval = _runtime.SpinFrameIntervalSeconds(frameIndex, totalFrames);
        _core.Scheduler.DelayBySeconds(interval, () =>
            PlaySpinFrame(slot, broadcast, frameIndex + 1, totalFrames, modifiers, onRevealed, generation));
    }

    /// <summary>Hides the extra cards and the overflow row, leaving just card 0 - the roll's single slot.</summary>
    private void ShowCardChrome(int? slot, bool broadcast)
    {
        Show(slot, broadcast, HudPanelIds.Card(0), true);

        for (var card = 1; card < HudPanelIds.Cards; card++)
        {
            Show(slot, broadcast, HudPanelIds.Card(card), false);
        }

        Show(slot, broadcast, HudPanelIds.CardOverflow, false);
    }

    /// <summary>Populates and shows the reveal card, then schedules its fade-out.</summary>
    private void ShowCard(int? slot, bool broadcast, IReadOnlyList<GameModifierBase> modifiers, int generation)
    {
        var shown = Math.Min(modifiers.Count, HudPanelIds.Cards);

        SetText(slot, broadcast, HudPanelIds.RevealTitle, HudPanelIds.VarName,
            modifiers.Count == 1 ? "MODIFIER ACTIVATED" : "MODIFIERS ACTIVATED");

        for (var card = 0; card < HudPanelIds.Cards; card++)
        {
            if (card >= shown)
            {
                Show(slot, broadcast, HudPanelIds.Card(card), false);
                continue;
            }

            var modifier = modifiers[card];
            var presentation = _runtime.HudPresentation.For(modifier.Name);

            Show(slot, broadcast, HudPanelIds.Card(card), true);
            SetText(slot, broadcast, HudPanelIds.CardName(card), HudPanelIds.VarName, CSRollUtils.GetModifierDisplayName(_core, modifier));
            SetText(slot, broadcast, HudPanelIds.CardIcon(card), HudPanelIds.VarName, presentation.Glyph);
            SetClassGroup(slot, broadcast, HudPanelIds.CardIcon(card), HudClasses.GroupAccent, presentation.AccentClass);
            SetClassGroup(slot, broadcast, HudPanelIds.Card(card), HudClasses.GroupAccent, presentation.AccentClass);

            if (Config.SpinReveal.ShowDescription)
            {
                // Descriptions carry this plugin's chat colour tokens ("[green]{count}[default]"). Dialog
                // variables are plain strings and a Panorama Label will render those tokens literally, so
                // they are stripped with the same helper the center-HTML scramble already uses - emphasis
                // is expressed in CSS instead.
                var description = CSRollUtils.PlainTextFromChatColors(CSRollUtils.GetModifierDescription(_core, modifier));
                SetText(slot, broadcast, HudPanelIds.CardDesc(card), HudPanelIds.VarDesc, description);
                SetClass(slot, broadcast, HudPanelIds.CardDesc(card), HudClasses.DescWipe, Config.SpinReveal.DescriptionScrambleEnabled);
            }
            else
            {
                SetText(slot, broadcast, HudPanelIds.CardDesc(card), HudPanelIds.VarDesc, string.Empty);
            }
        }

        // Nothing is silently dropped: a roll wider than the card can show says so.
        var hidden = modifiers.Count - shown;
        Show(slot, broadcast, HudPanelIds.CardOverflow, hidden > 0);
        if (hidden > 0)
        {
            SetText(slot, broadcast, HudPanelIds.CardOverflow, HudPanelIds.VarName, $"+{hidden} more");
        }

        Show(slot, broadcast, HudPanelIds.Reveal, true);
        SetClass(slot, broadcast, HudPanelIds.Reveal, HudClasses.RevealOut, false);
        SetClass(slot, broadcast, HudPanelIds.Reveal, HudClasses.RevealIn, true);

        var holdSeconds = _runtime.RevealHoldMilliseconds() / 1000f;

        _core.Scheduler.DelayBySeconds(holdSeconds, () =>
        {
            if (_runtime.RollGeneration != generation)
            {
                return;
            }

            SetClass(slot, broadcast, HudPanelIds.Reveal, HudClasses.RevealIn, false);
            SetClass(slot, broadcast, HudPanelIds.Reveal, HudClasses.RevealOut, true);

            _core.Scheduler.DelayBySeconds(RevealFadeOutSeconds, () =>
            {
                if (_runtime.RollGeneration != generation)
                {
                    return;
                }

                HideReveal(slot, broadcast);

                // The tracker stays suppressed for a beat after the card has gone - see
                // TrackerReturnDelaySeconds. reveal-active is what holds it back, so it is cleared
                // late rather than here.
                //
                // DELIBERATELY NOT generation-guarded, unlike everything else in this file. This class
                // is the only thing keeping the tracker hidden, so failing to clear it does not abandon
                // a stale reveal - it hides the tracker for the REST OF THE ROUND. A roll superseded
                // mid-reveal (a re-roll, or the double EventRoundStart at warmup->live that
                // CSRoll.OnRoundStart documents) stranded exactly this write, which is why the
                // spectator tracker appeared only about half the time.
                //
                // Clearing it late is harmless: a newer reveal sets it true again at its own start, and
                // the worst case is the tracker being visible for a fraction of a second underneath a
                // reveal that is about to re-hide it.
                _core.Scheduler.DelayBySeconds(TrackerReturnDelaySeconds, () =>
                    SetClass(slot, broadcast, HudPanelIds.Root, HudClasses.RevealActive, false));
            });
        });
    }

    private void HideReveal(int? slot, bool broadcast)
    {
        Show(slot, broadcast, HudPanelIds.Reveal, false);
        Show(slot, broadcast, HudPanelIds.Spin, false);
        SetClass(slot, broadcast, HudPanelIds.Reveal, HudClasses.RevealIn, false);
        SetClass(slot, broadcast, HudPanelIds.Reveal, HudClasses.RevealOut, false);
    }

    /// <summary>
    /// Re-resolved by slot rather than captured, because these run from scheduler continuations that can
    /// easily outlive a disconnecting player - the same reasoning the center-HTML spin documents.
    /// </summary>
    private bool IsPlayerPresent(int slot)
        => _core.PlayerManager.GetPlayer(slot) is { IsValid: true };

    // ---------------------------------------------------------------------------------------------
    // Global-vs-per-player dispatch. Every write in this file goes through one of these four, so the
    // broadcast path is genuinely one write per panel rather than one per player.
    // ---------------------------------------------------------------------------------------------

    // Every per-player write below goes to the rolling player AND to anyone spectating them, so a
    // spectator sees the same roll and reveal rather than an empty screen. Per-player dialog variables
    // are addressed by viewer, so mirroring is just writing the same value to more than one slot.
    //
    // The viewer set is recomputed on each write rather than captured at the start of the roll: a
    // spectator who switches to this player mid-spin picks the animation up from wherever it has got
    // to, and one who switches away stops being written to.

    private void SetText(int? slot, bool broadcast, string panelId, string variable, string value)
    {
        if (broadcast)
        {
            _hud.SetText(panelId, variable, value);
            return;
        }

        foreach (var viewer in _runtime.HudViewersOf(slot!.Value))
        {
            _hud.SetTextFor(viewer, panelId, variable, value);
        }
    }

    private void SetClass(int? slot, bool broadcast, string panelId, string className, bool on)
    {
        if (broadcast)
        {
            _hud.SetClass(panelId, className, on);
            return;
        }

        foreach (var viewer in _runtime.HudViewersOf(slot!.Value))
        {
            _hud.SetClassFor(viewer, panelId, className, on);
        }
    }

    private void SetClassGroup(int? slot, bool broadcast, string panelId, string groupKey, string? className)
    {
        if (broadcast)
        {
            _hud.SetClassGroup(panelId, groupKey, className);
            return;
        }

        foreach (var viewer in _runtime.HudViewersOf(slot!.Value))
        {
            _hud.SetClassGroupFor(viewer, panelId, groupKey, className);
        }
    }

    private void Show(int? slot, bool broadcast, string panelId, bool visible)
    {
        if (broadcast)
        {
            _hud.Show(panelId, visible);
            return;
        }

        foreach (var viewer in _runtime.HudViewersOf(slot!.Value))
        {
            _hud.ShowFor(viewer, panelId, visible);
        }
    }
}
