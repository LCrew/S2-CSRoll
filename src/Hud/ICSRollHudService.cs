using CSRoll.Config;

namespace CSRoll.Hud;

/// <summary>
/// The plugin's entire interface to the CS2 custom HUD.
///
/// Everything that touches <c>CCSCustomHudLayout</c> lives behind this one type on purpose. The
/// underlying API landed in SwiftlyS2 1.4.6 and is flagged beta by both Valve and SwiftlyS2, so a
/// minor bump could rename a method or reshape an overload; containing it here means that is one
/// file to fix rather than a search across ModifierRuntime, the sequencer, the tracker and 42
/// modifier files.
///
/// THREAD SAFETY: every method here is main-thread only and uses the synchronous (non-Async) entity
/// setters. OnTick, HookPost&lt;EventRoundStart&gt; and Scheduler.DelayBySeconds callbacks all run
/// there; command and menu handlers do NOT, and must hop through Core.Scheduler.NextWorldUpdate first
/// rather than reaching for the Async variants piecemeal.
///
/// That hop is not a formality. Off the main thread these setters fail SILENTLY - no exception, so
/// nothing is logged and the caller reports success. Two diagnostic commands were written without it
/// and spent several rounds producing confident, entirely fictional evidence: a test notice that never
/// appeared and a probe value that never landed, both read as proof that the client was ignoring
/// per-player writes. Anything here that appears to do nothing is worth checking against this first.
/// </summary>
public interface ICSRollHudService
{
    /// <summary>
    /// True only when the HUD is enabled in config AND a live layout entity exists. Every caller gates
    /// on this; when it is false the plugin's original center-HTML behaviour is what players get.
    /// </summary>
    bool Available { get; }

    /// <summary>Subscribes events and schedules entity creation. A no-op when the HUD is disabled in
    /// config, so a server that never installs the Workshop addon pays nothing at all.</summary>
    void Install();

    /// <summary>Unsubscribes, despawns the layout entity, and clears all cached state.</summary>
    void Uninstall();

    /// <summary>Re-reads the HUD section after a config.jsonc hot reload.</summary>
    void OnConfigReloaded(CSRollConfig config);

    /// <summary>Human-readable state for the !hudstatus admin command: entity presence, index, layout
    /// path, and the last creation failure if there was one.</summary>
    string DescribeStatus();

    // --- text -------------------------------------------------------------------------------------

    /// <summary>Sets a dialog-variable string for everyone. `{s:variable}` on the panel with this id.</summary>
    void SetText(string panelId, string variable, string value);

    /// <summary>
    /// Sets a dialog-variable string that only this player sees, overriding the global value.
    /// </summary>
    /// <param name="force">
    /// Re-send even when the cache says the value is unchanged.
    ///
    /// The cache exists so an idle HUD costs nothing, and it assumes every write it issues arrives. When
    /// one does not, it becomes the reason the client never recovers: it believes the client already has
    /// the value and skips the write forever. That is exactly what left a spectator looking at the
    /// previous player's modifier while the server reported having sent the right one.
    ///
    /// Use it where correctness matters more than the write count - the spectator view, which is
    /// re-derived from scratch each refresh anyway - and leave it off everywhere else.
    /// </param>
    void SetTextFor(int slot, string panelId, string variable, string value, bool force = false);

    // --- classes ----------------------------------------------------------------------------------

    void SetClass(string panelId, string className, bool on);

    void SetClassFor(int slot, string panelId, string className, bool on);

    /// <summary>
    /// Applies one class out of a mutually exclusive group (icon-*, accent-*, dur-*, w*), removing
    /// whichever member of that group was previously applied. Pass null to clear the group.
    ///
    /// The group is identified by a key rather than a list of every possible member, so swapping an
    /// icon costs one removal plus one addition instead of clearing all 47 icon classes to set one.
    /// </summary>
    void SetClassGroup(string panelId, string groupKey, string? className);

    /// <inheritdoc cref="SetClassGroup"/>
    void SetClassGroupFor(int slot, string panelId, string groupKey, string? className);

    // --- visibility -------------------------------------------------------------------------------

    void Show(string panelId, bool visible);

    void ShowFor(int slot, string panelId, bool visible);

    // --- bars -------------------------------------------------------------------------------------

    /// <summary>
    /// Animates a bar from full to empty over <paramref name="seconds"/>, client-side, from a handful
    /// of network writes - the correct choice for anything with a known duration and start instant.
    ///
    /// The duration is quantised to the nearest rung of <see cref="HudClasses.DurationLadder"/>, so the
    /// bar may finish up to a couple of seconds early or late on an unusual config value. Any numeric
    /// countdown shown beside it stays exact; the bar is decoration.
    /// </summary>
    void StartBarFor(int slot, in HudBar bar, float seconds, bool fillUp = false);

    /// <summary>
    /// Sets a bar to a fraction of full, snapped to the nearest 5%. For gauges that are not monotonic
    /// countdowns and so cannot be expressed as a transition - jetpack fuel, invisibility alpha,
    /// regeneration rate. Costs a write only when the bucket actually changes.
    /// </summary>
    void SetBarFor(int slot, in HudBar bar, float fraction);

    /// <summary>
    /// Starts a countdown bar only if one is not already running towards the same deadline.
    ///
    /// This is the call to use from anything that polls. A bar is not a value you can idempotently
    /// re-assert: re-issuing <see cref="StartBarFor"/> on every tracker refresh would restart the
    /// transition several times a second and the bar would never visibly move.
    /// </summary>
    /// <param name="fillUp">
    /// True for a cooldown - the bar fills towards ready. False for a duration - it empties as the
    /// thing runs out. Getting this backwards tells the player the opposite of the truth.
    /// </param>
    void SyncBarFor(int slot, in HudBar bar, float secondsRemaining, bool fillUp = false);

    /// <summary>Countdown text and its bar together, with the same already-running guard as
    /// <see cref="SyncBarFor"/>.</summary>
    void SyncCountdownFor(int slot, string panelId, string variable, in HudBar bar, float secondsRemaining);

    /// <summary>Clears both fills of a bar and forgets its alternation state.</summary>
    void StopBarFor(int slot, in HudBar bar);

    // --- countdown text ---------------------------------------------------------------------------

    /// <summary>
    /// Drives a numeric countdown into a dialog variable from the service's own pump, so callers do not
    /// each grow a scheduler chain. Formatting is whole seconds above five, one decimal below, then
    /// "READY" - which, combined with dirty tracking, means roughly one network write per second for
    /// most of a cooldown's life.
    /// </summary>
    void StartCountdownFor(int slot, string panelId, string variable, float seconds);

    void StopCountdownFor(int slot, string panelId, string variable);

    // --- lifecycle --------------------------------------------------------------------------------

    /// <summary>
    /// Shows a short-lived message to one player in the HUD's bottom slot - "no enemy to flank", "out
    /// of fuel", anything a modifier needs to say in the moment.
    ///
    /// Repeated calls replace the current message and restart its timer, so a modifier that fires this
    /// on every failed attempt cannot stack notices or make one linger. Safe to call at any rate; it is
    /// throttled internally against the identical message.
    /// </summary>
    /// <param name="slot">The player who sees it.</param>
    /// <param name="message">Plain text. Chat colour tokens are not parsed here - strip them first.</param>
    /// <param name="seconds">How long it stays up.</param>
    void ShowNoticeFor(int slot, string message, float seconds = 2.5f);

    /// <summary>
    /// Whether a class is currently applied for a player, as far as the service's own cache knows.
    /// Diagnostic only - the cache is what it has SENT, which is the useful thing when a panel is
    /// invisible and the question is whether the server thinks it should be.
    /// </summary>
    bool IsClassSetFor(int slot, string panelId, string className);

    /// <summary>
    /// The last value the service SENT for a dialog variable, or null if it never sent one.
    ///
    /// Diagnostic only, and deliberately the cache rather than a read-back: when a panel shows the wrong
    /// text, the question is whether the server sent the right thing and it did not arrive, or the
    /// server sent the wrong thing. Nothing else can tell those apart.
    /// </summary>
    string? GetSentTextFor(int slot, string panelId, string variable);

    // --- reveal ownership -------------------------------------------------------------------------

    /// <summary>
    /// Records which subsystem owns the reveal card on this viewer's screen. See <see cref="HudRevealOwner"/>.
    ///
    /// Pure bookkeeping - it writes nothing to the entity. It exists so the two writers can tell each
    /// other apart rather than guessing from panel state, which they cannot do correctly.
    /// </summary>
    void ClaimReveal(int slot, HudRevealOwner owner);

    /// <inheritdoc cref="ClaimReveal"/>
    HudRevealOwner RevealOwnerOf(int slot);

    // --- diagnostics ------------------------------------------------------------------------------

    /// <summary>
    /// Reads a per-player dialog variable back OUT of the layout entity.
    ///
    /// Unlike <see cref="GetSentTextFor"/>, which reports what this service believes it sent, this is
    /// the value the entity itself is holding. Comparing the two splits the one question that has been
    /// impossible to answer from the server side: when the screen shows the wrong text, did the write
    /// never reach the entity, or did it reach the entity and never reach the client? Those have
    /// completely different fixes and nothing else distinguishes them.
    /// </summary>
    string? GetLiveTextFor(int slot, string panelId, string variable);

    /// <summary>
    /// How much per-player state the HUD is currently holding: override counts by kind, and the number
    /// of distinct slots they are spread across.
    ///
    /// Every per-player override is networked entity state, and this HUD writes far more of it than the
    /// single center-HTML string it replaced. If the client is silently dropping overrides past some
    /// capacity, the count is the first place that would show.
    /// </summary>
    string DescribeLoad();

    /// <summary>Hides a player's notice immediately.</summary>
    void ClearNoticeFor(int slot);

    /// <summary>
    /// Drops every per-player override for a slot. Slots are recycled by the next player to connect, so
    /// without this a joiner inherits the previous occupant's tracker rows and countdowns - the same
    /// hazard ModifierRuntime already handles for its center-HTML sections.
    /// </summary>
    void ResetPlayer(int slot);

    /// <summary>
    /// Forgets all cached HUD state without touching the entity. Called on map change, where the entity
    /// is already gone - issuing removal calls against a dead handle at that point is the single most
    /// likely way to crash this feature.
    /// </summary>
    void ResetAll();
}
