using CSRoll.Modifiers;

namespace CSRoll.Hud;

/// <summary>
/// Which icon and colour category each modifier draws with on the custom HUD.
///
/// The icon is the modifier's internal Name - the SVGs in hud/panorama/images/custom_game/csroll are
/// named after it - so a new modifier only needs its SVG dropped in (and the generator re-run) to get
/// a picture. Anything without one, including ConVarModifiers/*.cfg modifiers, falls back to the
/// generic InfiniteRoll icon.
///
/// The category is the colour family: six accent classes in the stylesheet tint every icon, row edge,
/// tile edge and bar, so the palette stays at six however many modifiers there are.
/// </summary>
public static class HudCatalog
{
    private static readonly HashSet<string> KnownIcons = new(HudLayout.Icons, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> CategoryByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Speedhack"] = "move", ["Drunk"] = "move", ["SwapOnDeath"] = "move", ["SwapOnHit"] = "move",
        ["TeleportOnReload"] = "move", ["TeleportOnHit"] = "move", ["Jetpack"] = "move", ["BunnyHop"] = "move",
        ["Flanker"] = "move", ["Recall"] = "move",

        ["MoreDamage"] = "weap", ["OnePerReload"] = "weap", ["NoRecoil"] = "weap", ["Butterfingers"] = "weap",
        ["BoomerangBullets"] = "weap", ["RandomLoadout"] = "weap", ["MasterZeus"] = "weap", ["FlashingBullets"] = "weap",
        ["InfiniteAmmo"] = "weap", ["IncreasedSpread"] = "weap", ["DisarmingBullets"] = "weap", ["Bounty"] = "weap",
        ["WeaponRoulette"] = "weap",

        ["WalkingGrenadier"] = "util", ["LongerFlashes"] = "util", ["ChineseGrenades"] = "util", ["ClusterGrenades"] = "util",
        ["PoisonousSmoke"] = "util", ["AtomicExplosions"] = "util", ["PlantAnywhere"] = "util", ["SuicideBomber"] = "util",
        ["KamikazeChickens"] = "util",

        ["Juggernaut"] = "surv", ["RandomHealth"] = "surv", ["Vampire"] = "surv", ["HardHead"] = "surv",
        ["SteelBody"] = "surv", ["Revive"] = "surv", ["Saint"] = "surv", ["HeavyBoots"] = "surv",
        ["Regeneration"] = "surv",

        ["SmallPlayers"] = "stealth", ["Wallhack"] = "stealth", ["SmokeImmunity"] = "stealth",
        ["ConditionalInvisibility"] = "stealth", ["Vanish"] = "stealth",

        ["ButterflyEffect"] = "chaos", ["Mimic"] = "chaos",
    };

    private static readonly Dictionary<string, string> CategoryLabels = new(StringComparer.Ordinal)
    {
        ["move"] = "Movement",
        ["weap"] = "Weapons",
        ["util"] = "Grenades & bomb",
        ["surv"] = "Survival",
        ["stealth"] = "Stealth & vision",
        ["chaos"] = "Chaos",
    };

    public static string Icon(GameModifierBase modifier) =>
        KnownIcons.Contains(modifier.Name) ? modifier.Name : HudLayout.FallbackIcon;

    public static string Category(GameModifierBase modifier) =>
        CategoryByName.TryGetValue(modifier.Name, out var category) ? category : "chaos";

    public static string CategoryLabel(string category) =>
        CategoryLabels.TryGetValue(category, out var label) ? label : category;
}
