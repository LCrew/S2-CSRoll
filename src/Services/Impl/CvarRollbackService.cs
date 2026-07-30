using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Convars;
using SwiftlyS2.Shared.Players;

using CSRoll.Services.Interfaces;

namespace CSRoll.Services.Impl;

/// <summary>
/// Parses and applies the two `.cfg`-driven cvar systems (ConVarModifiers/*.cfg and
/// ModifierConfig/&lt;Name&gt;.cfg), replacing CounterStrikeSharp's ModifierConfig/GameModifierCvar pair.
///
/// Directory convention: bundled defaults ship under the plugin's own resources/ folder
/// (Core.PluginPath) and are read first; PluginDataDirectory is the writable, update-surviving
/// location for server-operator-added files, checked second - mirrors the original CSS
/// plugin-bundled-dir-then-external-configs-dir fallback.
/// </summary>
public sealed class CvarRollbackService : ICvarRollbackService
{
    private readonly ISwiftlyCore _core;

    public CvarRollbackService(ISwiftlyCore core)
    {
        _core = core;
    }

    private string BundledConVarModifiersDir => Path.Combine(_core.PluginPath, "resources", "ConVarModifiers");
    private string DataConVarModifiersDir => Path.Combine(_core.PluginDataDirectory, "ConVarModifiers");
    private string BundledModifierConfigDir => Path.Combine(_core.PluginPath, "resources", "ModifierConfig");
    private string DataModifierConfigDir => Path.Combine(_core.PluginDataDirectory, "ModifierConfig");

    public void Install()
    {
        EnsureDirectory(BundledConVarModifiersDir);
        EnsureDirectory(DataConVarModifiersDir);
        EnsureDirectory(BundledModifierConfigDir);
        EnsureDirectory(DataModifierConfigDir);
    }

    public void Uninstall()
    {
        // Individual handles own their rollback state and are torn down by each modifier's
        // Deactivate() call; nothing global to clean up here.
    }

    public IReadOnlyList<string> FindCvarModifierFiles()
    {
        var files = new List<string>();
        files.AddRange(SafeGetFiles(BundledConVarModifiersDir));
        files.AddRange(SafeGetFiles(DataConVarModifiersDir));
        return files;
    }

    public ICvarConfigHandle? TryLoadBoltOnConfig(string modifierName)
    {
        var path = FindMatchingFile(BundledModifierConfigDir, modifierName)
                   ?? FindMatchingFile(DataModifierConfigDir, modifierName);

        if (path is null)
        {
            return null;
        }

        var handle = new CvarConfigHandle(_core, recognizeMetadata: false);
        return handle.ParseConfigFile(path) ? handle : null;
    }

    public ICvarConfigHandle ParseCvarModifierFile(string filePath)
    {
        var handle = new CvarConfigHandle(_core, recognizeMetadata: true);
        handle.ParseConfigFile(filePath);
        return handle;
    }

    private static string? FindMatchingFile(string directory, string modifierName)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return Directory.GetFiles(directory, "*.cfg")
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == modifierName);
    }

    private static IEnumerable<string> SafeGetFiles(string directory)
    {
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.cfg") : [];
    }

    private static void EnsureDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// One parsed .cfg file: a list of server cvar lines, a list of client cvar lines
    /// (after a `Client:` marker), and - for ConVarModifiers/*.cfg only - metadata keywords.
    /// </summary>
    private sealed class CvarConfigHandle : ICvarConfigHandle
    {
        private readonly ISwiftlyCore _core;
        private readonly bool _recognizeMetadata;

        private readonly List<(string Name, string Value)> _serverLines = [];
        private readonly List<(string Name, string Value)> _clientLines = [];
        private readonly List<(string Name, string Value)> _serverRollback = [];
        private readonly Dictionary<int, List<(string Name, string Value)>> _clientRollback = [];

        public string? ModifierName { get; private set; }
        public string? ModifierDescription { get; private set; }
        public bool SupportsRandomRounds { get; private set; }
        public IReadOnlyCollection<string> IncompatibleModifiers { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public CvarConfigHandle(ISwiftlyCore core, bool recognizeMetadata)
        {
            _core = core;
            _recognizeMetadata = recognizeMetadata;
        }

        public bool ParseConfigFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            _serverLines.Clear();
            _clientLines.Clear();

            bool inClientSection = false;

            foreach (var rawLine in File.ReadAllLines(filePath))
            {
                var line = rawLine.Trim();

                if (line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Contains("Client:", StringComparison.Ordinal))
                {
                    inClientSection = true;
                    continue;
                }

                var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIndex >= 0)
                {
                    line = line[..commentIndex].Trim();
                }

                if (line.Length == 0)
                {
                    continue;
                }

                if (!inClientSection && _recognizeMetadata && TryParseMetadataLine(line))
                {
                    continue;
                }

                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                var name = parts[0];
                var value = parts.Length > 1 ? parts[1].Trim() : "";
                (inClientSection ? _clientLines : _serverLines).Add((name, value));
            }

            if (_recognizeMetadata && string.IsNullOrEmpty(ModifierName))
            {
                _core.Logger.LogWarning("[CSRoll] Cvar modifier file {File} has no modifier_name entry.", filePath);
                ModifierName = "Unnamed";
            }

            return true;
        }

        private bool TryParseMetadataLine(string line)
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return false;
            }

            var key = parts[0];
            var value = parts.Length > 1 ? parts[1].Trim() : "";

            if (key.Equals("modifier_name", StringComparison.OrdinalIgnoreCase))
            {
                ModifierName = value;
                return true;
            }

            if (key.Equals("modifier_description", StringComparison.OrdinalIgnoreCase))
            {
                ModifierDescription = value;
                return true;
            }

            if (key.Equals("supports_random_rounds", StringComparison.OrdinalIgnoreCase))
            {
                if (bool.TryParse(value, out var parsed))
                {
                    SupportsRandomRounds = parsed;
                }
                return true;
            }

            if (key.Equals("incompatible_modifiers", StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = value.Trim('[', ']');
                IncompatibleModifiers = trimmed
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return true;
            }

            return false;
        }

        public void Apply()
        {
            foreach (var (name, value) in _serverLines)
            {
                var cvar = _core.ConVar.FindAsString(name);
                if (cvar is null)
                {
                    _core.Logger.LogWarning("[CSRoll] Unknown server cvar {Name}, skipping.", name);
                    continue;
                }

                _serverRollback.Add((name, cvar.ValueAsString));
                _core.Engine.ExecuteCommand($"{name} {value}");
            }

            foreach (var player in _core.PlayerManager.GetAllValidPlayers())
            {
                ApplyClientConfig(player);
            }
        }

        public void Remove()
        {
            foreach (var player in _core.PlayerManager.GetAllValidPlayers())
            {
                RemoveClientConfig(player);
            }

            // Undo server cvars in reverse order they were applied.
            for (var i = _serverRollback.Count - 1; i >= 0; i--)
            {
                var (name, value) = _serverRollback[i];
                _core.Engine.ExecuteCommand($"{name} {value}");
            }

            _serverRollback.Clear();
            _clientRollback.Clear();
        }

        public void ApplyClientConfig(IPlayer player)
        {
            if (_clientLines.Count == 0)
            {
                return;
            }

            var slot = player.Slot;
            var rollback = _clientRollback.TryGetValue(slot, out var existing) ? existing : _clientRollback[slot] = [];

            foreach (var (name, targetValue) in _clientLines)
            {
                var cvar = _core.ConVar.FindAsString(name);
                if (cvar is null)
                {
                    _core.Logger.LogWarning("[CSRoll] Unknown client cvar {Name}, aborting remaining client cvars for this player.", name);
                    return;
                }

                if (cvar.Flags.HasFlag(ConvarFlags.REPLICATED))
                {
                    // QueryClient is async - the callback may fire after the player has disconnected,
                    // so revalidate before touching rollback state or issuing a replicated write.
                    cvar.QueryClient(slot, currentValue =>
                    {
                        var stillValidPlayer = _core.PlayerManager.GetPlayer(slot);
                        if (stillValidPlayer is not { IsValid: true })
                        {
                            return;
                        }

                        rollback.Add((name, currentValue));
                        _core.ConVar.ReplicateToClient(slot, name, targetValue);
                    });
                }
                else
                {
                    // Bug fix vs. the CSS original: rollback keeps the pre-change value, but the
                    // apply step must issue the parsed TARGET value, not the value being rolled back to.
                    var currentValue = cvar.ValueAsString;
                    rollback.Add((name, currentValue));
                    player.ExecuteCommand($"{name} {targetValue}");
                }
            }
        }

        public void RemoveClientConfig(IPlayer player)
        {
            var slot = player.Slot;
            if (!_clientRollback.TryGetValue(slot, out var rollback) || rollback.Count == 0)
            {
                return;
            }

            for (var i = rollback.Count - 1; i >= 0; i--)
            {
                var (name, value) = rollback[i];
                var cvar = _core.ConVar.FindAsString(name);
                if (cvar is not null && cvar.Flags.HasFlag(ConvarFlags.REPLICATED))
                {
                    _core.ConVar.ReplicateToClient(slot, name, value);
                }
                else
                {
                    player.ExecuteCommand($"{name} {value}");
                }
            }

            _clientRollback.Remove(slot);
        }

        public void ClearClientState(int slot)
        {
            _clientRollback.Remove(slot);
        }
    }
}
