using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;

using CSRoll.Core;

namespace CSRoll;

public partial class CSRoll
{
    private Guid _roundStartHookId;
    private Guid _roundEndHookId;

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
            foreach (var modifier in Runtime.ActiveModifiers.ToList())
            {
                modifier.Deactivate();
                modifier.Activate();
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
