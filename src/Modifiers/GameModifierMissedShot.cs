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

public sealed class GameModifierDropOnMiss : GameModifierMissedShot
{
    public GameModifierDropOnMiss()
    {
        Name = "DropOnMiss";
        Description = "Weapons are dropped on missed shots";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["DontMiss"];
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

public sealed class GameModifierDontMiss : GameModifierMissedShot
{
    public GameModifierDontMiss()
    {
        Name = "DontMiss";
        Description = "You take the damage from your missed shots";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["DropOnMiss"];
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
}
