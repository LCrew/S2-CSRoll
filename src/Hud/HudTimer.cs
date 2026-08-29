namespace CSRoll.Hud;

/// <summary>
/// What kind of bar a <see cref="HudTimer"/> wants, which decides how it is drawn.
///
/// The distinction is not cosmetic - it picks between two mechanisms with very different costs. A
/// countdown has a known duration and start, so it can be handed to a CSS transition and animate
/// client-side from a couple of network writes. A gauge moves in both directions at rates the server
/// cannot predict, so it has to be pushed as quantised width steps.
/// </summary>
public enum HudTimerKind
{
    /// <summary>Winds down to zero over a known duration. Drawn as a CSS transition.</summary>
    Countdown,

    /// <summary>An arbitrary 0..1 level that can move either way. Drawn as quantised width steps.</summary>
    Gauge,
}

/// <summary>
/// A modifier's live state for one player, as the custom HUD tracker wants to draw it.
/// </summary>
/// <param name="Kind">Which drawing mechanism applies - see <see cref="HudTimerKind"/>.</param>
/// <param name="SecondsRemaining">
/// Seconds left, for <see cref="HudTimerKind.Countdown"/>. Rendered exactly as text; the bar beside it
/// is quantised to the nearest rung of the duration ladder and so may finish slightly early or late.
/// </param>
/// <param name="Fraction">Fill level 0..1, for <see cref="HudTimerKind.Gauge"/>.</param>
/// <param name="Status">
/// Short word shown in the row's right-hand readout instead of a number - "READY", "ACTIVE". Null
/// means show the numeric countdown there.
/// </param>
/// <param name="Detail">
/// A full-width line below the bar, at the same size as the modifier's name. For state that genuinely
/// does not fit in the right-hand readout: the weapon WeaponRoulette has handed you, whether
/// ConditionalInvisibility currently has you hidden. Null hides the line and the row stays compact.
/// </param>
/// <param name="Prompt">
/// What the player has to DO - "PRESS INSPECT", "HOLD JUMP". Supplying one opts the modifier into the
/// helper card: a single prominent panel in the roll's slot showing the control, the cooldown and its
/// bar. That is the job the center-HTML gauges did, and it is the reason those gauges existed at all -
/// a tracker row is a list entry, not something you read mid-fight.
/// </param>
/// <param name="Tone">Colour for <paramref name="Detail"/>.</param>
public readonly record struct HudTimer(
    HudTimerKind Kind,
    float SecondsRemaining,
    float Fraction,
    string? Status,
    string? Detail = null,
    string? Prompt = null,
    HudTone Tone = HudTone.Neutral)
{
    /// <summary>A cooldown or duration counting down to zero.</summary>
    public static HudTimer Countdown(float secondsRemaining, string? status = null, string? detail = null, string? prompt = null, HudTone tone = HudTone.Neutral)
        => new(HudTimerKind.Countdown, Math.Max(0f, secondsRemaining), 0f, status, detail, prompt, tone);

    /// <summary>A level that moves in both directions - fuel, concealment, a ramping rate.</summary>
    public static HudTimer Gauge(float fraction, string? status = null, string? detail = null, string? prompt = null, HudTone tone = HudTone.Neutral)
        => new(HudTimerKind.Gauge, 0f, Math.Clamp(fraction, 0f, 1f), status, detail, prompt, tone);

    /// <summary>Nothing is counting; the modifier is simply available.</summary>
    public static HudTimer Ready(string status = "READY", string? detail = null, string? prompt = null, HudTone tone = HudTone.Neutral)
        => new(HudTimerKind.Countdown, 0f, 1f, status, detail, prompt, tone);
}

/// <summary>Colour for a tracker row's detail line. Deliberately semantic rather than named colours,
/// so the palette can change in one place in the stylesheet.</summary>
public enum HudTone
{
    /// <summary>Informational - the default.</summary>
    Neutral,

    /// <summary>Something is working in the player's favour: hidden, healing, ready.</summary>
    Good,

    /// <summary>Transitional or degrading: fading, low fuel.</summary>
    Warn,

    /// <summary>Actively bad or exposed: visible, out of fuel.</summary>
    Bad,
}
