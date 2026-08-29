using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;
using CSRoll.Hud;

namespace CSRoll.Modifiers;

/// <summary>
/// The player(s) this rolled for are invisible while silent - making any sound (footsteps, gunfire,
/// reload, grenade throw, or starting a bomb plant) reveals them, and they fade invisible again after
/// a config-tunable cooldown of continued silence. Taking damage also reveals them, but as its own
/// short, snappy flash (DamageFlashDurationSeconds hold, DamageFlashFadeSeconds fade both ways)
/// completely independent of the sound-cooldown timer - getting hit is a single instant, not an
/// ongoing noise, so it shouldn't stay visible for a full SoundCooldownSeconds the way a footstep
/// does. See GetFadeDurationSeconds for how the two independent timers pick which fade speed applies
/// at any given moment. Scoped the same way every other per-player modifier is (via
/// IsAssignedTo/AssignedSlots) - this used to pick one random player itself instead, which meant it
/// couldn't participate in normal per-player random rolls at all (whoever the roll "gave" it to and
/// whoever actually turned invisible could be two different people). All the per-slot cosmetic state
/// below (fade alpha, HTML refresh timing, last-sound timestamps) is keyed by slot rather than a
/// single chosen one, since more than one assigned player is now possible.
///
/// Fade: transitions use a real alpha blend (RenderMode_t.kRenderTransAlpha + Color alpha channel)
/// ramped over FadeDurationSeconds, not an instant ShouldBlockTransmitEntity toggle. Going invisible
/// ramps alpha down to 0 while still network-visible, THEN transmit-blocks once fully transparent
/// (no visible pop). Going visible transmit-UNblocks first (so the client has an entity to render
/// at all) starting from alpha 0, then ramps up to fully opaque - the reverse order, for the same
/// reason. CheckHidePlayer/base hide-unhide plumbing still gate the final settled network state;
/// the alpha ramp is purely the cosmetic transition layered on top.
///
/// Status HUD: a center-HTML box showing how visible the player actually IS, as the same ASCII gauge
/// format Vanish uses (CSRollUtils.BuildGaugeHtml). The bar is driven by the live alpha ramp, not by
/// the cooldown - it fills toward 100% as they fade out, and reads red VISIBLE / yellow FADING /
/// green INVISIBLE according to where that ramp currently sits. It previously reported the logical
/// silence check instead, which flipped to INVISIBLE a whole fade duration before the player had
/// finished disappearing; see ShouldFadeToHidden for the matching offset that makes the fade land
/// exactly on the deadline rather than starting there.
/// </summary>
public sealed class GameModifierConditionalInvisibility : GameModifierInvisibleBase
{
    private const float VisibleAlpha = 255f;
    private const float InvisibleAlpha = 0f;
    private const float HtmlRefreshIntervalSeconds = 0.1f;
    private const int HtmlDurationMs = 400;

    /// <summary>Narrowed from BuildGaugeHtml's default 20 for the same reason Recall's was - at 20 the bar plus its trailing percentage overflows the HUD line and wraps onto an extra line.</summary>
    private const int GaugeBarWidth = 12;

    private readonly Dictionary<int, float> _lastSoundTime = [];
    private readonly Dictionary<int, float> _damageFlashUntil = [];
    private readonly Dictionary<int, float> _currentAlpha = [];
    private readonly Dictionary<int, float> _lastAlphaUpdateTime = [];
    private readonly Dictionary<int, float> _lastHtmlUpdateTime = [];

    private Guid _soundHookId;
    private Guid _fireHookId;
    private Guid _reloadHookId;
    private Guid _grenadeHookId;
    private Guid _hurtHookId;
    private Guid _bombPlantHookId;
    private Guid _spawnResetHookId;

    public GameModifierConditionalInvisibility()
    {
        Name = "ConditionalInvisibility";
        Description = "One random player is invisible while silent - any sound briefly reveals them";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["Vanish"];
    }

    protected override bool CheckHidePlayer(IPlayer player) => IsAssignedTo(player.Slot) && IsSilent(player.Slot);

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

        // player_sound is CS2's own generic "audible player noise" event - hooked as the primary
        // signal. The other three are hooked too as a belt-and-suspenders safety net in case
        // player_sound doesn't cover every action - calling MarkSoundMade redundantly is harmless.
        _soundHookId = Core.GameEvent.HookPost<EventPlayerSound>(OnPlayerSound);
        _fireHookId = Core.GameEvent.HookPost<EventWeaponFire>(OnWeaponFire);
        _reloadHookId = Core.GameEvent.HookPost<EventWeaponReload>(OnWeaponReload);
        _grenadeHookId = Core.GameEvent.HookPost<EventGrenadeThrown>(OnGrenadeThrown);

        // Bug fix: taking damage (a pained grunt, physically flinching) and starting a bomb plant
        // (a long, committed, noisy action) used to not reveal at all - a hidden player could tank
        // hits or plant the bomb in total silence from the game's perspective.
        _hurtHookId = Core.GameEvent.HookPost<EventPlayerHurt>(OnPlayerHurt);
        _bombPlantHookId = Core.GameEvent.HookPost<EventBombBeginplant>(OnBombBeginPlant);

        // Runs Pre, before the base class's own Post EventPlayerSpawn hook re-checks
        // CheckHidePlayer, so a fresh life never inherits a stale cooldown from the last one.
        _spawnResetHookId = Core.GameEvent.HookPre<EventPlayerSpawn>(OnPlayerSpawnPre);

        Core.Event.OnTick += OnTick;
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= OnTick;

        Core.GameEvent.Unhook(_soundHookId);
        Core.GameEvent.Unhook(_fireHookId);
        Core.GameEvent.Unhook(_reloadHookId);
        Core.GameEvent.Unhook(_grenadeHookId);
        Core.GameEvent.Unhook(_hurtHookId);
        Core.GameEvent.Unhook(_bombPlantHookId);
        Core.GameEvent.Unhook(_spawnResetHookId);

        // Bug fix: was "foreach (var slot in AssignedSlots)" - AssignedSlots is the raw per-player
        // scoping set, which is deliberately EMPTY for a global-scope activation (!rolltoggle, as
        // opposed to !memodifier/a per-player random roll) per IsAssignedTo's own "empty means
        // everyone" convention used everywhere else in this codebase. Iterating it directly skipped
        // every player entirely for a globally-active instance. GetAssignedPlayers() (GameModifierBase)
        // encapsulates the correct "every connected player, filtered by IsAssignedTo" idiom instead.
        foreach (var player in GetAssignedPlayers())
        {
            ResetRenderState(player);
        }

        _lastSoundTime.Clear();
        _damageFlashUntil.Clear();
        _currentAlpha.Clear();
        _lastAlphaUpdateTime.Clear();
        _lastHtmlUpdateTime.Clear();

        base.OnDisabled();
    }

    private bool IsSilent(int slot) =>
        !_lastSoundTime.TryGetValue(slot, out var last) ||
        Core.Engine.GlobalVars.CurrentTime - last >= Runtime.Config.ConditionalInvisibility.SoundCooldownSeconds;

    private bool IsDamageFlashActive(int slot, float now) =>
        _damageFlashUntil.TryGetValue(slot, out var until) && now < until;

    /// <summary>
    /// The moment this player stops being revealed - whichever of the two independent reveal timers
    /// (sound cooldown, damage flash) runs out LAST, since either one alone is enough to keep them
    /// visible.
    /// </summary>
    private float GetRevealUntil(int slot)
    {
        var soundUntil = _lastSoundTime.TryGetValue(slot, out var lastSound)
            ? lastSound + Runtime.Config.ConditionalInvisibility.SoundCooldownSeconds
            : float.NegativeInfinity;
        var flashUntil = _damageFlashUntil.TryGetValue(slot, out var until) ? until : float.NegativeInfinity;

        return MathF.Max(soundUntil, flashUntil);
    }

    /// <summary>
    /// Whether the fade toward invisible should be running.
    ///
    /// Bug fix: this used to be "the reveal timer has expired", which started the fade only once the
    /// cooldown was already over - so the player then spent a further FadeDurationSeconds visibly
    /// fading while every readout already said INVISIBLE. The fade is now OFFSET to start a full fade
    /// duration EARLY, so alpha reaches zero exactly as the timer runs out: when the cooldown ends the
    /// player is genuinely gone, rather than just beginning to disappear.
    ///
    /// The settled network state (CheckHidePlayer/IsSilent) deliberately still flips at the real
    /// deadline - that's the instant alpha hits zero, so the transmit block engages on a pawn that is
    /// already fully transparent and there's still no visible pop.
    /// </summary>
    private bool ShouldFadeToHidden(int slot) =>
        Core.Engine.GlobalVars.CurrentTime >= GetRevealUntil(slot) - GetFadeDurationSeconds(slot);

    /// <summary>
    /// Picks which fade speed currently governs slot's alpha ramp by comparing whichever of the two
    /// independent reveal timers (normal sound-cooldown vs the damage flash) expires LATER - that's
    /// whichever one is actually "in charge" of keeping the player visible right now, so it also
    /// correctly governs the fade-out the moment it's the one that finally lets go. A flash with no
    /// concurrent noise makes its own deadline the later one throughout, giving both directions
    /// (reveal on hit, then snap back) the fast damage timing; ordinary noise does the same for the
    /// normal timing.
    /// </summary>
    private float GetFadeDurationSeconds(int slot)
    {
        var soundUntil = _lastSoundTime.TryGetValue(slot, out var lastSound)
            ? lastSound + Runtime.Config.ConditionalInvisibility.SoundCooldownSeconds
            : float.NegativeInfinity;
        var flashUntil = _damageFlashUntil.TryGetValue(slot, out var until) ? until : float.NegativeInfinity;

        return flashUntil > soundUntil
            ? MathF.Max(0.05f, Runtime.Config.ConditionalInvisibility.DamageFlashFadeSeconds)
            : MathF.Max(0.05f, Runtime.Config.ConditionalInvisibility.FadeDurationSeconds);
    }

    private void OnTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;

        // Bug fix: was "foreach (var slot in AssignedSlots)" - see OnDisabled's matching bug-fix note.
        // AssignedSlots is empty for a global-scope activation (!rolltoggle), so this entire tick
        // loop - the only thing that ever checks silence/damage state and drives the fade/HUD - ran
        // zero iterations for a globally-active instance. Confirmed live: OnPlayerHurt was correctly
        // firing and setting the damage-flash timer every hit (it uses IsAssignedTo), but nothing
        // ever read that timer because this loop never touched the player at all - "didn't see him
        // show up" even though the flash was being armed correctly every single time.
        foreach (var player in GetAssignedPlayers())
        {
            if (player is not { IsValid: true, IsAlive: true } || player.PlayerPawn is not { } pawn)
            {
                continue;
            }

            var slot = player.Slot;
            var desiredHidden = ShouldFadeToHidden(slot);
            var settledHidden = CachedHiddenSlots.Contains(slot);

            AdvanceFade(player, slot, pawn, desiredHidden, settledHidden, now);
            RefreshStatusHtml(player, slot, now);
        }
    }

    private void AdvanceFade(IPlayer player, int slot, CCSPlayerPawn pawn, bool desiredHidden, bool settledHidden, float now)
    {
        var lastUpdate = _lastAlphaUpdateTime.TryGetValue(slot, out var last) ? last : now;
        var deltaTime = MathF.Max(0f, now - lastUpdate);
        _lastAlphaUpdateTime[slot] = now;

        var currentAlpha = _currentAlpha.TryGetValue(slot, out var alpha) ? alpha : VisibleAlpha;
        var fadeDuration = GetFadeDurationSeconds(slot);
        var step = 255f * deltaTime / fadeDuration;

        if (desiredHidden && !settledHidden)
        {
            // Fading toward invisible - still network-visible, ramp alpha down first.
            currentAlpha = MathF.Max(InvisibleAlpha, currentAlpha - step);
            _currentAlpha[slot] = currentAlpha;
            ApplyAlpha(pawn, currentAlpha);

            if (currentAlpha <= InvisibleAlpha)
            {
                HidePlayer(player);
            }
        }
        else if (!desiredHidden && settledHidden)
        {
            // Just decided to reveal - unblock transmission now (the client needs an entity to
            // render at all before any alpha value means anything), starting fully transparent so
            // there's no instant pop to opaque.
            UnhidePlayer(player);
            currentAlpha = InvisibleAlpha;
            _currentAlpha[slot] = currentAlpha;
            ApplyAlpha(pawn, currentAlpha);

            if (Runtime.DebugMode)
            {
                Core.Logger.LogInformation("[CSRoll][ConditionalInvisibility] Revealing slot={Slot} reason={Reason} fadeDuration={FadeDuration}", slot, IsDamageFlashActive(slot, now) ? "damage" : "sound-cooldown-elapsed", fadeDuration);
            }
        }
        else if (!desiredHidden && !settledHidden && currentAlpha < VisibleAlpha)
        {
            currentAlpha = MathF.Min(VisibleAlpha, currentAlpha + step);
            _currentAlpha[slot] = currentAlpha;
            ApplyAlpha(pawn, currentAlpha);

            if (currentAlpha >= VisibleAlpha)
            {
                ResetRenderState(player);
            }
        }
    }

    private static void ApplyAlpha(CCSPlayerPawn pawn, float alpha)
    {
        pawn.RenderMode = RenderMode_t.kRenderTransAlpha;
        pawn.RenderModeUpdated();
        pawn.Render = new Color((byte)255, (byte)255, (byte)255, (byte)Math.Clamp(alpha, 0f, 255f));
        pawn.RenderUpdated();
    }

    private static void ResetRenderState(IPlayer player)
    {
        if (player.PlayerPawn is not { } pawn)
        {
            return;
        }

        pawn.RenderMode = RenderMode_t.kRenderNormal;
        pawn.RenderModeUpdated();
        pawn.Render = new Color((byte)255, (byte)255, (byte)255, (byte)255);
        pawn.RenderUpdated();
    }

    /// <summary>
    /// Reports how visible the player actually IS, read straight off the live alpha ramp, rather than
    /// how long is left on the cooldown that will eventually hide them.
    ///
    /// Bug fix: the gauge used to fill with elapsed silence and flip to INVISIBLE the instant the
    /// cooldown expired - which was a full fade duration before the player had actually finished
    /// disappearing, so it announced INVISIBLE while they were still plainly on screen. Driving it off
    /// alpha means the readout can't disagree with what the player looks like, whichever timer (sound
    /// or damage flash) happens to be governing the fade.
    /// </summary>
    private void RefreshStatusHtml(IPlayer player, int slot, float now)
    {
        if (_lastHtmlUpdateTime.TryGetValue(slot, out var lastUpdate) && now - lastUpdate < HtmlRefreshIntervalSeconds)
        {
            return;
        }

        // Stay off the center-HTML surface while the roll's own reveal owns it - see
        // ModifierRuntime.IsModifierHudSuppressed.
        if (Runtime.IsModifierHudSuppressed)
        {
            return;
        }

        _lastHtmlUpdateTime[slot] = now;

        var alpha = _currentAlpha.TryGetValue(slot, out var current) ? current : VisibleAlpha;

        // Bar fills toward invisibility, so 100% means "fully hidden" - same direction the old
        // cooldown gauge filled in, just measuring the thing it was only predicting.
        var concealment = Math.Clamp(1f - (alpha / VisibleAlpha), 0f, 1f);

        var (label, labelColor) = alpha <= InvisibleAlpha
            ? ("INVISIBLE", "lime")
            : alpha >= VisibleAlpha ? ("VISIBLE", "red") : ("FADING", "yellow");

        SetHud(slot, CSRollUtils.BuildGaugeHtml(label, labelColor, concealment, CSRollUtils.GetGaugeBarColor(concealment), GaugeBarWidth));
    }

    private void MarkSoundMade(int slot) => _lastSoundTime[slot] = Core.Engine.GlobalVars.CurrentTime;

    private HookResult OnPlayerSound(EventPlayerSound @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            MarkSoundMade(player.Slot);
        }

        return HookResult.Continue;
    }

    private HookResult OnWeaponFire(EventWeaponFire @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            MarkSoundMade(player.Slot);
        }

        return HookResult.Continue;
    }

    private HookResult OnWeaponReload(EventWeaponReload @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            MarkSoundMade(player.Slot);
        }

        return HookResult.Continue;
    }

    private HookResult OnGrenadeThrown(EventGrenadeThrown @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            MarkSoundMade(player.Slot);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event)
    {
        // Deliberately its own timer (see GetFadeDurationSeconds) rather than MarkSoundMade - taking
        // a hit is a single instant, not an ongoing noise, so it should flash briefly and snap back
        // rather than stay visible for the same full SoundCooldownSeconds a footstep/gunshot gets.
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            _damageFlashUntil[player.Slot] = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.ConditionalInvisibility.DamageFlashDurationSeconds;

            if (Runtime.DebugMode)
            {
                Core.Logger.LogInformation("[CSRoll][ConditionalInvisibility] OnPlayerHurt: slot={Slot} assigned={Assigned} settledHidden={SettledHidden} flashDuration={FlashDuration}", player.Slot, IsAssignedTo(player.Slot), CachedHiddenSlots.Contains(player.Slot), Runtime.Config.ConditionalInvisibility.DamageFlashDurationSeconds);
            }
        }
        else if (Runtime.DebugMode)
        {
            Core.Logger.LogInformation("[CSRoll][ConditionalInvisibility] OnPlayerHurt fired but UserIdPlayer was null/invalid");
        }

        return HookResult.Continue;
    }

    private HookResult OnBombBeginPlant(EventBombBeginplant @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            MarkSoundMade(player.Slot);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawnPre(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            _lastSoundTime.Remove(player.Slot);
            _damageFlashUntil.Remove(player.Slot);

            if (IsAssignedTo(player.Slot))
            {
                _currentAlpha[player.Slot] = VisibleAlpha;
                _lastAlphaUpdateTime[player.Slot] = Core.Engine.GlobalVars.CurrentTime;
            }
        }

        return HookResult.Continue;
    }

    /// <summary>Bug fix: this class's own per-slot cosmetic/timing dictionaries were only ever cleared in OnDisabled - a mid-round disconnect left stale entries a reconnecting player into the same slot could briefly inherit.</summary>
    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _lastSoundTime.Remove(@event.PlayerId);
        _damageFlashUntil.Remove(@event.PlayerId);
        _currentAlpha.Remove(@event.PlayerId);
        _lastAlphaUpdateTime.Remove(@event.PlayerId);
        _lastHtmlUpdateTime.Remove(@event.PlayerId);
    }

    /// <summary>
    /// Concealment, as a gauge. Reads the same live alpha the center-HTML gauge draws, so the two
    /// surfaces cannot disagree.
    /// </summary>
    public override HudTimer? GetHudTimer(int slot)
    {
        if (!IsAssignedTo(slot))
        {
            return null;
        }

        // The base class tracks alpha as visibility; concealment is its inverse.
        var alpha = Math.Clamp(_currentAlpha.GetValueOrDefault(slot, 1f), 0f, 1f);
        var concealment = 1f - alpha;

        // The state is the whole point of this modifier, so it goes on the detail line at full size and
        // in colour rather than as a small word beside the bar. Whether you are currently hidden is not
        // something a player should have to squint at.
        var (detail, tone) = concealment >= 0.99f ? ("INVISIBLE", HudTone.Good)
                           : concealment <= 0.01f ? ("VISIBLE", HudTone.Bad)
                           : ("FADING", HudTone.Warn);

        return HudTimer.Gauge(concealment, status: null, detail: detail, helpTop: detail, tone: tone);
    }

}
