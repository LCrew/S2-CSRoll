using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;

using CSRoll.Core;

namespace CSRoll;

public partial class CSRoll
{
    private Guid _roundStartHookId;
    private Guid _roundEndHookId;
    private float _lastRoundStartHandledTime = float.NegativeInfinity;

    // Bug fix: CS2 fires EventRoundStart twice in a row during the warmup-to-live-match transition
    // (see ModifierRuntime._rollGeneration's doc comment for the reveal-side half of this same
    // quirk) - without this, the automatic random-round roll ran twice for one real round, silently
    // costing every player one round of PerPlayerRepeatCooldownRounds cooldown history, and playing
    // the spin-reveal banner animation twice in a row. Two genuinely separate rounds are always
    // several seconds apart (round time + freeze time), so a second EventRoundStart arriving
    // implausibly soon after the last one is treated as the same underlying transition and skipped
    // entirely. Deliberately scoped to just this automatic per-round-start path, not
    // ApplyRandomRoundsForRound itself - !randomroundsreroll and other manual admin triggers call
    // that directly and are legitimate even in quick succession.
    private const float RoundStartDebounceSeconds = 2f;

    // Bug fix: these were [GameEventHandler(HookMode.Post)] attributes, relying on the same
    // SwiftlyS2 attribute-scanning auto-registration that turned out to double-register every
    // [Command] in this plugin (see CSRoll.Commands.cs). Converted to manual registration
    // for the same reason and to stay consistent with every other hook in this codebase.
    private void InitializeGameEvents()
    {
        _roundStartHookId = Core.GameEvent.HookPost<EventRoundStart>(OnRoundStart);
        _roundEndHookId = Core.GameEvent.HookPost<EventRoundEnd>(OnRoundEnd);
        Core.Event.OnMapLoad += OnMapLoad;
    }

    private void UninitializeGameEvents()
    {
        Core.GameEvent.Unhook(_roundStartHookId);
        Core.GameEvent.Unhook(_roundEndHookId);
        Core.Event.OnMapLoad -= OnMapLoad;
    }

    /// <summary>
    /// Bug fix: GlobalVars.CurrentTime is map-relative and restarts near zero on every map change,
    /// but any timestamp captured on the previous map keeps its old (large) value. Every
    /// "now - stored &lt; interval" or "now &lt; deadline" comparison then reads as though the interval
    /// had only just started, and stays that way until CurrentTime climbs back past the stale value.
    ///
    /// Reported live on a Best-of-3: after the first map change, random rounds never rolled again -
    /// OnRoundStart's debounce below saw a hugely negative delta, took its early return, and (because
    /// the timestamp is only written after that guard) never refreshed it, so every later round was
    /// skipped too. A manual re-roll still worked because it deliberately bypasses that debounce,
    /// and a plugin reload "fixed" it only because that reinitialises the field.
    ///
    /// Anything re-seeded on spawn or cleared on Deactivate self-heals; these are the values that
    /// survive a map change untouched, so they're reset explicitly here.
    /// </summary>
    private void OnMapLoad(SwiftlyS2.Shared.Events.IOnMapLoadEvent @event)
    {
        _lastRoundStartHandledTime = float.NegativeInfinity;
        Runtime.ResetMapRelativeTimeState();
    }

    public HookResult OnRoundStart(EventRoundStart @event)
    {
        if (Runtime.RandomRoundsEnabled)
        {
            var now = Core.Engine.GlobalVars.CurrentTime;

            // Time running backwards means the map clock restarted (see OnMapLoad). Belt-and-braces
            // alongside the OnMapLoad reset: without this, a stale future timestamp would make the
            // debounce below swallow every round start forever, since it returns before refreshing
            // the timestamp that would otherwise let it recover.
            if (now < _lastRoundStartHandledTime)
            {
                _lastRoundStartHandledTime = float.NegativeInfinity;
            }

            if (now - _lastRoundStartHandledTime < RoundStartDebounceSeconds)
            {
                return HookResult.Continue;
            }

            _lastRoundStartHandledTime = now;

            if (Runtime.RegisteredModifiers.Count == 0)
            {
                CSRollUtils.PrintTitleToChatAll(Core, "No registered modifiers found! Skipping random round...");
                return HookResult.Continue;
            }

            Runtime.RemoveAllModifiers();

            if (Config.DisableRandomRoundsInWarmup && CSRollUtils.IsWarmupActive(Core))
            {
                CSRollUtils.PrintTitleToChatAll(Core, "Random rounds will start after warmup period...");
            }
            else
            {
                // Bug fix: showing the center banner right here fires during the spawn/freeze-time
                // animation, before players have control - it either doesn't render yet or expires
                // before it's readable. Effects apply now as normal; ScheduleFreezeTimeBanner below
                // shows it a moment later, once the HUD has settled and players can actually read
                // it - still comfortably within freeze time, not just at the very end of it.
                Runtime.ApplyRandomRoundsForRound(showBanner: false);
                ScheduleFreezeTimeBanner();
            }
        }
        else
        {
            // Re-apply all active modifiers on round start in case anything was reset since last round.
            //
            // Bug fix: this used to call Deactivate() then Activate() with no arguments - Deactivate()
            // unconditionally clears GameModifierBase's own AssignedSlots, and Activate(slots) only
            // refills it when a non-null slots argument is passed. Every per-player-scoped modifier
            // (anything added via !memodifier, or any per-player random-round assignment) silently lost
            // its scoping and widened to "everyone" (empty AssignedSlots) starting from the very next
            // round transition. Invisible for cheap/instant reapply effects (RandomLoadout/WalkingGrenadier
            // just re-hand out weapons - looks the same for one player or all of them), but WeaponRoulette
            // reported this as "spreads to the whole server and won't go away" - explicitly re-passing
            // each modifier's own current slots preserves its scope across this reapply.
            foreach (var modifier in Runtime.ActiveModifiers.ToList())
            {
                var slots = modifier.AssignedSlots.ToList();
                modifier.Deactivate();
                modifier.Activate(slots);
            }

            ScheduleFreezeTimeBanner();
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// Plays the spin-then-reveal a short moment into freeze time (not immediately at round start,
    /// which fires before the HUD is ready to render it) so players actually get to watch it while
    /// they're standing still. The reveal itself now stays on screen for SpinReveal.RevealDurationSeconds
    /// (15s by default), so there's no separate re-display once freeze time ends - EventRoundFreezeEnd
    /// used to trigger a second, non-animated "Activating Modifiers" redisplay here, which is gone now.
    /// </summary>
    private void ScheduleFreezeTimeBanner()
    {
        Core.Scheduler.DelayBySeconds(1.0f, () => Runtime.PlaySpinThenRevealActiveModifiersBanner());
    }

    public HookResult OnRoundEnd(EventRoundEnd @event)
    {
        if (Runtime.RandomRoundsEnabled)
        {
            Runtime.RemoveAllModifiers();
        }

        return HookResult.Continue;
    }
}
