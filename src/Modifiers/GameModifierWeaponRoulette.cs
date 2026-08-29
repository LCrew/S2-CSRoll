using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;
using CSRoll.Hud;

namespace CSRoll.Modifiers;

/// <summary>
/// Forces the assigned player onto a single random gun (grenades untouched - buy/pickup of grenades
/// stays completely normal), re-rolled to a fresh weapon on a timer (Config.WeaponRoulette.
/// RerollIntervalSeconds). Reuses GameModifierRemoveWeapons for the strip/restore-on-disable and
/// CanAcquire.Pre buy-and-pickup block (same base RandomLoadout/WalkingGrenadier already use), scoped to
/// CSRollUtils.AllGunWeaponTypes so grenades are explicitly excluded from the block.
///
/// Bug fix (structural rewrite): the spin used to be driven by a self-rescheduling chain of
/// Core.Scheduler.DelayBySeconds calls - a mechanism no other modifier in this codebase uses. Every
/// other tick-based modifier (Flanker, Regeneration, etc.) drives 100% of its logic from
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
/// colored span, matching how every other HUD in this codebase (e.g. Flanker's own cooldown
/// text) already renders colored status text with no issues.
/// </summary>
public sealed class GameModifierWeaponRoulette : GameModifierRemoveWeapons
{
    private const int HtmlDurationMs = 400;
    private const float HtmlRefreshIntervalSeconds = 0.1f;

    /// <summary>Fixed rendered width of the weapon-name field, so the countdown after it never moves while the roll flickers through names of different lengths. Sized by what fits on one line - see GameModifierButterflyEffect.NameFieldWidth for why that matters more than fitting the longest name.</summary>
    private const int NameFieldWidth = 15;

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

    /// <summary>
    /// Floor on how fast a single spin frame can flip, regardless of config. Reported bug: with the
    /// shipped defaults (SpinDurationSeconds=2, SpinFrameCount=30), each frame only got ~67ms on
    /// screen - fast enough that neither the "Rolling" animation nor the landing "Active: weapon"
    /// text was visibly rendering at all (weapon-giving itself, a plain ItemServices call with no
    /// HTML involved, was unaffected - only the HUD text was silently swallowed by updates arriving
    /// faster than the panel could settle). 150ms is comfortably above that failure point while still
    /// reading as a fast slot-machine flicker.
    /// </summary>
    private const float MinFrameIntervalSeconds = 0.15f;

    /// <summary>
    /// Single clamped source for the spin's total duration - both the early-trigger window in OnGameTick
    /// and AdvanceSpin's per-frame interval must agree, or the spin can't fill exactly the countdown's
    /// final stretch (see AdvanceSpin's own bug-fix note).
    ///
    /// Bug fix: this used to only clamp the FLOOR of the total duration (0.1s), which let a high
    /// SpinFrameCount divide it into imperceptibly-fast frames - see MinFrameIntervalSeconds. Now
    /// widens the effective total duration whenever frameCount * MinFrameIntervalSeconds would exceed
    /// the configured value, so every individual frame is guaranteed at least MinFrameIntervalSeconds
    /// on screen. This means the spin can now run LONGER than SpinDurationSeconds for a high frame
    /// count - a deliberate tradeoff (a visible animation that lands late beats an invisible one that
    /// lands on time) - and since OnGameTick's early-trigger window is computed from this same
    /// property, the "lands exactly when the countdown reaches zero" property still holds against
    /// whatever the REAL total duration ends up being, not the nominal config value.
    /// </summary>
    private float SpinDurationSeconds
    {
        get
        {
            var configured = Math.Max(0.1f, Runtime.Config.WeaponRoulette.SpinDurationSeconds);
            var frameCount = Math.Max(1, Runtime.Config.WeaponRoulette.SpinFrameCount);
            return Math.Max(configured, frameCount * MinFrameIntervalSeconds);
        }
    }

    public GameModifierWeaponRoulette()
    {
        Name = "WeaponRoulette";
        Description = "Forced onto a single random gun, re-rolled every so often";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = [
            "RandomLoadout",
            "WalkingGrenadier",
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
        // Bug fix: the "< 0f" sentinel alone couldn't recover from a map change. GlobalVars.CurrentTime
        // is map-relative and restarts near zero, so a deadline carried over from the previous map is
        // a large POSITIVE number - not < 0, so it was left alone, and OnGameTick's "now >= deadline"
        // then never became true. The reroll timer simply stopped for the rest of the session.
        // A deadline further out than the whole interval can only be stale, so re-seed on that too.
        var now = Core.Engine.GlobalVars.CurrentTime;
        if (_nextRerollTime < 0f || _nextRerollTime > now + Runtime.Config.WeaponRoulette.RerollIntervalSeconds)
        {
            _nextRerollTime = now + Runtime.Config.WeaponRoulette.RerollIntervalSeconds;
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

            // Reverted from GetAssignedPlayers() (a provably identical expansion of this same
            // GetAllValidPlayers()+IsAssignedTo filter - compared line-for-line, there is no logic
            // difference) back to the manual loop, per an explicit live report that the rolling
            // animation/landing text stopped rendering specifically after that conversion, isolated
            // to this file alone despite the same conversion being applied identically to 13 other
            // modifiers with no reported issue. The cause was not identified through code review; this
            // reverts the one change pinpointed by testing, on the reasoning that it costs nothing to
            // revert something provably equivalent, while leaving the actual root cause (if this
            // wasn't it) still to be found.
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
            SetHud(player.Slot, BuildStatusHtml(spin.FinalWeaponName, remaining));

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
        // additionally produced a negative panel duration back when this pushed center-HTML
        // directly (it now publishes through the shared HUD composer). Both sites now read the
        // same clamped value.
        var interval = SpinDurationSeconds / frameCount;
        var randomName = CSRollUtils.GetRandomMainWeaponName(spin.Team);

        // Spin frames go through the shared HUD composer too, rather than pushing center-HTML
        // directly. A direct push would be overwritten within 100ms by the composer's next
        // pass for this player (which is drawing every OTHER modifier's block for them), so a
        // player running WeaponRoulette alongside anything else would see the animation shredded.
        // Composing costs the frames some timing precision - they land quantized to the composer's
        // 0.1s cadence rather than exactly on `interval` - which is harmless here: frames are held
        // for at least MinFrameIntervalSeconds (0.15s) anyway, so quantizing re-pushes identical
        // markup at worst. The per-frame tick sound below still fires on exact timing.
        // The countdown keeps running through the roll rather than being replaced by a "Rolling"
        // label. _nextRerollTime was already advanced to the NEXT cycle when the roll was triggered,
        // so one interval is subtracted back off here to get THIS roll's landing moment - otherwise
        // the timer would visibly jump forward by a full interval the instant the roll started,
        // instead of counting smoothly down to zero as the new weapon lands.
        var landingRemaining = Math.Max(0f, _nextRerollTime - Runtime.Config.WeaponRoulette.RerollIntervalSeconds - now);
        SetHud(player.Slot, BuildStatusHtml(randomName, landingRemaining));
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

        // Stay off the center-HTML surface while the roll's own reveal owns it - see
        // ModifierRuntime.IsModifierHudSuppressed. Only this persistent countdown is gated, not the
        // spin frames in AdvanceSpin: the spin is a short one-off tied to real gameplay timing (the
        // weapon actually changes when it lands), and half-suppressing it would leave the animation
        // visibly truncated. This idle HUD is the one that would genuinely fight the reveal.
        if (Runtime.IsModifierHudSuppressed)
        {
            return;
        }

        _lastHtmlUpdateTime[player.Slot] = now;

        var weaponName = _currentWeaponName.GetValueOrDefault(player.Slot, "-");
        var remaining = Math.Max(0f, _nextRerollTime - now);
        SetHud(player.Slot, BuildStatusHtml(weaponName, remaining));
    }

    /// <summary>
    /// Two-line HUD: the modifier's title, then the active weapon with its countdown to the right.
    ///
    /// Replaces a four-line block that put a gradient "Rolling" label on line 2 and the weapon on
    /// line 4, with a blank line between them. The roll is legible without a label - the weapon name
    /// visibly flickers - so the label was spending two lines to say what the animation already
    /// showed. Dropping it also means the countdown no longer has to be hidden during the roll: one
    /// line now reads the same way in both states, just with a name that stops changing when it
    /// lands.
    ///
    /// Height matters more than usual here because the shared HUD composer stacks this block above
    /// any other modifier's block for the same player (see ModifierRuntime._hudSections).
    /// </summary>
    private static string BuildStatusHtml(string weaponName, float secondsRemaining)
    {
        var friendlyName = weaponName == "-" ? weaponName : CSRollUtils.GetFriendlyWeaponName(weaponName);
        var timer = $"{secondsRemaining:0.0}s".Replace('.', ',');

        return "<span color=\"gold\" class=\"fontWeight-Bold\">Weapon Roulette</span><br/>" +
               CSRollUtils.BuildFixedWidthField(friendlyName, NameFieldWidth) +
               $"<span class=\"{CSRollUtils.MonoFontClass}\">&nbsp;</span>" +
               $"<span color=\"orange\" class=\"fontWeight-Bold {CSRollUtils.MonoFontClass}\">{timer}</span>";
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _currentWeaponName.Remove(@event.PlayerId);
        _lastHtmlUpdateTime.Remove(@event.PlayerId);
        _spins.Remove(@event.PlayerId);
    }

    /// <summary>
    /// Time until the next weapon reroll, with the current weapon as the readout. _nextRerollTime is a
    /// single server-wide deadline rather than per-slot, so every assigned player sees the same clock.
    /// </summary>
    public override HudTimer? GetHudTimer(int slot)
    {
        if (!IsAssignedTo(slot))
        {
            return null;
        }

        if (_spins.ContainsKey(slot))
        {
            return HudTimer.Ready("ROLLING", detail: "ROLLING", tone: HudTone.Warn);
        }

        // The weapon goes on the detail line rather than in the right-hand readout, so it does not have
        // to compete with the countdown - which one you are holding and how long you keep it are both
        // worth knowing, and the readout only has room for one of them.
        var weapon = _currentWeaponName.GetValueOrDefault(slot, string.Empty);
        var detail = string.IsNullOrEmpty(weapon) ? null : $"ACTIVE: {weapon}";

        var remaining = _nextRerollTime - Core.Engine.GlobalVars.CurrentTime;
        if (remaining <= 0f || _nextRerollTime < 0f)
        {
            return HudTimer.Ready("READY", detail, HudTone.Neutral);
        }

        return HudTimer.Countdown(remaining, detail: detail);
    }

}
