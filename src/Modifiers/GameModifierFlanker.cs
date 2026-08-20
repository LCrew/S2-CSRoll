using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Player-triggered ability, separate from TeleportOnReload/TeleportOnHit: starting on a cooldown
/// (RoundStartCooldownSeconds at round start/spawn, CooldownSeconds after every use), pressing the
/// "Inspect Weapon" button teleports the assigned player directly behind a random living enemy,
/// facing the same direction that enemy is.
///
/// The "Inspect Weapon" (+lookatweapon) bind has no named entry in SwiftlyS2.Shared.Events.
/// GameButtonFlags - SwiftlyS2 labels its members by their default keybind rather than the engine
/// action, and inspect defaults to F. Confirmed (not guessed) by cross-referencing the raw bit value:
/// GameButtonFlags.F = 34359738368 (2^35), which matches CounterStrikeSharp's own
/// InputBitMask_t.IN_LOOK_AT_WEAPON = 34359738368 exactly - the same underlying engine bit, just
/// named differently by each SDK's authors.
///
/// No explicit edge-detection is needed to stop a held key from firing repeatedly: the moment it
/// fires, NextAvailableTime is pushed CooldownSeconds into the future, so the very next tick (even
/// with the button still held) fails the readiness check. It can only fire again once the cooldown
/// has genuinely elapsed AND the button is down at that instant (or any tick after).
///
/// Landing: the destination is offset upward by DropHeight before teleporting, so the player falls a
/// short, harmless distance and lands with an audible thud - a silent, zero-warning teleport directly
/// into melee range felt like a completely free backstab; a landing sound gives the target (and
/// anyone else nearby) at least a chance to react to something having just appeared behind them.
///
/// Status HUD: a center-HTML popup is kept continuously visible (same re-send-before-it-expires
/// pattern ConditionalInvisibility/Vanish use) showing the trigger key and the current
/// state - gold "Ready" or red "Cooldown: N,Ns" (comma decimal separator) counting down live.
///
/// Wall check: the computed landing spot is validated with a zero-length TracePlayerBBox (see
/// IsPositionClear) before actually teleporting - without this, a target standing close to a wall or
/// corner could put the assigned player stuck inside solid geometry. A blocked spot is treated the
/// same as "no valid target": the attempt is skipped without consuming the cooldown.
/// </summary>
public sealed class GameModifierFlanker : GameModifierBase
{
    private const float HtmlRefreshIntervalSeconds = 0.1f;
    private const int HtmlDurationMs = 400;

    // Failure feedback (no valid target / blocked landing spot) deliberately doesn't consume the
    // cooldown, so a held F key re-hits that branch every tick - this throttle stops that from
    // spamming chat dozens of times per second.
    private const float FailureMessageThrottleSeconds = 1.5f;

    private readonly Dictionary<int, float> _nextAvailableTime = [];
    private readonly Dictionary<int, float> _lastHtmlUpdateTime = [];
    private readonly Dictionary<int, float> _lastFailureMessageTime = [];

    private Guid _spawnHookId;

    public GameModifierFlanker()
    {
        Name = "Flanker";
        Description = "After a cooldown, press Inspect Weapon to teleport behind a random enemy";
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
        Core.Event.OnTick += OnTick;
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        var readyAt = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.Flanker.RoundStartCooldownSeconds;
        foreach (var player in GetAssignedPlayers())
        {
            _nextAvailableTime[player.Slot] = readyAt;
        }
    }

    /// <summary>
    /// Seeds the cooldown for players handed this modifier while it's already active. AddAssignedSlots
    /// deliberately doesn't re-run OnEnabled, so without this their _nextAvailableTime entry is simply
    /// missing - and the readiness check defaults a missing entry to "ready now", letting them
    /// teleport on the very next tick instead of waiting out RoundStartCooldownSeconds.
    /// </summary>
    protected override void OnSlotsAdded(IReadOnlyCollection<int> slots)
    {
        var readyAt = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.Flanker.RoundStartCooldownSeconds;
        foreach (var slot in slots)
        {
            _nextAvailableTime[slot] = readyAt;
        }
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= OnTick;
        Core.GameEvent.Unhook(_spawnHookId);
        _nextAvailableTime.Clear();
        _lastHtmlUpdateTime.Clear();
        _lastFailureMessageTime.Clear();
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            _nextAvailableTime[player.Slot] = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.Flanker.RoundStartCooldownSeconds;
        }

        return HookResult.Continue;
    }

    private void OnTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;

        foreach (var player in GetAssignedPlayers())
        {
            if (!player.IsAlive)
            {
                continue;
            }

            RefreshStatusHtml(player, now);

            if (!player.PressedButtons.HasFlag(GameButtonFlags.F))
            {
                continue;
            }

            if (now < _nextAvailableTime.GetValueOrDefault(player.Slot, now))
            {
                continue;
            }

            if (player.Controller is not { IsValid: true } controller)
            {
                continue;
            }

            var enemyTeam = controller.Team == Team.T ? Team.CT : Team.T;
            var enemies = Core.PlayerManager.GetInTeam(enemyTeam)
                .Where(p => p.IsValid && p.IsAlive && p.PlayerPawn is not null)
                .ToList();

            // Deliberately not consuming the cooldown here - no valid target is bad luck, not a
            // wasted use, so the player can just try again the moment one becomes available.
            if (enemies.Count == 0)
            {
                NotifyFlankFailed(player, now, "No living enemy to flank - try again!");
                continue;
            }

            var target = enemies[Random.Shared.Next(enemies.Count)];
            var targetPawn = target.PlayerPawn!;

            if (targetPawn.AbsOrigin is not { } targetOrigin)
            {
                continue;
            }

            targetPawn.EyeAngles.ToDirectionVectors(out var forward, out _, out _);
            var behind = targetOrigin - (forward * Runtime.Config.Flanker.TeleportDistance);
            var dropPosition = new Vector(behind.X, behind.Y, behind.Z + Runtime.Config.Flanker.DropHeight);

            // Bug fix: the destination used to be teleported to unconditionally - a wall or other
            // solid geometry directly behind the target (or a target standing right against a corner)
            // could land the assigned player stuck inside it. Same "no valid target, don't consume
            // the cooldown" treatment as the enemies.Count==0 case above - the player can just press
            // F again immediately, which may well pick a different (clear) target.
            if (!IsPositionClear(dropPosition))
            {
                NotifyFlankFailed(player, now, "Flank position is blocked - try again!");
                continue;
            }

            CSRollUtils.TeleportPlayer(Core, player, dropPosition, targetPawn.EyeAngles);
            _nextAvailableTime[player.Slot] = now + Runtime.Config.Flanker.CooldownSeconds;

            var targetName = target.Controller is { IsValid: true } targetController ? targetController.PlayerName : "an enemy";
            CSRollUtils.PrintTitleToChat(Core, player, $"Teleported behind {targetName}!");
        }
    }

    /// <summary>Standard CS2 standing player hull (VEC_HULL_MIN/MAX) - confirmed via SwiftlyS2's own TracePlayerBBox test-plugin usage.</summary>
    private static readonly BBox_t PlayerBounds = new()
    {
        Mins = new Vector(-16f, -16f, 0f),
        Maxs = new Vector(16f, 16f, 72f),
    };

    /// <summary>Zero-length player-bbox trace at the given point - StartInSolid reports whether a standing player-sized hull would fit there without actually moving anyone first.</summary>
    private bool IsPositionClear(Vector position)
    {
        var result = Core.Trace.TracePlayerBBox(position, position, PlayerBounds);
        return !result.StartInSolid;
    }

    /// <summary>Throttled failure feedback - without this, holding F with no valid/clear target would spam this message every tick, since failures deliberately don't consume the cooldown.</summary>
    private void NotifyFlankFailed(IPlayer player, float now, string message)
    {
        if (_lastFailureMessageTime.TryGetValue(player.Slot, out var lastMessage) && now - lastMessage < FailureMessageThrottleSeconds)
        {
            return;
        }

        _lastFailureMessageTime[player.Slot] = now;
        CSRollUtils.PrintTitleToChat(Core, player, message);
    }

    private void RefreshStatusHtml(IPlayer player, float now)
    {
        if (_lastHtmlUpdateTime.TryGetValue(player.Slot, out var lastUpdate) && now - lastUpdate < HtmlRefreshIntervalSeconds)
        {
            return;
        }

        // Stay off the center-HTML surface while the roll's own reveal owns it - see
        // ModifierRuntime.IsModifierHudSuppressed.
        if (Runtime.IsModifierHudSuppressed)
        {
            return;
        }

        _lastHtmlUpdateTime[player.Slot] = now;

        var remaining = _nextAvailableTime.GetValueOrDefault(player.Slot, now) - now;
        var statusLine = remaining > 0f
            ? $"<span color=\"red\" class=\"fontWeight-Bold\">Cooldown: {remaining:0.0}s</span>".Replace('.', ',')
            : "<span color=\"gold\" class=\"fontWeight-Bold\">Ready</span>";

        var html = "<span color=\"gold\" class=\"fontWeight-Bold\">Teleporter</span><br/>" +
                   "<span class=\"fontWeight-Bold\">Press \"F\" to Teleport</span><br/>" +
                   statusLine;

        player.SendCenterHTML(html, HtmlDurationMs);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _nextAvailableTime.Remove(@event.PlayerId);
        _lastHtmlUpdateTime.Remove(@event.PlayerId);
        _lastFailureMessageTime.Remove(@event.PlayerId);
    }
}
