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
    /// How long the landed card takes to fade out, in seconds. Matches the reveal-out transition in
    /// csroll_hud.css; the panel is hidden after it so a collapsed panel never cuts the fade short.
    /// </summary>
    private const float RevealFadeOutSeconds = 0.3f;

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

        FillReel(slot, broadcast, landing);

        SetText(slot, broadcast, HudPanelIds.RevealTitle, HudPanelIds.VarName, "ROLLING");
        Show(slot, broadcast, HudPanelIds.Spin, true);
        SetClassGroup(slot, broadcast, HudPanelIds.SpinReel, HudClasses.GroupDuration, HudClasses.Duration(spinSeconds));
        SetClass(slot, broadcast, HudPanelIds.SpinReel, HudClasses.Spinning, true);

        // The reel travels client-side, but the tick sound still has to be emitted per frame - that is
        // the only way it is audible. This chain sends no text, only sound, on the same eased schedule
        // the center-HTML spin uses.
        PlayTickSound(slot, broadcast, 0, Config.SpinReveal.SpinCount, generation);

        _core.Scheduler.DelayBySeconds(spinSeconds, () =>
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

            // Landing frame. Commit FIRST, before any presentation, so the mechanical effect lands with
            // the reveal rather than after it.
            onRevealed();

            SetClass(slot, broadcast, HudPanelIds.SpinReel, HudClasses.Spinning, false);
            Show(slot, broadcast, HudPanelIds.Spin, false);
            ShowCard(slot, broadcast, modifiers, generation);
        });
    }

    /// <summary>
    /// Loads the reel with random modifier names and puts the real result in the landing row.
    ///
    /// Names come from the same registered pool the center-HTML spin draws from, so the reel reads as
    /// plausible rather than as filler.
    /// </summary>
    private void FillReel(int? slot, bool broadcast, string landingName)
    {
        var pool = _runtime.RegisteredModifiers;

        for (var row = 0; row < HudPanelIds.ReelRows - 1; row++)
        {
            var name = pool.Count > 0
                ? CSRollUtils.GetModifierDisplayName(_core, pool[Random.Shared.Next(pool.Count)])
                : string.Empty;

            SetText(slot, broadcast, HudPanelIds.ReelRow(row), HudPanelIds.VarName, name);
        }

        SetText(slot, broadcast, HudPanelIds.ReelLandingRow(), HudPanelIds.VarName, landingName);
    }

    private void PlayTickSound(int? slot, bool broadcast, int frameIndex, int totalFrames, int generation)
    {
        if (frameIndex >= totalFrames || _runtime.RollGeneration != generation || string.IsNullOrEmpty(Config.SpinReveal.TickSoundEventName))
        {
            return;
        }

        if (broadcast)
        {
            CSRollUtils.PlaySoundToAll(_core, Config.SpinReveal.TickSoundEventName, Config.SpinReveal.TickSoundVolume, debugMode: _runtime.DebugMode);
        }
        else if (_core.PlayerManager.GetPlayer(slot!.Value) is { IsValid: true } player)
        {
            CSRollUtils.PlaySoundToPlayer(_core, player, Config.SpinReveal.TickSoundEventName, Config.SpinReveal.TickSoundVolume, debugMode: _runtime.DebugMode);
        }
        else
        {
            return;
        }

        var interval = _runtime.SpinFrameIntervalSeconds(frameIndex, totalFrames);
        _core.Scheduler.DelayBySeconds(interval, () => PlayTickSound(slot, broadcast, frameIndex + 1, totalFrames, generation));
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
