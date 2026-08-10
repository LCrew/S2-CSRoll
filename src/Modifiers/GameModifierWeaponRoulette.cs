using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Forces the assigned player onto a single random gun (grenades untouched - buy/pickup of grenades
/// stays completely normal), re-rolled to a fresh weapon on a timer (Config.WeaponRoulette.
/// RerollIntervalSeconds). Reuses GameModifierRemoveWeapons for the strip/restore-on-disable and
/// CanAcquire.Pre buy-and-pickup block (same base RandomLoadout/GrenadesOnly already use), scoped to
/// CSRollUtils.AllGunWeaponTypes so grenades are explicitly excluded from the block.
///
/// The spin reveal is a small self-contained version of the same visual pattern ModifierRuntime's
/// own spin-then-reveal system uses (CSRollUtils.BuildSpinFrameHtml per frame, the same tick sound,
/// Core.Scheduler.DelayBySeconds to chain frames) - not a call into ModifierRuntime itself, which is
/// tightly coupled to revealing which MODIFIERS got rolled, not an arbitrary weapon pool. Scoped to
/// one player at a time and driven by CSRollUtils.GetRandomMainWeaponName() (the same weighted pool
/// RandomLoadout already uses). The player keeps their current gun for the whole spin, only swapping
/// at the very last frame - a mid-round reroll doesn't leave them weaponless for the whole animation.
/// </summary>
public sealed class GameModifierWeaponRoulette : GameModifierRemoveWeapons
{
    private const int HtmlDurationMs = 400;
    private const float HtmlRefreshIntervalSeconds = 0.1f;

    private readonly Dictionary<int, string> _currentWeaponName = [];
    private readonly Dictionary<int, float> _lastHtmlUpdateTime = [];
    private float _nextRerollTime;

    public GameModifierWeaponRoulette()
    {
        Name = "WeaponRoulette";
        Description = "Forced onto a single random gun, re-rolled every so often";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = [
            "RandomLoadout",
            "GrenadesOnly",
        ];
    }

    protected override HashSet<CSWeaponType> TypesToStrip => CSRollUtils.AllGunWeaponTypes;

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

    protected override void OnEnabled()
    {
        _nextRerollTime = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.WeaponRoulette.RerollIntervalSeconds;
        Core.Event.OnTick += OnGameTick;

        base.OnEnabled();

        // base.OnEnabled() just stripped every assigned player's guns via GiveReplacementWeapons
        // below (a no-op the first time, since _currentWeaponName is still empty) - kick off the
        // real first spin+roll for each of them now.
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot))
            {
                RollNewWeapon(player);
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= OnGameTick;
        _currentWeaponName.Clear();
        _lastHtmlUpdateTime.Clear();
        base.OnDisabled();
    }

    protected override void GiveReplacementWeapons(IPlayer player)
    {
        if (_currentWeaponName.TryGetValue(player.Slot, out var weaponName))
        {
            player.PlayerPawn?.ItemServices?.GiveItem(weaponName);
        }
    }

    private void OnGameTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;

        if (now >= _nextRerollTime)
        {
            _nextRerollTime = now + Runtime.Config.WeaponRoulette.RerollIntervalSeconds;

            foreach (var player in Core.PlayerManager.GetAllValidPlayers())
            {
                if (IsAssignedTo(player.Slot))
                {
                    RollNewWeapon(player);
                }
            }
        }

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot))
            {
                RefreshCountdownHtml(player, now);
            }
        }
    }

    private void RollNewWeapon(IPlayer player)
    {
        PlaySpin(player.Slot, CSRollUtils.GetRandomMainWeaponName(), 0);
    }

    private void PlaySpin(int slot, string finalWeaponName, int frameIndex)
    {
        if (Core.PlayerManager.GetPlayer(slot) is not { IsValid: true } player)
        {
            return;
        }

        var frameCount = Math.Max(1, Runtime.Config.WeaponRoulette.SpinFrameCount);

        if (frameIndex >= frameCount)
        {
            _currentWeaponName[slot] = finalWeaponName;
            player.SendCenterHTML(CSRollUtils.BuildSpinFrameHtml(finalWeaponName), HtmlDurationMs);

            // Only ever holding one gun at a time (CanAcquire.Pre blocks acquiring anything else),
            // so stripping AllGunWeaponTypes here removes exactly the previous roll's weapon (or
            // nothing, on the very first roll right after base.OnEnabled()'s initial strip) without
            // touching the knife/grenades.
            CSRollUtils.StripWeaponTypes(player, TypesToStrip);
            player.PlayerPawn?.ItemServices?.GiveItem(finalWeaponName);
            return;
        }

        var interval = Runtime.Config.WeaponRoulette.SpinDurationSeconds / frameCount;
        var randomName = CSRollUtils.GetRandomMainWeaponName();
        player.SendCenterHTML(CSRollUtils.BuildSpinFrameHtml(randomName), (int)(interval * 1000) + 50);
        CSRollUtils.PlaySoundToPlayer(Core, player, Runtime.Config.SpinReveal.TickSoundEventName, Runtime.Config.SpinReveal.TickSoundVolume, debugMode: Runtime.DebugMode);

        Core.Scheduler.DelayBySeconds(interval, () => PlaySpin(slot, finalWeaponName, frameIndex + 1));
    }

    private void RefreshCountdownHtml(IPlayer player, float now)
    {
        if (_lastHtmlUpdateTime.TryGetValue(player.Slot, out var lastUpdate) && now - lastUpdate < HtmlRefreshIntervalSeconds)
        {
            return;
        }

        _lastHtmlUpdateTime[player.Slot] = now;

        var remaining = Math.Max(0f, _nextRerollTime - now);
        var html = "<span color=\"gold\" class=\"fontWeight-bold\">Weapon Roulette</span><br/>" +
                   $"<span class=\"fontWeight-bold\">Next reroll: {remaining:0.0}s</span>".Replace('.', ',');

        player.SendCenterHTML(html, HtmlDurationMs);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _currentWeaponName.Remove(@event.PlayerId);
        _lastHtmlUpdateTime.Remove(@event.PlayerId);
    }
}
