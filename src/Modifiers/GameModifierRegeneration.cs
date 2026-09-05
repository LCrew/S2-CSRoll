using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Heals the assigned player(s) over time, up to their current max health - faster while standing
/// still than while moving.
///
/// Actual heal rate is a binary switch: MovingRatePerSecond (default 2) normally, or
/// StationaryRatePerSecond (default 5) once the player has been standing still (horizontal speed
/// below MovementThreshold) for at least StationaryDelaySeconds. Either rate is achieved by healing
/// exactly 1 HP at a time, spaced (1/rate) seconds apart - "give it more or less often", never a
/// bigger lump sum - so 2/sec means 1 HP every 0.5s and 5/sec means 1 HP every 0.2s. The switch
/// itself is immediate (no ramp) the instant the moving/stationary state changes.
///
/// The HUD's displayed HP/s number is a separate, purely cosmetic ramp on top of that: it steps by
/// 1 every DisplayRampStepSeconds toward whichever rate is currently real, so standing still reads
/// as the number visibly counting up 2->3->4->5 (and back down on moving again) rather than an
/// instant jump - doesn't affect the real heal timer, which already switched the moment the state did.
/// Turns gold once the display reaches the stationary rate.
/// </summary>
public sealed class GameModifierRegeneration : GameModifierBase
{
    private const int HtmlDurationMs = 400;
    private const float HtmlRefreshIntervalSeconds = 0.15f;

    private readonly Dictionary<int, float> _stillSinceTime = [];
    private readonly Dictionary<int, float> _lastHealTime = [];
    private readonly Dictionary<int, float> _displayedRate = [];
    private readonly Dictionary<int, float> _lastRampStepTime = [];
    private readonly Dictionary<int, float> _lastHtmlUpdateTime = [];

    public GameModifierRegeneration()
    {
        Name = "Regeneration";
        Description = "Slowly heals over time, up to your max health - faster while standing still";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
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
        Core.Event.OnTick += OnGameTick;
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= OnGameTick;
        _stillSinceTime.Clear();
        _lastHealTime.Clear();
        _displayedRate.Clear();
        _lastRampStepTime.Clear();
        _lastHtmlUpdateTime.Clear();
    }

    private void OnGameTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;

        foreach (var player in Core.PlayerManager.GetAlive())
        {
            if (!IsAssignedTo(player.Slot) || player.PlayerPawn is not { } pawn)
            {
                continue;
            }

            var slot = player.Slot;
            var velocity = pawn.AbsVelocity;
            var horizontalSpeed = MathF.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y));
            var isMoving = horizontalSpeed > Runtime.Config.Regeneration.MovementThreshold;

            if (isMoving)
            {
                _stillSinceTime.Remove(slot);
            }
            else if (!_stillSinceTime.ContainsKey(slot))
            {
                _stillSinceTime[slot] = now;
            }

            var stillDuration = _stillSinceTime.TryGetValue(slot, out var since) ? now - since : 0f;
            var targetRate = stillDuration >= Runtime.Config.Regeneration.StationaryDelaySeconds
                ? Runtime.Config.Regeneration.StationaryRatePerSecond
                : Runtime.Config.Regeneration.MovingRatePerSecond;

            // Real healing: exactly 1 HP, spaced (1/targetRate) seconds apart - switches instantly
            // with targetRate, no ramp.
            var healInterval = 1f / MathF.Max(0.01f, targetRate);
            if (pawn.Health < pawn.MaxHealth && now - _lastHealTime.GetValueOrDefault(slot) >= healInterval)
            {
                _lastHealTime[slot] = now;
                pawn.Health = Math.Min(pawn.MaxHealth, pawn.Health + 1);
                pawn.HealthUpdated();
            }

            // Cosmetic-only ramp of the HUD's displayed number toward targetRate.
            var displayed = _displayedRate.TryGetValue(slot, out var current) ? current : Runtime.Config.Regeneration.MovingRatePerSecond;
            if (displayed != targetRate && now - _lastRampStepTime.GetValueOrDefault(slot) >= Runtime.Config.Regeneration.DisplayRampStepSeconds)
            {
                displayed += MathF.Sign(targetRate - displayed);
                _lastRampStepTime[slot] = now;
            }

            _displayedRate[slot] = displayed;

            RefreshHtml(player, slot, displayed, now);
        }
    }

    private void RefreshHtml(IPlayer player, int slot, float displayedRate, float now)
    {
        if (_lastHtmlUpdateTime.TryGetValue(slot, out var lastUpdate) && now - lastUpdate < HtmlRefreshIntervalSeconds)
        {
            return;
        }

        // Stay off the center-HTML surface while the roll's own reveal owns it - see
        // ModifierRuntime.IsModifierHudSuppressed.
        if (Runtime.IsModifierHudSuppressed)
        {
            return;
        }

        _lastHtmlUpdateTime[slot] = now;

        var color = displayedRate >= Runtime.Config.Regeneration.StationaryRatePerSecond ? "gold" : "lime";
        var html = "<span color=\"gold\" class=\"fontWeight-Bold\">Regeneration</span><br/>" +
                   "<span class=\"fontWeight-Bold\">Health: </span>" +
                   $"<span color=\"{color}\" class=\"fontWeight-Bold\">{displayedRate:0} HP/s</span>";

        SetHud(slot, html);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _stillSinceTime.Remove(@event.PlayerId);
        _lastHealTime.Remove(@event.PlayerId);
        _displayedRate.Remove(@event.PlayerId);
        _lastRampStepTime.Remove(@event.PlayerId);
        _lastHtmlUpdateTime.Remove(@event.PlayerId);
    }
}
