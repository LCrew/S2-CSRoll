using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace CSRoll.Hud;

/// <summary>
/// Each player's own choice of HUD, remembered across visits by SteamID - true for the custom HUD,
/// false for center-HTML, absent for "hasn't chosen" (which is when the opt-in prompt shows).
///
/// Kept in a small JSON file in the plugin's data directory. Loaded once when the HUD is installed;
/// saved on a background thread after each change, from a snapshot, so a !hud never waits on disk.
/// </summary>
public sealed class HudPreferences
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Dictionary<ulong, bool> _choices = [];
    private readonly object _saveLock = new();

    public HudPreferences(string dataDirectory, ILogger logger)
    {
        _path = Path.Combine(dataDirectory, "hud-players.json");
        _logger = logger;
    }

    public int Count => _choices.Count;

    public void Load()
    {
        _choices.Clear();

        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var stored = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(_path)) ?? [];
            foreach (var (steamId, useCustomHud) in stored)
            {
                if (ulong.TryParse(steamId, out var id) && id != 0)
                {
                    _choices[id] = useCustomHud;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CSRoll][HUD] Couldn't read {Path} - every player starts undecided.", _path);
        }
    }

    /// <summary>The player's choice, or null when they haven't made one. Bots (SteamID 0) never have one.</summary>
    public bool? Get(ulong steamId) => steamId != 0 && _choices.TryGetValue(steamId, out var choice) ? choice : null;

    public void Set(ulong steamId, bool useCustomHud)
    {
        if (steamId == 0)
        {
            return;
        }

        _choices[steamId] = useCustomHud;

        var snapshot = _choices.ToDictionary(entry => entry.Key.ToString(), entry => entry.Value);
        _ = Task.Run(() => Save(snapshot));
    }

    private void Save(Dictionary<string, bool> snapshot)
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CSRoll][HUD] Couldn't save {Path}.", _path);
            }
        }
    }
}
