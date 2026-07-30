using System.Globalization;
using System.Threading;

using Microsoft.Extensions.Logging;

namespace CSRoll.Modifiers;

/// <summary>
/// Bug fix/redesign: this used to be a static resources/ConVarModifiers/PlantAnywhere.cfg entry -
/// just mp_plant_c4_anywhere set on for the whole round with no timing control and no bomb-timer
/// change. Rewritten as a proper C# modifier: mp_plant_c4_anywhere only turns on
/// Config.PlantAnywhere.DelaySeconds into the round (before that, planting still requires a normal
/// bombsite), and mp_c4_timer is extended to Config.PlantAnywhere.BombTimerSeconds for the whole
/// activation. Both cvars are read and cached before being changed, then restored to whatever they
/// actually were once the round ends (OnDisabled - this codebase's existing round-cycle flow already
/// deactivates all active modifiers at round end via ModifierRuntime.RemoveAllModifiers).
///
/// Inherently global rather than per-player: there's exactly one bomb per round regardless of who's
/// carrying it, so "plant anywhere"/bomb timer aren't concepts that can be scoped to one specific
/// player the way movement/damage modifiers can.
///
/// Uses FindAsString/ValueAsString rather than the generic Core.ConVar.Find&lt;T&gt;(name) - confirmed
/// this session (MasterZeus's cooldown read) that guessing a cvar's exact native storage type is
/// risky and can silently misbehave; reading/writing as text sidesteps that entirely.
///
/// Bug fix: the bomb timer cvar name was wrong - "mp_c4_timer" doesn't exist in this CS2 build at
/// all (confirmed via live !debug logging: FindAsString returned null for it). The real name,
/// confirmed via a live CS2 server config bundled with MatchZy (a widely-used match plugin), is
/// "mp_c4timer" - no underscore between "c4" and "timer".
/// </summary>
public sealed class GameModifierPlantAnywhere : GameModifierBase
{
    private const string PlantAnywhereCvarName = "mp_plant_c4_anywhere";
    private const string C4TimerCvarName = "mp_c4timer";

    private string? _originalPlantAnywhereValue;
    private string? _originalC4TimerValue;
    private CancellationTokenSource? _delayToken;

    public override IReadOnlyDictionary<string, string>? DynamicTextTokens => new Dictionary<string, string>
    {
        ["delay"] = $"{Runtime.Config.PlantAnywhere.DelaySeconds:0.#}s",
        ["timer"] = $"{Runtime.Config.PlantAnywhere.BombTimerSeconds:0.#}s",
    };

    public override string Description =>
        $"Bomb can be planted anywhere after {Runtime.Config.PlantAnywhere.DelaySeconds:0.#}s into the round, bomb timer set to {Runtime.Config.PlantAnywhere.BombTimerSeconds:0.#}s";

    public GameModifierPlantAnywhere()
    {
        Name = "PlantAnywhere";
        SupportsRandomRounds = true;
        // Not per-player randomizable: see remarks - there's only ever one bomb per round.
        SupportsPerPlayerRandomization = false;
    }

    protected override void OnEnabled()
    {
        var plantAnywhereCvar = Core.ConVar.FindAsString(PlantAnywhereCvarName);
        _originalPlantAnywhereValue = plantAnywhereCvar?.ValueAsString;

        var timerCvar = Core.ConVar.FindAsString(C4TimerCvarName);
        _originalC4TimerValue = timerCvar?.ValueAsString;

        if (timerCvar is not null)
        {
            timerCvar.ValueAsString = Runtime.Config.PlantAnywhere.BombTimerSeconds.ToString(CultureInfo.InvariantCulture);

            // Diagnostic: live testing reported the bomb timer not actually changing. Logging the
            // read-back value immediately after the write distinguishes "the cvar doesn't exist under
            // this name" (already ruled out by reaching this branch), "the write is silently rejected
            // by the engine" (read-back would show the OLD value), from "the write genuinely sticks
            // but something reads the timer at a point earlier than this, or from a different cvar" -
            // the last case would need a different fix (e.g. setting it sooner, or a different name).
            if (Runtime.DebugMode)
            {
                Core.Logger.LogInformation("[CSRoll] PlantAnywhere: {Cvar} original={Original} target={Target} read-back={ReadBack}",
                    C4TimerCvarName, _originalC4TimerValue, Runtime.Config.PlantAnywhere.BombTimerSeconds, timerCvar.ValueAsString);
            }
        }
        else if (Runtime.DebugMode)
        {
            Core.Logger.LogWarning("[CSRoll] PlantAnywhere: {Cvar} was not found via FindAsString - the cvar name may be wrong for this game build.", C4TimerCvarName);
        }

        _delayToken = Core.Scheduler.DelayBySeconds(Runtime.Config.PlantAnywhere.DelaySeconds, () =>
        {
            if (Core.ConVar.FindAsString(PlantAnywhereCvarName) is { } cvar)
            {
                cvar.ValueAsString = "true";
            }
        });
    }

    protected override void OnDisabled()
    {
        _delayToken?.Cancel();
        _delayToken = null;

        if (_originalPlantAnywhereValue is not null && Core.ConVar.FindAsString(PlantAnywhereCvarName) is { } plantAnywhereCvar)
        {
            plantAnywhereCvar.ValueAsString = _originalPlantAnywhereValue;
        }

        if (_originalC4TimerValue is not null && Core.ConVar.FindAsString(C4TimerCvarName) is { } timerCvar)
        {
            timerCvar.ValueAsString = _originalC4TimerValue;
        }

        _originalPlantAnywhereValue = null;
        _originalC4TimerValue = null;
    }
}
