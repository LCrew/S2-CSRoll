using System.Globalization;
using System.Text.Json;

using CSRoll.Config;
using CSRoll.Core;

namespace CSRoll.Hud;

/// <summary>What the admin panel needs from the plugin: the saved baseline, saving, reloading, and the convar plumbing.</summary>
public interface IAdminPanelHost
{
    string Version { get; }

    /// <summary>What a reload from disk would give right now - the panel's "unsaved" is anything live that differs from it. Null until the first config load.</summary>
    AdminSnapshot? Baseline { get; }

    /// <summary>config.jsonc's full path, for the Config page.</summary>
    string ConfigPath { get; }

    /// <summary>Which csr_ convars the console or a .cfg has set, or null when none - they win over config.jsonc.</summary>
    string? ConVarNote { get; }

    /// <summary>A setting a convar mirrors was changed in the panel: the convar follows, and the change outranks an earlier override.</summary>
    void SyncConVar(string name);

    void AnnounceRollMode(ModifierRollMode mode);

    /// <summary>Writes every key that differs from the baseline into config.jsonc and reloads it.</summary>
    bool Save(AdminSnapshot live, out string error);

    /// <summary>Re-reads config.jsonc and puts the live state back to it, dropping every unsaved change.</summary>
    void ReloadFromDisk();
}

/// <summary>One unsaved change: the page it belongs to, a key for the row/slot it marks, and the Config page's line.</summary>
public readonly record struct AdminChange(string Page, string Key, string Text);

/// <summary>One config.jsonc write: the key path under the file's root, and the JSON value.</summary>
public readonly record struct AdminConfigEdit(string[] Path, string Json, bool OnlyIfPresent = false);

/// <summary>
/// Every value the admin panel edits, at one moment. The baseline is one of these taken whenever the
/// config loads, and the live state is one taken on every draw: their difference is the unsaved list,
/// the dirty markers and exactly what Save writes - so all three always agree.
/// </summary>
public sealed record AdminSnapshot
{
    public bool RandomRounds { get; init; }
    public ModifierRollMode RollMode { get; init; }
    public int MinRounds { get; init; }
    public int MaxRounds { get; init; }
    public int RepeatCooldown { get; init; }
    public bool CanRepeat { get; init; }
    public bool RollInWarmup { get; init; }
    public bool Reveal { get; init; }
    public bool Spin { get; init; }
    public bool SpectatorPanel { get; init; }
    public bool Hud { get; init; }
    public bool HudEveryone { get; init; }
    public bool HudSpectators { get; init; }
    public int ListOffset { get; init; }
    public bool Rarity { get; init; }

    /// <summary>Tier weights, indexed by ModifierTier.</summary>
    public required float[] Weights { get; init; }

    /// <summary>Switched-off modifiers. Live, only known ones; a baseline keeps whatever the file lists, names this load doesn't know included.</summary>
    public required HashSet<string> Disabled { get; init; }

    /// <summary>Each known modifier's tier, as rolls would resolve it.</summary>
    public required Dictionary<string, ModifierTier> Tiers { get; init; }

    /// <summary>Rarity.Overrides as written, for Save - it keeps entries for modifiers this load doesn't know.</summary>
    public required Dictionary<string, string> Overrides { get; init; }

    /// <param name="randomRounds">Random rounds as this snapshot should record them - live, or the file's default for a baseline.</param>
    /// <param name="disabled">The switched-off modifiers - the unregistered ones for live, the file's DisabledModifiers for a baseline.</param>
    public static AdminSnapshot Capture(ModifierRuntime runtime, bool randomRounds, IEnumerable<string> disabled)
    {
        var config = runtime.Config;
        var known = runtime.KnownModifierNames;
        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in disabled)
        {
            // Spelled the way the modifier spells itself, so Save writes clean names.
            off.Add(known.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name);
        }

        return new AdminSnapshot
        {
            RandomRounds = randomRounds,
            RollMode = config.ResolveRollMode(),
            MinRounds = runtime.MinRandomRounds,
            MaxRounds = runtime.MaxRandomRounds,
            RepeatCooldown = config.PerPlayerRepeatCooldownRounds,
            CanRepeat = config.CanRepeat,
            RollInWarmup = !config.DisableRandomRoundsInWarmup,
            Reveal = config.ShowCentreMsg,
            Spin = config.SpinReveal.Enabled,
            SpectatorPanel = config.SpectatorHud.Enabled,
            Hud = config.CustomHud.Enabled,
            HudEveryone = string.Equals(config.CustomHud.Mode, "Everyone", StringComparison.OrdinalIgnoreCase),
            HudSpectators = config.CustomHud.ShowToSpectatorTeam,
            ListOffset = config.CustomHud.ListOffset,
            Rarity = config.Rarity.Enabled,
            Weights = [.. ModifierRarity.Tiers.Select(tier => RawWeight(tier, config.Rarity))],
            Disabled = off,
            Tiers = known.ToDictionary(name => name, name => ModifierRarity.Resolve(name, config.Rarity), StringComparer.OrdinalIgnoreCase),
            Overrides = new Dictionary<string, string>(config.Rarity.Overrides ?? [], StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>The weight as configured (not clamped like ModifierRarity.Weight), so the panel shows what the file says.</summary>
    public static float RawWeight(ModifierTier tier, RarityConfig rarity) => tier switch
    {
        ModifierTier.Restricted => rarity.Restricted,
        ModifierTier.Classified => rarity.Classified,
        ModifierTier.Covert => rarity.Covert,
        ModifierTier.Gold => rarity.Gold,
        _ => rarity.MilSpec,
    };

    public static void SetWeight(ModifierTier tier, RarityConfig rarity, float value)
    {
        switch (tier)
        {
            case ModifierTier.Restricted: rarity.Restricted = value; break;
            case ModifierTier.Classified: rarity.Classified = value; break;
            case ModifierTier.Covert: rarity.Covert = value; break;
            case ModifierTier.Gold: rarity.Gold = value; break;
            default: rarity.MilSpec = value; break;
        }
    }

    public static string FormatWeight(float weight) => weight.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Every way this (live) differs from baseline, in the order the panel lists them.</summary>
    public List<AdminChange> Diff(AdminSnapshot baseline, Func<string, string> displayName)
    {
        var changes = new List<AdminChange>();
        void Flag(string page, string key, string label, bool before, bool after)
        {
            if (before != after)
            {
                changes.Add(new AdminChange(page, key, $"{label}: {OnOff(before)} → {OnOff(after)}"));
            }
        }

        void Number(string page, string key, string label, int before, int after)
        {
            if (before != after)
            {
                changes.Add(new AdminChange(page, key, $"{label}: {before} → {after}"));
            }
        }

        Flag("gen", "rr", "Random rounds", baseline.RandomRounds, RandomRounds);
        if (baseline.RollMode != RollMode)
        {
            changes.Add(new AdminChange("gen", "mode", $"Roll mode: {baseline.RollMode} → {RollMode}"));
        }

        Number("gen", "min", "Fewest per roll", baseline.MinRounds, MinRounds);
        Number("gen", "max", "Most per roll", baseline.MaxRounds, MaxRounds);
        Number("gen", "cd", "Repeat cooldown", baseline.RepeatCooldown, RepeatCooldown);
        Flag("gen", "rep", "Repeat last set", baseline.CanRepeat, CanRepeat);
        Flag("gen", "warm", "Roll in warmup", baseline.RollInWarmup, RollInWarmup);

        Flag("hud", "reveal", "Roll reveal", baseline.Reveal, Reveal);
        Flag("hud", "spin", "Centre-text spin", baseline.Spin, Spin);
        Flag("hud", "spec", "Spectator panel", baseline.SpectatorPanel, SpectatorPanel);
        Flag("hud", "hud", "Workshop HUD", baseline.Hud, Hud);
        if (baseline.HudEveryone != HudEveryone)
        {
            changes.Add(new AdminChange("hud", "hudmode", $"HUD mode: {HudMode(baseline.HudEveryone)} → {HudMode(HudEveryone)}"));
        }

        Flag("hud", "hudspec", "Spectator team", baseline.HudSpectators, HudSpectators);
        Number("hud", "listy", "List position", baseline.ListOffset, ListOffset);

        Flag("rar", "rar", "Rarity tiers", baseline.Rarity, Rarity);
        foreach (var tier in ModifierRarity.Tiers)
        {
            var (before, after) = (baseline.Weights[(int)tier], Weights[(int)tier]);
            if (before != after)
            {
                changes.Add(new AdminChange("rar", $"w:{ModifierRarity.CssKey(tier)}", $"{ModifierRarity.Label(tier)} weight: {FormatWeight(before)} → {FormatWeight(after)}"));
            }
        }

        foreach (var name in Tiers.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            var wasOff = baseline.Disabled.Contains(name);
            var isOff = Disabled.Contains(name);
            if (wasOff != isOff)
            {
                changes.Add(new AdminChange("mod", $"m:{name}", $"{displayName(name)}: {(isOff ? "disabled" : "enabled")}"));
            }

            if (baseline.Tiers.TryGetValue(name, out var before) && before != Tiers[name])
            {
                changes.Add(new AdminChange("mod", $"t:{name}", $"{displayName(name)} tier: {ModifierRarity.Label(before)} → {ModifierRarity.Label(Tiers[name])}"));
            }
        }

        return changes;
    }

    /// <summary>The config.jsonc writes that turn baseline into this - only the keys that changed.</summary>
    public List<AdminConfigEdit> ConfigEdits(AdminSnapshot baseline)
    {
        var edits = new List<AdminConfigEdit>();
        void Add(bool changed, string json, params string[] path)
        {
            if (changed)
            {
                edits.Add(new AdminConfigEdit(["Main", .. path], json));
            }
        }

        Add(baseline.RandomRounds != RandomRounds, Json(RandomRounds), "RandomRoundsEnabledByDefault");
        if (baseline.RollMode != RollMode)
        {
            edits.Add(new AdminConfigEdit(["Main", "RollMode"], Json(RollMode.ToString())));

            // An older config's RandomizePlayers: false would turn "Player" back into "Game".
            edits.Add(new AdminConfigEdit(["Main", "RandomizePlayers"], "true", OnlyIfPresent: true));
        }

        Add(baseline.MinRounds != MinRounds, Json(MinRounds), "MinRandomRounds");
        Add(baseline.MaxRounds != MaxRounds, Json(MaxRounds), "MaxRandomRounds");
        Add(baseline.RepeatCooldown != RepeatCooldown, Json(RepeatCooldown), "PerPlayerRepeatCooldownRounds");
        Add(baseline.CanRepeat != CanRepeat, Json(CanRepeat), "CanRepeat");
        Add(baseline.RollInWarmup != RollInWarmup, Json(!RollInWarmup), "DisableRandomRoundsInWarmup");
        Add(baseline.Reveal != Reveal, Json(Reveal), "ShowCentreMsg");
        Add(baseline.Spin != Spin, Json(Spin), "SpinReveal", "Enabled");
        Add(baseline.SpectatorPanel != SpectatorPanel, Json(SpectatorPanel), "SpectatorHud", "Enabled");
        Add(baseline.Hud != Hud, Json(Hud), "CustomHud", "Enabled");
        Add(baseline.HudEveryone != HudEveryone, Json(HudEveryone ? "Everyone" : "OptIn"), "CustomHud", "Mode");
        Add(baseline.HudSpectators != HudSpectators, Json(HudSpectators), "CustomHud", "ShowToSpectatorTeam");
        Add(baseline.ListOffset != ListOffset, Json(ListOffset), "CustomHud", "ListOffset");
        Add(baseline.Rarity != Rarity, Json(Rarity), "Rarity", "Enabled");
        foreach (var tier in ModifierRarity.Tiers)
        {
            Add(baseline.Weights[(int)tier] != Weights[(int)tier], FormatWeight(Weights[(int)tier]), "Rarity", tier.ToString());
        }

        Add(Tiers.Any(kv => baseline.Tiers.TryGetValue(kv.Key, out var before) && before != kv.Value), OverridesJson(), "Rarity", "Overrides");
        // Only known modifiers can differ - but names the file lists that this load doesn't know (a
        // missing .cfg, a typo) are written back as they were, the way Overrides keeps its unknown entries.
        var unknown = baseline.Disabled.Where(name => !Tiers.ContainsKey(name)).ToList();
        var known = baseline.Disabled.Where(Tiers.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var written = Disabled.Concat(unknown).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        Add(!known.SetEquals(Disabled), "[" + string.Join(", ", written.Select(Json)) + "]", "DisabledModifiers");
        return edits;
    }

    private string OverridesJson() =>
        Overrides.Count == 0
            ? "{}"
            : "{ " + string.Join(", ", Overrides.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{Json(kv.Key)}: {Json(kv.Value)}")) + " }";

    private static string Json(bool value) => value ? "true" : "false";

    private static string Json(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string HudMode(bool everyone) => everyone ? "Everyone" : "Opt-in";
}
