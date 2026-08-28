namespace CSRoll.Hud;

/// <summary>
/// The two interchangeable fill elements of one progress bar.
///
/// A transition is restarted by writing "snap to full, duration 0" followed by "drain, duration N".
/// Those two writes cannot share a tick: class state reaches the client as an entity netvar diff, so
/// the element would never observe the full state and would render already empty.
///
/// The original plan was to sidestep that by alternating between two elements. In practice the service
/// solves it with a short delay between the two phases instead, which is simpler and does not depend on
/// the pair staying in step - so only <see cref="FillA"/> is driven today.
///
/// <see cref="FillB"/> is kept because the DOM is static and ships inside a Workshop addon: removing it
/// would save nothing, while needing it back later would cost a republish and a re-download for every
/// player. It is held at the ready for a future two-element effect (a trailing "ghost" bar showing the
/// previous value, for instance).
/// </summary>
public readonly record struct HudBar(string FillA, string FillB);
