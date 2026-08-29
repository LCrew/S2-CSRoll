namespace CSRoll.Config;

public class CSRollConfig
{
    public bool RandomRoundsEnabledByDefault { get; set; } = true;
    public bool DisableRandomRoundsInWarmup { get; set; } = true;
    public bool ShowCentreMsg { get; set; } = true;

    /// <summary>Whole-server "don't repeat" for the non-RandomizePlayers shared/global roll only - the same set from last round can't be picked again as a whole. Unrelated to PerPlayerRepeatCooldownRounds below, which is per-player and applies to the per-player roll.</summary>
    public bool CanRepeat { get; set; } = false;

    /// <summary>
    /// Per-player, per-modifier cooldown for the RandomizePlayers roll: once a specific player rolls
    /// a specific modifier, that SAME player can't roll that SAME modifier again for this many
    /// rounds - a different player is entirely unaffected and can still roll it next round. 0 disables
    /// this cooldown entirely (a player could roll the same modifier again as soon as next round).
    /// </summary>
    public int PerPlayerRepeatCooldownRounds { get; set; } = 3;

    /// <summary>
    /// Prefix shown before every chat message this plugin sends (e.g. "Added BunnyHop modifier.").
    /// Supports SwiftlyS2's chat color tokens - resolved once via SwiftlyS2.Shared.Helper.Colored()
    /// whenever this config (re)loads. SwiftlyS2 uses square brackets for color tokens (unlike
    /// CounterStrikeSharp's curly braces) - confirmed via the official porting guide
    /// (swiftlys2.net/docs/guides/porting-from-css) - so a token must be written "[colorname]",
    /// never "{colorname}", or it's left as literal text. A literal "[CSRoll]" tag in the middle
    /// (confirmed working live) relies on "CSRoll" not being a recognized color name, so
    /// Helper.Colored() leaves that bracketed text exactly as-is rather than stripping it.
    /// </summary>
    public string BannerText { get; set; } = "[orange][CSRoll][default] ";
    public int MinRandomRounds { get; set; } = 1;
    public int MaxRandomRounds { get; set; } = 1;
    public string[] DisabledModifiers { get; set; } = [];

    /// <summary>
    /// When true, random rounds assign each connected player their own independent random
    /// modifier(s) (from modifiers with SupportsPerPlayerRandomization=true) instead of one
    /// shared set applied to everyone. ConVar-driven modifiers never participate in this mode -
    /// they touch server-wide cvars and can't hold a different value per player.
    /// </summary>
    public bool RandomizePlayers { get; set; } = true;

    /// <summary>Tunables for the RandomHealth modifier (health set to a random number in this range each activation).</summary>
    public RandomHealthConfig RandomHealth { get; set; } = new();

    /// <summary>Tunables for the ConditionalInvisibility modifier (invisible while silent, briefly visible after any sound).</summary>
    public ConditionalInvisibilityConfig ConditionalInvisibility { get; set; } = new();

    /// <summary>Tunables for the Speedhack modifier (faster movement).</summary>
    public SpeedhackConfig Speedhack { get; set; } = new();

    /// <summary>Tunables for the Vanish modifier (Inspect-Weapon-triggered brief invisibility + total disarm, on a cooldown).</summary>
    public VanishConfig Vanish { get; set; } = new();

    /// <summary>Tunables for the Recall modifier (Inspect-Weapon-triggered rewind to a recent position/health, on a cooldown).</summary>
    public RecallConfig Recall { get; set; } = new();

    /// <summary>Tunables for the Butterfly Effect modifier (the carrier's extra modifier is swapped for a different random one on an interval).</summary>
    public ButterflyEffectConfig ButterflyEffect { get; set; } = new();

    /// <summary>Tunables for the Mimic modifier (steal the modifier of whoever you last killed or assisted on).</summary>
    public MimicConfig Mimic { get; set; } = new();

    /// <summary>Tunables for the HeavyBoots modifier (slower movement - now per-player, previously a server-wide sv_maxspeed cvar).</summary>
    public HeavyBootsConfig HeavyBoots { get; set; } = new();

    /// <summary>Tunables for the Jetpack modifier (per-player jump velocity boost plus jetpack thrust/fuel/air-strafe, previously a server-wide sv_jump_impulse cvar).</summary>
    public JetpackConfig Jetpack { get; set; } = new();

    /// <summary>Tunables for the BunnyHop modifier (per-player auto-bhop while holding jump, no landing speed penalty).</summary>
    public BunnyHopConfig BunnyHop { get; set; } = new();

    /// <summary>Tunables for the AtomicExplosions modifier (per-player HE damage multiplier, previously a server-wide sv_hegrenade_damage_multiplier cvar).</summary>
    public AtomicExplosionsConfig AtomicExplosions { get; set; } = new();

    /// <summary>Tunables for the IncreasedSpread modifier (per-player weapon accuracy penalty, previously a server-wide weapon_accuracy_forcespread cvar).</summary>
    public IncreasedSpreadConfig IncreasedSpread { get; set; } = new();

    /// <summary>Tunables for the PoisonousSmoke modifier (damage dealt per tick to enemies standing in the assigned player's smoke).</summary>
    public PoisonousSmokeConfig PoisonousSmoke { get; set; } = new();

    /// <summary>Tunables for the FlashingBullets modifier (per-bullet-hit blind chance).</summary>
    public FlashingBulletsConfig FlashingBullets { get; set; } = new();

    /// <summary>Tunables for the DisarmingBullets modifier (per-bullet-hit disarm chance).</summary>
    public DisarmingBulletsConfig DisarmingBullets { get; set; } = new();

    /// <summary>Tunables for the SuicideBomber modifier (grenades dropped on death, and their damage multiplier).</summary>
    public SuicideBomberConfig SuicideBomber { get; set; } = new();

    /// <summary>Tunables for the Revive modifier (escalating chance to survive lethal damage).</summary>
    public ReviveConfig Revive { get; set; } = new();

    /// <summary>Tunables for the Saint modifier (chance to revive a dead teammate on a kill).</summary>
    public SaintConfig Saint { get; set; } = new();

    /// <summary>Tunables for the MasterZeus modifier (extended-range zap damage - its cooldown tracks the mp_taser_recharge_time cvar directly instead of a separate config value).</summary>
    public MasterZeusConfig MasterZeus { get; set; } = new();

    /// <summary>Tunables for the PlantAnywhere modifier (delayed anywhere-plant + extended bomb timer, previously a static ConVarModifiers/*.cfg entry with neither).</summary>
    public PlantAnywhereConfig PlantAnywhere { get; set; } = new();

    /// <summary>Tunables for the Flanker modifier (Inspect-Weapon-triggered teleport behind a random enemy, on a cooldown).</summary>
    public FlankerConfig Flanker { get; set; } = new();

    /// <summary>
    /// Modifiers excluded from a player's random roll unless THEIR OWN team has at least 2 players.
    ///
    /// Two distinct reasons a modifier lands here:
    /// - it needs a teammate to act on at all (Saint revives a dead teammate - in a 1v1 there is
    ///   never one, so it can only ever no-op)
    /// - it only pays off AFTER the roller dies, which in a 1v1 is the moment the round ends, so
    ///   nothing is left to affect (SwapOnDeath swaps into a round that's already over;
    ///   SuicideBomber's grenades detonate with no live round around them)
    ///
    /// Checked per-roller, not server-wide, so on a 1v5 the lone player is excluded while the
    /// five-stack still rolls these normally.
    /// </summary>
    public string[] RequiresMultiplePlayersPerTeam { get; set; } = ["Saint", "SwapOnDeath", "SuicideBomber"];

    /// <summary>Tunables for the "slot machine" style spin animation shown in the center-HTML popup before the real assigned modifier(s) are revealed.</summary>
    public SpinRevealConfig SpinReveal { get; set; } = new();

    /// <summary>Tunables for the persistent center-HTML popup shown to spectators, listing whatever modifiers are active on whoever they're currently observing.</summary>
    public SpectatorHudConfig SpectatorHud { get; set; } = new();

    /// <summary>Tunables for the CS2 Custom HUD (Panorama) surface - the styled, animated alternative to the center-HTML popups. Requires a separately published Workshop addon; off by default.</summary>
    public CustomHudConfig CustomHud { get; set; } = new();

    /// <summary>Tunables for the ChineseGrenades ("Chinese Grenades") modifier (randomized fuse timer range applied to HE/flashbang/smoke).</summary>
    public ChineseGrenadesConfig ChineseGrenades { get; set; } = new();

    /// <summary>Tunables for the BoomerangBullets ("Boomerang Bullets") modifier (bonus max health so a heavy weapon's self-damage on a miss doesn't one-shot the player).</summary>
    public BoomerangBulletsConfig BoomerangBullets { get; set; } = new();

    /// <summary>Tunables for the SmallPlayers modifier (max health while shrunk).</summary>
    public SmallPlayersConfig SmallPlayers { get; set; } = new();

    /// <summary>Tunables for the LongerFlashes modifier (blind-duration multiplier applied to the assigned player's own flashbang throws).</summary>
    public LongerFlashesConfig LongerFlashes { get; set; } = new();

    /// <summary>Tunables for the ClusterGrenades modifier (min/max mini-grenade count rolled per detonation).</summary>
    public ClusterGrenadesConfig ClusterGrenades { get; set; } = new();

    /// <summary>Tunables for the Regeneration modifier (heal rate/amount over time).</summary>
    public RegenerationConfig Regeneration { get; set; } = new();

    /// <summary>Tunables for the Bounty modifier (random money-reward multiplier range per damage dealt).</summary>
    public BountyConfig Bounty { get; set; } = new();

    /// <summary>Tunables for the WeaponRoulette modifier (reroll interval, spin reveal pacing).</summary>
    public WeaponRouletteConfig WeaponRoulette { get; set; } = new();
}

public class ConditionalInvisibilityConfig
{
    /// <summary>Seconds of complete silence required after last making a sound before the player fades invisible again.</summary>
    public float SoundCooldownSeconds { get; set; } = 2.0f;

    /// <summary>Seconds the visual fade in/out takes (real alpha blend via RenderMode.kRenderTransAlpha, not an instant transmit-block toggle).</summary>
    public float FadeDurationSeconds { get; set; } = 0.5f;

    /// <summary>
    /// Taking damage reveals the player too, but as a short, snappy flash rather than the same
    /// "visible until SoundCooldownSeconds of silence" window a footstep/gunshot/etc gets - getting
    /// hit isn't an ongoing noise, it's a single instant, so this is its own independent timer/fade
    /// pair instead of feeding into the normal sound-cooldown system.
    /// </summary>
    public float DamageFlashDurationSeconds { get; set; } = 0.3f;

    /// <summary>Fade speed (both in and out) used specifically for the damage flash above - deliberately much quicker than FadeDurationSeconds so it reads as a startled "flinch" rather than a normal reveal.</summary>
    public float DamageFlashFadeSeconds { get; set; } = 0.07f;
}

public class VanishConfig
{
    /// <summary>How long a single vanish lasts - invisible and holding nothing at all for this many seconds.</summary>
    public float DurationSeconds { get; set; } = 3f;

    /// <summary>Seconds before the power can be used again, measured from when the vanish ENDS (not from when it was triggered) - so a full cycle is DurationSeconds + this. Timed that way so the HUD meter reads as one continuous drain-then-refill with no jump between the two phases.</summary>
    public float CooldownSeconds { get; set; } = 20f;

    /// <summary>Cooldown seeded on activation and on every respawn, so the power isn't available the instant a round begins.</summary>
    public float RoundStartCooldownSeconds { get; set; } = 5f;

    /// <summary>
    /// Whether the knife is stripped too, making the disarm total.
    ///
    /// Kept as a switch because this is the known-risky part: GameModifierWalkingGrenadier documents that
    /// stripping the knife was tried and reverted, since CS2 doesn't cleanly handle a player holding
    /// literally nothing (it confuses weapon-switch/inventory state). A brief self-reverting window is
    /// a much smaller exposure than a whole-round restriction, but if weapon-switching or the restore
    /// misbehaves live, turn this off - everything except the knife still gets stripped.
    /// </summary>
    public bool RemoveKnife { get; set; } = true;
}

public class RecallConfig
{
    /// <summary>How far back a rewind goes. The teleport targets the oldest recorded sample still inside this window, so it lands within one SampleIntervalSeconds of this value.</summary>
    public float RewindSeconds { get; set; } = 3f;

    /// <summary>Seconds before the power can be used again, measured from the moment it fires.</summary>
    public float CooldownSeconds { get; set; } = 20f;

    /// <summary>Cooldown seeded on activation and on every respawn, so the power isn't available the instant a round begins.</summary>
    public float RoundStartCooldownSeconds { get; set; } = 5f;

    /// <summary>
    /// How often a position/health snapshot is taken. Not every tick on purpose: at 64 tick a 3 second
    /// window would be ~192 samples per player per life for no visible gain, where 0.1s gives 30 and is
    /// still accurate to within a tenth of a second of the intended moment.
    /// </summary>
    public float SampleIntervalSeconds { get; set; } = 0.1f;

    /// <summary>
    /// How long the rewind itself takes to play out. The player is dragged back along the exact path
    /// they walked, one interpolated step per tick, instead of being snapped there instantly - so a
    /// 3 second rewind replayed over 0.4s reads as a fast "trace back" rather than a teleport.
    ///
    /// They keep camera control throughout and can't act until it finishes. Raising this makes the
    /// rewind prettier but leaves them a sitting target for longer, since they can't shoot or steer
    /// while it runs.
    /// </summary>
    public float RewindAnimationSeconds { get; set; } = 0.4f;
}

public class ButterflyEffectConfig
{
    /// <summary>Seconds between swaps. Each swap revokes the currently granted extra modifier and grants a different random one in its place.</summary>
    public float SwapIntervalSeconds { get; set; } = 20f;

    /// <summary>Delay before the first swap, so the carrier isn't handed a second modifier the instant the round starts (and while the reveal popup is still playing).</summary>
    public float FirstSwapDelaySeconds { get; set; } = 10f;

    /// <summary>Whether each swap announces the new modifier's name to the affected player in chat. Off makes the modifier considerably more chaotic to play against.</summary>
    public bool AnnounceSwaps { get; set; } = true;

    /// <summary>
    /// How long the slot-machine roll runs before landing on the new modifier. The roll STARTS this
    /// many seconds before the countdown reaches zero (exactly as WeaponRoulette does), so it fills
    /// the timer's final stretch and lands the moment the timer hits 0 rather than starting there.
    /// The carrier has no extra modifier for the duration of the roll - that gap is the "rolling"
    /// window, and it's why this shouldn't be pushed much higher.
    /// </summary>
    public float SpinDurationSeconds { get; set; } = 3f;

    /// <summary>Number of names flashed during the roll. Widened automatically if the frame count would push individual frames below the 150ms floor the HUD needs to render them - see GameModifierButterflyEffect.MinFrameIntervalSeconds.</summary>
    public int SpinFrameCount { get; set; } = 20;
}

public class MimicConfig
{
    /// <summary>Whether an assist counts as a steal, not just the killing blow.</summary>
    public bool CountAssists { get; set; } = true;

    /// <summary>Whether each steal announces the stolen modifier's name to the thief in chat.</summary>
    public bool AnnounceSteals { get; set; } = true;
}

public class SpeedhackConfig
{
    /// <summary>Movement speed multiplier (VelocityModifier mechanism).</summary>
    public float SpeedMultiplier { get; set; } = 2.0f;

    /// <summary>
    /// Zeroes CCSPlayer_MovementServices.Stamina every tick, so jumping doesn't strip the speed
    /// bonus away. Stamina is CS2's own jump/land fatigue value - it rises on every jump and landing
    /// and reduces max speed until it decays, which is why a boosted player visibly slows the moment
    /// they leave the ground. GameModifierBunnyHop already zeroes it for exactly this reason; this
    /// applies the same proven fix to Speedhack. Turn off to keep vanilla jump fatigue.
    /// </summary>
    public bool RemoveJumpStaminaPenalty { get; set; } = true;
}

public class RandomHealthConfig
{
    /// <summary>Lowest possible health value that can be rolled (inclusive).</summary>
    public int MinHealth { get; set; } = 1;

    /// <summary>Highest possible health value that can be rolled (inclusive).</summary>
    public int MaxHealth { get; set; } = 250;
}

public class BoomerangBulletsConfig
{
    /// <summary>
    /// Max health granted to the assigned player while active, restored to normal on disable -
    /// self-inflicted damage from a full-damage weapon (AWP, etc.) on a miss could otherwise
    /// one-shot the player at the base 100 HP, reported as dying far too quickly to actually play.
    /// </summary>
    public int BonusHealth { get; set; } = 250;
}

public class SmallPlayersConfig
{
    /// <summary>Max health set while active, restored to normal on disable - a smaller hitbox is harder to hit, so lower health keeps it a real glass-cannon trade-off instead of a pure upside.</summary>
    public int MaxHealth { get; set; } = 50;
}

public class HeavyBootsConfig
{
    /// <summary>Movement speed multiplier (VelocityModifier mechanism) - below 1.0 to feel "heavy".</summary>
    public float SpeedMultiplier { get; set; } = 0.5f;

    /// <summary>Armor value granted (full kevlar+helmet) to compensate for the reduced mobility.</summary>
    public int ArmorValue { get; set; } = 100;

    /// <summary>Health granted on top of the normal spawn health.</summary>
    public int BonusHealth { get; set; } = 50;
}

public class JetpackConfig
{
    /// <summary>Upward velocity (units/sec) applied on the initial jump off the ground - CS2's own default is ~301.</summary>
    public float JumpVelocityZ { get; set; } = 350f;

    /// <summary>Minimum seconds between initial-jump boosts, per player - stops spamming jump instead of holding it from re-triggering the big boost repeatedly and bypassing fuel entirely. Sustained lift while holding jump is unaffected; that's the separate, uncapped-duration thrust mechanic (ThrustSpeed).</summary>
    public float BigBoostCooldownSeconds { get; set; } = 0.75f;

    /// <summary>Vertical speed (units/sec) floored while holding jump in the air with fuel remaining - a gentle lift, not a sustained full jump.</summary>
    public float ThrustSpeed { get; set; } = 140f;

    /// <summary>Hard cap on upward velocity the thrust will ever floor to - keeps repeated thrust from stacking indefinitely on top of the initial jump boost.</summary>
    public float MaxVerticalSpeed { get; set; } = 400f;

    /// <summary>Multiplier applied to CS2's normal air-accelerate value while airborne, for stronger in-flight steering during thrust.</summary>
    public float AirStrafeMultiplier { get; set; } = 2.5f;

    /// <summary>Fuel capacity, in percentage points (0-100).</summary>
    public float MaxFuel { get; set; } = 100f;

    /// <summary>Fuel drained per second while actively thrusting (holding jump, airborne, fuel remaining).</summary>
    public float FuelDrainPerSecond { get; set; } = 25f;

    /// <summary>Fuel regenerated per second once RefillDelaySeconds has elapsed since last thrusting.</summary>
    public float FuelRegenPerSecond { get; set; } = 15f;

    /// <summary>Seconds after releasing jump (or running dry) before fuel starts regenerating - stops fuel refilling instantly the moment jump is released mid-flight.</summary>
    public float RefillDelaySeconds { get; set; } = 1f;

    /// <summary>How often, in seconds, the center-HTML fuel gauge popup refreshes.</summary>
    public float GaugeUpdateIntervalSeconds { get; set; } = 0.2f;
}

public class BunnyHopConfig
{
    /// <summary>Upward velocity (units/sec) applied on each auto-triggered jump while holding jump and grounded - CS2's own normal jump impulse is ~301, matched here since this is meant to feel like normal bhop, not a boosted jump.</summary>
    public float JumpVelocityZ { get; set; } = 301f;
}

public class AtomicExplosionsConfig
{
    /// <summary>Multiplier applied to DMG_BLAST damage dealt by the assigned player's grenades.</summary>
    public float DamageMultiplier { get; set; } = 3.0f;
}

public class IncreasedSpreadConfig
{
    /// <summary>
    /// Flat accuracy penalty forced onto the assigned player's currently held weapon every tick.
    /// Bug fix: this defaulted to 15 - CCSWeaponBase.AccuracyPenalty normally only ranges roughly
    /// 0-2 even during a full-auto spray, so forcing it to 15 every tick made bullets land
    /// essentially at random regardless of aim, reported as far too strong. Lowered to a value that
    /// still clearly and noticeably worsens accuracy without making the weapon unusable.
    /// </summary>
    public float AccuracyPenalty { get; set; } = 1.15f;
}

public class PoisonousSmokeConfig
{
    /// <summary>Damage dealt once per tick (see TickIntervalSeconds in code) to each enemy standing inside the assigned player's smoke.</summary>
    public float DamagePerTick { get; set; } = 5f;
}

public class FlashingBulletsConfig
{
    /// <summary>
    /// Minimum/maximum percent chance for a bullet hit to blind the enemy. A single value is rolled
    /// from this range once per activation (each time the modifier is applied to a player), not per
    /// bullet hit - so different activations get different odds within this range, but the odds stay
    /// fixed for the duration of one activation.
    /// </summary>
    public float MinBlindChancePercent { get; set; } = 10f;
    public float MaxBlindChancePercent { get; set; } = 40f;

    /// <summary>Blind duration in seconds applied on a successful proc.</summary>
    public float BlindDurationSeconds { get; set; } = 2f;
}

public class DisarmingBulletsConfig
{
    /// <summary>
    /// Minimum/maximum percent chance to make a hit player drop their weapon. A single value is
    /// rolled from this range once per activation (each time the modifier is applied to a player -
    /// e.g. a fresh random round), not per bullet hit - so different rounds get different odds
    /// within this range, but the odds stay fixed for the duration of one activation.
    /// </summary>
    public float MinChancePercent { get; set; } = 1f;
    public float MaxChancePercent { get; set; } = 20f;
}

public class LongerFlashesConfig
{
    /// <summary>
    /// Multiplier applied to a flashbang's own naturally-computed blind duration for the assigned
    /// player's throws. Bug fix: this used to ignore the natural duration entirely and set a flat
    /// random 2-10s regardless of the flash's real strength/distance - not actually a multiplier at
    /// all, despite the modifier's own description claiming "3 times longer".
    /// </summary>
    public float DurationMultiplier { get; set; } = 3.0f;
}

public class ClusterGrenadesConfig
{
    /// <summary>Fewest mini grenades a detonation can split into (inclusive).</summary>
    public int MinClusterCount { get; set; } = 1;

    /// <summary>Most mini grenades a detonation can split into (inclusive) - rolled fresh per detonation, so one HE could split into 2 while a smoke thrown moments later splits into 4.</summary>
    public int MaxClusterCount { get; set; } = 4;

    /// <summary>Outward toss speed given to each spawned mini grenade.</summary>
    public float ClusterSpeed { get; set; } = 250f;
}

public class RegenerationConfig
{
    /// <summary>
    /// HP/sec healed while moving. Achieved as "give 1 HP every (1/rate) seconds", not a lump sum -
    /// at the default 2, that's 1 HP every 0.5s.
    /// </summary>
    public float MovingRatePerSecond { get; set; } = 2f;

    /// <summary>HP/sec healed once standing still for at least StationaryDelaySeconds - same "1 HP, more often" calculation (at the default 5, that's 1 HP every 0.2s).</summary>
    public float StationaryRatePerSecond { get; set; } = 5f;

    /// <summary>How long the player must stand still before the faster stationary rate actually kicks in.</summary>
    public float StationaryDelaySeconds { get; set; } = 0.5f;

    /// <summary>Horizontal speed (units/sec) below which the player counts as "standing still".</summary>
    public float MovementThreshold { get; set; } = 5f;

    /// <summary>How often (seconds) the HUD's displayed HP/s number steps by 1 while cosmetically ramping toward the real target rate - purely visual, never affects actual heal timing.</summary>
    public float DisplayRampStepSeconds { get; set; } = 0.15f;
}

public class BountyConfig
{
    /// <summary>Lowest possible money-reward multiplier applied to damage dealt (inclusive).</summary>
    public float MinMultiplier { get; set; } = 0.5f;

    /// <summary>Highest possible money-reward multiplier applied to damage dealt (inclusive).</summary>
    public float MaxMultiplier { get; set; } = 3.0f;
}

public class WeaponRouletteConfig
{
    /// <summary>How often (seconds) the assigned player's weapon rerolls to a new random one.</summary>
    public float RerollIntervalSeconds { get; set; } = 25f;

    /// <summary>Total duration (seconds) of the slot-machine-style spin reveal shown before each new weapon lands - starts exactly this long before RerollIntervalSeconds' own countdown would hit 0, so the new weapon is ready right as the timer reaches zero.</summary>
    public float SpinDurationSeconds { get; set; } = 2f;

    /// <summary>Number of random-name frames the spin cycles through before landing - SpinDurationSeconds is split evenly across this many frames, so a higher count also means a faster per-frame flicker within the same total duration.</summary>
    public int SpinFrameCount { get; set; } = 30;
}

public class SuicideBomberConfig
{
    /// <summary>How many live HE grenades are dropped near the assigned player's body on death.</summary>
    public int GrenadeCount { get; set; } = 3;

    /// <summary>Damage multiplier applied to the blast damage these specific grenades deal.</summary>
    public float DamageMultiplier { get; set; } = 1.25f;

    /// <summary>Outward scatter speed given to each grenade dropped on death.</summary>
    public float ScatterSpeed { get; set; } = 150f;
}

public class ReviveConfig
{
    /// <summary>
    /// Minimum/maximum starting percent chance to revive instead of dying. A single value is rolled
    /// from this range once per activation (not per revive, not per life) and used as the reset
    /// point every spawn. Deliberately high by design - see the multiplicative decay below for why
    /// this doesn't make Revive overpowered.
    /// </summary>
    public float MinBasePercent { get; set; } = 70f;
    public float MaxBasePercent { get; set; } = 90f;

    /// <summary>Health the player is set to immediately after a successful revive.</summary>
    public int HealthAfterRevive { get; set; } = 50;
}

public class SaintConfig
{
    /// <summary>
    /// Minimum/maximum percent chance that killing an enemy revives one random dead teammate. A
    /// single value is rolled from this range once per activation (each time the modifier is
    /// applied to a player), not per kill - so different activations get different odds within this
    /// range, but the odds stay fixed for the duration of one activation.
    /// </summary>
    public float MinRevivePercent { get; set; } = 10f;
    public float MaxRevivePercent { get; set; } = 50f;
}

public class MasterZeusConfig
{
    /// <summary>Flat damage dealt by a successful extended-range zap. The cooldown between zaps is NOT here - it reads the real mp_taser_recharge_time server cvar live instead (deliberately global, see the class-level remarks), so there's one single source of truth shared with the native close-range zeus recharge.</summary>
    public float ZapDamage { get; set; } = 200f;

    /// <summary>Max distance (units) the extended-range zap can reach - the single most balance-defining number for this modifier, previously a hardcoded constant.</summary>
    public float RangeDistance { get; set; } = 4000f;



    /// <summary>Distance the LOS trace's start point is nudged forward along the aim direction before tracing, clearing the shooter's own head hitbox so the trace doesn't immediately self-block.</summary>
    public float MuzzleOffsetDistance { get; set; } = 24f;

    /// <summary>Cooldown used only if mp_taser_recharge_time somehow can't be read live.</summary>
    public float FallbackCooldownSeconds { get; set; } = 2f;

    /// <summary>
    /// Particle dispatched at each point of the muzzle-to-target lightning chain on every extended-
    /// range zap. Composite/wrapper particles (e.g. particles/unified_weapon_fx/weapon_tracers_taser.vpcf,
    /// the real native zap asset) were confirmed live to never render through any dispatch mechanism
    /// tried, so this must be one of that wrapper's standalone children instead - wire1a is the actual
    /// wire/bolt visual (as opposed to weapon_taser_glow.vpcf, a plain glow dot with no wire shape),
    /// referenced as a child in the composite's decompiled .vpcf_c alongside wire1b and
    /// weapon_taser_glow_impact.vpcf. Swap to another standalone particle
    /// (e.g. one of the particles/generic_fx/fx_electricspark_*.vpcf assets) if this doesn't look
    /// right once tested.
    /// </summary>
    public string LightningParticlePath { get; set; } = "particles/weapons/cs_weapon_fx/weapon_tracers_taser_wire1a.vpcf";

    /// <summary>Second particle spawned alongside LightningParticlePath on every strand (same control points) - wire1a and wire1b are siblings in the real taser tracer's m_Children list, so layering both gives a denser combined visual than either alone. Empty string disables it.</summary>
    public string LightningSecondaryParticlePath { get; set; } = "particles/weapons/cs_weapon_fx/weapon_tracers_taser_wire1b.vpcf";

    /// <summary>Seconds the spawned lightning strand entities are kept alive before being despawned - long enough for each strand's particle playback to finish without leaking entities at repeated-zap frequency. Bumped from the particle's own ~1.4-1.8s natural fade so a slow client/late join still gets the full effect, though the particle's own baked-in fade timing (not this value) is what actually governs how long it stays visible.</summary>
    public float LightningLifetimeSeconds { get; set; } = 3f;

    /// <summary>
    /// Number of straight segments the muzzle-to-target line is split into, each spawned as its own
    /// particle with control point 0 = segment start and control point 1 = segment end. This is the
    /// lever that controls how straight the bolt looks: wire1a's own path params bulge the path by
    /// m_flBulge = 1.0, which behaves as a fraction of path length, so one segment spanning the whole
    /// extended range produces an enormous sideways arc (barely noticeable on the native short-range
    /// zap, very obvious across thousands of units). More segments means each bulge is proportionally
    /// smaller, so the line reads as slightly jagged lightning rather than one giant swerve.
    /// Total entities per zap is roughly segments x strands x 2 (wire1a + wire1b), so raising this a
    /// lot is not free.
    /// </summary>
    public int LightningChainSegments { get; set; } = 8;

    /// <summary>Number of parallel bolts spawned per zap, each independently jittered off the centre line - higher values look denser. Kept at 1 by default because segmentation already provides visual richness and total entity count multiplies by this.</summary>
    public int LightningStrandCount { get; set; } = 1;

    /// <summary>Max perpendicular offset (units) randomly applied to each strand beyond the first, so multiple strands read as a bundle of distinct bolts instead of perfectly overlapping copies that just look like one weak strand.</summary>
    public float LightningStrandJitterDistance { get; set; } = 5f;

    /// <summary>
    /// Where the bolt visually starts, as an offset from the shooter's eye position along their own
    /// forward/right/up axes. This is purely cosmetic and deliberately separate from
    /// MuzzleOffsetDistance, which positions the gameplay trace and must not be retuned for looks.
    /// An approximation is unavoidable: the SDK exposes no way to read a model's real muzzle_flash
    /// attachment position, so the weapon's actual muzzle can't be queried. Defaults put the bolt
    /// forward of, right of, and below the eye, roughly where the world model's barrel sits.
    /// Note the bolt is a world-space effect, so it lines up with the world model other players see;
    /// it can't also line up with the shooter's own first-person viewmodel, which is rendered
    /// separately and positioned differently.
    /// </summary>
    public float LightningMuzzleForwardOffset { get; set; } = 24f;

    /// <summary>Sideways offset of the bolt's visual start point from the shooter's eye - positive is to their right, matching a right-handed weapon hold. See LightningMuzzleForwardOffset.</summary>
    public float LightningMuzzleRightOffset { get; set; } = 8f;

    /// <summary>Vertical offset of the bolt's visual start point from the shooter's eye - negative drops it below eye level, where the weapon actually sits. See LightningMuzzleForwardOffset.</summary>
    public float LightningMuzzleUpOffset { get; set; } = -6f;

    /// <summary>
    /// Minimum seconds between lightning bolts from the same player, enforced inside the render call
    /// itself rather than at any one call site, so every trigger path is covered. The real zap is
    /// already gated by mp_taser_recharge_time well above this, so at the default value this never
    /// interferes with normal play - it exists purely as a spam backstop, since each bolt spawns
    /// roughly segments x strands x 2 entities and an ungated trigger firing at weapon rate would
    /// flood the entity list. Set to 0 to disable the guard entirely.
    /// </summary>
    public float LightningCooldownSeconds { get; set; } = 0.5f;
}

public class PlantAnywhereConfig
{
    /// <summary>Seconds into the round before mp_plant_c4_anywhere is turned on - before this, planting still requires a normal bombsite.</summary>
    public float DelaySeconds { get; set; } = 10f;

    /// <summary>Bomb fuse duration (mp_c4_timer) in seconds while this modifier is active.</summary>
    public float BombTimerSeconds { get; set; } = 75f;
}

public class FlankerConfig
{
    /// <summary>Seconds before the teleport becomes usable at the start of each round/life - deliberately separate from (and longer than) CooldownSeconds, so it can't be used the instant a round begins.</summary>
    public float RoundStartCooldownSeconds { get; set; } = 20f;

    /// <summary>Seconds before the teleport becomes usable again after a successful use.</summary>
    public float CooldownSeconds { get; set; } = 15f;

    /// <summary>Distance behind the target enemy the assigned player is teleported to.</summary>
    public float TeleportDistance { get; set; } = 100f;

    /// <summary>Height above the landing spot the player is dropped from - a short, harmless fall so landing makes an audible thud, rather than a completely silent zero-warning appearance right behind the target.</summary>
    public float DropHeight { get; set; } = 48f;
}

public class SpinRevealConfig
{
    /// <summary>If false, the real modifier assignment is shown immediately with no spin-up animation.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between name changes at the very start of the spin - fast enough to actually blur rather than be readable.</summary>
    public float StartIntervalSeconds { get; set; } = 0.025f;

    /// <summary>
    /// Seconds between name changes right before landing on the real result - slow (the "ease out").
    ///
    /// Kept above the ~150ms center-HTML render floor (see DescriptionScrambleFrames for the full
    /// explanation) so the last few frames, and the landing itself, always render.
    /// </summary>
    public float EndIntervalSeconds { get; set; } = 0.18f;

    /// <summary>
    /// How many random names to cycle through before landing on the real result.
    ///
    /// This and EndIntervalSeconds together set the spin's length: the interval eases quadratically
    /// from StartIntervalSeconds to EndIntervalSeconds across this many frames, so the total is the
    /// sum of that curve, not count x average. 52 frames ending at 0.18s runs ~4.0s at roughly 77ms
    /// per frame - deliberately a lot of fast frames rather than fewer slow ones, so the names blur
    /// past instead of ticking over one at a time.
    ///
    /// Budgeted against a standard 15s freeze time: 1s initial delay (ScheduleFreezeTimeBanner) + 4s
    /// spin + 10s reveal hold = 15s, so the popup clears exactly as the round goes live.
    ///
    /// Note this is per-player when RandomizePlayers is on - each assigned player runs their own
    /// spin, so the message count scales with player count.
    /// </summary>
    public int SpinCount { get; set; } = 52;

    /// <summary>How long the real result (name + description) stays on screen once the spin lands on it. Sized so 1s delay + 4s spin + this = a standard 15s freeze time, clearing right as the round starts.</summary>
    public float RevealDurationSeconds { get; set; } = 10f;

    /// <summary>Whether each revealed modifier's description is shown on its own line under the name.</summary>
    public bool ShowDescription { get; set; } = true;

    /// <summary>
    /// Whether the description wipes in with a left-to-right scramble instead of appearing at once.
    /// Only affects the description - the modifier-name spin above it is unchanged either way.
    /// </summary>
    public bool DescriptionScrambleEnabled { get; set; } = true;

    /// <summary>Total time the description scramble takes to resolve, from blank to fully readable.</summary>
    public float DescriptionScrambleDurationSeconds { get; set; } = 0.8f;

    /// <summary>
    /// How many frames the scramble is drawn in - the one number here worth tuning by eye.
    ///
    /// Each frame is a separate center-HTML message, and the client rebuilds the whole panel per
    /// message rather than drawing it like a normal render frame, so this is bounded by panel
    /// replacement rate, not by client FPS. The evidence in this codebase is genuinely mixed:
    /// GameModifierWeaponRoulette measured frames being silently swallowed at ~67ms and settled on
    /// 150ms, yet StartIntervalSeconds above runs the name spin at 25ms and visibly works. The likely
    /// reconciliation is that a dropped frame mid-churn is invisible while a dropped FINAL frame is
    /// obvious - which is exactly what that modifier reported.
    ///
    /// So this starts ambitious (20 frames over 0.8s = 40ms each) and the landing frame is held
    /// separately via DescriptionHoldMs. If the wipe looks stuttery live, step down to 10 or 5.
    /// </summary>
    public int DescriptionScrambleFrames { get; set; } = 20;

    /// <summary>
    /// How long the fully-resolved description is held immediately after the scramble finishes,
    /// deliberately well clear of the frame interval above so the readable text can never be the
    /// message that gets swallowed. Raise this first if the final line ever fails to appear.
    /// </summary>
    public int DescriptionHoldMs { get; set; } = 250;

    /// <summary>
    /// CS2 soundevent name played on every spin-frame tick (each name flip during the animation) -
    /// already built into the game, no custom sound asset required. "UI.ContractType" is confirmed
    /// (via swiftlys2's own source, src/server/configuration/configuration.cpp) to be the exact
    /// soundevent SwiftlyS2's native menu system itself plays for scroll/navigate between menu items
    /// - the earlier default, "Player.WeaponSelectionMoveSlot", was an unverified guess that turned
    /// out not to produce any audible sound. Empty string disables the sound entirely.
    /// </summary>
    public string TickSoundEventName { get; set; } = "UI.ContractType";

    /// <summary>Volume (0-1) for the per-frame spin tick sound - matches SwiftlyS2's own menu-scroll default.</summary>
    public float TickSoundVolume { get; set; } = 0.75f;

    /// <summary>
    /// Optional image shown above the modifier name on the reveal banner (e.g. a server/CSRoll logo).
    /// Empty (the default) disables it entirely and emits no &lt;img&gt; tag at all.
    ///
    /// OFF BY DEFAULT ON PURPOSE - this is a remote fetch performed by every client that sees the
    /// banner, which (a) exposes each player's IP address to whatever host serves the image and
    /// (b) puts a network round-trip in the reveal path. Only set this if you're comfortable with
    /// both, and prefer a host you control over a random image host.
    ///
    /// Must be a direct image URL (https://.../logo.png). Panorama also accepts
    /// "file://{images}/..." paths, but those resolve against the CLIENT's own installed CS2 image
    /// tree, so they can only reference art the game already ships - not anything you supply.
    ///
    /// NOTE: image rendering in this panel is evidenced by other live plugins doing the same thing,
    /// but has not been verified first-hand for this plugin - test it in-game before relying on it.
    /// If it doesn't render, leaving this empty restores exactly the previous behaviour.
    /// </summary>
    public string RevealImageUrl { get; set; } = "";

    /// <summary>
    /// Pixel width/height for RevealImageUrl. Both are always emitted when an image is configured:
    /// Panorama has a long-standing quirk where an &lt;img&gt; with no explicit dimensions renders
    /// tiny on first display and only corrects itself on a later show, so these are not optional.
    /// </summary>
    public int RevealImageWidth { get; set; } = 256;

    /// <summary>See RevealImageWidth.</summary>
    public int RevealImageHeight { get; set; } = 64;

    /// <summary>
    /// Brief screen flash when a modifier reveal lands, via CUserMessageFade - a cheap way to give
    /// the reveal some physical punch that markup alone can't. Disabled by default since it's an
    /// intrusive full-screen effect; FadeDurationMs/FadeHoldMs control how brief it is.
    /// </summary>
    public bool FadeOnReveal { get; set; } = false;

    /// <summary>Fade-in/out duration in milliseconds for FadeOnReveal.</summary>
    public int FadeDurationMs { get; set; } = 400;

    /// <summary>How long the fade colour holds at full strength, in milliseconds, before fading back out.</summary>
    public int FadeHoldMs { get; set; } = 100;

    /// <summary>Fade colour as "R,G,B,A" (0-255 each). The default is a low-alpha white flash rather than a heavy blackout.</summary>
    public string FadeColor { get; set; } = "255,255,255,64";
}

public class SpectatorHudConfig
{
    /// <summary>Whether spectators watching a player with active modifiers see a persistent HUD listing what's rolled on them.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often, in seconds, the popup refreshes - catches the spectator switching targets or the target's modifiers changing.</summary>
    public float RefreshIntervalSeconds { get; set; } = 0.2f;
}

/// <summary>
/// The CS2 Custom HUD (Panorama) surface - a real styled HUD with CSS animation, icons and progress
/// bars, as opposed to the center-HTML text popup everything else in this plugin draws into.
///
/// This needs a piece of infrastructure nothing else here does: the Panorama layout and stylesheet are
/// CLIENT resources. They have to be compiled with CS2's Workshop Tools, published as a Steam Workshop
/// addon, mounted on the server, and downloaded by every player. None of that ships in the plugin zip -
/// see tools/HUD_SETUP.md. Which is why <see cref="Enabled"/> defaults to false: with no addon
/// installed, an enabled HUD renders literally nothing for players.
/// </summary>
public class CustomHudConfig
{
    /// <summary>
    /// Master switch. While false (the default) the layout entity is never created, no events are
    /// subscribed, and every center-HTML path behaves exactly as it did before this feature existed.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Panorama layout resource path, as it exists inside the published Workshop addon.</summary>
    public string LayoutPath { get; set; } = "panorama/layout/custom_game/csroll_hud.xml";

    /// <summary>
    /// When true, the roll's spin/reveal is drawn ONLY on the custom HUD and the center-HTML reveal is
    /// skipped.
    ///
    /// Defaults to false on purpose, and it is worth understanding why rather than flipping it blind:
    /// there is no way for the server to detect whether an individual client actually downloaded the
    /// addon, so this is all-or-nothing for everyone. Turn the HUD on, confirm with !hudstatus and your
    /// own eyes in-game that it renders, and only then set this. If the addon is missing or broken while
    /// this is true, players get no reveal at all.
    /// </summary>
    public bool ReplaceCenterHtml { get; set; } = false;

    /// <summary>Whether a roll draws the animated spin reel and reveal card.</summary>
    public bool ShowRevealCard { get; set; } = true;

    /// <summary>Whether the persistent active-modifier tracker panel is drawn.</summary>
    public bool ShowTracker { get; set; } = true;

    /// <summary>
    /// How many tracker rows to use. Clamped to the row count compiled into the layout - raising it past
    /// that does nothing without republishing the Workshop addon.
    /// </summary>
    public int TrackerRowCount { get; set; } = 6;

    /// <summary>How often the tracker re-reads each player's modifiers. Dirty tracking means an idle
    /// tracker costs nothing regardless of this value, so it only bounds how fast a change appears.</summary>
    public float TrackerRefreshIntervalSeconds { get; set; } = 0.25f;

    /// <summary>How often numeric countdown text is recomputed. Same dirty-tracking caveat.</summary>
    public float CountdownRefreshIntervalSeconds { get; set; } = 0.25f;

    /// <summary>
    /// Show the old center-HTML spectator readout to players on the spectator team, instead of the
    /// custom HUD's tracker.
    ///
    /// CS2 does not deliver custom HUD state to a client that is not on a playing team. Their panels
    /// keep whatever they last received and every subsequent write - per-player or global, forced or
    /// not - is discarded, which is why a spectator's tracker froze on one modifier and no amount of
    /// re-sending moved it. That is a property of the game, not something this plugin can write its
    /// way out of.
    ///
    /// Center-HTML has no such restriction, so spectators go back to the surface that reaches them. Set
    /// false only to confirm the behaviour yourself, or if a future CS2 build fixes it.
    /// </summary>
    public bool SpectatorFallbackCenterHtml { get; set; } = true;

    /// <summary>
    /// Seconds between attempts to (re)create the layout entity when it is missing. After several
    /// consecutive failures the service gives up for the rest of the map rather than retrying forever.
    /// </summary>
    public float EntityRetryIntervalSeconds { get; set; } = 2f;

    /// <summary>Build stamp written into the HUD's version label, so a client running a stale addon can
    /// be told apart from one where the HUD is simply broken.</summary>
    public string VersionStamp { get; set; } = "";
}

public class ChineseGrenadesConfig
{
    /// <summary>Minimum possible fuse length, in seconds, rolled fresh for each HE/flashbang/smoke thrown.</summary>
    public float MinFuseSeconds { get; set; } = 0.1f;

    /// <summary>Maximum possible fuse length, in seconds.</summary>
    public float MaxFuseSeconds { get; set; } = 10f;
}
