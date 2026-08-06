namespace CSRoll.Config;

public class CSRollConfig
{
    public bool RandomRoundsEnabledByDefault { get; set; } = false;
    public bool DisableRandomRoundsInWarmup { get; set; } = false;
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
    /// Prefix shown before every chat message this plugin sends (e.g. "Added Bhop modifier.").
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

    /// <summary>
    /// TeleportOnReload/TeleportOnHit depend on raw memory signature scanning to
    /// locate CS2's internal nav-mesh data - inherently fragile and tied to the exact game
    /// binary. Set to false to skip that scan entirely (e.g. if it crashes the plugin or the
    /// server on a given CS2 build) without affecting any other modifier.
    /// </summary>
    public bool EnableNavMeshTeleports { get; set; } = true;

    /// <summary>Tunables for the RandomHealth modifier (health set to a random number in this range each activation).</summary>
    public RandomHealthConfig RandomHealth { get; set; } = new();

    /// <summary>Tunables for the ConditionalInvisibility modifier (invisible while silent, briefly visible after any sound).</summary>
    public ConditionalInvisibilityConfig ConditionalInvisibility { get; set; } = new();

    /// <summary>Tunables for the Speedhack modifier (faster movement).</summary>
    public SpeedhackConfig Speedhack { get; set; } = new();

    /// <summary>Tunables for the LeadBoots modifier (slower movement - now per-player, previously a server-wide sv_maxspeed cvar).</summary>
    public LeadBootsConfig LeadBoots { get; set; } = new();

    /// <summary>Tunables for the Jetpack modifier (per-player jump velocity boost plus jetpack thrust/fuel/air-strafe, previously a server-wide sv_jump_impulse cvar).</summary>
    public JetpackConfig Jetpack { get; set; } = new();

    /// <summary>Tunables for the BiggerExplosions modifier (per-player HE damage multiplier, previously a server-wide sv_hegrenade_damage_multiplier cvar).</summary>
    public BiggerExplosionsConfig BiggerExplosions { get; set; } = new();

    /// <summary>Tunables for the IncreasedSpread modifier (per-player weapon accuracy penalty, previously a server-wide weapon_accuracy_forcespread cvar).</summary>
    public IncreasedSpreadConfig IncreasedSpread { get; set; } = new();

    /// <summary>Tunables for the PoisonSmoke modifier (damage dealt per tick to enemies standing in the assigned player's smoke).</summary>
    public PoisonSmokeConfig PoisonSmoke { get; set; } = new();

    /// <summary>Tunables for the FlashingBullets modifier (per-bullet-hit blind chance).</summary>
    public FlashingBulletsConfig FlashingBullets { get; set; } = new();

    /// <summary>Tunables for the DisarmingBullets modifier (per-bullet-hit disarm chance).</summary>
    public DisarmingBulletsConfig DisarmingBullets { get; set; } = new();

    /// <summary>Tunables for the Kamikaze modifier (grenades dropped on death, and their damage multiplier).</summary>
    public KamikazeConfig Kamikaze { get; set; } = new();

    /// <summary>Tunables for the Revive modifier (escalating chance to survive lethal damage).</summary>
    public ReviveConfig Revive { get; set; } = new();

    /// <summary>Tunables for the Saint modifier (chance to revive a dead teammate on a kill).</summary>
    public SaintConfig Saint { get; set; } = new();

    /// <summary>Tunables for the MasterZeus modifier (extended-range zap damage - its cooldown tracks the mp_taser_recharge_time cvar directly instead of a separate config value).</summary>
    public MasterZeusConfig MasterZeus { get; set; } = new();

    /// <summary>Tunables for the PlantAnywhere modifier (delayed anywhere-plant + extended bomb timer, previously a static ConVarModifiers/*.cfg entry with neither).</summary>
    public PlantAnywhereConfig PlantAnywhere { get; set; } = new();

    /// <summary>Tunables for the FlankTeleport modifier (Inspect-Weapon-triggered teleport behind a random enemy, on a cooldown).</summary>
    public FlankTeleportConfig FlankTeleport { get; set; } = new();

    /// <summary>Modifier names excluded from random rolls unless the relevant team has at least this many players - e.g. Saint is pointless in a 1v1 (no teammate to ever revive).</summary>
    public string[] RequiresMultiplePlayersPerTeam { get; set; } = ["Saint"];

    /// <summary>Tunables for the "slot machine" style spin animation shown in the center-HTML popup before the real assigned modifier(s) are revealed.</summary>
    public SpinRevealConfig SpinReveal { get; set; } = new();
}

public class ConditionalInvisibilityConfig
{
    /// <summary>Seconds of complete silence required after last making a sound before the player fades invisible again.</summary>
    public float SoundCooldownSeconds { get; set; } = 2.0f;

    /// <summary>Seconds the visual fade in/out takes (real alpha blend via RenderMode.kRenderTransAlpha, not an instant transmit-block toggle).</summary>
    public float FadeDurationSeconds { get; set; } = 0.5f;
}

public class SpeedhackConfig
{
    /// <summary>Movement speed multiplier (VelocityModifier mechanism).</summary>
    public float SpeedMultiplier { get; set; } = 2.0f;
}

public class RandomHealthConfig
{
    /// <summary>Lowest possible health value that can be rolled (inclusive).</summary>
    public int MinHealth { get; set; } = 1;

    /// <summary>Highest possible health value that can be rolled (inclusive).</summary>
    public int MaxHealth { get; set; } = 250;
}

public class LeadBootsConfig
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
    /// <summary>Upward velocity (units/sec) applied on the initial jump off the ground - CS2's own default is ~301, so this is roughly 2.5x that.</summary>
    public float JumpVelocityZ { get; set; } = 750f;

    /// <summary>Minimum seconds between initial-jump boosts, per player - stops spamming jump instead of holding it from re-triggering the big boost repeatedly and bypassing fuel entirely. Sustained lift while holding jump is unaffected; that's the separate, uncapped-duration thrust mechanic (ThrustSpeed).</summary>
    public float BigBoostCooldownSeconds { get; set; } = 0.75f;

    /// <summary>
    /// Vertical speed (units/sec) floored while holding jump in the air with fuel remaining. Bug fix:
    /// this defaulted to 280 - close to CS2's own ~301 jump impulse - which felt like a sustained full
    /// jump rather than a light thrust once the gauge/fuel bugs above were fixed and thrust actually
    /// started engaging reliably. Lowered to read as a gentle lift instead.
    /// </summary>
    public float ThrustSpeed { get; set; } = 140f;

    /// <summary>Multiplier applied to CS2's normal air-accelerate value while airborne, for stronger in-flight steering during thrust.</summary>
    public float AirStrafeMultiplier { get; set; } = 2.5f;

    /// <summary>Fuel capacity, in percentage points (0-100).</summary>
    public float MaxFuel { get; set; } = 100f;

    /// <summary>Fuel drained per second while actively thrusting (holding jump, airborne, fuel remaining).</summary>
    public float FuelDrainPerSecond { get; set; } = 25f;

    /// <summary>Fuel regenerated per second whenever not thrusting - grounded or just coasting/falling.</summary>
    public float FuelRegenPerSecond { get; set; } = 15f;

    /// <summary>How often, in seconds, the center-HTML fuel gauge popup refreshes.</summary>
    public float GaugeUpdateIntervalSeconds { get; set; } = 0.2f;
}

public class BiggerExplosionsConfig
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
    public float AccuracyPenalty { get; set; } = 2f;
}

public class PoisonSmokeConfig
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

public class KamikazeConfig
{
    /// <summary>How many live HE grenades are dropped near the assigned player's body on death.</summary>
    public int GrenadeCount { get; set; } = 3;

    /// <summary>Damage multiplier applied to the blast damage these specific grenades deal.</summary>
    public float DamageMultiplier { get; set; } = 1.25f;
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
}

public class PlantAnywhereConfig
{
    /// <summary>Seconds into the round before mp_plant_c4_anywhere is turned on - before this, planting still requires a normal bombsite.</summary>
    public float DelaySeconds { get; set; } = 10f;

    /// <summary>Bomb fuse duration (mp_c4_timer) in seconds while this modifier is active.</summary>
    public float BombTimerSeconds { get; set; } = 75f;
}

public class FlankTeleportConfig
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

    /// <summary>Seconds between name changes right before landing on the real result - slow (the "ease out").</summary>
    public float EndIntervalSeconds { get; set; } = 0.45f;

    /// <summary>How many random names to cycle through before landing on the real result.</summary>
    public int SpinCount { get; set; } = 30;

    /// <summary>How long the real result stays on screen once the spin lands on it.</summary>
    public float RevealDurationSeconds { get; set; } = 15f;
}
