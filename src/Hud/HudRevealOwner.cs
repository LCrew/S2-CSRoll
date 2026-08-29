namespace CSRoll.Hud;

/// <summary>
/// Who is currently responsible for the reveal card on one viewer's screen.
///
/// The card has two writers - <see cref="HudSequencer"/> animates it during a roll, and
/// <see cref="HudTracker"/> holds it open for spectators - and until this existed neither knew which of
/// them had put the card up. The tracker inferred it by asking whether the panel was hidden, which
/// cannot tell a live roll apart from a card the tracker itself is holding, so it tore down the
/// sequencer's card on its very next refresh: the description flashed up and vanished within a tenth of
/// a second, for the rolling player as much as for a spectator.
///
/// One writer at a time, named explicitly. Nothing may clear the card unless it owns it.
/// </summary>
public enum HudRevealOwner
{
    /// <summary>No card up, or one nobody has claimed - free for either writer to take.</summary>
    None,

    /// <summary>The sequencer is mid-roll. The tracker must not draw over it or clear it.</summary>
    Roll,

    /// <summary>The tracker is holding a spectator's card open. Only the tracker may close it.</summary>
    Spectator,
}
