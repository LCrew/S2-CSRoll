using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;

using CSRoll.Core;
using CSRoll.Hud;

namespace CSRoll.Modifiers;

/// <summary>
/// Nothing stays put: the carrier gets a second modifier on top of this one, and every few seconds it
/// is thrown away and re-rolled into a different random one, for the whole round.
///
/// Only ever one extra at a time - this rerolls what you have rather than stacking onto it, so the
/// carrier is running exactly two modifiers (this plus one) at any moment no matter how long the
/// round runs.
///
/// Candidates come from Runtime.GetGrantableModifiersForSlot, which already excludes this modifier
/// itself, anything globally scoped (it would apply to the carrier regardless, making the swap look
/// broken), and anything incompatible with what they're currently running.
///
/// The countdown/roll presentation deliberately mirrors GameModifierWeaponRoulette: a "Timer: N,Ns"
/// HUD counting down, then a slot-machine roll flashing candidate names that STARTS SpinDurationSeconds
/// before the timer reaches zero, so it fills the timer's final stretch and lands exactly as the
/// timer hits 0 - rather than starting there and landing late. Same reasoning, same shape, same
/// MinFrameIntervalSeconds floor, so both modifiers read identically to a player.
///
/// The outgoing modifier is revoked when the roll STARTS, not when it lands: the pool the new one is
/// picked from has to reflect what the carrier will actually be running, or the outgoing modifier's
/// own incompatibilities keep filtering out candidates that are about to become perfectly legal. The
/// side effect is that the carrier has no extra modifier for the few seconds the roll is on screen,
/// which reads correctly - the HUD says "Rolling" for exactly that window.
/// </summary>
public sealed class GameModifierButterflyEffect : GameModifierBase
{
    /// <summary>See GameModifierWeaponRoulette.MinFrameIntervalSeconds - center-HTML frames flipping faster than ~150ms were confirmed live to not render at all, so the roll would be invisible.</summary>
    private const float MinFrameIntervalSeconds = 0.15f;

    /// <summary>Drawn above the modifier it hands out, so the block reads as "Butterfly Effect, currently: X" rather than two unrelated HUDs stacked in arbitrary order.</summary>
    private const int HudPriority = 10;

    /// <summary>
    /// Fixed rendered width of the modifier-name field, so the countdown after it never moves.
    ///
    /// Sized by what fits on ONE line, not by the longest display name. Sizing it to the longest
    /// ("Conditional Invisibility", 24) pushed the line past the HUD's width, so it wrapped and the
    /// timer rendered on a third line of its own - the same overflow that made Recall's gauge four
    /// lines tall. 13 plus the separator and the "[16,4s]" timer keeps the whole line around 21
    /// characters, comfortably inside the width Recall's fixed gauge is now known to fit in. The
    /// timer's surrounding brackets were dropped to buy those two characters back for the name -
    /// the fixed-width field already separates the two well enough without them.
    ///
    /// The cost is that longer names are ellipsized. Raise this if the panel turns out to have more
    /// room than that - it's the single constant controlling both the truncation and the wrap.
    /// </summary>
    private const int NameFieldWidth = 15;

    private sealed class SpinState
    {
        public int FrameIndex;
        public GameModifierBase? Target;
        public float NextFrameTime;
    }

    /// <summary>The one extra modifier each carrier currently has, so the next roll knows what to take away first.</summary>
    private readonly Dictionary<int, GameModifierBase> _granted = [];
    private readonly Dictionary<int, float> _nextSwapTime = [];
    private readonly Dictionary<int, SpinState> _spins = [];

    public GameModifierButterflyEffect()
    {
        Name = "ButterflyEffect";
        Description = "A 2nd modifier, re-rolled every {interval}";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    public override IReadOnlyDictionary<string, string>? DynamicTextTokens => new Dictionary<string, string>
    {
        ["interval"] = $"{Runtime.Config.ButterflyEffect.SwapIntervalSeconds:0.#}s",
    };

    /// <summary>
    /// Clamped roll duration - the early-trigger window and the per-frame interval must agree, or the
    /// roll can't fill exactly the countdown's final stretch. Widened whenever the frame count would
    /// divide the configured duration into frames too fast to render. Copied deliberately from
    /// GameModifierWeaponRoulette.SpinDurationSeconds, including the tradeoff: a visible roll that
    /// lands slightly late beats an invisible one that lands on time.
    /// </summary>
    private float SpinDurationSeconds
    {
        get
        {
            var configured = Math.Max(0.1f, Runtime.Config.ButterflyEffect.SpinDurationSeconds);
            var frameCount = Math.Max(1, Runtime.Config.ButterflyEffect.SpinFrameCount);
            return Math.Max(configured, frameCount * MinFrameIntervalSeconds);
        }
    }

    protected override void OnRegistered()
    {
        Core.Event.OnClientDisconnected += OnClientDisconnected;
    }

    protected override void OnUnregistered()
    {
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
    }

    protected override void OnEnabled()
    {
        Core.Event.OnTick += OnTick;

        var firstSwap = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.ButterflyEffect.FirstSwapDelaySeconds;
        foreach (var player in GetAssignedPlayers())
        {
            _nextSwapTime[player.Slot] = firstSwap;
        }
    }

    /// <summary>Seeds the first-roll delay for carriers handed this mid-round, which AddAssignedSlots doesn't re-run OnEnabled for (see GameModifierBase.OnSlotsAdded). This is also the path Mimic uses when it steals this modifier off someone.</summary>
    protected override void OnSlotsAdded(IReadOnlyCollection<int> slots)
    {
        var firstSwap = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.ButterflyEffect.FirstSwapDelaySeconds;
        foreach (var slot in slots)
        {
            _nextSwapTime[slot] = firstSwap;
        }
    }

    /// <summary>
    /// Releases what was handed to a carrier who is no longer one - reachable because Mimic can steal
    /// THIS modifier and then replace it on its next kill. Without this the modifier handed out here
    /// would stay scoped to that player with nothing left driving or ending it. See
    /// GameModifierBase.OnSlotsRemoved.
    /// </summary>
    protected override void OnSlotsRemoved(IReadOnlyCollection<int> slots)
    {
        foreach (var slot in slots)
        {
            ReleaseGrant(slot);
            _nextSwapTime.Remove(slot);
            _spins.Remove(slot);
        }
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= OnTick;

        // Granted modifiers are scoped onto players by this one, so they end when it does - otherwise
        // whatever was handed out last would survive into the next round. Round start does
        // Deactivate() + Activate(), so this runs every round, not just on a real removal.
        foreach (var slot in _granted.Keys.ToList())
        {
            ReleaseGrant(slot);
        }

        _granted.Clear();
        _nextSwapTime.Clear();
        _spins.Clear();
    }

    /// <summary>Removes the dictionary entry BEFORE revoking, so the OnSlotsRemoved that revoking may trigger on the granted modifier can't re-enter this and revoke it a second time.</summary>
    private void ReleaseGrant(int slot)
    {
        if (_granted.Remove(slot, out var granted))
        {
            Runtime.RevokeModifierFromSlot(granted, slot);
        }
    }

    private void OnTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;
        var spinDuration = SpinDurationSeconds;

        foreach (var player in GetAssignedPlayers())
        {
            var slot = player.Slot;

            if (_spins.TryGetValue(slot, out var spin))
            {
                if (now >= spin.NextFrameTime)
                {
                    AdvanceSpin(player, spin, now);
                }

                continue;
            }

            // Started a full roll-duration early so it lands exactly as the countdown reaches zero -
            // see the class comment and WeaponRoulette's own note on the same trigger.
            if (now >= _nextSwapTime.GetValueOrDefault(slot, now + spinDuration) - spinDuration)
            {
                StartSpin(player, now);
                continue;
            }

            PublishIdleHud(slot, now);
        }
    }

    private void StartSpin(IPlayer player, float now)
    {
        var slot = player.Slot;

        var interval = Runtime.Config.ButterflyEffect.SwapIntervalSeconds;

        // Snapped forward rather than advanced by += whenever the deadline has fallen more than a
        // full interval behind - a stale timestamp (this modifier sat deactivated for a few rounds,
        // or the map changed and CurrentTime restarted near zero) would otherwise burn through
        // several cycles in consecutive ticks. Same fix as WeaponRoulette's reroll timer.
        var deadline = _nextSwapTime.GetValueOrDefault(slot, now);
        _nextSwapTime[slot] = deadline + interval < now ? now + interval : deadline + interval;

        // Revoked here, at the START of the roll, not at landing - see the class comment.
        ReleaseGrant(slot);

        var candidates = Runtime.GetGrantableModifiersForSlot(slot, this);

        var spin = new SpinState
        {
            FrameIndex = 0,
            Target = candidates.Count > 0 ? candidates[Random.Shared.Next(candidates.Count)] : null,
            NextFrameTime = now,
        };

        _spins[slot] = spin;

        // First frame plays immediately rather than waiting for the next tick's threshold, so the
        // roll visibly starts the instant the countdown runs out.
        AdvanceSpin(player, spin, now);
    }

    private void AdvanceSpin(IPlayer player, SpinState spin, float now)
    {
        var slot = player.Slot;
        var frameCount = Math.Max(1, Runtime.Config.ButterflyEffect.SpinFrameCount);

        if (spin.FrameIndex >= frameCount)
        {
            _spins.Remove(slot);

            // Nothing was grantable when the roll started - a small registered pool, or a carrier
            // already running enough that everything else is incompatible. The countdown keeps
            // running, so it simply tries again next cycle rather than switching itself off.
            if (spin.Target is { } target && Runtime.GrantModifierToSlot(target, slot))
            {
                _granted[slot] = target;

                if (Runtime.Config.ButterflyEffect.AnnounceSwaps)
                {
                    CSRollUtils.PrintTitleToChatColored(Core, player, $"Butterfly Effect: [gold]{CSRollUtils.GetModifierDisplayName(Core, target)}[default]");
                }
            }

            PublishIdleHud(slot, now, force: true);
            return;
        }

        var interval = SpinDurationSeconds / frameCount;

        // Flashes names from the live candidate pool. Falls back to the landing target (or a dash)
        // when the pool is empty, so an empty roll still animates rather than rendering blank.
        var pool = Runtime.GetGrantableModifiersForSlot(slot, this);
        var flashed = pool.Count > 0
            ? CSRollUtils.GetModifierDisplayName(Core, pool[Random.Shared.Next(pool.Count)])
            : spin.Target is { } fallback ? CSRollUtils.GetModifierDisplayName(Core, fallback) : "-";

        // Countdown keeps running through the roll - see WeaponRoulette's matching note. _nextSwapTime
        // was advanced to the next cycle when the roll started, so one interval comes back off here to
        // get THIS roll's landing moment, or the timer would jump forward a full interval mid-roll.
        var landingRemaining = Math.Max(0f, _nextSwapTime.GetValueOrDefault(slot, now) - Runtime.Config.ButterflyEffect.SwapIntervalSeconds - now);
        SetHud(slot, BuildStatusHtml(flashed, landingRemaining), HudPriority);
        CSRollUtils.PlaySoundToPlayer(Core, player, Runtime.Config.SpinReveal.TickSoundEventName, Runtime.Config.SpinReveal.TickSoundVolume, debugMode: Runtime.DebugMode);

        spin.FrameIndex++;
        spin.NextFrameTime = now + interval;
    }

    private void PublishIdleHud(int slot, float now, bool force = false)
    {
        if (!force && _spins.ContainsKey(slot))
        {
            return;
        }

        // The granted modifier's name is always shown here, even when it also draws its own block
        // directly beneath. An earlier version suppressed it to avoid printing the name twice, but the
        // name is now the line that carries the countdown - dropping it would leave a bare "[14,2s]"
        // with nothing to anchor it to.
        var activeName = _granted.TryGetValue(slot, out var granted)
            ? CSRollUtils.GetModifierDisplayName(Core, granted)
            : "<none>";

        var remaining = Math.Max(0f, _nextSwapTime.GetValueOrDefault(slot, now) - now);
        SetHud(slot, BuildStatusHtml(activeName, remaining), HudPriority);
    }

    /// <summary>
    /// Two-line HUD - title, then the currently granted modifier with its countdown to the right -
    /// deliberately the same shape as GameModifierWeaponRoulette.BuildStatusHtml, including dropping
    /// the old gradient "Rolling" label: the flickering name already shows that a roll is running, so
    /// the label cost two lines to say nothing extra, and the countdown can stay visible throughout.
    ///
    /// Height matters here because the composer stacks this block above whatever modifier this one
    /// hands out, and several of those draw HUDs of their own.
    /// </summary>
    private static string BuildStatusHtml(string modifierName, float secondsRemaining)
    {
        var timer = $"{secondsRemaining:0.0}s".Replace('.', ',');

        // The name goes in a fixed-width monospaced field rather than being written inline. Names
        // change every frame during a roll, and in Panorama's proportional default font that made the
        // trailing timer visibly jump left and right for the whole animation. See
        // CSRollUtils.BuildFixedWidthField - it escapes the text too, so raw "<none>" is safe here.
        return "<span color=\"gold\" class=\"fontWeight-Bold\">Butterfly Effect</span><br/>" +
               CSRollUtils.BuildFixedWidthField(modifierName, NameFieldWidth) +
               $"<span class=\"{CSRollUtils.MonoFontClass}\">&nbsp;</span>" +
               $"<span color=\"orange\" class=\"fontWeight-Bold {CSRollUtils.MonoFontClass}\">{timer}</span>";
    }

    /// <summary>Slots are recycled by the next player to join, so a stale entry here would make OnDisabled revoke a modifier from someone who was never a carrier.</summary>
    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _granted.Remove(@event.PlayerId);
        _nextSwapTime.Remove(@event.PlayerId);
        _spins.Remove(@event.PlayerId);
    }

    /// <summary>
    /// Time until the next modifier swap, with whatever is currently granted as the readout.
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

        var detail = _granted.TryGetValue(slot, out var granted)
            ? $"ACTIVE: {CSRollUtils.GetModifierDisplayName(Core, granted)}"
            : null;

        var remaining = _nextSwapTime.GetValueOrDefault(slot, 0f) - Core.Engine.GlobalVars.CurrentTime;
        return HudTimer.Cooldown(remaining, Runtime.Config.ButterflyEffect.SwapIntervalSeconds,
                                 detail: detail);
    }

}
