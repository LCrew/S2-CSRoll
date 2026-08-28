namespace CSRoll.Hud;

/// <summary>
/// Every panel id this plugin will ever address on the custom HUD, in one place.
///
/// This exists because a wrong panel id has NO runtime signal: CCSCustomHudLayout.SetDialogVariableString
/// against an id that isn't in the layout silently does nothing - no exception, no log, no visual
/// difference from "the value happened to be empty". A typo'd literal scattered through the codebase
/// would be found by a player, not by us. So: no panel-id string literal exists anywhere outside this
/// file, indexed panels are reached through accessors that throw on an out-of-range index, and
/// <see cref="All"/> is diffed against the actual ids in hud/layout/csroll_hud.xml by
/// `tools/build_hud_resources.ps1 -Action Validate`, which fails the build on any asymmetry in either
/// direction.
///
/// Naming: csr_&lt;area&gt;[&lt;index&gt;][_&lt;role&gt;], lowercase snake. The csr_ prefix is a namespace - another
/// plugin's layout mounted alongside ours must not be able to collide with these.
///
/// IMPORTANT: the counts below are baked into the published Workshop addon. Raising one is not a code
/// change - it requires recompiling the Panorama layout, republishing the Workshop item, and every
/// client re-downloading it. They are deliberately provisioned above what the plugin currently uses.
/// </summary>
public static class HudPanelIds
{
    /// <summary>The dialog-variable name used by every text-bearing panel (`{s:name}` in the XML).</summary>
    public const string VarName = "name";

    /// <summary>Dialog-variable name for a modifier description line (`{s:desc}`).</summary>
    public const string VarDesc = "desc";

    /// <summary>Dialog-variable name for a countdown/duration readout (`{s:time}`).</summary>
    public const string VarTime = "time";

    // ---------------------------------------------------------------------------------------------
    // Root
    // ---------------------------------------------------------------------------------------------

    /// <summary>Full-screen, hittest=false container. Carries the `reveal-active` state class that
    /// dims the tracker out of the way during a roll reveal - see HudSequencer.</summary>
    public const string Root = "csr_root";

    /// <summary>Build stamp label. Lets you tell a client running a stale addon from a broken one.</summary>
    public const string Version = "csr_version";

    // ---------------------------------------------------------------------------------------------
    // Spin reel
    // ---------------------------------------------------------------------------------------------

    public const string Spin = "csr_spin";

    /// <summary>The strip that actually translates. One duration class + one state class animates the
    /// entire spin client-side; the server never pushes per-frame text.</summary>
    public const string SpinReel = "csr_spin_reel";

    /// <summary>
    /// Reel row count. The spin runs ~4s (SpinRevealConfig.SpinCount x the eased interval), so 20 rows
    /// is roughly one name per 200ms of travel - enough for the strip to read as a blur rather than a
    /// list. The last row is always the real result.
    /// </summary>
    public const int ReelRows = 20;

    private static readonly string[] _reelRows = Build(ReelRows, i => $"csr_reel{i}");

    public static string ReelRow(int index) => _reelRows[index];

    /// <summary>The reel row the spin lands on - always the last one, and the only one holding a real result.</summary>
    public static string ReelLandingRow() => _reelRows[ReelRows - 1];

    // ---------------------------------------------------------------------------------------------
    // Reveal card
    // ---------------------------------------------------------------------------------------------

    public const string Reveal = "csr_reveal";
    public const string RevealTitle = "csr_reveal_title";

    /// <summary>
    /// How many modifiers the reveal card can show at once. MinRandomRounds/MaxRandomRounds default to
    /// 1, but ButterflyEffect and Mimic exist specifically to stack a second, and !memodifier can add a
    /// third on top - so 3 covers the realistic case and <see cref="CardOverflow"/> catches the rest.
    /// </summary>
    public const int Cards = 3;

    private static readonly string[] _cards = Build(Cards, i => $"csr_card{i}");
    private static readonly string[] _cardIcons = Build(Cards, i => $"csr_card{i}_icon");
    private static readonly string[] _cardNames = Build(Cards, i => $"csr_card{i}_name");
    private static readonly string[] _cardDescs = Build(Cards, i => $"csr_card{i}_desc");

    public static string Card(int index) => _cards[index];
    public static string CardIcon(int index) => _cardIcons[index];
    public static string CardName(int index) => _cardNames[index];
    public static string CardDesc(int index) => _cardDescs[index];

    /// <summary>"+N more" row, shown when a roll produced more modifiers than there are cards. Nothing
    /// is ever silently dropped from the player's view.</summary>
    public const string CardOverflow = "csr_card_more";

    // ---------------------------------------------------------------------------------------------
    // Active-modifier tracker
    // ---------------------------------------------------------------------------------------------

    public const string Track = "csr_track";
    public const string TrackTitle = "csr_track_title";

    /// <summary>
    /// Tracker row count. Same reasoning as <see cref="Cards"/> but with more headroom, since a global
    /// !rolltoggle modifier stacks on top of whatever the player rolled. Row (Rows - 1) doubles as the
    /// "+N more" row when the list overflows.
    /// </summary>
    public const int Rows = 6;

    private static readonly string[] _rows = Build(Rows, i => $"csr_row{i}");
    private static readonly string[] _rowIcons = Build(Rows, i => $"csr_row{i}_icon");
    private static readonly string[] _rowNames = Build(Rows, i => $"csr_row{i}_name");
    private static readonly string[] _rowTimes = Build(Rows, i => $"csr_row{i}_time");
    private static readonly string[] _rowBars = Build(Rows, i => $"csr_row{i}_bar");
    private static readonly string[] _rowBarsA = Build(Rows, i => $"csr_row{i}_bar_a");
    private static readonly string[] _rowBarsB = Build(Rows, i => $"csr_row{i}_bar_b");

    public static string Row(int index) => _rows[index];
    public static string RowIcon(int index) => _rowIcons[index];
    public static string RowName(int index) => _rowNames[index];
    public static string RowTime(int index) => _rowTimes[index];
    public static string RowBar(int index) => _rowBars[index];

    /// <summary>
    /// First of the two interchangeable fill elements in a row's bar.
    ///
    /// Two fills per bar is not redundancy - it's the fix for a real Panorama behaviour. Restarting a
    /// CSS width transition means writing "snap back to full with duration 0" and then "drain with
    /// duration N"; if the client coalesces both into one frame, the bar jumps to the end instead of
    /// animating. Alternating between two pre-declared elements guarantees every transition starts on
    /// an element that was already at rest. Since the DOM is static and shipped in the Workshop addon,
    /// this cannot be added later without a republish - so both exist from the first version, even
    /// while only one is in use.
    /// </summary>
    public static string RowBarA(int index) => _rowBarsA[index];

    /// <inheritdoc cref="RowBarA"/>
    public static string RowBarB(int index) => _rowBarsB[index];

    /// <summary>Both fills of a row's bar, as the pair the HUD service alternates between.</summary>
    public static HudBar RowBarPair(int index) => new(_rowBarsA[index], _rowBarsB[index]);

    // ---------------------------------------------------------------------------------------------
    // Reserved for the phase-2 migration of the nine center-HTML modifier gauges and the spectator HUD.
    // Declared now because adding DOM later costs a full Workshop republish and a client re-download.
    // ---------------------------------------------------------------------------------------------

    public const string Self = "csr_self";
    public const int SelfLines = 3;
    private static readonly string[] _selfLines = Build(SelfLines, i => $"csr_self{i}");
    public static string SelfLine(int index) => _selfLines[index];

    public const string Spectator = "csr_spec";
    public const string SpectatorTitle = "csr_spec_title";
    public const int SpectatorRows = 6;
    private static readonly string[] _specRows = Build(SpectatorRows, i => $"csr_spec{i}");
    public static string SpectatorRow(int index) => _specRows[index];

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Every id above, flattened. `-Action Validate` diffs this against the ids present in
    /// hud/layout/csroll_hud.xml in both directions - an id here that the layout lacks is a silently
    /// dead write, and an id in the layout that isn't here is dead weight in the published addon.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = BuildAll();

    private static string[] Build(int count, Func<int, string> format)
        => Enumerable.Range(0, count).Select(format).ToArray();

    private static string[] BuildAll()
    {
        var all = new List<string>
        {
            Root, Version,
            Spin, SpinReel,
            Reveal, RevealTitle, CardOverflow,
            Track, TrackTitle,
            Self, Spectator, SpectatorTitle,
        };

        all.AddRange(_reelRows);
        all.AddRange(_cards);
        all.AddRange(_cardIcons);
        all.AddRange(_cardNames);
        all.AddRange(_cardDescs);
        all.AddRange(_rows);
        all.AddRange(_rowIcons);
        all.AddRange(_rowNames);
        all.AddRange(_rowTimes);
        all.AddRange(_rowBars);
        all.AddRange(_rowBarsA);
        all.AddRange(_rowBarsB);
        all.AddRange(_selfLines);
        all.AddRange(_specRows);

        return [.. all];
    }
}
