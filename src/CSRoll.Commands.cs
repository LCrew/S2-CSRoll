using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Misc;

using CSRoll.Config;
using CSRoll.Core;
using CSRoll.Hud;
using CSRoll.Modifiers;

namespace CSRoll;

public partial class CSRoll
{
    private const string AdminPermission = CSRollUtils.AdminPermission;
    private const double DebounceWindowMs = 400;

    private Guid _chatHookId;
    private readonly List<Guid> _commandGuids = [];

    /// <summary>
    /// Identifies the current !rolltestall sweep. Every scheduled step captures the value it was
    /// started under and does nothing if it has moved on, so a second invocation cancels the run in
    /// flight rather than ending up with two sweeps applying modifiers to the same player at once.
    /// </summary>
    private int _testAllRunId;

    private bool _testAllRunning;

    /// <summary>Default seconds a modifier is left on before being removed. Overridable per run, since some are far easier to judge with longer than a second to look at them.</summary>
    private const float TestAllDefaultHoldSeconds = 1f;

    /// <summary>Gap between removing one modifier and applying the next, so teardown and setup never land on the same tick.</summary>
    private const float TestAllGapSeconds = 0.25f;
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
        _commandGuids.Add(Core.Command.RegisterCommand("rollmode", Debounce("rollmode", OnRollMode), registerRaw: true, permission: AdminPermission, helpText: "[player|team|game] - How random rounds hand out modifiers: each player their own, one per team, or one for everyone. Saved to config.jsonc."));
        _commandGuids.Add(Core.Command.RegisterCommand("rollmethod", Debounce("rollmethod", OnRollMode), registerRaw: true, permission: AdminPermission, helpText: "[player|team|game] - Same as !rollmode."));
        _commandGuids.Add(Core.Command.RegisterCommand("rollsim", Debounce("rollsim", OnRollSim), registerRaw: true, permission: AdminPermission, helpText: "[rolls] - Simulates that many single picks (default 10000) and prints how often each rarity tier came up."));
        _commandGuids.Add(Core.Command.RegisterCommand("rolldebug", Debounce("rolldebug", OnRollDebug), registerRaw: true, permission: AdminPermission, helpText: "Toggle whether per-player random-round assignments are reported to admins in chat."));
        _commandGuids.Add(Core.Command.RegisterCommand("rollreload", Debounce("rollreload", OnRollReload), registerRaw: true, permission: AdminPermission, helpText: "Reload config.jsonc from disk without restarting the plugin or resetting active modifiers."));
        _commandGuids.Add(Core.Command.RegisterCommand("memodifier", Debounce("memodifier", OnMeModifier), registerRaw: true, permission: AdminPermission, helpText: "<modifier name> - Apply a modifier scoped to just yourself, without affecting anyone else."));
        _commandGuids.Add(Core.Command.RegisterCommand("rolltestall", Debounce("rolltestall", OnRollTestAll), registerRaw: true, permission: AdminPermission, helpText: "[seconds] - Applies every registered modifier to you one at a time, announcing each on and off, so broken ones can be spotted. Run again to stop."));
        _commandGuids.Add(Core.Command.RegisterCommand("rollhelp", Debounce("rollhelp", OnRollHelp), registerRaw: true, helpText: "Prints every available CSRoll command."));
        _commandGuids.Add(Core.Command.RegisterCommand("hud", Debounce("hud", OnHud), registerRaw: true, helpText: "Switch between the new CSRoll HUD and the centre-text HUD (needs the HUD addon). Remembered for next time."));
        _commandGuids.Add(Core.Command.RegisterCommand("hudstatus", Debounce("hudstatus", OnHudStatus), registerRaw: true, permission: AdminPermission, helpText: "Reports whether the custom HUD entity is live and who is using it."));

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
        // Cancel any !rolltestall sweep still in flight. Its steps are scheduler callbacks holding
        // Runtime, and a map change or plugin reload tears Runtime down underneath them - bumping
        // the run id makes every remaining step a no-op instead.
        _testAllRunId++;
        _testAllRunning = false;

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

    public void OnHud(ICommandContext context)
    {
        if (context.Sender is not { IsValid: true } sender)
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, "Only an in-game player can switch HUDs.");
            return;
        }

        CSRollUtils.PrintTitleToChat(Core, sender, _customHud?.Toggle(sender) ?? "The custom HUD isn't available.");
    }

    public void OnHudStatus(ICommandContext context)
    {
        CSRollUtils.PrintTitleToChat(Core, context.Sender, _customHud?.Status(context.Sender) ?? "The custom HUD isn't available.");
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

    /// <summary>
    /// Walks every registered modifier, applying each to the calling admin alone for a moment and
    /// then removing it, announcing both to console and chat.
    ///
    /// Built for exactly the job of finding which modifiers misbehave: one modifier is live at a
    /// time, each is scoped to a single player rather than the server, and every step is named in
    /// the console - so if the server dies or something visibly breaks, the last "turning ON" line
    /// is the modifier responsible. The same reasoning as the ACTIVATING/ACTIVATED breadcrumbs in
    /// GameModifierBase, applied deliberately rather than only when something has already gone
    /// wrong.
    /// </summary>
    public void OnRollTestAll(ICommandContext context)
    {
        if (context.Sender is not { IsValid: true } sender)
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, "Only an in-game player can use this command.");
            return;
        }

        // Second invocation stops the sweep - the alternative is being stuck riding it out while
        // something visibly broken is applied to you.
        if (_testAllRunning)
        {
            _testAllRunId++;
            _testAllRunning = false;
            Core.Logger.LogInformation("[CSRoll] TESTALL: stopped by {Player}.", sender.Slot);
            CSRollUtils.PrintTitleToChat(Core, sender, "Modifier sweep stopped.");
            Runtime.RemoveAllModifiers();
            return;
        }

        if (Runtime.RegisteredModifiers.Count == 0)
        {
            CSRollUtils.PrintTitleToChat(Core, sender, "No registered modifiers to test.");
            return;
        }

        var holdSeconds = TestAllDefaultHoldSeconds;
        if (context.Args.Length > 0 && float.TryParse(context.Args[0], out var parsed) && parsed > 0f)
        {
            holdSeconds = Math.Clamp(parsed, 0.25f, 60f);
        }

        var runId = ++_testAllRunId;
        _testAllRunning = true;

        Core.Logger.LogInformation(
            "[CSRoll] TESTALL: starting sweep of {Count} modifiers for slot {Slot}, {Hold}s each.",
            Runtime.RegisteredModifiers.Count, sender.Slot, holdSeconds);
        CSRollUtils.PrintTitleToChat(Core, sender, $"Testing all {Runtime.RegisteredModifiers.Count} modifiers, {holdSeconds:0.#}s each. Run !rolltestall again to stop.");

        StepTestAll(sender.Slot, 0, holdSeconds, runId);
    }

    /// <summary>
    /// One modifier of the sweep: announce, apply, hold, announce, remove, then schedule the next.
    ///
    /// Self-rescheduling DelayBySeconds rather than a repeating timer, matching what the spin-reveal
    /// animation settled on - it is the one scheduler primitive this codebase has confirmed working
    /// live. The player is re-resolved every step because a sweep outlives a disconnect easily.
    /// </summary>
    private void StepTestAll(int slot, int index, float holdSeconds, int runId)
    {
        if (runId != _testAllRunId)
        {
            return;
        }

        var modifiers = Runtime.RegisteredModifiers;
        if (index >= modifiers.Count)
        {
            _testAllRunning = false;
            Core.Logger.LogInformation("[CSRoll] TESTALL: sweep complete, {Count} modifiers tested.", modifiers.Count);
            CSRollUtils.PrintTitleToChat(Core, Core.PlayerManager.GetPlayer(slot), $"Modifier sweep complete - {modifiers.Count} tested.");
            return;
        }

        if (Core.PlayerManager.GetPlayer(slot) is not { IsValid: true } player)
        {
            _testAllRunning = false;
            Core.Logger.LogWarning("[CSRoll] TESTALL: aborted - slot {Slot} is no longer a valid player.", slot);
            return;
        }

        var modifier = modifiers[index];
        var position = $"{index + 1}/{modifiers.Count}";

        Core.Logger.LogInformation("[CSRoll] TESTALL {Position}: turning ON {Name}", position, modifier.Name);
        CSRollUtils.PrintTitleToChat(Core, player, $"[{position}] Turning ON [gold]{modifier.Name}[default]");

        if (!Runtime.AddModifierToPlayer(modifier.Name, slot, out var addMessage))
        {
            // Not fatal, and worth seeing rather than skipping silently - a modifier that refuses to
            // apply is itself a result the sweep exists to surface.
            Core.Logger.LogWarning("[CSRoll] TESTALL {Position}: {Name} did NOT apply - {Message}", position, modifier.Name, addMessage);
        }

        Core.Scheduler.DelayBySeconds(holdSeconds, () =>
        {
            if (runId != _testAllRunId)
            {
                return;
            }

            Core.Logger.LogInformation("[CSRoll] TESTALL {Position}: turning OFF {Name}", position, modifier.Name);
            if (Core.PlayerManager.GetPlayer(slot) is { IsValid: true } current)
            {
                CSRollUtils.PrintTitleToChat(Core, current, $"[{position}] Turning OFF {modifier.Name}");
            }

            Runtime.RevokeModifierFromSlot(modifier, slot);

            // A gap before the next one so a modifier's teardown and the next one's setup never
            // land on the same tick - several of these strip weapons or spawn entities on both.
            Core.Scheduler.DelayBySeconds(TestAllGapSeconds, () => StepTestAll(slot, index + 1, holdSeconds, runId));
        });
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

    public void OnRollMode(ICommandContext context)
    {
        var current = Runtime.RollMode;
        if (context.Args.Length == 0)
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, $"Roll mode: {current} - {DescribeRollMode(current)}. Change it with !rollmode player, team or game.");
            return;
        }

        if (!CSRollConfig.TryParseRollMode(context.Args[0], out var mode))
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, $"Unknown roll mode \"{context.Args[0]}\" - use player, team or game.");
            return;
        }

        if (mode == current)
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, $"Roll mode is already {mode}.");
            return;
        }

        // Before the save: it reloads the config, which would otherwise put an old csr_rollmode override back.
        _overriddenConVars.Remove("csr_rollmode");
        var saved = SaveRollMode(mode, out var error);
        if (!saved)
        {
            // Couldn't write the file - still switch for now, and say so.
            Config.RollMode = mode.ToString();
            Config.RandomizePlayers = null;
            Core.Logger.LogWarning("[CSRoll] !rollmode couldn't save to config.jsonc: {Error}", error);
        }

        SyncConVar("csr_rollmode");
        AnnounceRollMode(mode);
        if (!saved)
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, $"Couldn't save it to config.jsonc ({error}) - it lasts until the config reloads.");
        }
    }

    /// <summary>
    /// Runs the real weighted picker many times over the per-player pool and prints each tier's share,
    /// so the rarity weights can be checked without playing a few hundred rounds. Single picks only -
    /// cooldowns and incompatibilities, which shift a real roll a little, aren't simulated.
    /// </summary>
    public void OnRollSim(ICommandContext context)
    {
        var rolls = context.Args.Length > 0 && int.TryParse(context.Args[0], out var requested) ? Math.Clamp(requested, 100, 100_000) : 10_000;
        var pool = Runtime.RegisteredModifiers.Where(m => m.SupportsPerPlayerRandomization).ToList();
        if (pool.Count == 0)
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, "No modifiers are registered to simulate.");
            return;
        }

        var rarity = Config.Rarity;
        var counts = ModifierRarity.Tiers.ToDictionary(tier => tier, _ => 0);
        var random = new Random();
        for (var i = 0; i < rolls; i++)
        {
            counts[ModifierRarity.Resolve(ModifierRarity.PickWeighted(pool, rarity, random)!, rarity)]++;
        }

        CSRollUtils.PrintTitleToChat(Core, context.Sender, $"{rolls} simulated picks{(rarity.Enabled ? "" : " (rarity is off - uniform)")}:");
        foreach (var tier in ModifierRarity.Tiers.Reverse())
        {
            var members = pool.Count(m => ModifierRarity.Resolve(m, rarity) == tier);
            var line = $"• {ModifierRarity.ChatToken(tier)}{ModifierRarity.Label(tier)}[default]: {100.0 * counts[tier] / rolls:0.0}%  ({members} modifiers, weight {ModifierRarity.Weight(tier, rarity):0.#})";
            if (context.Sender is { } sender)
            {
                sender.SendChat(SwiftlyS2.Shared.Helper.Colored(line));
            }
            else
            {
                Core.Logger.LogInformation("[CSRoll] {Line}", line);
            }
        }
    }

    private static string DescribeRollMode(ModifierRollMode mode) => mode switch
    {
        ModifierRollMode.Team => "each team shares one roll",
        ModifierRollMode.Game => "everyone gets the same roll",
        _ => "every player rolls their own",
    };

    /// <summary>Tells the admins - only them; a settings change is nothing the players need in chat.</summary>
    private void AnnounceRollMode(ModifierRollMode mode) =>
        CSRollUtils.PrintTitleToAdminsOnly(Core, $"Roll mode set to {mode} - {DescribeRollMode(mode)}. Takes effect from the next roll.");

    /// <summary>
    /// Writes RollMode into config.jsonc by editing that one value in the text, so the admin's comments
    /// and formatting survive, then reloads the config so it applies straight away.
    /// </summary>
    private bool SaveRollMode(ModifierRollMode mode, out string error)
    {
        try
        {
            var path = Core.Configuration.GetConfigPath("config.jsonc");
            var text = File.ReadAllText(path);
            var entry = $"\"RollMode\": \"{mode}\"";

            var existing = new Regex("\"RollMode\"\\s*:\\s*\"[^\"]*\"");
            if (existing.IsMatch(text))
            {
                text = existing.Replace(text, entry, 1);
            }
            else if (Regex.Match(text, "\"Main\"\\s*:\\s*\\{") is { Success: true } main)
            {
                // A config written before 1.40 has no RollMode yet.
                text = text.Insert(main.Index + main.Length, $"\n    {entry},");
            }
            else
            {
                error = "no \"Main\" section found";
                return false;
            }

            // An older config's RandomizePlayers: false would turn "Player" back into "Game".
            text = Regex.Replace(text, "\"RandomizePlayers\"\\s*:\\s*false", "\"RandomizePlayers\": true");

            var temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        // Apply now rather than waiting on the file watcher.
        if (Core.Configuration.Manager is IConfigurationRoot configRoot)
        {
            configRoot.Reload();
        }

        ReloadConfigFromManager();
        error = "";
        return true;
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
        SyncConVar("csr_randomrounds");
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

        // Same spin-then-reveal pairing the menu's Re-roll option and the automatic round-start roll
        // use - showBanner:false stashes the roll so PlaySpinThenRevealActiveModifiersBanner can
        // commit it exactly when the animation lands, rather than swapping modifiers in silently.
        //
        // Marshalled to the main thread for the same reason the menu version is: the reveal runs on a
        // Core.Scheduler.DelayBySeconds chain and ultimately calls Activate(), which touches
        // thread-unsafe APIs. Harmless if this command already runs on the main thread - it just
        // starts a tick later.
        Core.Scheduler.NextWorldUpdate(() =>
        {
            Runtime.RemoveAllModifiers();
            Runtime.ApplyRandomRoundsForRound(showBanner: false);
            Runtime.PlaySpinThenRevealActiveModifiersBanner();
        });
    }
}
