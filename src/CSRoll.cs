using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared;

using CSRoll.Config;
using CSRoll.Core;
using CSRoll.Services.Impl;
using CSRoll.Services.Interfaces;

namespace CSRoll;

[PluginMetadata(Id = "CSRoll", Version = CSRoll.PluginVersion, Name = "CSRoll", Author = "lafkis", Description = "Apply game modifiers dynamically based on pre-defined classes or config files.")]
public partial class CSRoll : BasePlugin
{
    // Single source of truth for the version - also referenced in the PluginMetadata attribute
    // above and logged on every load, so the running build is always identifiable in the console.
    private const string PluginVersion = "1.30.5";

    private IServiceProvider _serviceProvider = null!;
    private ICvarRollbackService _cvarService = null!;
    private bool _isLoaded;
    private IDisposable? _configChangeSubscription;

    public CSRollConfig Config { get; private set; } = new();
    public ModifierRuntime Runtime { get; private set; } = null!;

    public CSRoll(ISwiftlyCore core) : base(core)
    {
    }

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager)
    {
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
    }

    public override void Load(bool hotReload)
    {
        // Bug fix: every command and game-event hook in this plugin doubled up on every single
        // action ("Enabled" immediately followed by "Disabled", etc.), even after switching off
        // attribute-based auto-registration and after a genuine full server restart. That rules
        // out stale state surviving a reload - the only way every single registration doubles is
        // if Load() itself runs twice for one logical plugin load (a file-system-watcher firing
        // two change events for a single file write is a common cause of exactly this). Rather
        // than depend on pinning down why the framework might call Load() twice, make Load()
        // idempotent: if it's ever invoked while already loaded, tear down first so there is
        // never more than one live registration for anything, regardless of the cause.
        if (_isLoaded)
        {
            Core.Logger.LogWarning("[CSRoll] Load() called while already loaded - unloading previous registrations first.");
            Unload();
        }

        _isLoaded = true;

        InitializeConfig();

        var services = new ServiceCollection();
        services.AddSwiftly(Core);
        services.AddSingleton(Config);
        services.AddSingleton<ICvarRollbackService, CvarRollbackService>();
        _serviceProvider = services.BuildServiceProvider();

        _cvarService = _serviceProvider.GetRequiredService<ICvarRollbackService>();
        _cvarService.Install();

        Runtime = new ModifierRuntime(Core, Config, _cvarService);
        Runtime.Initialise(BuildModifierFactories());

        InitializeCommands();
        InitializeGameEvents();

        Core.Logger.LogInformation("[CSRoll] Successfully loaded! Version {Version} ({Count} modifiers registered)", PluginVersion, Runtime.RegisteredModifiers.Count);
    }

    public override void Unload()
    {
        _isLoaded = false;
        UninitializeCommands();
        UninitializeGameEvents();
        Runtime?.Unregister();
        _cvarService?.Uninstall();

        // Bug fix: ChangeToken.OnChange's returned IDisposable used to be discarded - Load() is
        // documented to sometimes run twice (see its own comment above) and self-heals by calling
        // Unload() then re-running InitializeConfig(), so each occurrence installed one more
        // permanent config-reload subscription that was never removed. Disposing it here means a
        // fresh Load() always starts from zero subscriptions instead of accumulating one per reload.
        _configChangeSubscription?.Dispose();
        _configChangeSubscription = null;
    }

    private void InitializeConfig()
    {
        Core.Configuration.InitializeJsonWithModel<CSRollConfig>("config.jsonc", "Main")
            .Configure(builder => builder.AddJsonFile("config.jsonc", optional: false, reloadOnChange: true));

        ReloadConfigFromManager();
        _configChangeSubscription = ChangeToken.OnChange(() => Core.Configuration.Manager.GetReloadToken(), ReloadConfigFromManager);
    }

    private void ReloadConfigFromManager()
    {
        var newConfig = Core.Configuration.Manager.GetSection("Main").Get<CSRollConfig>() ?? new CSRollConfig();
        var disabledModifiersChanged = Runtime is not null &&
            !Config.DisabledModifiers.SequenceEqual(newConfig.DisabledModifiers, StringComparer.OrdinalIgnoreCase);

        Config = newConfig;
        CSRollUtils.SetTitlePrefix(newConfig.BannerText);
        if (Runtime is not null)
        {
            Runtime.Config = newConfig;
        }

        if (disabledModifiersChanged)
        {
            Core.Logger.LogWarning("[CSRoll] DisabledModifiers changed - run !reloadmodifiers for this to take effect.");
        }
    }
}
