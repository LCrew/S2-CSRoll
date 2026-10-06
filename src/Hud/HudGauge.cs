namespace CSRoll.Hud;

/// <summary>
/// One live readout on the custom HUD's bottom strip - the structured twin of the center-HTML block a
/// modifier draws with SetHud. Label names the thing ("Jetpack fuel"), Value is the short readout on
/// the right ("64%", "Ready · Inspect", "AK-47 · 12.4s"), Fill is the bar from 0 to 1, and Ready
/// colours the readout with the modifier's accent for a "usable now" state.
/// </summary>
public readonly record struct HudGauge(string Label, string Value, float Fill, bool Ready = false);
