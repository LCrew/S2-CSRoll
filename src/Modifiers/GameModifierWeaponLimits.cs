using Microsoft.Extensions.Logging;

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
    private readonly HashSet<int> _grantInProgress = [];
    private Guid _spawnHookId;

    /// <summary>Weapon categories this modifier strips. Ranged-weapon-restricting modifiers strip everything but knives; GrenadesOnly leaves grenades alone.</summary>
    protected abstract HashSet<CSWeaponType> TypesToStrip { get; }

    /// <summary>
    /// Opt-in for a modifier that's genuinely global in scope (e.g. one driven by a server-wide
    /// cvar, like the now-removed KnivesOnly). Both current subclasses (GrenadesOnly, RandomLoadout)
    /// are per-player-scoped and override this to false - broadcasting "weapons restored" to the
    /// whole server when only one player's weapons were ever touched was misleading everyone else
    /// into thinking it affected them too.
    /// </summary>
    protected virtual bool AnnounceRemovalGlobally => true;

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

        foreach (var player in GetAssignedPlayers())
        {
            StripWeapons(player);
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

        if (AnnounceRemovalGlobally)
        {
            CSRollUtils.PrintTitleToChatAll(Core, $"{Name} modifier removed - weapons restored.");
        }
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

        // Bug fix: GiveReplacementWeapons() (e.g. RandomWeapon/RandomWeapons handing the player
        // their own designated weapon via ItemServices.GiveItem) routes through this same native
        // CanAcquire check. Once SetHookResult(CancelOriginal) below actually started blocking
        // things (see next comment), that forced grant started blocking itself too, since the
        // weapon it hands out always belongs to the same TypesToStrip category it's blocking -
        // leaving the player with no weapon at all. Skip enforcement while our own grant for this
        // player is in flight so ground/buy pickups of anything else stay blocked, but the
        // modifier's own assigned weapon still gets through.
        if (_grantInProgress.Contains(player.Slot))
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

            // Diagnostic (confirmed harmless, not the missing-weapon cause - see StripWeaponTypes'
            // own bug-fix note for the actual root cause): CS2 retries giving the default spawn
            // pistol many times per respawn, and every one of those attempts gets correctly blocked
            // here - gated behind DebugMode since this fires dozens of times per respawn otherwise.
            if (Runtime.DebugMode)
            {
                Core.Logger.LogInformation("[CSRoll][{Modifier}] Blocked acquisition: slot={Slot} weaponType={WeaponType}", Name, player.Slot, type);
            }
        }
    }

    /// <summary>
    /// Protected (not private) so a subclass can re-run the strip+give cycle on demand - e.g.
    /// WeaponRoulette re-rolling mid-life - properly wrapped in the _grantInProgress guard, unlike
    /// calling CSRollUtils.StripWeaponTypes/ItemServices.GiveItem directly would be (which would let
    /// this same class's own CanAcquire.Pre hook block the forced grant, per the bug-fix note on
    /// that hook above).
    /// </summary>
    protected void StripWeapons(IPlayer player)
    {
        var removed = CSRollUtils.StripWeaponTypes(player, TypesToStrip);
        if (removed.Count > 0)
        {
            _cachedItems[player.Slot] = removed;
        }

        _grantInProgress.Add(player.Slot);
        try
        {
            GiveReplacementWeapons(player);
        }
        finally
        {
            _grantInProgress.Remove(player.Slot);
        }
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
        _grantInProgress.Remove(@event.PlayerId);
    }
}

/// <summary>
/// Replaces the old RandomWeapon (one fixed weapon) and RandomWeapons (a fresh random weapon every
/// spawn, no pistol/nades/armor) modifiers with a single richer one: a full random loadout chosen
/// once per player (same weapons every spawn for that player, like the old RandomWeapon) - a random
/// main weapon, a random pistol, 1-4 random grenades, and a coin-flip for armor+helmet.
///
/// Bug fix: the main weapon/pistol used to be a single pair picked ONCE per activation and given to
/// every assigned player regardless of team - CS2 enforces standard team weapon restrictions
/// (M4A4/AUG/etc. are CT-only, AK-47/Galil/etc. are T-only - see CSRollUtils' TOnly/CTOnly sets), so
/// a player on the wrong team for the picked weapon could silently receive nothing. Now rolled once
/// per player (cached per slot, so it's still consistent across that player's own respawns) using
/// CSRollUtils.GetRandomMainWeaponName(team)/GetRandomPistolName(team), filtered to their actual team.
/// </summary>
public sealed class GameModifierRandomLoadout : GameModifierRemoveWeapons
{
    private readonly Dictionary<int, string> _mainWeaponName = [];
    private readonly Dictionary<int, string> _pistolName = [];
    private readonly Dictionary<int, List<string>> _grenadeNames = [];
    private readonly Dictionary<int, bool> _hasArmor = [];

    public GameModifierRandomLoadout()
    {
        Name = "RandomLoadout";
        Description = "Buy menu is disabled - random main weapon, pistol and grenades (sometimes with armor)";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = [
            "GrenadesOnly",
        ];
    }

    protected override HashSet<CSWeaponType> TypesToStrip => AllRangedWeaponTypes;

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

    protected override void OnDisabled()
    {
        _mainWeaponName.Clear();
        _pistolName.Clear();
        _grenadeNames.Clear();
        _hasArmor.Clear();
        base.OnDisabled();
    }

    protected override void GiveReplacementWeapons(IPlayer player)
    {
        var itemServices = player.PlayerPawn?.ItemServices;
        if (itemServices is null)
        {
            return;
        }

        var slot = player.Slot;
        var team = player.Controller is { IsValid: true } controller ? controller.Team : Team.None;

        if (!_mainWeaponName.TryGetValue(slot, out var mainWeapon))
        {
            mainWeapon = CSRollUtils.GetRandomMainWeaponName(team);
            _mainWeaponName[slot] = mainWeapon;
        }

        if (!_pistolName.TryGetValue(slot, out var pistol))
        {
            pistol = CSRollUtils.GetRandomPistolName(team);
            _pistolName[slot] = pistol;
        }

        if (!_grenadeNames.TryGetValue(slot, out var grenades))
        {
            grenades = CSRollUtils.GetRandomGrenadeNames(Random.Shared.Next(1, 5));
            _grenadeNames[slot] = grenades;
        }

        if (!_hasArmor.TryGetValue(slot, out var hasArmor))
        {
            hasArmor = Random.Shared.Next(2) == 0;
            _hasArmor[slot] = hasArmor;
        }

        itemServices.GiveItem(mainWeapon);
        itemServices.GiveItem(pistol);

        foreach (var grenadeName in grenades)
        {
            itemServices.GiveItem(CSRollUtils.ResolveGrenadeName(grenadeName, team));
        }

        if (hasArmor && player.PlayerPawn is { } pawn)
        {
            pawn.ArmorValue = 100;
            pawn.ArmorValueUpdated();
            itemServices.HasHelmet = true;
            itemServices.HasHelmetUpdated();
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _mainWeaponName.Remove(@event.PlayerId);
        _pistolName.Remove(@event.PlayerId);
        _grenadeNames.Remove(@event.PlayerId);
        _hasArmor.Remove(@event.PlayerId);
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
/// base class's CanAcquire hook instead.
///
/// Bug fix: knife was briefly stripped too (for a genuinely grenades-only feel) but reverted per
/// explicit report - throwing your only grenade left the player weapon-less for the instant between
/// the throw and OnGrenadeThrown's replacement GiveItem landing, and CS2 doesn't handle a player
/// holding literally nothing cleanly (confuses weapon-switch/inventory state). Keeping the knife
/// guarantees there's always at least one weapon in hand, throw or no throw.
/// </summary>
public sealed class GameModifierGrenadesOnly : GameModifierRemoveWeapons
{
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
            "RandomLoadout",
        ];
    }

    protected override HashSet<CSWeaponType> TypesToStrip => CSRollUtils.AllGunWeaponTypes;

    protected override bool AnnounceRemovalGlobally => false;

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
