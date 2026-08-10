using SwiftlyS2.Shared;
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
/// own spin-then-reveal system uses (the same tick sound, Core.Scheduler.DelayBySeconds to chain
/// frames) - not a call into ModifierRuntime itself, which is tightly coupled to revealing which
/// MODIFIERS got rolled, not an arbitrary weapon pool. Driven by CSRollUtils.GetRandomMainWeaponName()
/// (the same weighted pool RandomLoadout already uses). The player keeps their current gun for the
/// whole spin, only swapping at the very last frame - a mid-round reroll doesn't leave them
/// weaponless for the whole animation.
///
/// Bug fix: the landing frame used to call CSRollUtils.StripWeaponTypes/ItemServices.GiveItem
/// directly - outside GameModifierRemoveWeapons' own _grantInProgress guard, which exists
/// specifically because this class's own CanAcquire.Pre hook blocks acquiring anything in
/// TypesToStrip, including its own forced grant (see that hook's bug-fix comment). Now goes through
/// the base class's own (newly protected) StripWeapons instead, which wraps GiveReplacementWeapons
/// in that guard correctly.
///
/// Bug fix: a player receiving the modifier used to only get their first weapon via a separate
/// manual loop in OnEnabled, which meant anyone not yet alive at that exact moment (or connecting/
/// spawning mid-cycle) got nothing until the next scheduled reroll, up to RerollIntervalSeconds
/// later. GiveReplacementWeapons now triggers an immediate roll itself whenever it's asked to arm a
/// player with no weapon rolled yet for them - covering initial activation AND every later spawn
/// through the exact same code path (GameModifierRemoveWeapons' OnPlayerSpawn already calls this on
/// every spawn), so the separate OnEnabled loop is no longer needed.
///
/// Bug fix: the spin animation and the reroll-countdown HUD used to be two independent
/// SendCenterHTML call sites racing each other, each overwriting whatever the other had just shown -
/// visibly flickering/interrupting the spin. Unified into one BuildStatusHtml template (title +
/// timer-or-"Rolling" line + blank spacer + weapon line) used by both, and the countdown refresh
/// skips entirely for any slot currently mid-spin (tracked via _rollingSlots) rather than racing it.
/// </summary>
public sealed class GameModifierWeaponRoulette : GameModifierRemoveWeapons
{
    private const int HtmlDurationMs = 400;
    private const float HtmlRefreshIntervalSeconds = 0.1f;

    private readonly Dictionary<int, string> _currentWeaponName = [];
    private readonly Dictionary<int, float> _lastHtmlUpdateTime = [];
    private readonly HashSet<int> _rollingSlots = [];
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

        // base.OnEnabled() strips every assigned player's guns and calls GiveReplacementWeapons
        // below for each - which itself detects "no weapon rolled yet" and kicks off the first spin.
        base.OnEnabled();
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= OnGameTick;
        _currentWeaponName.Clear();
        _lastHtmlUpdateTime.Clear();
        _rollingSlots.Clear();
        base.OnDisabled();
    }

    protected override void GiveReplacementWeapons(IPlayer player)
    {
        if (_currentWeaponName.TryGetValue(player.Slot, out var weaponName))
        {
            player.PlayerPawn?.ItemServices?.GiveItem(weaponName);
        }
        else
        {
            RollNewWeapon(player);
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
        _rollingSlots.Add(player.Slot);
        PlaySpin(player.Slot, CSRollUtils.GetRandomMainWeaponName(), 0);
    }

    private void PlaySpin(int slot, string finalWeaponName, int frameIndex)
    {
        if (Core.PlayerManager.GetPlayer(slot) is not { IsValid: true } player)
        {
            _rollingSlots.Remove(slot);
            return;
        }

        var frameCount = Math.Max(1, Runtime.Config.WeaponRoulette.SpinFrameCount);

        if (frameIndex >= frameCount)
        {
            _rollingSlots.Remove(slot);
            _currentWeaponName[slot] = finalWeaponName;

            var remaining = Math.Max(0f, _nextRerollTime - Core.Engine.GlobalVars.CurrentTime);
            player.SendCenterHTML(BuildStatusHtml(isRolling: false, finalWeaponName, remaining), HtmlDurationMs);

            // Goes through the base class's own StripWeapons (strips whatever gun they currently
            // hold, then calls GiveReplacementWeapons under the _grantInProgress guard) rather than
            // stripping/giving directly - see class doc comment for why that matters here.
            StripWeapons(player);
            return;
        }

        var interval = Runtime.Config.WeaponRoulette.SpinDurationSeconds / frameCount;
        var randomName = CSRollUtils.GetRandomMainWeaponName();
        player.SendCenterHTML(BuildStatusHtml(isRolling: true, randomName, 0f), (int)(interval * 1000) + 50);
        CSRollUtils.PlaySoundToPlayer(Core, player, Runtime.Config.SpinReveal.TickSoundEventName, Runtime.Config.SpinReveal.TickSoundVolume, debugMode: Runtime.DebugMode);

        Core.Scheduler.DelayBySeconds(interval, () => PlaySpin(slot, finalWeaponName, frameIndex + 1));
    }

    private void RefreshCountdownHtml(IPlayer player, float now)
    {
        // The spin's own per-frame updates already refresh this player's HTML faster than this
        // countdown would - sending here too would just flicker/fight with the spin frames.
        if (_rollingSlots.Contains(player.Slot))
        {
            return;
        }

        if (_lastHtmlUpdateTime.TryGetValue(player.Slot, out var lastUpdate) && now - lastUpdate < HtmlRefreshIntervalSeconds)
        {
            return;
        }

        _lastHtmlUpdateTime[player.Slot] = now;

        var weaponName = _currentWeaponName.GetValueOrDefault(player.Slot, "-");
        var remaining = Math.Max(0f, _nextRerollTime - now);
        player.SendCenterHTML(BuildStatusHtml(isRolling: false, weaponName, remaining), HtmlDurationMs);
    }

    /// <summary>
    /// Single 4-line template shared by both the spin animation and the idle countdown, so there's
    /// only ever one place building this modifier's HUD text. Line 2 and line 4 swap meaning based
    /// on state: "Timer: Ns" / "[orange]Active:[default] weapon" while idle, or a gradient "Rolling"
    /// / the current random spin-frame weapon name while spinning.
    ///
    /// "Rolling" uses SwiftlyS2.Shared.HtmlGradient.GenerateGradientText - a general-purpose SDK
    /// helper (lives in the plain SwiftlyS2.Shared namespace, not SwiftlyS2.Shared.Menus), not
    /// something scoped to the Menu system, confirmed via SDK reflection.
    /// </summary>
    private static string BuildStatusHtml(bool isRolling, string weaponName, float secondsRemaining)
    {
        var line2 = isRolling
            ? "<span class=\"fontWeight-bold\">" + HtmlGradient.GenerateGradientText("Rolling", "#FFA500", "#FF4500") + "</span>"
            : $"<span class=\"fontWeight-bold\">Timer: {secondsRemaining:0.0}s</span>".Replace('.', ',');

        var line4 = isRolling
            ? weaponName
            : $"<span color=\"orange\" class=\"fontWeight-bold\">Active:</span> {weaponName}";

        return "<span color=\"gold\" class=\"fontWeight-bold\">Weapon Roulette</span><br/>" +
               line2 + "<br/><br/>" +
               line4;
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _currentWeaponName.Remove(@event.PlayerId);
        _lastHtmlUpdateTime.Remove(@event.PlayerId);
        _rollingSlots.Remove(@event.PlayerId);
    }
}
