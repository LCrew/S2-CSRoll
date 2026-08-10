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
    }

    private void UninitializeGameEvents()
    {
        Core.GameEvent.Unhook(_roundStartHookId);
        Core.GameEvent.Unhook(_roundEndHookId);
    }

    public HookResult OnRoundStart(EventRoundStart @event)
    {
        if (Runtime.RandomRoundsEnabled)
        {
            var now = Core.Engine.GlobalVars.CurrentTime;
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
            // round transition. Invisible for cheap/instant reapply effects (RandomLoadout/GrenadesOnly
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
