using SwiftlyS2.Shared.Players;

using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Hud;

/// <summary>
/// The tracker's diagnostic surface - everything behind !hudstatus, and nothing that draws.
///
/// Split out because it grew to be a third of the file while answering a question the drawing code had
/// no part in: whether a viewer's rows were wrong because the wrong thing was computed, or because the
/// right thing never reached them. That question is now answered (a client off a playing team receives
/// no HUD state at all), but the reporting stays - it is what makes the next such question cheap.
/// </summary>
public sealed partial class HudTracker
{
    /// <summary>
    /// Every step of the spectator lookup, as text, for !hudstatus.
    ///
    /// The lookup has several places it can silently return nothing - no pawn, no observer services, a
    /// target that resolves to no player - and all of them look identical from outside: the tracker
    /// simply shows the viewer's own modifiers instead. Rather than guess which one, report all of them.
    /// </summary>
    public string DescribeSubject(IPlayer viewer)
    {
        var pawn = viewer.Pawn;
        if (pawn is null)
        {
            return $"alive={viewer.IsAlive}; Pawn is NULL - cannot read observer state";
        }

        var services = pawn.ObserverServices;
        if (services is null)
        {
            return $"alive={viewer.IsAlive}; Pawn ok, ObserverServices is NULL - not spectating, or the "
                 + "observer pawn is not the one Pawn returns";
        }

        var target = services.ObserverTarget.Value;
        if (target is null)
        {
            var (heldSlot, heldName) = ResolveSubject(viewer);
            return $"alive={viewer.IsAlive}; ObserverServices ok, mode={services.ObserverMode}, "
                 + $"ObserverTarget is NULL this tick - holding slot {heldSlot} ({heldName ?? "<self>"})";
        }

        var resolved = _core.PlayerManager.GetPlayerFromPawn(target.As<CBasePlayerPawn>());
        if (resolved is null)
        {
            return $"alive={viewer.IsAlive}; mode={services.ObserverMode}; target entity #{target.Index} "
                 + "found but GetPlayerFromPawn returned NULL";
        }

        var name = resolved.Controller is { IsValid: true } c ? c.PlayerName : "<no controller>";
        var (subject, spectatingName) = ResolveSubject(viewer);

        var drawn = _shownSubject.TryGetValue(viewer.Slot, out var shown) ? shown.ToString() : "<never>";
        var age = _lastDrawnAt.TryGetValue(viewer.Slot, out var at)
            ? $"{_core.Engine.GlobalVars.CurrentTime - at:0.0}s ago"
            : "NEVER - the refresh loop is not reaching you";

        // What the subject SHOULD show, and what was last sent to this viewer's row 0. If those disagree
        // the drawing is wrong; if they agree and the screen still differs, the viewer is one the HUD
        // cannot reach - which onSpectatorTeam above answers directly.
        var expected = _runtime.GetModifiersForSlot(subject)
            .Select(m => CSRollUtils.GetModifierDisplayName(_core, m))
            .ToList();

        var sent = _hud.GetSentTextFor(viewer.Slot, HudPanelIds.RowName(0), HudPanelIds.VarName) ?? "<nothing>";

        // The entity read-back that used to sit here is gone. GetDialogVariableStringForPlayer threw
        // NullReferenceException on most calls, so it never measured anything - and the question it was
        // meant to answer has since been answered by !hudprobe: a client off a playing team receives no
        // HUD state at all. There is nothing left for it to distinguish.
        return $"alive={viewer.IsAlive}; onSpectatorTeam={IsOnSpectatorTeam(viewer)}; "
             + $"mode={services.ObserverMode}; target=slot {resolved.Slot} "
             + $"({name}); resolved=slot {subject} ({spectatingName ?? "<self>"}); "
             + $"their modifiers=[{string.Join(", ", expected)}]; "
             + $"row0 sent=\"{sent}\"; reveal owner={_hud.RevealOwnerOf(viewer.Slot)}; "
             + $"last drew subject={drawn}, {age}; "
             + $"outcome={_lastOutcome.GetValueOrDefault(viewer.Slot, "<none>")}; "
             + $"{_hud.DescribeLoad()}";
    }

    /// <summary>
    /// Viewers on the spectator team, and whether their panels have already been told to clear.
    ///
    /// See <see cref="CustomHudConfig.SpectatorFallbackCenterHtml"/> - CS2 stops delivering custom HUD
    /// state to a client that is not on a playing team, so anything drawn for them from here is
    /// discarded and they keep whatever they last received while alive.
    /// </summary>
    private readonly HashSet<int> _clearedForSpectatorTeam = [];

    /// <summary>
    /// True when this viewer is on the spectator team, where custom HUD writes do not reach them.
    ///
    /// Deliberately NOT "is dead". A dead player on a playing team still receives HUD state, and
    /// treating them as unreachable would take the tracker away from most of the server for most of
    /// every round.
    /// </summary>
    private static bool IsOnSpectatorTeam(IPlayer viewer)
        => viewer.Controller is { IsValid: true } controller
        && controller.Team is not (Team.T or Team.CT);
}
