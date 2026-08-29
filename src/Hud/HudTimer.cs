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
    /// <summary>
    /// Something ACTIVE is running out - a Vanish window, a duration. The bar EMPTIES, because what it
    /// shows is how much of the thing you still have.
    /// </summary>
    Countdown,

    /// <summary>
    /// An ability RECHARGING. The bar FILLS, because what it shows is progress towards being able to
    /// use it again - full means ready.
    ///
    /// The distinction matters more than it looks: a draining bar next to "PRESS F TO FLANK" tells the
    /// player the opposite of the truth, since the bar is emptiest exactly when the ability is closest
    /// to being available.
    /// </summary>
    Cooldown,

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
/// <param name="TotalSeconds">
/// The full length of a <see cref="HudTimerKind.Cooldown"/>, needed to know how far along it is -
/// remaining alone only says how much is left, not what fraction that represents.
/// </param>
/// <param name="Status">
/// Short word shown in the row's right-hand readout instead of a number - "READY", "ACTIVE". Null
/// means show the numeric countdown there.
/// </param>
/// <param name="Detail">
/// A full-width line below the bar, at the same size as the modifier's name. For state that genuinely
/// does not fit in the right-hand readout: the weapon WeaponRoulette has handed you, whether
/// ConditionalInvisibility currently has you hidden. Null hides the line and the row stays compact.
/// </param>
/// <param name="HelpTop">
/// The helper card's line ABOVE the bar - "FUEL", "INVISIBLE". Use this when the state is the headline
/// and the bar quantifies it.
/// </param>
/// <param name="Prompt">
/// The helper card's line BELOW the bar - "PRESS F TO FLANK". Use this when the bar is a cooldown and
/// the headline is what to do once it fills.
///
/// Supplying either this or <paramref name="HelpTop"/> opts the modifier into the helper card: a
/// single prominent panel in the roll's slot. That is the job the center-HTML gauges did, and the
/// reason they existed - a tracker row is a list entry, not something you read mid-fight. The two
/// slots are what let a gauge modifier read label-then-bar and an ability read bar-then-prompt from
/// one fixed layout.
/// </param>
/// <param name="Tone">Colour for <paramref name="Detail"/>.</param>
public readonly record struct HudTimer(
    HudTimerKind Kind,
    float SecondsRemaining,
    float Fraction,
    float TotalSeconds,
    string? Status,
    string? Detail = null,
    string? HelpTop = null,
    string? Prompt = null,
    HudTone Tone = HudTone.Neutral)
{
    /// <summary>A cooldown or duration counting down to zero.</summary>
    public static HudTimer Countdown(float secondsRemaining, string? status = null, string? detail = null, string? helpTop = null, string? prompt = null, HudTone tone = HudTone.Neutral)
        => new(HudTimerKind.Countdown, Math.Max(0f, secondsRemaining), 0f, 0f, status, detail, helpTop, prompt, tone);

    /// <summary>A level that moves in both directions - fuel, concealment, a ramping rate.</summary>
    public static HudTimer Gauge(float fraction, string? status = null, string? detail = null, string? helpTop = null, string? prompt = null, HudTone tone = HudTone.Neutral)
        => new(HudTimerKind.Gauge, 0f, Math.Clamp(fraction, 0f, 1f), 0f, status, detail, helpTop, prompt, tone);

    /// <summary>Nothing is counting; the modifier is simply available.</summary>
    public static HudTimer Ready(string status = "READY", string? detail = null, string? helpTop = null, string? prompt = null, HudTone tone = HudTone.Neutral)
        => new(HudTimerKind.Countdown, 0f, 1f, 0f, status, detail, helpTop, prompt, tone);

    /// <summary>
    /// An ability recharging. Pass how long is left AND how long the cooldown is in total; the bar fills
    /// towards ready rather than emptying.
    /// </summary>
    public static HudTimer Cooldown(float secondsRemaining, float totalSeconds, string? status = null,
                                    string? detail = null, string? helpTop = null, string? prompt = null,
                                    HudTone tone = HudTone.Neutral)
        => new(HudTimerKind.Cooldown, Math.Max(0f, secondsRemaining), 0f, Math.Max(0.01f, totalSeconds),
               status, detail, helpTop, prompt, tone);
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
