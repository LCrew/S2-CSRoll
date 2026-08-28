using System.Text.Json;

using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared;

namespace CSRoll.Hud;

/// <summary>
/// How a modifier is drawn on the custom HUD: the symbol in its badge, and the accent class that
/// colours it.
///
/// The icon is a glyph written into the panel as text rather than a texture reference. CS2 has no way
/// to set an image source from the server - only classes - so a per-modifier texture would mean 46
/// icon classes in the stylesheet, 46 image files in the addon, and a console warning on every client
/// for each one that hasn't been drawn yet. A glyph needs none of that and can be changed by editing a
/// data file.
/// </summary>
public readonly record struct ModifierPresentation(string Glyph, string AccentClass);

/// <summary>
/// Maps a modifier's internal <c>Name</c> to the CSS classes the HUD draws it with.
///
/// Deliberately NOT a rarity or tier system: every modifier in this plugin is drawn from a uniform
/// random pool, so colouring them by "rarity" would be telling the player something untrue. The accents
/// are thematic families instead - what a modifier does, not how lucky you were to get it.
/// </summary>
public interface IHudPresentationCatalog
{
    /// <summary>Loads the mapping from disk. Safe to call repeatedly.</summary>
    void Load();

    /// <summary>Presentation for a modifier, falling back to the generic icon for anything unlisted.</summary>
    ModifierPresentation For(string modifierName);
}

/// <inheritdoc />
public sealed class HudPresentationCatalog : IHudPresentationCatalog
{
    /// <summary>Key of the catch-all entry, used for any modifier without one of its own.</summary>
    private const string FallbackKey = "*";

    private static readonly ModifierPresentation HardFallback = new(HudClasses.GlyphFallback, HudClasses.AccentFallback);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ISwiftlyCore _core;
    private readonly Dictionary<string, ModifierPresentation> _byName = new(StringComparer.OrdinalIgnoreCase);

    private ModifierPresentation _fallback = HardFallback;
    private bool _loaded;

    public HudPresentationCatalog(ISwiftlyCore core)
    {
        _core = core;
    }

    /// <summary>Ships with the plugin; the baseline every server gets.</summary>
    private string BundledPath => Path.Combine(_core.PluginPath, "resources", "hud", "modifiers.jsonc");

    /// <summary>Operator override in the writable data directory, so a colour tweak survives an update.
    /// Same bundled-then-data precedence the cvar service already uses for its .cfg files.</summary>
    private string DataPath => Path.Combine(_core.PluginDataDirectory, "hud", "modifiers.jsonc");

    public void Load()
    {
        _byName.Clear();
        _fallback = HardFallback;
        _loaded = true;

        LoadFile(BundledPath);
        LoadFile(DataPath);

        _core.Logger.LogInformation("[CSRoll][HUD] Loaded presentation for {Count} modifier(s).", _byName.Count);
    }

    public ModifierPresentation For(string modifierName)
    {
        if (!_loaded)
        {
            Load();
        }

        return _byName.TryGetValue(modifierName, out var presentation) ? presentation : _fallback;
    }

    private void LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var entries = JsonSerializer.Deserialize<Dictionary<string, PresentationEntry>>(stream, JsonOptions);

            if (entries is null)
            {
                return;
            }

            foreach (var (name, entry) in entries)
            {
                var presentation = new ModifierPresentation(
                    string.IsNullOrWhiteSpace(entry.Glyph) ? HudClasses.GlyphFallback : entry.Glyph,
                    HudClasses.Accent(string.IsNullOrWhiteSpace(entry.Accent) ? "grey" : entry.Accent));

                if (name == FallbackKey)
                {
                    _fallback = presentation;
                    continue;
                }

                _byName[name] = presentation;
            }
        }
        catch (Exception ex)
        {
            // A malformed override must not take the plugin down - fall back to whatever loaded before it.
            _core.Logger.LogWarning(ex, "[CSRoll][HUD] Failed to read modifier presentation from {Path}; using defaults for anything it would have defined.", path);
        }
    }

    private sealed class PresentationEntry
    {
        public string? Glyph { get; set; }
        public string? Accent { get; set; }
    }
}
