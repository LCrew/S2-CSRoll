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

        Show(slot, broadcast, HudPanelIds.Spin, true);
        SetClass(slot, broadcast, HudPanelIds.SpinReel, HudClasses.Spinning, true);
        SetText(slot, broadcast, HudPanelIds.RevealTitle, HudPanelIds.VarName, "ROLLING");

        PlaySpinFrame(slot, broadcast, 0, Config.SpinReveal.SpinCount, modifiers, landing, onRevealed, generation);
    }

    /// <summary>
    /// One frame of the spin: shuffle the three visible rows and play the tick.
    ///
    /// This pushes text per frame rather than animating a CSS transform, which is a deliberate step
    /// back. Two separate attempts at a client-side animation - a rotary wheel on spokes, then a
    /// translating strip - both failed to render in game and neither was diagnosable from outside it.
    /// Dialog-variable writes are the one mechanism here proven to work every time; the tracker runs on
    /// them constantly. Three writes per frame for one panel is nothing next to the center-HTML path's
    /// full panel rebuild per message, which is what made that approach fragile.
    ///
    /// The eased interval comes from the same GetSpinFrameIntervalSeconds the center-HTML spin uses, so
    /// the reel slows into its landing on the identical curve and the freeze-time budget is unchanged.
    /// </summary>
    private void PlaySpinFrame(int? slot, bool broadcast, int frameIndex, int totalFrames,
                               IReadOnlyList<GameModifierBase> modifiers, string landing,
                               Action onRevealed, int generation)
    {
        if (_runtime.RollGeneration != generation)
        {
            return;
        }

        if (!broadcast && !IsPlayerPresent(slot!.Value))
        {
            // Matches the center-HTML path: a player who left mid-spin never commits.
            return;
        }

        if (frameIndex >= totalFrames)
        {
            // Landing. The real result goes in the centre row, with fresh neighbours either side so the
            // reel does not appear to have run out of cards.
            SetText(slot, broadcast, HudPanelIds.ReelRow(0), HudPanelIds.VarName, RandomName());
            SetText(slot, broadcast, HudPanelIds.ReelLandingRow(), HudPanelIds.VarName, landing);
            SetText(slot, broadcast, HudPanelIds.ReelRow(2), HudPanelIds.VarName, RandomName());

            // Commit FIRST, before any presentation, so the mechanical effect lands with the reveal.
            onRevealed();

            SetClass(slot, broadcast, HudPanelIds.SpinReel, HudClasses.Spinning, false);
            Show(slot, broadcast, HudPanelIds.Spin, false);
            ShowCard(slot, broadcast, modifiers, generation);
            return;
        }

        SetText(slot, broadcast, HudPanelIds.ReelRow(0), HudPanelIds.VarName, RandomName());
        SetText(slot, broadcast, HudPanelIds.ReelLandingRow(), HudPanelIds.VarName, RandomName());
        SetText(slot, broadcast, HudPanelIds.ReelRow(2), HudPanelIds.VarName, RandomName());

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
            PlaySpinFrame(slot, broadcast, frameIndex + 1, totalFrames, modifiers, landing, onRevealed, generation));
    }

    /// <summary>A random registered modifier's display name, for the reel's filler rows.</summary>
    private string RandomName()
    {
        var pool = _runtime.RegisteredModifiers;
        return pool.Count > 0
            ? CSRollUtils.GetModifierDisplayName(_core, pool[Random.Shared.Next(pool.Count)])
            : string.Empty;
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
                SetClass(slot, broadcast, HudPanelIds.Root, HudClasses.RevealActive, false);
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

    private void SetText(int? slot, bool broadcast, string panelId, string variable, string value)
    {
        if (broadcast)
        {
            _hud.SetText(panelId, variable, value);
        }
        else
        {
            _hud.SetTextFor(slot!.Value, panelId, variable, value);
        }
    }

    private void SetClass(int? slot, bool broadcast, string panelId, string className, bool on)
    {
        if (broadcast)
        {
            _hud.SetClass(panelId, className, on);
        }
        else
        {
            _hud.SetClassFor(slot!.Value, panelId, className, on);
        }
    }

    private void SetClassGroup(int? slot, bool broadcast, string panelId, string groupKey, string? className)
    {
        if (broadcast)
        {
            _hud.SetClassGroup(panelId, groupKey, className);
        }
        else
        {
            _hud.SetClassGroupFor(slot!.Value, panelId, groupKey, className);
        }
    }

    private void Show(int? slot, bool broadcast, string panelId, bool visible)
    {
        if (broadcast)
        {
            _hud.Show(panelId, visible);
        }
        else
        {
            _hud.ShowFor(slot!.Value, panelId, visible);
        }
    }
}
