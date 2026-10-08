using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Convars;
using SwiftlyS2.Shared.Events;

using CSRoll.Config;
using CSRoll.Core;

namespace CSRoll;

/// <summary>
/// Server convars for the general settings, so they can be set from the server console, RCON and
/// .cfg files (server.cfg runs on every map load):
///
///   csr_randomrounds 0/1      random rounds off / on
///   csr_rollmode 1/2/3        each player rolls / one set per team / one set for everyone
///   csr_minrounds, csr_maxrounds   modifiers per roll
///   csr_rarity 0/1            rarity tiers off / on
///   csr_hud 0/1               custom HUD off / on
///   csr_hud_mode 0/1          opt-in (!hud) / everyone
///
/// config.jsonc stays the source of the starting values and nothing is written back to it: a value
/// set from the console or a .cfg overrides the config, and keeps overriding it through config
/// reloads, until the plugin reloads. Commands and the menu that change one of these settings
/// (!rollmode, !randomrounds, !rollmenu) mirror their change into the convar, and their change wins
/// over an earlier convar override.
///
/// A change only counts as an override when it differs from the live value. That is also what keeps
/// the plugin's own mirroring writes from being mistaken for one - whether or not the engine reports
/// them back synchronously.
/// </summary>
public partial class CSRoll
{
    private IConVar<bool>? _cvRandomRounds;
    private IConVar<int>? _cvRollMode;
    private IConVar<int>? _cvMinRounds;
    private IConVar<int>? _cvMaxRounds;
    private IConVar<bool>? _cvRarity;
    private IConVar<bool>? _cvHud;
    private IConVar<int>? _cvHudMode;

    /// <summary>Convars set from outside (console, .cfg) - re-applied over every config reload.</summary>
    private readonly HashSet<string> _overriddenConVars = new(StringComparer.OrdinalIgnoreCase);

    private void InitializeConVars()
    {
        // CreateOrFind: a plugin reload finds the convars still registered from the last load.
        _cvRandomRounds = Core.ConVar.CreateOrFind("csr_randomrounds", "CSRoll: random rounds - 0 off, 1 on.", Runtime.RandomRoundsEnabled, ConvarFlags.NONE);
        _cvRollMode = Core.ConVar.CreateOrFind("csr_rollmode", "CSRoll: how modifiers are handed out - 1 each player rolls their own, 2 one set per team, 3 one set for everyone.", RollModeNumber(Runtime.RollMode), 1, 3, ConvarFlags.NONE);
        _cvMinRounds = Core.ConVar.CreateOrFind("csr_minrounds", "CSRoll: fewest modifiers per roll.", Runtime.MinRandomRounds, 0, 10, ConvarFlags.NONE);
        _cvMaxRounds = Core.ConVar.CreateOrFind("csr_maxrounds", "CSRoll: most modifiers per roll.", Runtime.MaxRandomRounds, 0, 10, ConvarFlags.NONE);
        _cvRarity = Core.ConVar.CreateOrFind("csr_rarity", "CSRoll: rarity tiers - 0 off (every modifier equally likely), 1 on.", Config.Rarity.Enabled, ConvarFlags.NONE);
        _cvHud = Core.ConVar.CreateOrFind("csr_hud", "CSRoll: custom HUD - 0 off (centre text for everyone), 1 on.", Config.CustomHud.Enabled, ConvarFlags.NONE);
        _cvHudMode = Core.ConVar.CreateOrFind("csr_hud_mode", "CSRoll: custom HUD mode - 0 opt-in with !hud, 1 everyone.", HudModeNumber(Config.CustomHud), 0, 1, ConvarFlags.NONE);

        // A load starts from config.jsonc; server.cfg re-applies any overrides on the next map.
        _overriddenConVars.Clear();
        MirrorConVars();

        Core.Event.OnConVarValueChanged += OnConVarValueChanged;
    }

    private void UninitializeConVars()
    {
        Core.Event.OnConVarValueChanged -= OnConVarValueChanged;
    }

    private void OnConVarValueChanged(IOnConVarValueChanged @event)
    {
        if (!@event.ConVarName.StartsWith("csr_", StringComparison.OrdinalIgnoreCase) || Runtime is null)
        {
            return;
        }

        if (ApplyConVar(@event.ConVarName, announce: true))
        {
            _overriddenConVars.Add(@event.ConVarName);
        }
    }

    /// <summary>
    /// Puts one convar's value into effect. Returns false when it already matches the live value -
    /// nothing to do, and not an override (this is what the plugin's own mirroring writes look like).
    /// </summary>
    private bool ApplyConVar(string name, bool announce)
    {
        switch (name.ToLowerInvariant())
        {
            case "csr_randomrounds" when _cvRandomRounds is not null:
            {
                var enabled = _cvRandomRounds.Value;
                if (enabled == Runtime.RandomRoundsEnabled)
                {
                    return false;
                }

                if (enabled && Runtime.RegisteredModifiers.Count == 0)
                {
                    Core.Logger.LogWarning("[CSRoll] csr_randomrounds 1: no modifiers are registered - random rounds stay off.");
                    return false;
                }

                // Through the toggle, so the change is announced like !randomrounds announces it.
                Runtime.ToggleRandomRounds();
                return true;
            }

            case "csr_rollmode" when _cvRollMode is not null:
            {
                var mode = RollModeFromNumber(_cvRollMode.Value);
                if (mode == Runtime.RollMode)
                {
                    return false;
                }

                Config.RollMode = mode.ToString();
                Config.RandomizePlayers = null;
                if (announce)
                {
                    AnnounceRollMode(mode);
                }

                return true;
            }

            case "csr_minrounds" when _cvMinRounds is not null:
                if (_cvMinRounds.Value == Runtime.MinRandomRounds)
                {
                    return false;
                }

                Runtime.MinRandomRounds = _cvMinRounds.Value;
                return true;

            case "csr_maxrounds" when _cvMaxRounds is not null:
                if (_cvMaxRounds.Value == Runtime.MaxRandomRounds)
                {
                    return false;
                }

                Runtime.MaxRandomRounds = _cvMaxRounds.Value;
                return true;

            case "csr_rarity" when _cvRarity is not null:
                if (_cvRarity.Value == Config.Rarity.Enabled)
                {
                    return false;
                }

                Config.Rarity.Enabled = _cvRarity.Value;
                return true;

            case "csr_hud" when _cvHud is not null:
                if (_cvHud.Value == Config.CustomHud.Enabled)
                {
                    return false;
                }

                Config.CustomHud.Enabled = _cvHud.Value;
                return true;

            case "csr_hud_mode" when _cvHudMode is not null:
                if (_cvHudMode.Value == HudModeNumber(Config.CustomHud))
                {
                    return false;
                }

                Config.CustomHud.Mode = _cvHudMode.Value == 1 ? "Everyone" : "OptIn";
                return true;

            default:
                return false;
        }
    }

    /// <summary>After a config reload: overridden convars are put back into effect on top of the new config, the rest follow it.</summary>
    private void ReapplyConVarOverrides()
    {
        if (_cvRandomRounds is null || Runtime is null)
        {
            return;
        }

        // Quietly - this runs on every config reload, and the override is old news.
        foreach (var name in _overriddenConVars)
        {
            ApplyConVar(name, announce: false);
        }

        MirrorConVars();
    }

    /// <summary>A command or the menu changed one of these settings: the convar follows, and the change outranks an earlier convar override.</summary>
    private void SyncConVar(string name)
    {
        _overriddenConVars.Remove(name);
        MirrorConVars();
    }

    /// <summary>Sets every convar to the live value. Never counted as an override - see ApplyConVar.</summary>
    private void MirrorConVars()
    {
        if (Runtime is null)
        {
            return;
        }

        SetIfDifferent(_cvRandomRounds, Runtime.RandomRoundsEnabled);
        SetIfDifferent(_cvRollMode, RollModeNumber(Runtime.RollMode));
        SetIfDifferent(_cvMinRounds, Runtime.MinRandomRounds);
        SetIfDifferent(_cvMaxRounds, Runtime.MaxRandomRounds);
        SetIfDifferent(_cvRarity, Config.Rarity.Enabled);
        SetIfDifferent(_cvHud, Config.CustomHud.Enabled);
        SetIfDifferent(_cvHudMode, HudModeNumber(Config.CustomHud));
    }

    private static void SetIfDifferent<T>(IConVar<T>? convar, T value)
    {
        if (convar is not null && !EqualityComparer<T>.Default.Equals(convar.Value, value))
        {
            convar.Value = value;
        }
    }

    private static int RollModeNumber(ModifierRollMode mode) => mode switch
    {
        ModifierRollMode.Team => 2,
        ModifierRollMode.Game => 3,
        _ => 1,
    };

    private static ModifierRollMode RollModeFromNumber(int number) => number switch
    {
        2 => ModifierRollMode.Team,
        3 => ModifierRollMode.Game,
        _ => ModifierRollMode.Player,
    };

    private static int HudModeNumber(CustomHudConfig hud) =>
        string.Equals(hud.Mode, "Everyone", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
}
