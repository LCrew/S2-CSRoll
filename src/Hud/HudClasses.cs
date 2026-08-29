namespace CSRoll.Hud;

/// <summary>
/// Every CSS class name the server can toggle on the custom HUD, plus the two "ladders" that stand in
/// for values the API cannot express.
///
/// The server cannot set a width, a duration, or an image - it can only add and remove classes
/// (CCSCustomHudLayout.SetHasClass). So anything continuous has to be quantised into a fixed set of
/// classes declared in the stylesheet: <see cref="Duration"/> for "animate over N seconds" and
/// <see cref="Width"/> for "this bar is X% full". Same contract as HudPanelIds - no class-name literal
/// lives outside this file, and `-Action Validate` diffs <see cref="All"/> against the selectors in
/// hud/styles/csroll_hud.css.
/// </summary>
public static class HudClasses
{
    // ---------------------------------------------------------------------------------------------
    // Group keys. Not class names - keys the HUD service uses to remember which class of a mutually
    // exclusive set is currently applied to a panel, so swapping one costs a single remove + add
    // instead of removing all 47 icon classes to set one.
    // ---------------------------------------------------------------------------------------------

    public const string GroupAccent = "accent";

    /// <summary>Group key for the detail line's tone classes.</summary>
    public const string GroupTone = "tone";
    public const string GroupDuration = "dur";
    public const string GroupWidth = "w";

    // ---------------------------------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------------------------------

    /// <summary>visibility: collapse. Show/hide flows through the same dirty cache as everything else.</summary>
    public const string Hidden = "hidden";

    /// <summary>On csr_root for the duration of a roll reveal. Pure CSS dims the tracker out of the way -
    /// the server does not need to know the tracker exists in order to get out of its own way.</summary>
    public const string RevealActive = "reveal-active";

    public const string RevealIn = "reveal-in";
    public const string RevealOut = "reveal-out";

    /// <summary>Drives the reel's travel. Paired with a `dur-*` class to set how long the spin takes.</summary>
    public const string Spinning = "spinning";

    /// <summary>Infinite rotation keyframe, for indeterminate "working" states.</summary>
    public const string Spin = "spin";

    /// <summary>Left-to-right mask sweep on a description line. Replaces the 20 server-pushed scramble
    /// frames the center-HTML path needs (see SpinRevealConfig.DescriptionScrambleFrames) with one class.</summary>
    public const string DescWipe = "desc-wipe";

    /// <summary>Turns a tracker/card row into the "+N more" overflow row.</summary>
    public const string Overflow = "overflow";

    /// <summary>A row currently holds a modifier (as opposed to being a spare declared in the layout).</summary>
    public const string Active = "is-active";

    /// <summary>Outlines every panel. Set by !rollhudtest so an unaddressed panel is visible.</summary>
    public const string Debug = "debug";

    /// <summary>Tone classes for a tracker row's detail line.</summary>
    public const string ToneGood = "tone-good";
    public const string ToneWarn = "tone-warn";
    public const string ToneBad = "tone-bad";

    /// <summary>The class for a tone, or null for the neutral default.</summary>
    public static string? Tone(HudTone tone) => tone switch
    {
        HudTone.Good => ToneGood,
        HudTone.Warn => ToneWarn,
        HudTone.Bad => ToneBad,
        _ => null,
    };

    // ---------------------------------------------------------------------------------------------
    // Bars
    // ---------------------------------------------------------------------------------------------

    /// <summary>Target state: width 0%. Combined with a `dur-*` class this is the whole countdown animation.</summary>
    public const string Drain = "drain";

    /// <summary>Target state: width 100%, for bars that fill rather than drain.</summary>
    public const string Fill = "fill";

    /// <summary>
    /// The duration ladder, in seconds. A `dur-N` class sets transition-duration: N s, so a countdown
    /// costs a couple of network writes for the entire animation instead of one per frame.
    ///
    /// The cost of quantising is that a 22s cooldown draws on the 20s bar and empties ~2s early. That
    /// is deliberate and documented: the numeric readout beside the bar is exact, the bar is decoration.
    /// Adding rungs is free in CSS but requires republishing the Workshop addon.
    /// </summary>
    public static readonly IReadOnlyList<float> DurationLadder = [0f, 1f, 2f, 3f, 5f, 8f, 10f, 15f, 20f, 25f, 30f, 45f, 60f];

    /// <summary>Instant - used to snap a bar back to full before restarting its transition.</summary>
    public const string DurationInstant = "dur-0";

    /// <summary>Nearest rung of <see cref="DurationLadder"/> to <paramref name="seconds"/>.</summary>
    public static string Duration(float seconds)
    {
        var best = DurationLadder[0];
        var bestDelta = float.MaxValue;

        foreach (var rung in DurationLadder)
        {
            var delta = Math.Abs(rung - seconds);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = rung;
            }
        }

        return FormatDuration(best);
    }

    private static string FormatDuration(float seconds)
        => $"dur-{(int)seconds}";

    // ---------------------------------------------------------------------------------------------
    // Widths
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Percentage step for the `w*` ladder: 1%, so 101 classes.
    ///
    /// It was 5%, which was fine while a CSS transition smoothed between buckets. Transitions turned
    /// out not to be dependable here - the same reason the reel ended up server-driven - so the steps
    /// themselves have to be the animation, and at 5% a three-second cooldown visibly jumped 100, 50,
    /// 0. At 1%, stepped ten times a second, the movement reads as continuous.
    ///
    /// 101 classes costs nothing in the stylesheet, and dirty tracking means a write only goes out when
    /// the bucket actually changes.
    /// </summary>
    public const int WidthStepPercent = 1;

    /// <summary>
    /// Quantised width class for a 0..1 fraction.
    ///
    /// This is the fallback for values that are NOT monotonic countdowns and so cannot use a transition:
    /// Jetpack fuel (drains and refills at different rates), ConditionalInvisibility's alpha, and
    /// Regeneration's ramping rate. For anything with a known duration and a known start, use
    /// <see cref="Duration"/> instead - it is an order of magnitude cheaper.
    /// </summary>
    public static string Width(float fraction)
    {
        var clamped = Math.Clamp(fraction, 0f, 1f);
        var bucket = (int)Math.Round(clamped * 100f / WidthStepPercent) * WidthStepPercent;
        return $"w{bucket}";
    }

    // ---------------------------------------------------------------------------------------------
    // Accents / icons
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The accent palette. These are thematic families (what a modifier *does*), deliberately NOT a
    /// rarity scale - every modifier in this plugin is drawn from a uniform random pool, so a rarity
    /// tier would be a lie told to the player.
    /// </summary>
    public static readonly IReadOnlyList<string> Accents =
        ["grey", "red", "orange", "amber", "green", "teal", "blue", "violet"];

    public const string AccentFallback = "accent-grey";

    /// <summary>Badge symbol used for any modifier without one of its own - including cvar-file modifiers,
    /// which cannot be listed in the presentation data ahead of time.</summary>
    public const string GlyphFallback = "◆";

    public static string Accent(string suffix) => $"accent-{suffix}";

    // ---------------------------------------------------------------------------------------------

    /// <summary>Every class the server can emit. Diffed against the stylesheet by `-Action Validate`.</summary>
    public static IReadOnlyList<string> All { get; } = BuildAll();

    private static string[] BuildAll()
    {
        var all = new List<string>
        {
            Hidden, RevealActive, RevealIn, RevealOut, Spinning, Spin, DescWipe, Overflow, Active, Debug,
            Drain, Fill, ToneGood, ToneWarn, ToneBad,
        };

        all.AddRange(DurationLadder.Select(FormatDuration));

        for (var percent = 0; percent <= 100; percent += WidthStepPercent)
        {
            all.Add($"w{percent}");
        }

        all.AddRange(Accents.Select(Accent));

        return [.. all];
    }
}
