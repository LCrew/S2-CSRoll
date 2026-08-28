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
/// setters. That is not a limitation in practice - OnTick, HookPost&lt;EventRoundStart&gt; and
/// Scheduler.DelayBySeconds callbacks all run on the main thread, and the paths that don't (menu
/// selections, command handlers) already hop through Core.Scheduler.NextWorldUpdate before touching
/// the runtime at all. Any future off-thread caller must do the same rather than reaching for the
/// Async variants piecemeal.
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

    /// <summary>Sets a dialog-variable string that only this player sees, overriding the global value.</summary>
    void SetTextFor(int slot, string panelId, string variable, string value);

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
    void StartBarFor(int slot, in HudBar bar, float seconds);

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
    void SyncBarFor(int slot, in HudBar bar, float secondsRemaining);

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
