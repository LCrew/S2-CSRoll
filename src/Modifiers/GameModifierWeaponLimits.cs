using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>Strips a configurable set of weapon types from every player, giving replacement items back on disable.</summary>
public abstract class GameModifierRemoveWeapons : GameModifierBase
{
    private readonly Dictionary<int, List<string>> _cachedItems = [];
    private Guid _spawnHookId;

    /// <summary>Weapon categories this modifier strips. Ranged-weapon-restricting modifiers strip everything but knives; GrenadesOnly leaves grenades alone.</summary>
    protected abstract HashSet<CSWeaponType> TypesToStrip { get; }

    /// <summary>Kept as an alias of CSRollUtils.AllRangedWeaponTypes (relocated there so GameModifierFullInvisibility can reuse it without inheriting this class).</summary>
    protected static HashSet<CSWeaponType> AllRangedWeaponTypes => CSRollUtils.AllRangedWeaponTypes;

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
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        // Bug fix: this used to rely entirely on a bolt-on ModifierConfig/<Name>.cfg setting
        // mp_buy_allow_guns/mp_buy_allow_grenades server-wide, which blocked the buy menu for
        // EVERY player - including the enemy team - not just whoever this modifier is assigned to.
        // Denying acquisition per-player here (buy AND pickup, since AcquireMethod covers both)
        // replaces that global cvar entirely.
        Core.GameHooks.Items.CanAcquire.Pre += OnCanAcquireItem;

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot))
            {
                StripWeapons(player);
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_spawnHookId);
        Core.GameHooks.Items.CanAcquire.Pre -= OnCanAcquireItem;

        foreach (var slot in _cachedItems.Keys.ToList())
        {
            if (Core.PlayerManager.GetPlayer(slot) is { IsValid: true } player)
            {
                ReturnWeapons(player);
            }
        }

        _cachedItems.Clear();
        CSRollUtils.PrintTitleToChatAll(Core, $"{Name} modifier removed - weapons restored.");
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            StripWeapons(player);
        }

        return HookResult.Continue;
    }

    private void OnCanAcquireItem(ref CanAcquireItemPreContext ctx)
    {
        var player = ctx.Params.Player;
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return;
        }

        var weaponType = ctx.Params.WeaponVData?.WeaponType;
        if (weaponType is { } type && TypesToStrip.Contains(type))
        {
            // Bug fix: SetReturn alone doesn't stop the native CanAcquire function from still
            // running afterward and doing its own default thing (allowing the acquisition) - this
            // SDK's own docs for other Pre hooks (TakeDamage, join-server) are explicit that
            // HookResult.Stop/CancelOriginal is what actually prevents the original from running;
            // without it here, every SetReturn(NotAllowedByProhibition) call in this codebase was a
            // no-op and buying/picking up "blocked" weapon types silently still worked.
            ctx.SetReturn(AcquireResult.NotAllowedByProhibition);
            ctx.SetHookResult(HookResult.CancelOriginal);
        }
    }

    private void StripWeapons(IPlayer player)
    {
        var removed = CSRollUtils.StripWeaponTypes(player, TypesToStrip);
        if (removed.Count > 0)
        {
            _cachedItems[player.Slot] = removed;
        }

        GiveReplacementWeapons(player);
    }

    protected virtual void GiveReplacementWeapons(IPlayer player)
    {
    }

    private void ReturnWeapons(IPlayer player)
    {
        if (!_cachedItems.TryGetValue(player.Slot, out var items))
        {
            return;
        }

        CSRollUtils.RestoreWeapons(player, items);
        _cachedItems.Remove(player.Slot);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _cachedItems.Remove(@event.PlayerId);
    }
}

public sealed class GameModifierKnifeOnly : GameModifierRemoveWeapons
{
    public GameModifierKnifeOnly()
    {
        Name = "KnivesOnly";
        Description = "Buy menu is disabled, knives only";
        SupportsRandomRounds = true;
        // Not per-player randomizable: the bolt-on ModifierConfig/KnivesOnly.cfg disables the buy
        // menu via a server-wide mp_buy_allow_* cvar (no Client: section), which would affect
        // every player regardless of who rolled this modifier.
        IncompatibleModifiers = [
            "RandomWeapon",
            "RandomWeapons",
            "GrenadesOnly",
        ];
    }

    protected override HashSet<CSWeaponType> TypesToStrip => AllRangedWeaponTypes;
}

public sealed class GameModifierRandomWeapon : GameModifierRemoveWeapons
{
    private string _chosenWeaponName = "";

    public GameModifierRandomWeapon()
    {
        Name = "RandomWeapon";
        Description = "Buy menu is disabled, random weapon only";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = [
            "KnivesOnly",
            "RandomWeapons",
            "GrenadesOnly",
        ];
    }

    protected override HashSet<CSWeaponType> TypesToStrip => AllRangedWeaponTypes;

    protected override void OnEnabled()
    {
        _chosenWeaponName = CSRollUtils.GetRandomRangedWeaponName();
        base.OnEnabled();
    }

    protected override void GiveReplacementWeapons(IPlayer player)
    {
        player.PlayerPawn?.ItemServices?.GiveItem(_chosenWeaponName);
    }
}

public sealed class GameModifierRandomWeapons : GameModifierRemoveWeapons
{
    public GameModifierRandomWeapons()
    {
        Name = "RandomWeapons";
        Description = "Buy menu is disabled, random weapons are given out";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = [
            "KnivesOnly",
            "RandomWeapon",
            "GrenadesOnly",
        ];
    }

    protected override HashSet<CSWeaponType> TypesToStrip => AllRangedWeaponTypes;

    protected override void GiveReplacementWeapons(IPlayer player)
    {
        player.PlayerPawn?.ItemServices?.GiveItem(CSRollUtils.GetRandomRangedWeaponName());
    }
}

/// <summary>
/// HE grenades only - and re-gives an HE every time one is thrown, so players never run out.
/// Bug fix: this used to also give molotov/smoke/flashbang once at spawn - per explicit request this
/// is unlimited HE and nothing else, so GiveReplacementWeapons only ever gives weapon_hegrenade now.
///
/// Bug fix: this used to be bolted onto ModifierConfig/GrenadesOnly.cfg, which set sv_infinite_ammo
/// 1 (and sv_cheats 1 to allow it) server-wide - infinite ammo for every weapon, for every player on
/// the server, not just unlimited HE grenades for whoever rolled this. The unlimited-HE effect never
/// actually needed that cvar: OnGrenadeThrown below already re-gives a fresh HE directly to the
/// assigned player on every throw. The same bolt-on file also set mp_buy_allow_guns 0 globally,
/// blocking the buy menu for everyone rather than just the roller - now handled per-player by the
/// base class's CanAcquire hook instead. Knife is now stripped too (previously left alone) so this
/// modifier is genuinely grenades-only.
/// </summary>
public sealed class GameModifierGrenadesOnly : GameModifierRemoveWeapons
{
    private static readonly HashSet<CSWeaponType> StripTypes =
    [
        CSWeaponType.WEAPONTYPE_PISTOL, CSWeaponType.WEAPONTYPE_SUBMACHINEGUN, CSWeaponType.WEAPONTYPE_RIFLE,
        CSWeaponType.WEAPONTYPE_SHOTGUN, CSWeaponType.WEAPONTYPE_SNIPER_RIFLE, CSWeaponType.WEAPONTYPE_MACHINEGUN,
        CSWeaponType.WEAPONTYPE_TASER, CSWeaponType.WEAPONTYPE_KNIFE,
    ];

    private const string HeGrenadeName = "weapon_hegrenade";

    private Guid _thrownHookId;

    public GameModifierGrenadesOnly()
    {
        Name = "GrenadesOnly";
        Description = "Buy menu is disabled, unlimited HE grenades only";
        SupportsRandomRounds = true;
        // Bug fix: this was missing SupportsPerPlayerRandomization, so when it got randomly rolled
        // it could only ever go through the global/shared activation path - which activates with an
        // empty AssignedSlots, and IsAssignedTo() treats an empty set as "everyone". That's why it
        // applied to the whole server instead of just the one player who rolled it.
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = [
            "KnivesOnly",
            "RandomWeapon",
            "RandomWeapons",
        ];
    }

    protected override HashSet<CSWeaponType> TypesToStrip => StripTypes;

    protected override void OnEnabled()
    {
        base.OnEnabled();
        _thrownHookId = Core.GameEvent.HookPost<EventGrenadeThrown>(OnGrenadeThrown);
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_thrownHookId);
        base.OnDisabled();
    }

    protected override void GiveReplacementWeapons(IPlayer player)
    {
        player.PlayerPawn?.ItemServices?.GiveItem(HeGrenadeName);
    }

    private HookResult OnGrenadeThrown(EventGrenadeThrown @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            player.PlayerPawn?.ItemServices?.GiveItem(HeGrenadeName);
        }

        return HookResult.Continue;
    }
}
