using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Press Inspect Weapon to rewind: you snap back to where you stood a few seconds ago and get that
/// moment's health back, on a cooldown.
///
/// Trigger and cooldown/HUD plumbing are the same shape as Flanker and Vanish (GameButtonFlags.F
/// polled per tick, cooldown pushed forward on use so a held key can't repeat, gauge refreshed on a
/// throttle behind Runtime.IsModifierHudSuppressed).
///
/// The rewind itself needs position history, which nothing else in this codebase keeps. Samples are
/// taken on an interval rather than every tick - at 64 tick a 3 second window would be ~192 samples
/// per player per life for no visible benefit, where 0.1s sampling gives 30 and still lands within
/// a tenth of a second of the intended moment. The buffer is a fixed-capacity queue trimmed by age,
/// so it can't grow without bound if a round runs long.
///
/// Health is restored, never granted: rewinding is capped at the health you actually had, and can
/// still lower your health if you healed since. Rewinding is deliberately allowed to be a downgrade
/// rather than silently skipping the health part, so the ability reads as a true "undo the last few
/// seconds" rather than a heal.
/// </summary>
public sealed class GameModifierRecall : GameModifierBase
{
    /// <summary>
    /// Narrowed from 20. At 20 the bar plus its trailing percentage overflowed the HUD's line width
    /// and wrapped, so the "100%" landed on a line of its own and the block rendered four lines tall
    /// instead of three. The percentage belongs at the end of the bar, on the same line.
    /// </summary>
    private const int GaugeBarWidth = 12;
    private const float HtmlRefreshIntervalSeconds = 0.1f;
    private const int HtmlDurationMs = 400;

    private sealed record Snapshot(float Time, Vector Position, QAngle Angles, int Health);

    /// <summary>An in-flight rewind: the recorded path being replayed, and when the replay started.</summary>
    private sealed record Rewind(IReadOnlyList<Snapshot> Path, float StartTime);

    private readonly Dictionary<int, Queue<Snapshot>> _history = [];
    private readonly Dictionary<int, Rewind> _rewinds = [];
    private readonly Dictionary<int, float> _nextSampleTime = [];
    private readonly Dictionary<int, float> _nextAvailableTime = [];
    private readonly Dictionary<int, float> _lastHtmlUpdateTime = [];

    private Guid _spawnHookId;

    public GameModifierRecall()
    {
        Name = "Recall";
        Description = "Press Inspect to rewind a few seconds";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    public override IReadOnlyDictionary<string, string>? DynamicTextTokens => new Dictionary<string, string>
    {
        ["rewind"] = $"{Runtime.Config.Recall.RewindSeconds:0.#}s",
        ["cooldown"] = $"{Runtime.Config.Recall.CooldownSeconds:0.#}s",
    };

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

        var readyAt = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.Recall.RoundStartCooldownSeconds;
        foreach (var player in GetAssignedPlayers())
        {
            _nextAvailableTime[player.Slot] = readyAt;
        }
    }

    /// <summary>Seeds the cooldown for players handed this mid-round, which AddAssignedSlots doesn't re-run OnEnabled for (see GameModifierBase.OnSlotsAdded).</summary>
    protected override void OnSlotsAdded(IReadOnlyCollection<int> slots)
    {
        var readyAt = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.Recall.RoundStartCooldownSeconds;
        foreach (var slot in slots)
        {
            _nextAvailableTime[slot] = readyAt;
        }
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= OnTick;
        Core.GameEvent.Unhook(_spawnHookId);

        _history.Clear();
        _rewinds.Clear();
        _nextSampleTime.Clear();
        _nextAvailableTime.Clear();
        _lastHtmlUpdateTime.Clear();
    }

    private void OnTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;

        foreach (var player in GetAssignedPlayers())
        {
            if (!player.IsAlive || player.PlayerPawn is not { } pawn)
            {
                // Dying mid-rewind drops the replay; the pawn is being respawned, which resets its
                // collision group anyway, so there's nothing to restore.
                _rewinds.Remove(player.Slot);
                continue;
            }

            // A rewind in flight owns the player's position outright - recording while it runs would
            // write the replayed path back into the history buffer, and re-triggering mid-rewind would
            // stack two replays fighting over the same pawn every tick.
            if (_rewinds.ContainsKey(player.Slot))
            {
                AdvanceRewind(player, now);
                RefreshStatusHtml(player, now);
                continue;
            }

            RecordSnapshot(player, pawn, now);

            if (now >= _nextAvailableTime.GetValueOrDefault(player.Slot, now) &&
                player.PressedButtons.HasFlag(GameButtonFlags.F))
            {
                TryRecall(player, now);
            }

            RefreshStatusHtml(player, now);
        }
    }

    private void RecordSnapshot(IPlayer player, SwiftlyS2.Shared.SchemaDefinitions.CCSPlayerPawn pawn, float now)
    {
        var slot = player.Slot;
        if (now < _nextSampleTime.GetValueOrDefault(slot, float.NegativeInfinity))
        {
            return;
        }

        _nextSampleTime[slot] = now + Runtime.Config.Recall.SampleIntervalSeconds;

        if (pawn.AbsOrigin is not { } position)
        {
            return;
        }

        if (!_history.TryGetValue(slot, out var buffer))
        {
            buffer = new Queue<Snapshot>();
            _history[slot] = buffer;
        }

        buffer.Enqueue(new Snapshot(now, position, pawn.EyeAngles, pawn.Health));

        // Trimmed by age rather than count so the window stays correct if the sample interval is
        // retuned in config, with a hard cap as a backstop against an interval of ~0.
        var oldest = now - (Runtime.Config.Recall.RewindSeconds * 2f);
        while (buffer.Count > 0 && (buffer.Peek().Time < oldest || buffer.Count > 512))
        {
            buffer.Dequeue();
        }
    }

    private void TryRecall(IPlayer player, float now)
    {
        var slot = player.Slot;
        if (!_history.TryGetValue(slot, out var buffer) || buffer.Count == 0)
        {
            return;
        }

        // The oldest sample still inside the rewind window - i.e. as far back as the ability allows.
        // Clamped to the oldest sample held, so the ability still does something sensible when a
        // player has been alive for less than the full rewind duration.
        var samples = buffer.ToList();
        var target = now - Runtime.Config.Recall.RewindSeconds;
        var startIndex = samples.FindLastIndex(sample => sample.Time <= target);
        var path = samples[(startIndex < 0 ? 0 : startIndex)..];

        if (path.Count == 0)
        {
            return;
        }

        _rewinds[slot] = new Rewind(path, now);
        _nextAvailableTime[slot] = now + Runtime.Config.Recall.CooldownSeconds;

        // Cleared so a second recall can't chain back to a pre-rewind position and effectively
        // rewind twice as far. The path is already copied out above, so this doesn't disturb the
        // replay in flight.
        buffer.Clear();

        // Non-solid for the duration, restored in FinishRewind. Dragging a solid pawn backwards
        // through several seconds of geometry in a fraction of the time it originally took can wedge
        // it in a doorway or on a step it had cleanly walked through - the same protection
        // CSRollUtils.TeleportPlayer applies for a single hop, held open across the whole replay.
        if (player.PlayerPawn is { } pawn)
        {
            pawn.Collision.CollisionGroup = (byte)CollisionGroup.Pushaway;
            pawn.Collision.CollisionGroupUpdated();
        }
    }

    /// <summary>
    /// Walks the player back along their recorded path, one interpolated step per tick, instead of
    /// snapping them there in a single Teleport.
    ///
    /// Sampled backwards through the path (newest first) and interpolated between adjacent snapshots,
    /// so the replay speed is independent of the sample interval - 3 seconds of history recorded at
    /// 0.1s replays smoothly over RewindAnimationSeconds regardless of how many samples that is.
    ///
    /// View angles are deliberately NOT written during the replay. Baking a recorded pitch into the
    /// pawn's rotation is what put the camera inside the ground for Flanker and the spawn teleport
    /// (see CSRollUtils.TeleportPlayer's note - entity rotation only ever has a meaningful yaw), and
    /// leaving the camera alone also lets the player watch themselves being pulled back rather than
    /// having their view yanked around mid-animation. Only the landing frame sets a yaw.
    /// </summary>
    private void AdvanceRewind(IPlayer player, float now)
    {
        var slot = player.Slot;
        if (!_rewinds.TryGetValue(slot, out var rewind))
        {
            return;
        }

        var duration = Math.Max(0.05f, Runtime.Config.Recall.RewindAnimationSeconds);
        var progress = Math.Clamp((now - rewind.StartTime) / duration, 0f, 1f);

        if (progress >= 1f)
        {
            FinishRewind(player, rewind);
            return;
        }

        // Path runs oldest -> newest, so playback reads it from the far end back to the start.
        var position = rewind.Path.Count - 1 - (progress * (rewind.Path.Count - 1));
        var index = (int)Math.Floor(position);
        var next = Math.Min(index + 1, rewind.Path.Count - 1);
        var blend = position - index;

        var from = rewind.Path[index].Position;
        var to = rewind.Path[next].Position;

        player.Teleport(
            new Vector(
                from.X + ((to.X - from.X) * blend),
                from.Y + ((to.Y - from.Y) * blend),
                from.Z + ((to.Z - from.Z) * blend)),
            null,
            new Vector(0f, 0f, 0f));
    }

    private void FinishRewind(IPlayer player, Rewind rewind)
    {
        var slot = player.Slot;
        _rewinds.Remove(slot);

        var landing = rewind.Path[0];

        // Yaw only, pitch and roll zeroed - see AdvanceRewind's note.
        player.Teleport(landing.Position, new QAngle(0f, landing.Angles.Yaw, 0f), new Vector(0f, 0f, 0f));

        if (player.PlayerPawn is { } pawn)
        {
            pawn.Collision.CollisionGroup = (byte)CollisionGroup.Player;
            pawn.Collision.CollisionGroupUpdated();

            pawn.Health = Math.Clamp(landing.Health, 1, pawn.MaxHealth);
            pawn.HealthUpdated();
        }
    }

    private void RefreshStatusHtml(IPlayer player, float now)
    {
        var slot = player.Slot;
        if (_lastHtmlUpdateTime.TryGetValue(slot, out var lastUpdate) && now - lastUpdate < HtmlRefreshIntervalSeconds)
        {
            return;
        }

        if (Runtime.IsModifierHudSuppressed)
        {
            return;
        }

        _lastHtmlUpdateTime[slot] = now;

        var cooldown = Math.Max(0.01f, Runtime.Config.Recall.CooldownSeconds);
        var readyAt = _nextAvailableTime.GetValueOrDefault(slot, now);
        var ratio = Math.Clamp((cooldown - (readyAt - now)) / cooldown, 0f, 1f);

        // Kept short deliberately - the old "Press Inspect Weapon to rewind" was long enough to wrap,
        // which (with the over-wide bar above) is what made this block four lines tall.
        var statusLine = _rewinds.ContainsKey(slot)
            ? "<span color=\"gold\" class=\"fontWeight-Bold\">Rewinding</span>"
            : readyAt > now
                ? "<span color=\"red\" class=\"fontWeight-Bold\">Charging</span>"
                : "<span class=\"fontWeight-Bold\">Press </span><span color=\"gold\" class=\"fontWeight-Bold\">Inspect</span>";

        var html = "<span color=\"gold\" class=\"fontWeight-Bold\">Recall</span><br/>" +
                   CSRollUtils.BuildBarHtml(ratio, readyAt > now ? CSRollUtils.GetGaugeBarColor(ratio) : "lime", GaugeBarWidth) + "<br/>" +
                   statusLine;

        SetHud(slot, html);
    }

    /// <summary>History is per-life - rewinding into where you stood before you died would teleport you across the map.</summary>
    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            _history.Remove(player.Slot);
            _nextSampleTime.Remove(player.Slot);
            _nextAvailableTime[player.Slot] = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.Recall.RoundStartCooldownSeconds;
        }

        return HookResult.Continue;
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _history.Remove(@event.PlayerId);
        _nextSampleTime.Remove(@event.PlayerId);
        _nextAvailableTime.Remove(@event.PlayerId);
        _lastHtmlUpdateTime.Remove(@event.PlayerId);
    }
}
