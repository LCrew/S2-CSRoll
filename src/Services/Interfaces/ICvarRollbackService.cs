using SwiftlyS2.Shared.Players;

namespace CSRoll.Services.Interfaces;

/// <summary>
/// A single parsed .cfg file's worth of server/client cvar changes, with rollback support.
/// </summary>
public interface ICvarConfigHandle
{
    /// <summary>Populated only when parsed via <see cref="ICvarRollbackService.ParseCvarModifierFile"/> (ConVarModifiers/*.cfg metadata).</summary>
    string? ModifierName { get; }
    string? ModifierDescription { get; }
    bool SupportsRandomRounds { get; }
    IReadOnlyCollection<string> IncompatibleModifiers { get; }

    /// <summary>Applies every server cvar line, then applies client cvars to every currently connected player.</summary>
    void Apply();

    /// <summary>Rolls back every connected client's cvars, then rolls back server cvars in reverse order.</summary>
    void Remove();

    /// <summary>Applies this handle's client cvar lines to a single player (e.g. one who just connected while the modifier is active).</summary>
    void ApplyClientConfig(IPlayer player);

    /// <summary>Rolls back this handle's client cvar lines for a single player.</summary>
    void RemoveClientConfig(IPlayer player);

    /// <summary>Discards a disconnecting player's rollback bookkeeping without attempting any network calls against them.</summary>
    void ClearClientState(int slot);
}

/// <summary>
/// Parses and applies the plugin's two `.cfg`-driven cvar systems:
/// ConVarModifiers/*.cfg (fully declarative modifiers) and ModifierConfig/&lt;Name&gt;.cfg
/// (supplemental cvars bolted onto a hardcoded <see cref="CSRoll.Modifiers.GameModifierBase"/>).
/// Owns all cvar rollback state so it can be cleared as a unit on plugin unload.
/// </summary>
public interface ICvarRollbackService
{
    void Install();
    void Uninstall();

    /// <summary>Looks up ModifierConfig/&lt;modifierName&gt;.cfg for a hardcoded modifier. Returns null if no matching file exists.</summary>
    ICvarConfigHandle? TryLoadBoltOnConfig(string modifierName);

    /// <summary>Parses a single ConVarModifiers/*.cfg file, including its modifier_name/description/etc. metadata.</summary>
    ICvarConfigHandle ParseCvarModifierFile(string filePath);

    /// <summary>Finds every ConVarModifiers/*.cfg file under the plugin's bundled and data directories.</summary>
    IReadOnlyList<string> FindCvarModifierFiles();
}
