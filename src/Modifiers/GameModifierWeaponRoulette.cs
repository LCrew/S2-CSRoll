using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Forces the assigned player onto a single random gun (grenades untouched - buy/pickup of grenades
/// stays completely normal), re-rolled to a fresh weapon on a timer (Config.WeaponRoulette.
/// RerollIntervalSeconds). Reuses GameModifierRemoveWeapons for the strip/restore-on-disable and
/// CanAcquire.Pre buy-and-pickup block (same base RandomLoadout/GrenadesOnly already use), scoped to
/// CSRollUtils.AllGunWeaponTypes so grenades are explicitly excluded from the block.
///
/// Bug fix (structural rewrite): the spin used to be driven by a self-rescheduling chain of
/// Core.Scheduler.DelayBySeconds calls - a mechanism no other modifier in this codebase uses. Every
/// other tick-based modifier (FlankTeleport, Regeneration, etc.) drives 100% of its logic from
/// Core.Event.OnTick, so the instant OnDisabled() unsubscribes it, there is no code path left that
/// can still run - full stop, by construction. The DelayBySeconds chain lived entirely outside that
/// subscription and depended on manually re-checking generation/IsActive/IsAssignedTo on every single
/// frame to stop itself; despite that guard being logically correct on paper, it was reported live as
/// not actually stopping the spin/removing the modifier reliably. Rather than patch that guard again,
/// the spin is now driven by _spins (a per-slot state dict) advanced from the same OnGameTick loop
/// that already drives the reroll timer and countdown HUD - so disabling this modifier stops the spin
/// exactly the same way it stops everything else about it: unconditionally, with nothing left running.
///
/// Bug fix: the landing frame used to call CSRollUtils.StripWeaponTypes/ItemServices.GiveItem
/// directly - outside GameModifierRemoveWeapons' own _grantInProgress guard, which exists
/// specifically because this class's own CanAcquire.Pre hook blocks acquiring anything in
/// TypesToStrip, including its own forced grant (see that hook's bug-fix comment). Goes through the
/// base class's own (protected) StripWeapons instead, which wraps GiveReplacementWeapons in that
/// guard correctly.
///
/// Bug fix: a player receiving the modifier used to only get their first weapon via a separate
/// manual loop in OnEnabled, which meant anyone not yet alive at that exact moment (or connecting/
/// spawning mid-cycle) got nothing until the next scheduled reroll, up to RerollIntervalSeconds
/// later. GiveReplacementWeapons now triggers an immediate roll itself whenever it's asked to arm a
/// player with no weapon rolled yet for them - covering initial activation AND every later spawn
/// through the exact same code path (GameModifierRemoveWeapons' OnPlayerSpawn already calls this on
/// every spawn), so the separate OnEnabled loop is no longer needed.
///
/// Bug fix: the spin animation and the reroll-countdown HUD used to be two independent
/// SendCenterHTML call sites racing each other, each overwriting whatever the other had just shown -
/// visibly flickering/interrupting the spin. Unified into one BuildStatusHtml template (title +
/// timer-or-"Rolling" line + blank spacer + weapon line) used by both, and the countdown refresh
/// skips entirely for any slot currently mid-spin (tracked via _spins) rather than racing it.
///
/// Bug fix: "Rolling" used to render via HtmlGradient.GenerateGradientText - after repeated reports
/// that no spin animation was ever visible (just static "Rolling" text) even once the underlying
/// state-machine bug above was fixed, the gradient helper itself is dropped in favor of a plain
/// colored span, matching how every other HUD in this codebase (e.g. FlankTeleport's own cooldown
/// text) already renders colored status text with no issues.
/// </summary>
public sealed class GameModifierWeaponRoulette : GameModifierRemoveWeapons
{
    private const int HtmlDurationMs = 400;
    private const float HtmlRefreshIntervalSeconds = 0.1f;

    private sealed class SpinState
    {
        public int FrameIndex;
        public string FinalWeaponName = "";
        public Team Team;
        public float NextFrameTime;
    }

    private readonly Dictionary<int, string> _currentWeaponName = [];
    private readonly Dictionary<int, float> _lastHtmlUpdateTime = [];
    private readonly Dictionary<int, SpinState> _spins = [];

    /// <summary>
    /// -1 means "not yet scheduled". OnRoundStart's "re-apply active modifiers in case anything was
    /// reset" (see its own bug-fix comment) runs Deactivate()+Activate() on every currently-active
    /// modifier on EVERY round start, not just once - previously this field got unconditionally reset
    /// to now+RerollIntervalSeconds on every one of those calls, so on a server with round times
    /// shorter than RerollIntervalSeconds the reroll timer could never actually elapse, pushed back
    /// every round instead of counting down 25 real seconds as intended. Only initialised the first
    /// time OnEnabled ever runs for a given activation; a genuinely elapsed timer at the moment of a
    /// mere reapply is left alone and fires on the very next tick once ticking resumes, which is correct.
    /// </summary>
    private float _nextRerollTime = -1f;

    /// <summary>Single clamped source for the spin's total duration - both the early-trigger window in OnGameTick and AdvanceSpin's per-frame interval must agree, or the spin can't fill exactly the countdown's final stretch (see AdvanceSpin's own bug-fix note).</summary>
    private float SpinDurationSeconds => Math.Max(0.1f, Runtime.Config.WeaponRoulette.SpinDurationSeconds);

    public GameModifierWeaponRoulette()
    {
        Name = "WeaponRoulette";
        Description = "Forced onto a single random gun, re-rolled every so often";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = [
            "RandomLoadout",
            "GrenadesOnly",
        ];
    }

    protected override HashSet<CSWeaponType> TypesToStrip => CSRollUtils.AllGunWeaponTypes;

    protected override bool AnnounceRemovalGlobally => false;

    protected override void OnRegistered()
    {
        base.OnRegistered();
        Core.Event.OnClientDisconnected += OnClientDisconnected;
    }

    protected override void OnUnregistered()
    {
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        base.OnUnregistered();
    }

    protected override void OnEnabled()
    {
        // Bug fix: used to unconditionally reset here, which - combined with OnRoundStart's "re-apply
        // active modifiers" cycle calling Deactivate()+Activate() every round - meant the 25s reroll
        // countdown got pushed back to now+25 every single round instead of counting down real time.
        // Only set on this activation's first OnEnabled(); a mere round-start reapply leaves whatever
        // was already in flight alone.
        if (_nextRerollTime < 0f)
        {
            _nextRerollTime = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.WeaponRoulette.RerollIntervalSeconds;
        }

        Core.Event.OnTick += OnGameTick;

        if (Runtime.DebugMode)
        {
            Core.Logger.LogInformation("[CSRoll][WeaponRoulette] OnEnabled - slots=[{Slots}], nextRerollIn={Seconds:0.0}s", string.Join(",", AssignedSlots), _nextRerollTime - Core.Engine.GlobalVars.CurrentTime);
        }

        // base.OnEnabled() strips every assigned player's guns and calls GiveReplacementWeapons
        // below for each - which itself detects "no weapon rolled yet" and kicks off the first spin.
        base.OnEnabled();
    }

    protected override void OnDisabled()
    {
        if (Runtime.DebugMode)
        {
            Core.Logger.LogInformation("[CSRoll][WeaponRoulette] OnDisabled - slots=[{Slots}], spinsInFlight=[{Spins}]", string.Join(",", AssignedSlots), string.Join(",", _spins.Keys));
        }

        // Unsubscribing this is the ONLY thing that drives any of this modifier's ongoing behavior -
        // the reroll timer, the spin frames, the countdown HUD all live inside OnGameTick. The instant
        // this line runs, nothing about this modifier executes again until a future OnEnabled().
        Core.Event.OnTick -= OnGameTick;

        // Bug fix: used to unconditionally clear _currentWeaponName here too - since OnRoundStart's
        // "re-apply active modifiers" cycle runs Deactivate() immediately followed by Activate() on
        // every round start (not just once), this forced a brand new spin from frame 0 for every
        // assigned player on every single round, regardless of whether the reroll timer had actually
        // elapsed. If a round was shorter than SpinDurationSeconds, the animation could never reach
        // its landing frame - reported as "only ever shows the Rolling text, never completes". Keeping
        // the currently-held weapon cached across a mere reapply means base.OnEnabled()'s forced
        // strip+regive silently re-hands the SAME weapon (a harmless full-ammo reset, matching every
        // other modifier's reapply semantics) instead of triggering a fresh roll - only the real
        // reroll timer (or a genuinely new player with no cached weapon yet) starts a new spin now.
        _lastHtmlUpdateTime.Clear();
        _spins.Clear();
        base.OnDisabled();
    }

    protected override void GiveReplacementWeapons(IPlayer player)
    {
        if (_currentWeaponName.TryGetValue(player.Slot, out var weaponName))
        {
            var itemServices = player.PlayerPawn?.ItemServices;
            itemServices?.GiveItem(weaponName);

            if (Runtime.DebugMode)
            {
                Core.Logger.LogInformation("[CSRoll][WeaponRoulette] GiveReplacementWeapons: slot={Slot} weapon={Weapon} pawnNull={PawnNull} itemServicesNull={ItemServicesNull}", player.Slot, weaponName, player.PlayerPawn is null, itemServices is null);
            }
        }
        else
        {
            StartSpin(player);
        }
    }

    private void OnGameTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;
        var spinDuration = SpinDurationSeconds;

        // Bug fix (per explicit request): the spin used to start only once the countdown had
        // already hit 0, so the new weapon didn't actually land until SpinDurationSeconds AFTER the
        // displayed timer reached zero. Triggering spinDuration seconds early instead means the spin
        // fills exactly the countdown's final stretch and lands right as the timer reaches 0 - "Timer:
        // 2.0s" switches straight to "Rolling" and the new weapon is ready the moment it would have
        // hit zero. _nextRerollTime is bumped here (once per cycle, at trigger time) rather than at
        // landing - the moment this fires, the trigger condition itself goes false again until the
        // NEXT cycle's window, so this remains a one-shot trigger exactly like the old "now >=
        // _nextRerollTime" check was, just shifted earlier by spinDuration. Advancing it now instead
        // of at landing also means a per-player landing (there can be several assigned players, each
        // landing independently) never double-advances this single shared timestamp.
        if (now >= _nextRerollTime - spinDuration)
        {
            // Bug fix: advancing purely by += drifts when _nextRerollTime is badly stale. It's
            // deliberately NOT reset in OnDisabled (so a mere round-start reapply doesn't restart the
            // countdown), so after this modifier sits deactivated for several rounds the timestamp can
            // be many intervals in the past - the trigger then stayed true for one tick per elapsed
            // interval, re-entering this block repeatedly instead of being the one-shot the comment
            // below describes. Snapping forward to a fresh now-based deadline whenever it has fallen
            // more than one interval behind keeps the += path (which preserves exact cadence in the
            // normal case) without letting a stale value burn through several cycles at once.
            var interval = Runtime.Config.WeaponRoulette.RerollIntervalSeconds;
            _nextRerollTime = _nextRerollTime + interval < now ? now + interval : _nextRerollTime + interval;

            foreach (var player in Core.PlayerManager.GetAllValidPlayers())
            {
                if (IsAssignedTo(player.Slot))
                {
                    StartSpin(player);
                }
            }
        }

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (!IsAssignedTo(player.Slot))
            {
                continue;
            }

            if (_spins.TryGetValue(player.Slot, out var spin))
            {
                if (now >= spin.NextFrameTime)
                {
                    AdvanceSpin(player, spin, now);
                }
            }
            else
            {
                RefreshCountdownHtml(player, now);
            }
        }
    }

    private void StartSpin(IPlayer player)
    {
        // Bug fix: without this guard, a second near-simultaneous trigger for the same player (e.g.
        // an EventPlayerSpawn firing right around !rolltoggle/!memodifier applying) started an
        // independent second spin that stomped the first one's SendCenterHTML calls every tick - the
        // player only ever saw whichever one's frame landed last, never a clean animation.
        if (_spins.ContainsKey(player.Slot))
        {
            return;
        }

        // Bug fix: GetRandomMainWeaponName ignored team entirely - CS2 enforces standard team
        // weapon restrictions (M4A4/AUG/etc. CT-only, AK-47/Galil/etc. T-only), so a mismatched
        // GiveItem could silently fail to arrive, leaving the player weapon-less after their pistol
        // was already stripped. Team is resolved once per roll and carried through the whole spin.
        var team = player.Controller is { IsValid: true } controller ? controller.Team : Team.None;
        var finalWeaponName = CSRollUtils.GetRandomMainWeaponName(team);

        var spin = new SpinState
        {
            FrameIndex = 0,
            FinalWeaponName = finalWeaponName,
            Team = team,
            NextFrameTime = Core.Engine.GlobalVars.CurrentTime,
        };

        _spins[player.Slot] = spin;

        // Play the first frame immediately rather than waiting for the next tick's threshold check -
        // per explicit request, the spin should start the instant the modifier (or a reroll) hits.
        AdvanceSpin(player, spin, Core.Engine.GlobalVars.CurrentTime);
    }

    private void AdvanceSpin(IPlayer player, SpinState spin, float now)
    {
        var frameCount = Math.Max(1, Runtime.Config.WeaponRoulette.SpinFrameCount);

        if (spin.FrameIndex >= frameCount)
        {
            _spins.Remove(player.Slot);
            _currentWeaponName[player.Slot] = spin.FinalWeaponName;

            var remaining = Math.Max(0f, _nextRerollTime - now);
            player.SendCenterHTML(BuildStatusHtml(isRolling: false, spin.FinalWeaponName, remaining), HtmlDurationMs);

            if (Runtime.DebugMode)
            {
                Core.Logger.LogInformation("[CSRoll][WeaponRoulette] Spin landed: slot={Slot} weapon={Weapon}", player.Slot, spin.FinalWeaponName);
            }

            // Goes through the base class's own StripWeapons (strips whatever gun they currently
            // hold, then calls GiveReplacementWeapons under the _grantInProgress guard) rather than
            // stripping/giving directly - see class doc comment for why that matters here.
            StripWeapons(player);
            return;
        }

        // Bug fix: this used the RAW config value while OnGameTick's early-trigger used the clamped
        // SpinDurationSeconds - so at SpinDurationSeconds <= 0 the trigger fired only 0.1s early but
        // the spin still took frameCount ticks to land, putting the new weapon AFTER the countdown
        // hit zero: exactly the defect the early-trigger change was made to fix. A negative value
        // additionally produced a negative SendCenterHTML duration below. Both sites now read the
        // same clamped value.
        var interval = SpinDurationSeconds / frameCount;
        var randomName = CSRollUtils.GetRandomMainWeaponName(spin.Team);
        player.SendCenterHTML(BuildStatusHtml(isRolling: true, randomName, 0f), (int)(interval * 1000) + 50);
        CSRollUtils.PlaySoundToPlayer(Core, player, Runtime.Config.SpinReveal.TickSoundEventName, Runtime.Config.SpinReveal.TickSoundVolume, debugMode: Runtime.DebugMode);

        spin.FrameIndex++;
        spin.NextFrameTime = now + interval;
    }

    private void RefreshCountdownHtml(IPlayer player, float now)
    {
        if (_lastHtmlUpdateTime.TryGetValue(player.Slot, out var lastUpdate) && now - lastUpdate < HtmlRefreshIntervalSeconds)
        {
            return;
        }

        _lastHtmlUpdateTime[player.Slot] = now;

        var weaponName = _currentWeaponName.GetValueOrDefault(player.Slot, "-");
        var remaining = Math.Max(0f, _nextRerollTime - now);
        player.SendCenterHTML(BuildStatusHtml(isRolling: false, weaponName, remaining), HtmlDurationMs);
    }

    /// <summary>
    /// Single 4-line template shared by both the spin animation and the idle countdown, so there's
    /// only ever one place building this modifier's HUD text. Line 2 and line 4 swap meaning based
    /// on state: "Timer: Ns" / "[orange]Active:[default] weapon" while idle, or an orange "Rolling"
    /// / the current random spin-frame weapon name while spinning.
    /// </summary>
    private static string BuildStatusHtml(bool isRolling, string weaponName, float secondsRemaining)
    {
        var friendlyName = weaponName == "-" ? weaponName : CSRollUtils.GetFriendlyWeaponName(weaponName);

        var line2 = isRolling
            ? "<span color=\"orange\" class=\"fontWeight-bold\">Rolling</span>"
            : $"<span class=\"fontWeight-bold\">Timer: {secondsRemaining:0.0}s</span>".Replace('.', ',');

        var line4 = isRolling
            ? friendlyName
            : $"<span color=\"orange\" class=\"fontWeight-bold\">Active:</span> {friendlyName}";

        return "<span color=\"gold\" class=\"fontWeight-bold\">Weapon Roulette</span><br/>" +
               line2 + "<br/><br/>" +
               line4;
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _currentWeaponName.Remove(@event.PlayerId);
        _lastHtmlUpdateTime.Remove(@event.PlayerId);
        _spins.Remove(@event.PlayerId);
    }
}
