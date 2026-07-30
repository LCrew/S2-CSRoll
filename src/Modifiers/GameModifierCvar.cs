using SwiftlyS2.Shared.Events;

using CSRoll.Services.Interfaces;

namespace CSRoll.Modifiers;

/// <summary>
/// A modifier defined entirely by a ConVarModifiers/*.cfg file - no hardcoded C# behavior.
/// One instance is created per file by <see cref="Core.ModifierRuntime.InitialiseCvarModifiers"/>.
/// </summary>
public sealed class GameModifierCvar : GameModifierBase
{
    private readonly ICvarConfigHandle _handle;

    public GameModifierCvar(ICvarConfigHandle handle)
    {
        _handle = handle;
        Name = handle.ModifierName ?? "Unnamed";
        Description = handle.ModifierDescription ?? "";
        SupportsRandomRounds = handle.SupportsRandomRounds;
        IncompatibleModifiers = new HashSet<string>(handle.IncompatibleModifiers, StringComparer.OrdinalIgnoreCase);
        IsRegistered = !string.Equals(Name, "Unnamed", StringComparison.Ordinal);
    }

    protected override void OnRegistered()
    {
        Core.Event.OnClientConnected += OnClientConnected;
        Core.Event.OnClientDisconnected += OnClientDisconnected;
    }

    protected override void OnUnregistered()
    {
        Core.Event.OnClientConnected -= OnClientConnected;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
    }

    protected override void OnEnabled()
    {
        _handle.Apply();
    }

    protected override void OnDisabled()
    {
        _handle.Remove();
    }

    private void OnClientConnected(IOnClientConnectedEvent @event)
    {
        if (!IsActive)
        {
            return;
        }

        var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (player is { IsValid: true })
        {
            _handle.ApplyClientConfig(player);
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        // The player is already leaving - just discard our bookkeeping for their slot,
        // don't attempt any network calls against them.
        _handle.ClearClientState(@event.PlayerId);
    }
}
