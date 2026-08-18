using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Misc;

using CSRoll.Core;
using CSRoll.Modifiers;

namespace CSRoll;

public partial class CSRoll
{
    private const string AdminPermission = CSRollUtils.AdminPermission;
    private const double DebounceWindowMs = 400;

    private Guid _chatHookId;
    private readonly List<Guid> _commandGuids = [];
    private readonly Dictionary<(int PlayerId, string Command), DateTime> _lastInvocation = [];

    // Every command is registered manually (not via [Command] attributes) so registration count
    // is fully in our control - `sw cmds` confirms exactly one entry per command, correctly
    // attributed to this plugin, no duplicates anywhere in the registry. Despite that, every
    // command's handler was observed firing twice per single invocation ("Enabled" immediately
    // followed by "Disabled", etc.) - so the duplication is happening in how the command gets
    // DISPATCHED to us, not in how many times it's registered, and that dispatch path is outside
    // this plugin's code (SwiftlyS2's own native layer, or whatever channel the command is issued
    // through). Debounce() below is a pragmatic guard against that: if the same player's same
    // command fires again within DebounceWindowMs, the second call is dropped. This treats the
    // symptom, not a confirmed root cause - remove it if the underlying double-dispatch is ever
    // fixed upstream.
    private void InitializeCommands()
    {
        _commandGuids.Add(Core.Command.RegisterCommand("rolllist", Debounce("rolllist", OnRollList), registerRaw: true, helpText: "Prints the name and description for each registered modifier."));
        _commandGuids.Add(Core.Command.RegisterCommand("rollactive", Debounce("rollactive", OnRollActive), registerRaw: true, helpText: "Prints the name, scope, and description for each active modifier."));
        _commandGuids.Add(Core.Command.RegisterCommand("rolltoggle", Debounce("rolltoggle", OnRollToggle), registerRaw: true, permission: AdminPermission, helpText: "<modifier name> - Adds the modifier globally if inactive, removes it if active."));
        _commandGuids.Add(Core.Command.RegisterCommand("addrandommodifier", Debounce("addrandommodifier", OnAddRandomModifier), registerRaw: true, permission: AdminPermission, helpText: "Add a random modifier to be activated immediately."));
        _commandGuids.Add(Core.Command.RegisterCommand("removemodifier", Debounce("removemodifier", OnRemoveModifier), registerRaw: true, permission: AdminPermission, helpText: "<modifier name> - Remove an active modifier."));
        _commandGuids.Add(Core.Command.RegisterCommand("removemodifiers", Debounce("removemodifiers", OnRemoveModifiers), registerRaw: true, permission: AdminPermission, helpText: "Clear / Remove all active modifiers."));
        _commandGuids.Add(Core.Command.RegisterCommand("disablemodifier", Debounce("disablemodifier", OnDisableModifier), registerRaw: true, permission: AdminPermission, helpText: "<modifier name> - Deactivate a modifier and remove it from the registered pool so it can't be added/rolled again until re-enabled (!rollmenu) or the plugin reloads."));
        _commandGuids.Add(Core.Command.RegisterCommand("randomrounds", Debounce("randomrounds", OnRandomRounds), registerRaw: true, permission: AdminPermission, helpText: "Toggle random rounds on/off."));
        _commandGuids.Add(Core.Command.RegisterCommand("randomroundsreroll", Debounce("randomroundsreroll", OnRandomRoundsReRoll), registerRaw: true, permission: AdminPermission, helpText: "Re-roll the current random round modifiers and apply them to the current round."));
        _commandGuids.Add(Core.Command.RegisterCommand("rolldebug", Debounce("rolldebug", OnRollDebug), registerRaw: true, permission: AdminPermission, helpText: "Toggle whether per-player random-round assignments are reported to admins in chat."));
        _commandGuids.Add(Core.Command.RegisterCommand("rollreload", Debounce("rollreload", OnRollReload), registerRaw: true, permission: AdminPermission, helpText: "Reload config.jsonc from disk without restarting the plugin or resetting active modifiers."));
        _commandGuids.Add(Core.Command.RegisterCommand("memodifier", Debounce("memodifier", OnMeModifier), registerRaw: true, permission: AdminPermission, helpText: "<modifier name> - Apply a modifier scoped to just yourself, without affecting anyone else."));
        _commandGuids.Add(Core.Command.RegisterCommand("rollhelp", Debounce("rollhelp", OnRollHelp), registerRaw: true, helpText: "Prints every available CSRoll command."));

        InitializeMenu();

        // SwiftlyS2 has no built-in "!chat command" mirroring (unlike CounterStrikeSharp's css_
        // commands), so bridge "!name args" chat messages to the matching console command ourselves.
        _chatHookId = Core.Command.HookClientChat(OnClientChat);
    }

    private ICommandService.CommandListener Debounce(string commandName, ICommandService.CommandListener handler)
    {
        return context =>
        {
            var playerId = context.Sender?.PlayerID ?? -1;
            var key = (playerId, commandName);
            var now = DateTime.UtcNow;

            if (_lastInvocation.TryGetValue(key, out var last) && (now - last).TotalMilliseconds < DebounceWindowMs)
            {
                Core.Logger.LogWarning("[CSRoll] Dropped duplicate '{Command}' invocation from player {PlayerId} within {Window}ms.", commandName, playerId, DebounceWindowMs);
                return;
            }

            _lastInvocation[key] = now;
            handler(context);
        };
    }

    private void UninitializeCommands()
    {
        foreach (var guid in _commandGuids)
        {
            Core.Command.UnregisterCommand(guid);
        }
        _commandGuids.Clear();

        Core.Command.UnhookClientChat(_chatHookId);
    }

    private HookResult OnClientChat(int playerId, string text, bool teamOnly)
    {
        if (!text.StartsWith('!'))
        {
            return HookResult.Continue;
        }

        var command = text[1..].Trim();
        if (command.Length == 0)
        {
            return HookResult.Continue;
        }

        var player = Core.PlayerManager.GetPlayer(playerId);
        if (player is not { IsValid: true })
        {
            return HookResult.Continue;
        }

        player.ExecuteCommand(command);
        return HookResult.Stop;
    }

    public void OnRollList(ICommandContext context)
    {
        CSRollUtils.PrintModifiersToChat(Core, context.Sender, Runtime.RegisteredModifiers, "Registered modifiers");
    }

    public void OnRollActive(ICommandContext context)
    {
        CSRollUtils.PrintActiveModifiersToChat(Core, context.Sender, Runtime.ActiveModifiers);
    }

    /// <summary>Merged !addmodifier/!togglemodifier - both did the same thing once the modifier was already active (nothing left to distinguish once !addmodifier's only path forward on an active modifier was to fail).</summary>
    public void OnRollToggle(ICommandContext context)
    {
        var modifierName = context.Args.Length > 0 ? context.Args[0] : "";
        var wasActive = Runtime.IsModifierActiveByName(modifierName);

        if (Runtime.ToggleModifierByName(modifierName, out var message))
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, $"{(wasActive ? "Removed" : "Added")} {modifierName} modifier.");
        }
        else
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, message);
        }
    }

    public void OnAddRandomModifier(ICommandContext context)
    {
        if (!Runtime.AddRandomModifier(out _))
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, "Failed to add random modifier.");
        }
    }

    public void OnRemoveModifier(ICommandContext context)
    {
        var modifierName = context.Args.Length > 0 ? context.Args[0] : "";
        Runtime.RemoveModifierByName(modifierName, out var message);
        CSRollUtils.PrintTitleToChat(Core, context.Sender, message);
    }

    public void OnRemoveModifiers(ICommandContext context)
    {
        Runtime.RemoveAllModifiers();
        CSRollUtils.PrintTitleToChat(Core, context.Sender, "Removed all modifiers.");
    }

    public void OnDisableModifier(ICommandContext context)
    {
        var modifierName = context.Args.Length > 0 ? context.Args[0] : "";
        Runtime.DisableModifierByName(modifierName, out var message);
        CSRollUtils.PrintTitleToChat(Core, context.Sender, message);
    }

    public void OnRollReload(ICommandContext context)
    {
        // Core.Configuration.Manager is typed as IConfigurationManager (IConfiguration +
        // IConfigurationBuilder only - no Reload()), but the concrete instance backing it is the
        // modern Microsoft.Extensions.Configuration.ConfigurationManager, which also implements
        // IConfigurationRoot (that's where Reload() actually lives) - confirmed via metadata
        // inspection of SwiftlyS2.CS2.dll. ReloadConfigFromManager() (CSRoll.cs) then rebinds
        // CSRollConfig and propagates it into Runtime.Config. The automatic file-watcher
        // (ChangeToken.OnChange in InitializeConfig) already does both of these on its own when
        // config.jsonc changes on disk - this command exists for a deterministic manual trigger in
        // case that watcher doesn't fire reliably for a given editor/save method.
        if (Core.Configuration.Manager is IConfigurationRoot configRoot)
        {
            configRoot.Reload();
        }

        ReloadConfigFromManager();
        CSRollUtils.PrintTitleToChat(Core, context.Sender, "Config reloaded from disk.");
    }

    public void OnMeModifier(ICommandContext context)
    {
        if (context.Sender is not { IsValid: true } sender)
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, "Only an in-game player can use this command.");
            return;
        }

        var modifierName = context.Args.Length > 0 ? context.Args[0] : "";
        Runtime.AddModifierToPlayer(modifierName, sender.Slot, out var message);
        CSRollUtils.PrintTitleToChat(Core, sender, message);
    }

    public void OnRollHelp(ICommandContext context)
    {
        CSRollUtils.PrintTitleToChat(Core, context.Sender, "Available commands:");

        foreach (var command in Core.Command.GetCommandsByPlugin("CSRoll").OrderBy(c => c.CommandName, StringComparer.OrdinalIgnoreCase))
        {
            var adminTag = string.IsNullOrEmpty(command.Permission) ? "" : " [admin]";
            var coloredCommand = SwiftlyS2.Shared.Helper.Colored($"[orange]!{command.CommandName}[default]");
            context.Sender?.SendChat($"{coloredCommand}{adminTag} - {command.HelpText}");
        }
    }

    public void OnRollDebug(ICommandContext context)
    {
        Runtime.DebugMode = !Runtime.DebugMode;
        CSRollUtils.PrintTitleToChat(Core, context.Sender, Runtime.DebugMode
            ? "Debug mode enabled - per-player random-round assignments will now be reported to admins in chat."
            : "Debug mode disabled.");
    }

    public void OnRandomRounds(ICommandContext context)
    {
        if (!Runtime.RandomRoundsEnabled && Runtime.RegisteredModifiers.Count == 0)
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, "No modifiers are registered! Cannot activate random rounds!");
            return;
        }

        Runtime.ToggleRandomRounds();
    }

    public void OnRandomRoundsReRoll(ICommandContext context)
    {
        if (!Runtime.RandomRoundsEnabled)
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, "Random rounds are not enabled! Cannot re-roll modifiers.");
            return;
        }

        if (Runtime.RegisteredModifiers.Count == 0)
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, "No registered modifiers found! Cannot re-roll modifiers.");
            return;
        }

        Runtime.RemoveAllModifiers();
        Runtime.ApplyRandomRoundsForRound();
    }
}
