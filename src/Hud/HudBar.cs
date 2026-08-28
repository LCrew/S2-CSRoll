namespace CSRoll.Hud;

/// <summary>
/// The two interchangeable fill elements of one progress bar.
///
/// A CSS width transition is restarted by writing "snap to full, duration 0" followed by "drain,
/// duration N". If the client collapses both writes into a single render frame, the element never
/// observes the intermediate state and the bar jumps straight to empty instead of animating. Handing
/// each restart to whichever element is currently at rest sidesteps the race entirely.
///
/// Both elements are declared in the shipped Panorama layout from the very first published version -
/// the DOM is static and lives in a Workshop addon, so adding the second one later would mean a
/// republish and a re-download for every player.
/// </summary>
public readonly record struct HudBar(string FillA, string FillB)
{
    /// <summary>The fill to drive next, given how many times this bar has already been started.</summary>
    public string Fill(int startCount) => (startCount & 1) == 0 ? FillA : FillB;

    /// <summary>The fill that was driven last, and so needs clearing when the next start takes over.</summary>
    public string Other(int startCount) => (startCount & 1) == 0 ? FillB : FillA;
}
