using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.EntitySystem;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Sounds;
using SwiftlyS2.Shared.Trace;

using CSRoll.Config;
using CSRoll.Core;
using CSRoll.Hud;

namespace CSRoll.Modifiers;

/// <summary>
/// Player-triggered ability on Flanker's Inspect-Weapon pattern (cooldown, gauge, status HTML):
/// pressing Inspect releases a wave of ChickensPerWave chickens, SpawnGapSeconds apart, that run at
/// the nearest living enemy. Each one beeps like a planted C4 - faster and faster as its FuseSeconds
/// run out, with the C4's red timer blink on every beep - and blows up as an HE grenade thrown by the
/// owner (so the owner gets the kill), dealing DamageMultiplier of its normal damage, when it reaches
/// an enemy, when the fuse runs out, or when shot.
///
/// Steering: "Direct" (default) moves the chicken itself, every tick, straight at its target at
/// RunSpeed - stepping up ledges, sliding along walls and dropping to the ground with hull traces.
/// "Leader" instead makes the target the chicken's Leader (the field a player's +use sets) and lets
/// its own AI chase them; live testing showed those chickens sometimes ran in circles, at the AI's own
/// speed.
///
/// The blink is the C4's own timer-light effect, spawned per beep and parented to the chicken. The
/// planted bomb's c4_timer_light is a composite effect, and composites never render when a plugin
/// spawns them (see MasterZeus) - so its two children are spawned directly instead.
///
/// The explosion is a real hegrenade_projectile from Core.Game.EmitHEGrenade - hand-built grenades
/// never went off (see ClusterGrenades). Its fuse is forced to "now" three times over: inside
/// OnEntitySpawned while the emit is still running, again once it returns, and again a tick later,
/// since ChineseGrenades found a single late write doesn't stick.
///
/// Chickens are tracked by entity handle, never by index: a round restart deletes them behind our
/// back and their indices get reused. A chicken whose handle stops resolving is dropped silently -
/// never exploded at its last position, which would set off phantom grenades in a fresh round.
///
/// Damage to our chickens is always zeroed, and nothing is despawned or emitted inside the damage
/// hook (the engine is still resolving the hit there - see Revive): a qualifying hit only flags the
/// chicken, and the next tick blows it. Blasts from this modifier's own grenades are ignored, so a
/// wave goes off one chicken at a time instead of chain-detonating.
/// </summary>
public sealed class GameModifierKamikazeChickens : GameModifierBase
{
    private const string ChickenDesignerName = "chicken";
    private const string ChickenModel = "models/chicken/chicken.vmdl";
    private const float HtmlRefreshIntervalSeconds = 0.1f;
    private const float FailureMessageThrottleSeconds = 1.5f;

    /// <summary>Server-wide cap on chickens out at once, queued spawns included.</summary>
    private const int MaxLiveChickens = 24;

    /// <summary>How far above or below an enemy a chicken can be and still count as touching them.</summary>
    private const float ContactHeight = 72f;

    private const float GrenadeTrackSeconds = 3f;

    /// <summary>How long each beep's blink effect lives before it is removed.</summary>
    private const float BlinkSeconds = 0.4f;

    /// <summary>Height above the chicken's feet the blink appears at - about its back.</summary>
    private const float BlinkHeight = 18f;

    /// <summary>Direct steering: the tallest ledge a chicken steps up, and how fast it falls off one.</summary>
    private const float StepHeight = 18f;
    private const float FallSpeed = 600f;

    /// <summary>Damage that sets a chicken off when it lands: guns, knives, Zeus, fire, and anyone else's grenades.</summary>
    private const DamageTypes_t DetonatingDamage =
        DamageTypes_t.DMG_BULLET | DamageTypes_t.DMG_BUCKSHOT | DamageTypes_t.DMG_SLASH | DamageTypes_t.DMG_CLUB |
        DamageTypes_t.DMG_BURN | DamageTypes_t.DMG_SHOCK | DamageTypes_t.DMG_BLAST;

    private static readonly Color ChickenTint = new((byte)255, (byte)110, (byte)110, (byte)255);

    private static readonly BBox_t ChickenBounds = new()
    {
        Mins = new Vector(-12f, -12f, 0f),
        Maxs = new Vector(12f, 12f, 24f),
    };

    private sealed class LiveChicken
    {
        public required CHandle<CChicken> Handle { get; init; }
        public required int OwnerSlot { get; init; }
        public required Team OwnerTeam { get; init; }
        public required CHandle<CCSPlayerPawn> OwnerPawn { get; init; }
        public required float ArmedAt { get; init; }
        public required float ExplodeAt { get; init; }
        public int TargetSlot { get; set; } = -1;
        public float NextBeepAt { get; set; }
        public float LastSteerAt { get; set; }
        public bool DetonateRequested { get; set; }
    }

    private sealed record PendingSpawn(int OwnerSlot, int Index, float DueAt);

    private readonly Dictionary<int, float> _nextAvailableTime = [];
    private readonly Dictionary<int, float> _lastHtmlUpdateTime = [];
    private readonly Dictionary<int, float> _lastFailureMessageTime = [];
    private readonly List<PendingSpawn> _pendingSpawns = [];
    private readonly List<LiveChicken> _chickens = [];

    /// <summary>Live blink effects and when to remove them.</summary>
    private readonly List<(CHandle<CParticleSystem> Handle, float RemoveAt)> _blinks = [];

    /// <summary>This modifier's grenades (raw handle -> when to stop tracking), so their blasts don't set off the rest of the wave.</summary>
    private readonly Dictionary<uint, float> _ourGrenades = [];

    /// <summary>Entity index -> emit time, for the !rolldebug detonation-latency log only.</summary>
    private readonly Dictionary<int, float> _emitTimes = [];

    private bool _emitting;
    private Guid _spawnHookId;
    private Guid _detonateHookId;

    public GameModifierKamikazeChickens()
    {
        Name = "KamikazeChickens";
        Description = "Press Inspect Weapon to release beeping chickens that run at the nearest enemy and explode";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;

        // ChineseGrenades would re-roll the instant fuse and ClusterGrenades would split the blast;
        // AtomicExplosions would scale it on top of DamageMultiplier.
        IncompatibleModifiers = ["ClusterGrenades", "ChineseGrenades", "AtomicExplosions"];
    }

    private KamikazeChickensConfig Cfg => Runtime.Config.KamikazeChickens;

    public override IReadOnlyDictionary<string, string>? DynamicTextTokens => new Dictionary<string, string>
    {
        ["count"] = $"{Math.Max(1, Cfg.ChickensPerWave)}",
        ["fuse"] = $"{Cfg.FuseSeconds:0.#}s",
    };

    protected override void OnRegistered()
    {
        Core.Event.OnClientDisconnected += OnClientDisconnected;
        Core.Event.OnPrecacheResource += OnPrecacheResource;
    }

    protected override void OnUnregistered()
    {
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        Core.Event.OnPrecacheResource -= OnPrecacheResource;
    }

    protected override void OnEnabled()
    {
        Core.Event.OnTick += OnTick;
        Core.Event.OnEntitySpawned += OnEntitySpawned;
        Core.GameHooks.Entities.TakeDamage.Pre += OnTakeDamage;
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);
        _detonateHookId = Core.GameEvent.HookPost<EventHegrenadeDetonate>(OnHegrenadeDetonate);

        var readyAt = Core.Engine.GlobalVars.CurrentTime + Cfg.RoundStartCooldownSeconds;
        foreach (var player in GetAssignedPlayers())
        {
            _nextAvailableTime[player.Slot] = readyAt;
        }
    }

    /// <summary>Seeds the cooldown for players handed this modifier mid-round - OnEnabled doesn't re-run for them (see Flanker).</summary>
    protected override void OnSlotsAdded(IReadOnlyCollection<int> slots)
    {
        var readyAt = Core.Engine.GlobalVars.CurrentTime + Cfg.RoundStartCooldownSeconds;
        foreach (var slot in slots)
        {
            _nextAvailableTime[slot] = readyAt;
        }
    }

    protected override void OnSlotsRemoved(IReadOnlyCollection<int> slots)
    {
        foreach (var slot in slots)
        {
            RemoveOwner(slot);
        }
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= OnTick;
        Core.Event.OnEntitySpawned -= OnEntitySpawned;
        Core.GameHooks.Entities.TakeDamage.Pre -= OnTakeDamage;
        Core.GameEvent.Unhook(_spawnHookId);
        Core.GameEvent.Unhook(_detonateHookId);

        foreach (var chicken in _chickens)
        {
            DespawnChicken(chicken);
        }

        _chickens.Clear();
        _pendingSpawns.Clear();
        RemoveBlinks(float.MaxValue);
        _ourGrenades.Clear();
        _emitTimes.Clear();
        _nextAvailableTime.Clear();
        _lastHtmlUpdateTime.Clear();
        _lastFailureMessageTime.Clear();
    }

    /// <summary>Precached on every map load, not just while active - a map without chickens has nothing to draw one with otherwise.</summary>
    private void OnPrecacheResource(IOnPrecacheResourceEvent @event)
    {
        @event.AddItem(ChickenModel);
        foreach (var path in BlinkParticlePaths)
        {
            @event.AddItem(path);
        }
    }

    private IEnumerable<string> BlinkParticlePaths =>
        new[] { Cfg.BlinkParticlePath, Cfg.BlinkSecondaryParticlePath }.Where(path => !string.IsNullOrWhiteSpace(path));

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            _nextAvailableTime[player.Slot] = Core.Engine.GlobalVars.CurrentTime + Cfg.RoundStartCooldownSeconds;
        }

        return HookResult.Continue;
    }

    private void OnTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;

        SpawnDueChickens(now);
        UpdateChickens(now);
        RemoveBlinks(now);

        foreach (var expired in _ourGrenades.Where(entry => now >= entry.Value).Select(entry => entry.Key).ToList())
        {
            _ourGrenades.Remove(expired);
        }

        foreach (var player in GetAssignedPlayers())
        {
            if (!player.IsAlive)
            {
                continue;
            }

            RefreshStatusHtml(player, now);

            if (!player.PressedButtons.HasFlag(GameButtonFlags.F) || now < _nextAvailableTime.GetValueOrDefault(player.Slot, now))
            {
                continue;
            }

            if (player.Controller is not { IsValid: true } controller)
            {
                continue;
            }

            // Not consuming the cooldown in either case - there's nothing to waste a wave on yet.
            if (LivingEnemiesOf(controller.Team).Count == 0)
            {
                NotifyFailed(player, now, "No living enemy for the chickens to chase - try again!");
                continue;
            }

            var count = Math.Max(1, Cfg.ChickensPerWave);
            if (_chickens.Count + _pendingSpawns.Count + count > MaxLiveChickens)
            {
                NotifyFailed(player, now, "Too many chickens are already out - try again in a moment!");
                continue;
            }

            for (var i = 0; i < count; i++)
            {
                _pendingSpawns.Add(new PendingSpawn(player.Slot, i, now + (i * Math.Max(0f, Cfg.SpawnGapSeconds))));
            }

            _nextAvailableTime[player.Slot] = now + Cfg.CooldownSeconds;
        }
    }

    private void SpawnDueChickens(float now)
    {
        foreach (var spawn in _pendingSpawns.Where(spawn => now >= spawn.DueAt).ToList())
        {
            _pendingSpawns.Remove(spawn);
            SpawnChicken(spawn, now);
        }
    }

    private void SpawnChicken(PendingSpawn spawn, float now)
    {
        // A dead or departed owner forfeits the rest of their wave.
        if (Core.PlayerManager.GetPlayer(spawn.OwnerSlot) is not { IsValid: true, IsAlive: true } owner ||
            owner.Controller is not { IsValid: true } controller ||
            owner.PlayerPawn is not { } pawn || !CSRollUtils.IsUsableHandle(pawn) || pawn.AbsOrigin is not { } origin)
        {
            return;
        }

        // Out in front of the owner on the ground, fanned left / middle / right so a wave doesn't stack.
        var yawRadians = pawn.EyeAngles.Yaw * MathF.PI / 180f;
        var forward = new Vector(MathF.Cos(yawRadians), MathF.Sin(yawRadians), 0f);
        var right = new Vector(MathF.Sin(yawRadians), -MathF.Cos(yawRadians), 0f);
        var side = ((spawn.Index % 3) - 1) * 16f;
        var spot = new Vector(origin.X + (forward.X * 32f) + (right.X * side), origin.Y + (forward.Y * 32f) + (right.Y * side), origin.Z + 4f);
        if (Core.Trace.TracePlayerBBox(spot, spot, ChickenBounds).StartInSolid)
        {
            spot = new Vector(origin.X, origin.Y, origin.Z + 4f);
        }

        var chicken = Core.EntitySystem.CreateEntityByDesignerName<CChicken>(ChickenDesignerName);
        using (var keyValues = new CEntityKeyValues())
        {
            keyValues.SetVector("origin", spot);
            keyValues.SetQAngle("angles", new QAngle(0f, pawn.EyeAngles.Yaw, 0f));
            chicken.DispatchSpawn(keyValues);
        }

        if (!CSRollUtils.IsUsableHandle(chicken))
        {
            Core.Logger.LogWarning("[CSRoll] KamikazeChickens: chicken for slot {Slot} was destroyed during DispatchSpawn.", spawn.OwnerSlot);
            return;
        }

        // A red tint, so they stand out from a map's own chickens.
        chicken.Render = ChickenTint;
        chicken.RenderUpdated();

        var live = new LiveChicken
        {
            Handle = Core.EntitySystem.GetRefEHandle(chicken),
            OwnerSlot = spawn.OwnerSlot,
            OwnerTeam = controller.Team,
            OwnerPawn = Core.EntitySystem.GetRefEHandle(pawn),
            ArmedAt = now + Math.Max(0f, Cfg.ArmDelaySeconds),
            ExplodeAt = now + Math.Max(0.5f, Cfg.FuseSeconds),
            NextBeepAt = now,
            LastSteerAt = now,
        };

        _chickens.Add(live);
        Steer(live, chicken, spot, now);

        Core.Logger.LogInformation("[CSRoll] KamikazeChickens: chicken #{Index} out for slot {Slot}.", chicken.Index, spawn.OwnerSlot);
    }

    private void UpdateChickens(float now)
    {
        foreach (var live in _chickens.ToList())
        {
            // Gone behind our back (round restart, map cleanup): forget it, never explode it.
            if (Resolve(live) is not { } chicken || chicken.AbsOrigin is not { } position)
            {
                _chickens.Remove(live);
                continue;
            }

            if (live.DetonateRequested || now >= live.ExplodeAt || TouchesEnemy(live, position))
            {
                Explode(live, chicken, position, now);
                continue;
            }

            Steer(live, chicken, position, now);
            Beep(live, chicken, position, now);
        }
    }

    private CChicken? Resolve(LiveChicken live) =>
        live.Handle.IsValid && live.Handle.Value is { } chicken && CSRollUtils.IsUsableHandle(chicken) && chicken.DesignerName == ChickenDesignerName
            ? chicken
            : null;

    private List<IPlayer> LivingEnemiesOf(Team team)
    {
        var enemyTeam = team == Team.T ? Team.CT : Team.T;
        return Core.PlayerManager.GetInTeam(enemyTeam)
            .Where(p => p.IsValid && p.IsAlive && p.PlayerPawn is { } pawn && CSRollUtils.IsUsableHandle(pawn))
            .ToList();
    }

    private bool TouchesEnemy(LiveChicken live, Vector position)
    {
        var radius = Math.Max(0f, Cfg.ContactRadius);
        foreach (var enemy in LivingEnemiesOf(live.OwnerTeam))
        {
            if (enemy.PlayerPawn!.AbsOrigin is not { } at)
            {
                continue;
            }

            var dx = at.X - position.X;
            var dy = at.Y - position.Y;
            if ((dx * dx) + (dy * dy) <= radius * radius && MathF.Abs(at.Z - position.Z) <= ContactHeight)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Keeps the chicken after its target - retargeting the nearest living enemy when the current one dies.</summary>
    private void Steer(LiveChicken live, CChicken chicken, Vector position, float now)
    {
        var enemies = LivingEnemiesOf(live.OwnerTeam);
        var target = enemies.FirstOrDefault(p => p.Slot == live.TargetSlot) ??
            enemies.MinBy(p => p.PlayerPawn!.AbsOrigin is { } at ? at.DistanceSquared(position) : float.MaxValue);
        if (target?.PlayerPawn is not { } targetPawn || targetPawn.AbsOrigin is not { } targetPosition)
        {
            return;
        }

        if (Runtime.DebugMode && target.Slot != live.TargetSlot)
        {
            Core.Logger.LogInformation("[CSRoll] KamikazeChickens: chicken #{Index} -> slot {Target} ({Distance:0}u).", chicken.Index, target.Slot, targetPosition.Distance(position));
        }

        live.TargetSlot = target.Slot;

        if (!string.Equals(Cfg.SteeringMode, "Leader", StringComparison.OrdinalIgnoreCase))
        {
            SteerDirect(live, chicken, position, targetPosition, now);
            return;
        }

        // Only written when it changed - it's networked, and a player pressing +use on the chicken
        // reassigns it, which this also undoes.
        var leader = Core.EntitySystem.GetRefEHandle(targetPawn);
        if (chicken.Leader.Raw != leader.Raw)
        {
            chicken.Leader = leader;
            chicken.LeaderUpdated();
        }

        if (chicken.FleeFrom.IsValid)
        {
            chicken.FleeFrom = CHandle<CBaseEntity>.Invalid;
        }
    }

    /// <summary>
    /// Moves the chicken itself toward the target at RunSpeed: a hull sweep raised by StepHeight (so it
    /// climbs ledges and stairs), sliding along whatever it hits, then dropped back to the ground - at
    /// most FallSpeed a second, so running off a ledge reads as a fall rather than a snap. Other
    /// chickens are ignored by the traces, so a wave doesn't jam on itself.
    /// </summary>
    private void SteerDirect(LiveChicken live, CChicken chicken, Vector position, Vector targetPosition, float now)
    {
        var dt = Math.Clamp(now - live.LastSteerAt, 0f, 0.1f);
        live.LastSteerAt = now;

        var dx = targetPosition.X - position.X;
        var dy = targetPosition.Y - position.Y;
        var distance = MathF.Sqrt((dx * dx) + (dy * dy));
        if (distance < 1f || dt <= 0f)
        {
            return;
        }

        var dirX = dx / distance;
        var dirY = dy / distance;
        var step = Math.Min(distance, Math.Max(0f, Cfg.RunSpeed) * dt);
        var trace = new TraceParams { ShouldHitEntity = entity => entity.DesignerName != ChickenDesignerName };

        var from = new Vector(position.X, position.Y, position.Z + StepHeight);
        var sweep = Core.Trace.TracePlayerBBox(from, new Vector(from.X + (dirX * step), from.Y + (dirY * step), from.Z), ChickenBounds, trace);
        if (sweep.StartInSolid)
        {
            // No headroom to step up from - move flat.
            from = position;
            sweep = Core.Trace.TracePlayerBBox(from, new Vector(from.X + (dirX * step), from.Y + (dirY * step), from.Z), ChickenBounds, trace);
            if (sweep.StartInSolid)
            {
                return;
            }
        }

        var moved = sweep.EndPos;
        if (sweep.Fraction < 1f)
        {
            // Slide: what's left of the step, minus the part pushing into the wall.
            var left = (1f - sweep.Fraction) * step;
            var normal = sweep.HitNormal;
            var into = (dirX * left * normal.X) + (dirY * left * normal.Y);
            var slideX = (dirX * left) - (normal.X * into);
            var slideY = (dirY * left) - (normal.Y * into);
            if ((slideX * slideX) + (slideY * slideY) > 0.01f)
            {
                var slide = Core.Trace.TracePlayerBBox(moved, new Vector(moved.X + slideX, moved.Y + slideY, moved.Z), ChickenBounds, trace);
                if (!slide.StartInSolid)
                {
                    moved = slide.EndPos;
                }
            }
        }

        var drop = Core.Trace.TracePlayerBBox(moved, new Vector(moved.X, moved.Y, moved.Z - StepHeight - (FallSpeed * dt) - 2f), ChickenBounds, trace);
        var landed = drop.StartInSolid ? moved : drop.EndPos;

        chicken.DesiredActivity = EChickenActivity.Run;
        chicken.Teleport(landed, new QAngle(0f, MathF.Atan2(dirY, dirX) * 180f / MathF.PI, 0f), null);
    }

    /// <summary>C4-style countdown: each beep comes sooner than the last (1s apart at release, 0.1s at the end), with the C4's red blink on every one.</summary>
    private void Beep(LiveChicken live, CChicken chicken, Vector position, float now)
    {
        if (now < live.NextBeepAt)
        {
            return;
        }

        using (var beep = new SoundEvent(Cfg.BeepSoundEventName, Cfg.BeepVolume, 1f) { SourceEntityIndex = (int)chicken.Index })
        {
            beep.Recipients.AddAllPlayers();
            beep.Emit();
        }

        var remaining = Math.Clamp((live.ExplodeAt - now) / Math.Max(0.5f, Cfg.FuseSeconds), 0f, 1f);
        live.NextBeepAt = now + 0.1f + (0.9f * MathF.Pow(remaining, 1.5f));

        foreach (var path in BlinkParticlePaths)
        {
            SpawnBlink(path, chicken, new Vector(position.X, position.Y, position.Z + BlinkHeight), now);
        }
    }

    /// <summary>MasterZeus's spawn recipe (empty keyvalues, Teleport, Start), then parented so the blink rides along with the chicken.</summary>
    private void SpawnBlink(string path, CChicken chicken, Vector at, float now)
    {
        var particle = Core.EntitySystem.CreateEntityByDesignerName<CParticleSystem>("info_particle_system");
        particle.EffectName = path;
        particle.StartActive = true;
        using (var keyValues = new CEntityKeyValues())
        {
            particle.DispatchSpawn(keyValues);
        }

        if (!CSRollUtils.IsUsableHandle(particle))
        {
            return;
        }

        particle.Teleport(at, null, null);
        particle.AcceptInput("SetParent", "!activator", chicken, chicken);
        particle.AcceptInput("Start", "", null, null, 0);
        _blinks.Add((Core.EntitySystem.GetRefEHandle(particle), now + BlinkSeconds));
    }

    /// <summary>Removes the blink effects due by now (all of them for float.MaxValue).</summary>
    private void RemoveBlinks(float now)
    {
        for (var i = _blinks.Count - 1; i >= 0; i--)
        {
            var (handle, removeAt) = _blinks[i];
            if (now < removeAt)
            {
                continue;
            }

            _blinks.RemoveAt(i);
            if (handle.IsValid && handle.Value is { } particle && CSRollUtils.IsUsableHandle(particle) && particle.DesignerName == "info_particle_system")
            {
                particle.Despawn();
            }
        }
    }

    private void Explode(LiveChicken live, CChicken chicken, Vector position, float now)
    {
        _chickens.Remove(live);

        // Gone before the grenade appears, so it doesn't bounce off the chicken.
        DespawnChicken(live);

        // A dead owner's pawn still throws (SuicideBomber relies on that); a departed owner's doesn't exist.
        if (!live.OwnerPawn.IsValid || live.OwnerPawn.Value is not { } ownerPawn || !CSRollUtils.IsUsableHandle(ownerPawn))
        {
            Core.Logger.LogInformation("[CSRoll] KamikazeChickens: chicken for slot {Slot} fizzled - its owner is gone.", live.OwnerSlot);
            return;
        }

        CHEGrenadeProjectile grenade;
        _emitting = true;
        try
        {
            grenade = Core.Game.EmitHEGrenade(new Vector(position.X, position.Y, position.Z + 8f), QAngle.Zero, new Vector(0f, 0f, 0f), ownerPawn);
        }
        finally
        {
            _emitting = false;
        }

        if (!CSRollUtils.IsUsableHandle(grenade))
        {
            Core.Logger.LogWarning("[CSRoll] KamikazeChickens: EmitHEGrenade returned nothing for slot {Slot}.", live.OwnerSlot);
            return;
        }

        ForceDetonate(grenade);

        var handle = Core.EntitySystem.GetRefEHandle(grenade);
        _ourGrenades[handle.Raw] = now + GrenadeTrackSeconds;
        _emitTimes[(int)grenade.Index] = now;
        Core.Logger.LogInformation("[CSRoll] KamikazeChickens: grenade #{Index} emitted for slot {Slot}.", grenade.Index, live.OwnerSlot);

        var invoke = Cfg.DetonateViaInvoke;
        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (!handle.IsValid || handle.Value is not { } deferred || !CSRollUtils.IsUsableHandle(deferred))
            {
                return;
            }

            ForceDetonate(deferred);
            if (invoke)
            {
                Core.GameHooks.Datamaps.CBaseGrenade.Detonate.Invoke(deferred);
            }
        });
    }

    private void ForceDetonate(CBaseGrenade grenade)
    {
        grenade.DetonateTime.Value = Core.Engine.GlobalVars.CurrentTime;
        grenade.DetonateTimeUpdated();
    }

    /// <summary>Catches our grenade while EmitHEGrenade is still setting it up - the earliest the fuse can be written.</summary>
    private void OnEntitySpawned(IOnEntitySpawnedEvent @event)
    {
        if (!_emitting || @event.Entity.DesignerName != "hegrenade_projectile")
        {
            return;
        }

        ForceDetonate(@event.Entity.As<CBaseCSGrenadeProjectile>());
    }

    private HookResult OnHegrenadeDetonate(EventHegrenadeDetonate @event)
    {
        if (_emitTimes.Remove(@event.EntityID, out var emittedAt) && Runtime.DebugMode)
        {
            Core.Logger.LogInformation("[CSRoll] KamikazeChickens: grenade #{Index} went off {Latency:0.000}s after it was emitted.", @event.EntityID, Core.Engine.GlobalVars.CurrentTime - emittedAt);
        }

        return HookResult.Continue;
    }

    private void OnTakeDamage(ref TakeDamageEntityPreContext ctx)
    {
        var entity = ctx.Params.Entity;
        var damageType = ctx.Params.Info.DamageType;
        var fromOurGrenade = (damageType & DamageTypes_t.DMG_BLAST) != 0 && _ourGrenades.ContainsKey(ctx.Params.Info.Inflictor.Raw);

        if (entity is not { IsValid: true } || entity.DesignerName != ChickenDesignerName)
        {
            // Anything else a chicken's blast reaches takes DamageMultiplier of a normal HE's damage.
            if (fromOurGrenade)
            {
                ctx.Params.Info.Damage *= Math.Max(0f, Cfg.DamageMultiplier);
            }

            return;
        }

        var index = entity.Index;
        var live = _chickens.Find(c => c.Handle.EntityIndex == index && c.Handle.IsValid);
        if (live is null)
        {
            // A map's own chicken - not ours to touch.
            return;
        }

        ctx.Params.Info.Damage = 0f;

        if (fromOurGrenade || Core.Engine.GlobalVars.CurrentTime < live.ArmedAt || (damageType & DetonatingDamage) == 0)
        {
            return;
        }

        // Exploded on the next tick, outside the damage hook.
        live.DetonateRequested = true;
    }

    private void DespawnChicken(LiveChicken live)
    {
        if (Resolve(live) is { } chicken)
        {
            chicken.Despawn();
        }
    }

    /// <summary>A player losing the modifier or leaving takes their queued and live chickens with them - despawned, not exploded.</summary>
    private void RemoveOwner(int slot)
    {
        _pendingSpawns.RemoveAll(spawn => spawn.OwnerSlot == slot);
        foreach (var live in _chickens.Where(c => c.OwnerSlot == slot).ToList())
        {
            DespawnChicken(live);
            _chickens.Remove(live);
        }
    }

    /// <summary>Throttled, since failures don't consume the cooldown and a held F key would otherwise repeat them every tick.</summary>
    private void NotifyFailed(IPlayer player, float now, string message)
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

        _lastHtmlUpdateTime[player.Slot] = now;

        var remaining = _nextAvailableTime.GetValueOrDefault(player.Slot, now) - now;
        var cooldown = Math.Max(0.01f, Math.Max(Cfg.CooldownSeconds, Cfg.RoundStartCooldownSeconds));
        SetGauge(player.Slot, remaining > 0f
            ? new HudGauge("Kamikaze Chickens", $"{remaining:0.0}s", 1f - Math.Clamp(remaining / cooldown, 0f, 1f))
            : new HudGauge("Kamikaze Chickens", "Ready · Inspect", 1f, Ready: true));

        if (Runtime.IsModifierHudSuppressed)
        {
            return;
        }

        var statusLine = remaining > 0f
            ? $"<span color=\"red\" class=\"fontWeight-Bold\">Cooldown: {remaining:0.0}s</span>".Replace('.', ',')
            : "<span color=\"gold\" class=\"fontWeight-Bold\">Ready</span>";

        var html = "<span color=\"gold\" class=\"fontWeight-Bold\">Kamikaze Chickens</span><br/>" +
                   "<span class=\"fontWeight-Bold\">Press \"F\" to release the chickens</span><br/>" +
                   statusLine;

        SetHud(player.Slot, html);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        RemoveOwner(@event.PlayerId);
        _nextAvailableTime.Remove(@event.PlayerId);
        _lastHtmlUpdateTime.Remove(@event.PlayerId);
        _lastFailureMessageTime.Remove(@event.PlayerId);
    }
}
