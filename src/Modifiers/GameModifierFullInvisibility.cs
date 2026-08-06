using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// The assigned player(s) are always invisible, knife only, and cannot buy or pick up any ranged
/// weapon/utility. Bomb pickup, planting and defusing are left completely untouched -
/// CSRollUtils.AllRangedWeaponTypes already excludes WEAPONTYPE_KNIFE and WEAPONTYPE_C4, so
/// stripping/denying that set alone is sufficient. Scoped the same way every other per-player
/// modifier is (IsAssignedTo/AssignedSlots) - this used to pick one random player itself instead,
/// which meant it couldn't participate in normal per-player random rolls (whoever the roll "gave" it
/// to and whoever actually turned invisible could be two different people).
///
/// Bug fix: the bolt-on ModifierConfig/FullInvisibility.cfg buy-menu block (mp_buy_allow_guns/
/// mp_buy_allow_grenades) was a server-wide cvar pair - it disabled buying for EVERYONE while this
/// modifier was active, not just the assigned player. Replaced with a per-player
/// Core.GameHooks.Items.CanAcquire.Pre denial (same AllRangedWeaponTypes check the CanUse hook below
/// already does for using weapons), so only the assigned player is actually blocked from buying or
/// picking anything up.
///
/// Needs both GameModifierInvisibleBase's hide/unhide plumbing AND GameModifierRemoveWeapons'
/// strip/restore plumbing at once - single inheritance can only give one directly, so this derives
/// the (more fragile) invisibility base and calls the (simpler, already-extracted) weapon-strip
/// helpers on CSRollUtils directly instead of also inheriting GameModifierRemoveWeapons.
///
/// Status HUD: unlike ConditionalInvisibility (whose visibility flips over time), this player is
/// invisible for the modifier's entire duration with nothing to count down - so rather than a real
/// percentage gauge, the bar (gold) shows "FULL" centered (padded evenly with '-' on both sides
/// rather than a hardcoded string, so it stays centered if the bar width ever changes) and "∞%"
/// in place of a percentage, kept continuously visible via the same re-send-before-it-expires
/// pattern ConditionalInvisibility uses.
/// </summary>
public sealed class GameModifierFullInvisibility : GameModifierInvisibleBase
{
    private const int GaugeBarWidth = 20;
    private const float HtmlRefreshIntervalSeconds = 0.1f;
    private const int HtmlDurationMs = 400;

    private static readonly string PermanentGaugeHtml = BuildPermanentGaugeHtml();

    private readonly Dictionary<int, List<string>> _cachedItems = [];
    private readonly Dictionary<int, float> _lastHtmlUpdateTime = [];

    private Guid _spawnHookId;

    public GameModifierFullInvisibility()
    {
        Name = "FullInvisibility";
        Description = "One random player is always invisible, knife only, can't buy or pick up weapons";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = [
            "ConditionalInvisibility",
            "KnivesOnly", "RandomWeapon", "RandomWeapons", "GrenadesOnly",
        ];
    }

    protected override bool CheckHidePlayer(IPlayer player) => IsAssignedTo(player.Slot);

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
        Core.GameHooks.Weapons.CanUse.Pre += OnCanUseWeapon;
        Core.GameHooks.Items.CanAcquire.Pre += OnCanAcquireItem;
        Core.Event.OnTick += OnTick;

        foreach (var slot in AssignedSlots)
        {
            if (Core.PlayerManager.GetPlayer(slot) is { IsValid: true } assignedPlayer)
            {
                StripWeapons(assignedPlayer);
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_spawnHookId);
        Core.GameHooks.Weapons.CanUse.Pre -= OnCanUseWeapon;
        Core.GameHooks.Items.CanAcquire.Pre -= OnCanAcquireItem;
        Core.Event.OnTick -= OnTick;

        foreach (var slot in _cachedItems.Keys.ToList())
        {
            if (Core.PlayerManager.GetPlayer(slot) is { IsValid: true } player)
            {
                CSRollUtils.RestoreWeapons(player, _cachedItems[slot]);
            }
        }

        _cachedItems.Clear();
        _lastHtmlUpdateTime.Clear();

        base.OnDisabled();
    }

    private void OnTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;

        foreach (var slot in AssignedSlots)
        {
            if (_lastHtmlUpdateTime.TryGetValue(slot, out var lastUpdate) && now - lastUpdate < HtmlRefreshIntervalSeconds)
            {
                continue;
            }

            if (Core.PlayerManager.GetPlayer(slot) is not { IsValid: true, IsAlive: true } player)
            {
                continue;
            }

            _lastHtmlUpdateTime[slot] = now;
            player.SendCenterHTML(PermanentGaugeHtml, HtmlDurationMs);
        }
    }

    /// <summary>Centers "FULL" inside a GaugeBarWidth-wide run of '-' - computed once rather than hardcoded, so it stays actually centered if GaugeBarWidth or the label text ever changes.</summary>
    private static string BuildPermanentGaugeHtml()
    {
        const string label = " FULL ";
        var totalPadding = Math.Max(0, GaugeBarWidth - label.Length);
        var leftPadding = totalPadding / 2;
        var rightPadding = totalPadding - leftPadding;
        var bar = new string('-', leftPadding) + label + new string('-', rightPadding);

        return "<span color=\"lime\" class=\"fontWeight-bold\">INVISIBLE</span><br/>" +
               $"<span color=\"gold\" class=\"fontWeight-bold\">[{bar}] ∞%</span>";
    }

    private void StripWeapons(IPlayer player)
    {
        var removed = CSRollUtils.StripWeaponTypes(player, CSRollUtils.AllRangedWeaponTypes);
        if (removed.Count > 0)
        {
            _cachedItems[player.Slot] = removed;
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

    private void OnCanUseWeapon(ref CanUseWeaponPreContext ctx)
    {
        var player = ctx.Params.Player;
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return;
        }

        var weaponType = ctx.Params.Weapon.PlayerWeaponVData?.As<CCSWeaponBaseVData>().WeaponType;
        if (weaponType is { } type && CSRollUtils.AllRangedWeaponTypes.Contains(type))
        {
            ctx.SetReturn(false);
        }
    }

    private void OnCanAcquireItem(ref CanAcquireItemPreContext ctx)
    {
        var player = ctx.Params.Player;
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return;
        }

        var weaponType = ctx.Params.WeaponVData?.WeaponType;
        if (weaponType is { } type && CSRollUtils.AllRangedWeaponTypes.Contains(type))
        {
            ctx.SetReturn(AcquireResult.NotAllowedByProhibition);
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _cachedItems.Remove(@event.PlayerId);
        _lastHtmlUpdateTime.Remove(@event.PlayerId);
    }
}
