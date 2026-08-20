using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace CSRoll.Modifiers;

/// <summary>
/// There's no direct "missed shot" event in CS2, so a shot is inferred to have missed if the
/// attacker's hit counter hasn't grown by the next world update after EventWeaponFire.
/// </summary>
public abstract class GameModifierMissedShot : GameModifierBase
{
    private static readonly HashSet<CSWeaponType> CountableWeaponTypes =
    [
        CSWeaponType.WEAPONTYPE_KNIFE,
        CSWeaponType.WEAPONTYPE_PISTOL,
        CSWeaponType.WEAPONTYPE_SUBMACHINEGUN,
        CSWeaponType.WEAPONTYPE_RIFLE,
        CSWeaponType.WEAPONTYPE_SHOTGUN,
        CSWeaponType.WEAPONTYPE_SNIPER_RIFLE,
        CSWeaponType.WEAPONTYPE_MACHINEGUN,
        CSWeaponType.WEAPONTYPE_TASER,
    ];

    private readonly Dictionary<int, int> _cachedHitBullets = [];
    private Guid _hurtHookId;
    private Guid _fireHookId;

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
        _hurtHookId = Core.GameEvent.HookPre<EventPlayerHurt>(OnPlayerHurt);
        _fireHookId = Core.GameEvent.HookPre<EventWeaponFire>(OnWeaponFire);
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_hurtHookId);
        Core.GameEvent.Unhook(_fireHookId);
        _cachedHitBullets.Clear();
    }

    protected abstract void OnMissedShot(IPlayer player);

    private HookResult OnPlayerHurt(EventPlayerHurt @event)
    {
        if (@event.AttackerPlayer is { IsValid: true } attacker && ShouldCountMissedShots(attacker))
        {
            _cachedHitBullets[attacker.Slot] = _cachedHitBullets.GetValueOrDefault(attacker.Slot) + 1;
        }

        return HookResult.Continue;
    }

    private HookResult OnWeaponFire(EventWeaponFire @event)
    {
        if (@event.UserIdPlayer is not { IsValid: true } player || !IsAssignedTo(player.Slot) || !ShouldCountMissedShots(player))
        {
            return HookResult.Continue;
        }

        var slot = player.Slot;
        var hitBulletsBeforeShot = _cachedHitBullets.GetValueOrDefault(slot);

        Core.Scheduler.NextWorldUpdate(() =>
        {
            var currentPlayer = Core.PlayerManager.GetPlayer(slot);
            if (currentPlayer is not { IsValid: true })
            {
                return;
            }

            if (_cachedHitBullets.GetValueOrDefault(slot) <= hitBulletsBeforeShot)
            {
                OnMissedShot(currentPlayer);
            }
        });

        return HookResult.Continue;
    }

    private static bool ShouldCountMissedShots(IPlayer player)
    {
        var weaponType = player.PlayerPawn?.WeaponServices?.ActiveWeapon.Value?.PlayerWeaponVData?.As<CCSWeaponBaseVData>().WeaponType;
        return weaponType is { } type && CountableWeaponTypes.Contains(type);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _cachedHitBullets.Remove(@event.PlayerId);
    }
}

public sealed class GameModifierButterfingers : GameModifierMissedShot
{
    public GameModifierButterfingers()
    {
        Name = "Butterfingers";
        Description = "Weapons are dropped on missed shots";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["BoomerangBullets"];
    }

    protected override void OnMissedShot(IPlayer player)
    {
        if (player.PlayerPawn?.WeaponServices is not { } weaponServices || weaponServices.ActiveWeapon.Value is not { } weapon)
        {
            return;
        }

        weaponServices.DropWeapon(weapon);
    }
}

/// <summary>
/// Bug fix: a missed shot deals the weapon's full listed damage to the shooter themselves - fine for
/// something like a pistol, but a single missed AWP/auto-sniper shot could one-shot the player
/// outright at the base 100 HP, reported as dying far too quickly to meaningfully play the modifier.
/// Config.BoomerangBullets.BonusHealth (default 250) is granted on activation and every spawn, restored to
/// normal on disable, giving enough of a buffer to survive a miss or two with a heavy weapon.
/// </summary>
public sealed class GameModifierBoomerangBullets : GameModifierMissedShot
{
    private readonly Dictionary<int, int> _cachedOriginalMaxHealth = [];
    private Guid _spawnHookId;

    public GameModifierBoomerangBullets()
    {
        Name = "BoomerangBullets";
        Description = "You take the damage from your missed shots - extra health to compensate";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["Butterfingers"];
    }

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
        base.OnEnabled();

        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        foreach (var player in GetAssignedPlayers())
        {
            ApplyBonusHealth(player);
        }
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_spawnHookId);

        foreach (var slot in _cachedOriginalMaxHealth.Keys.ToList())
        {
            if (Core.PlayerManager.GetPlayer(slot) is { IsValid: true } player)
            {
                RestoreHealth(player);
            }
        }

        _cachedOriginalMaxHealth.Clear();

        base.OnDisabled();
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            ApplyBonusHealth(player);
        }

        return HookResult.Continue;
    }

    private void ApplyBonusHealth(IPlayer player)
    {
        if (player.PlayerPawn is not { } pawn)
        {
            return;
        }

        _cachedOriginalMaxHealth[player.Slot] = pawn.MaxHealth;

        var health = Runtime.Config.BoomerangBullets.BonusHealth;
        pawn.MaxHealth = health;
        pawn.MaxHealthUpdated();
        pawn.Health = health;
        pawn.HealthUpdated();
    }

    private void RestoreHealth(IPlayer player)
    {
        if (player.PlayerPawn is not { } pawn)
        {
            return;
        }

        var originalMaxHealth = _cachedOriginalMaxHealth.TryGetValue(player.Slot, out var cached) ? cached : 100;
        pawn.MaxHealth = originalMaxHealth;
        pawn.MaxHealthUpdated();
        pawn.Health = Math.Min(pawn.Health, originalMaxHealth);
        pawn.HealthUpdated();
    }

    protected override void OnMissedShot(IPlayer player)
    {
        var weapon = player.PlayerPawn?.WeaponServices?.ActiveWeapon.Value;
        var vData = weapon?.PlayerWeaponVData?.As<CCSWeaponBaseVData>();
        if (vData is null || player.PlayerPawn is not { } pawn)
        {
            return;
        }

        // Bug fix: passing no inflictor/attacker meant this self-damage never actually applied
        // in testing - self-inflicted damage needs a valid attacker/inflictor entity reference.
        player.TakeDamage(vData.Damage, DamageTypes_t.DMG_GENERIC, pawn, pawn);
    }

    /// <summary>Bug fix: this class's own _cachedOriginalMaxHealth was only ever cleared in OnDisabled - a mid-round disconnect left a stale entry a reconnecting player into the same slot could briefly inherit.</summary>
    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _cachedOriginalMaxHealth.Remove(@event.PlayerId);
    }
}
