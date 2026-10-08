using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Commands;

using CSRoll.Config;
using CSRoll.Core;
using CSRoll.Hud;

namespace CSRoll;

/// <summary>
/// !rolladmin - the clickable admin panel on the custom HUD (drawn by CustomHudService.Admin). This is
/// the plugin's side of it:
/// - the baseline the panel's "unsaved" is measured against, retaken on every config load;
/// - Save, which edits only the changed keys in config.jsonc so its comments survive;
/// - Reload from disk, which also puts back what a config reload alone leaves alone (registration,
///   random rounds).
///
/// Changes apply live, the same way the commands and !rollmenu apply them; nothing reaches the file
/// until Save.
/// </summary>
public partial class CSRoll : IAdminPanelHost
{
    /// <summary>The panel's values as config.jsonc has them, before any csr_ convar override.</summary>
    private AdminSnapshot? _adminFileBaseline;

    /// <summary>Set around the panel's own config reloads: its DisabledModifiers change is already live, so the "takes a plugin reload" warning would be wrong.</summary>
    private bool _adminReloadingConfig;

    private void InitializeAdmin()
    {
        _commandGuids.Add(Core.Command.RegisterCommand("rolladmin", Debounce("rolladmin", OnRollAdmin), registerRaw: true, permission: AdminPermission, helpText: "Opens or closes the clickable admin panel (needs the CSRoll HUD addon - !rollmenu otherwise). Bind it with: bind F6 rolladmin"));

        if (_customHud is not null)
        {
            _customHud.AdminHost = this;
        }

        TakeAdminBaseline();
    }

    public void OnRollAdmin(ICommandContext context)
    {
        if (context.Sender is not { IsValid: true } sender)
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, "Only an in-game player can open the admin panel.");
            return;
        }

        if (_customHud is null)
        {
            CSRollUtils.PrintTitleToChat(Core, sender, "The admin panel isn't available right now - use !rollmenu.");
            return;
        }

        CSRollUtils.PrintTitleToChat(Core, sender, _customHud.ToggleAdmin(sender));
    }

    /// <summary>
    /// The file's values, taken on every config load before the csr_ convar overrides are re-applied.
    /// Random rounds aren't reloaded from the file, so theirs is the file's default.
    /// </summary>
    private void TakeAdminBaseline()
    {
        if (Runtime is not null)
        {
            _adminFileBaseline = AdminSnapshot.Capture(Runtime, Config.RandomRoundsEnabledByDefault, Config.DisabledModifiers);
        }
    }

    string IAdminPanelHost.Version => PluginVersion;

    /// <summary>
    /// What a reload from disk gives right now: the file, with every csr_ convar that's overriding it
    /// right now in its live value - a reload re-applies those, so a value set by server.cfg or the
    /// console isn't an unsaved change and Save never writes it. Worked out on each call, so an
    /// override set mid-map counts at once, and one the panel clears (SyncConVar) compares against the
    /// file again.
    /// </summary>
    AdminSnapshot? IAdminPanelHost.Baseline
    {
        get
        {
            if (_adminFileBaseline is not { } file || Runtime is null || _overriddenConVars.Count == 0)
            {
                return _adminFileBaseline;
            }

            bool Overridden(string name) => _overriddenConVars.Contains(name);
            return file with
            {
                RandomRounds = Overridden("csr_randomrounds") ? Runtime.RandomRoundsEnabled : file.RandomRounds,
                RollMode = Overridden("csr_rollmode") ? Runtime.RollMode : file.RollMode,
                MinRounds = Overridden("csr_minrounds") ? Runtime.MinRandomRounds : file.MinRounds,
                MaxRounds = Overridden("csr_maxrounds") ? Runtime.MaxRandomRounds : file.MaxRounds,
                Rarity = Overridden("csr_rarity") ? Config.Rarity.Enabled : file.Rarity,
                Hud = Overridden("csr_hud") ? Config.CustomHud.Enabled : file.Hud,
                HudEveryone = Overridden("csr_hud_mode") ? HudModeNumber(Config.CustomHud) == 1 : file.HudEveryone,
            };
        }
    }

    string IAdminPanelHost.ConfigPath => Core.Configuration.GetConfigPath("config.jsonc");

    string? IAdminPanelHost.ConVarNote
    {
        get
        {
            if (_overriddenConVars.Count == 0)
            {
                return null;
            }

            var names = string.Join(", ", _overriddenConVars.Order(StringComparer.OrdinalIgnoreCase));
            return _overriddenConVars.Count == 1
                ? $"{names} is set from the console or a .cfg - it wins over config.jsonc"
                : $"{names} are set from the console or a .cfg - they win over config.jsonc";
        }
    }

    void IAdminPanelHost.SyncConVar(string name) => SyncConVar(name);

    void IAdminPanelHost.AnnounceRollMode(ModifierRollMode mode) => AnnounceRollMode(mode);

    bool IAdminPanelHost.Save(AdminSnapshot live, out string error)
    {
        var edits = live.ConfigEdits(((IAdminPanelHost)this).Baseline ?? live);
        if (edits.Count == 0)
        {
            error = "";
            return true;
        }

        try
        {
            var path = Core.Configuration.GetConfigPath("config.jsonc");
            var text = File.ReadAllText(path);
            foreach (var edit in edits)
            {
                text = JsoncEditor.Set(text, edit.Path, edit.Json, edit.OnlyIfPresent);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "[CSRoll] The admin panel couldn't save config.jsonc.");
            error = ex switch
            {
                UnauthorizedAccessException => "access to config.jsonc was denied",
                FormatException => $"config.jsonc couldn't be read ({ex.Message})",
                _ => ex.Message,
            };
            return false;
        }

        Core.Logger.LogInformation("[CSRoll] Admin panel saved {Count} key(s) to config.jsonc: {Keys}", edits.Count, string.Join(", ", edits.Select(e => string.Join('.', e.Path.Skip(1)))));

        // Everything live is in the file now, so this reload changes nothing but the baseline.
        ReloadConfigFromDisk();
        error = "";
        return true;
    }

    void IAdminPanelHost.ReloadFromDisk()
    {
        ReloadConfigFromDisk();

        // What a config reload leaves alone. Registration follows DisabledModifiers...
        var disabled = new HashSet<string>(Config.DisabledModifiers, StringComparer.OrdinalIgnoreCase);
        foreach (var name in Runtime.KnownModifierNames.ToList())
        {
            var registered = Runtime.IsModifierRegisteredByName(name);
            if (registered && disabled.Contains(name))
            {
                Runtime.DisableModifierByName(name, out _);
            }
            else if (!registered && !disabled.Contains(name))
            {
                Runtime.EnableModifierByName(name, out _);
            }
        }

        // ...and random rounds go back to the file's default, unless a convar owns them.
        if (!_overriddenConVars.Contains("csr_randomrounds") &&
            Runtime.RandomRoundsEnabled != Config.RandomRoundsEnabledByDefault &&
            (Runtime.RandomRoundsEnabled || Runtime.RegisteredModifiers.Count > 0))
        {
            Runtime.ToggleRandomRounds();
        }

        MirrorConVars();
        TakeAdminBaseline();
    }

    private void ReloadConfigFromDisk()
    {
        _adminReloadingConfig = true;
        try
        {
            if (Core.Configuration.Manager is IConfigurationRoot configRoot)
            {
                configRoot.Reload();
            }

            ReloadConfigFromManager();
        }
        finally
        {
            _adminReloadingConfig = false;
        }
    }
}
