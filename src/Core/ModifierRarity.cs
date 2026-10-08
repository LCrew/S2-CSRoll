using Microsoft.Extensions.Logging;

using CSRoll.Config;
using CSRoll.Modifiers;

namespace CSRoll.Core;

/// <summary>CS case rarity tiers, lowest to highest.</summary>
public enum ModifierTier
{
    MilSpec,
    Restricted,
    Classified,
    Covert,
    Gold,
}

/// <summary>
/// Rarity tiers, CS case style. Every modifier has a built-in tier, which Rarity.Overrides in config
/// can move. A pick chooses a tier by its weight among the tiers that have something to offer, then a
/// modifier inside that tier uniformly - so a tier's weight is its share of every pick no matter how
/// many modifiers it holds, which is what makes "Gold about once a map" hold as modifiers are added.
/// </summary>
public static class ModifierRarity
{
    public static readonly ModifierTier[] Tiers = Enum.GetValues<ModifierTier>();

    private static readonly Dictionary<string, ModifierTier> DefaultTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Wallhack"] = ModifierTier.Gold, ["ButterflyEffect"] = ModifierTier.Gold, ["Mimic"] = ModifierTier.Gold,
        ["ConditionalInvisibility"] = ModifierTier.Gold,

        ["Juggernaut"] = ModifierTier.Covert, ["Revive"] = ModifierTier.Covert, ["AtomicExplosions"] = ModifierTier.Covert,
        ["Jetpack"] = ModifierTier.Covert, ["KamikazeChickens"] = ModifierTier.Covert, ["SmokeImmunity"] = ModifierTier.Covert,
        ["Vanish"] = ModifierTier.Covert,

        ["Speedhack"] = ModifierTier.Classified, ["Flanker"] = ModifierTier.Classified, ["Recall"] = ModifierTier.Classified,
        ["NoRecoil"] = ModifierTier.Classified, ["InfiniteAmmo"] = ModifierTier.Classified, ["Vampire"] = ModifierTier.Classified,
        ["Saint"] = ModifierTier.Classified, ["MasterZeus"] = ModifierTier.Classified, ["ClusterGrenades"] = ModifierTier.Classified,
        ["SuicideBomber"] = ModifierTier.Classified, ["BunnyHop"] = ModifierTier.Classified, ["SmallPlayers"] = ModifierTier.Classified,

        ["MoreDamage"] = ModifierTier.Restricted, ["Regeneration"] = ModifierTier.Restricted, ["HardHead"] = ModifierTier.Restricted,
        ["SteelBody"] = ModifierTier.Restricted, ["PoisonousSmoke"] = ModifierTier.Restricted, ["WalkingGrenadier"] = ModifierTier.Restricted,
        ["DisarmingBullets"] = ModifierTier.Restricted, ["FlashingBullets"] = ModifierTier.Restricted, ["Bounty"] = ModifierTier.Restricted,
        ["HeavyBoots"] = ModifierTier.Restricted, ["SwapOnDeath"] = ModifierTier.Restricted, ["LongerFlashes"] = ModifierTier.Restricted,

        // Everything else - Drunk, SwapOnHit, TeleportOnReload, TeleportOnHit, OnePerReload, Butterfingers,
        // BoomerangBullets, IncreasedSpread, ChineseGrenades, PlantAnywhere, RandomHealth, RandomLoadout,
        // WeaponRoulette, HumanShield, and any ConVar .cfg modifier - is Mil-Spec.
    };

    /// <summary>The tier map and the config it was built from, swapped as one reference so a reader never pairs one config with another's map.</summary>
    private sealed record TierCache(RarityConfig Config, Dictionary<string, ModifierTier> Tiers);

    // Rebuilt whenever a config reload hands over a new RarityConfig object.
    private static TierCache? _cache;

    /// <summary>Set by the runtime, so a bad Overrides entry is reported rather than silently ignored.</summary>
    public static ILogger? Logger { get; set; }

    /// <summary>A modifier's tier before any override - what the admin panel's tier cycling returns to.</summary>
    public static ModifierTier BuiltInTier(string modifierName) =>
        DefaultTiers.TryGetValue(modifierName, out var tier) ? tier : ModifierTier.MilSpec;

    /// <summary>
    /// Drops the cached tier map. It's keyed on the RarityConfig object, which a config reload replaces -
    /// but the admin panel edits Overrides in place, which the cache can't see on its own.
    /// </summary>
    public static void Invalidate() => _cache = null;

    public static ModifierTier Resolve(GameModifierBase modifier, RarityConfig config) => Resolve(modifier.Name, config);

    public static ModifierTier Resolve(string modifierName, RarityConfig config) =>
        TiersFor(config).TryGetValue(modifierName, out var tier) ? tier : ModifierTier.MilSpec;

    /// <summary>A tier's configured weight - anything that isn't a usable positive number counts as 0, so it never rolls.</summary>
    public static float Weight(ModifierTier tier, RarityConfig config)
    {
        var weight = tier switch
        {
            ModifierTier.Restricted => config.Restricted,
            ModifierTier.Classified => config.Classified,
            ModifierTier.Covert => config.Covert,
            ModifierTier.Gold => config.Gold,
            _ => config.MilSpec,
        };

        return float.IsFinite(weight) && weight > 0f ? weight : 0f;
    }

    /// <summary>
    /// One weighted pick from pool: a tier by weight among the tiers present in it, then uniformly
    /// within that tier. Uniform over the whole pool when rarity is off or every present tier weighs 0.
    /// </summary>
    public static GameModifierBase? PickWeighted(IReadOnlyList<GameModifierBase> pool, RarityConfig config, Random random)
    {
        if (pool.Count == 0)
        {
            return null;
        }

        if (!config.Enabled)
        {
            return pool[random.Next(pool.Count)];
        }

        var byTier = new Dictionary<ModifierTier, List<GameModifierBase>>();
        foreach (var modifier in pool)
        {
            var tier = Resolve(modifier, config);
            if (!byTier.TryGetValue(tier, out var members))
            {
                members = [];
                byTier[tier] = members;
            }

            members.Add(modifier);
        }

        var total = byTier.Keys.Sum(tier => Weight(tier, config));
        if (total <= 0f)
        {
            return pool[random.Next(pool.Count)];
        }

        var roll = random.NextDouble() * total;
        List<GameModifierBase>? chosen = null;
        foreach (var tier in Tiers)
        {
            var weight = Weight(tier, config);
            if (weight <= 0f || !byTier.TryGetValue(tier, out var members))
            {
                continue;
            }

            // Assigned before the check, so float rounding at the very top of the range still lands
            // on the last weighted tier instead of nothing.
            chosen = members;
            if (roll < weight)
            {
                break;
            }

            roll -= weight;
        }

        return chosen![random.Next(chosen.Count)];
    }

    public static string Label(ModifierTier tier) => tier switch
    {
        ModifierTier.MilSpec => "Mil-Spec",
        ModifierTier.Gold => "★ Gold",
        _ => tier.ToString(),
    };

    /// <summary>The tier's colour, as CS shows it on case items.</summary>
    public static string Hex(ModifierTier tier) => tier switch
    {
        ModifierTier.Restricted => "#8847ff",
        ModifierTier.Classified => "#d32ce6",
        ModifierTier.Covert => "#eb4b4b",
        ModifierTier.Gold => "#e4ae39",
        _ => "#4b69ff",
    };

    /// <summary>The closest of SwiftlyS2's fixed chat colours.</summary>
    public static string ChatToken(ModifierTier tier) => tier switch
    {
        ModifierTier.Restricted => "[purple]",
        ModifierTier.Classified => "[magenta]",
        ModifierTier.Covert => "[red]",
        ModifierTier.Gold => "[gold]",
        _ => "[blue]",
    };

    /// <summary>The tier's key in the HUD stylesheet's rar-* classes.</summary>
    public static string CssKey(ModifierTier tier) => tier.ToString().ToLowerInvariant();

    public static bool TryParse(string? text, out ModifierTier tier)
    {
        var key = text?.Replace("-", "").Replace(" ", "").Trim();
        return Enum.TryParse(key, ignoreCase: true, out tier) && Enum.IsDefined(tier);
    }

    private static Dictionary<string, ModifierTier> TiersFor(RarityConfig config)
    {
        if (_cache is { } cache && ReferenceEquals(config, cache.Config))
        {
            return cache.Tiers;
        }

        var tiers = new Dictionary<string, ModifierTier>(DefaultTiers, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in config.Overrides ?? [])
        {
            if (TryParse(value, out var tier))
            {
                tiers[name] = tier;
            }
            else
            {
                Logger?.LogWarning("[CSRoll] Rarity.Overrides: \"{Tier}\" for {Modifier} isn't a tier - use MilSpec, Restricted, Classified, Covert or Gold.", value, name);
            }
        }

        _cache = new TierCache(config, tiers);
        return tiers;
    }
}
