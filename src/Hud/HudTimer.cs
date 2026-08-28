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
/// Short word shown instead of a number when there is no meaningful time to display - "READY",
/// "ACTIVE", "CHARGING". Null means show the numeric readout.
/// </param>
public readonly record struct HudTimer(
    HudTimerKind Kind,
    float SecondsRemaining,
    float Fraction,
    string? Status)
{
    /// <summary>A cooldown or duration counting down to zero.</summary>
    public static HudTimer Countdown(float secondsRemaining, string? status = null)
        => new(HudTimerKind.Countdown, Math.Max(0f, secondsRemaining), 0f, status);

    /// <summary>A level that moves in both directions - fuel, concealment, a ramping rate.</summary>
    public static HudTimer Gauge(float fraction, string? status = null)
        => new(HudTimerKind.Gauge, 0f, Math.Clamp(fraction, 0f, 1f), status);

    /// <summary>Nothing is counting; the modifier is simply available.</summary>
    public static HudTimer Ready(string status = "READY")
        => new(HudTimerKind.Countdown, 0f, 1f, status);
}
