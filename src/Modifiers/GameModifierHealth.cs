using System.Collections.Concurrent;

using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>Sets every player's max health to a fixed or per-spawn-random value.</summary>
public abstract class GameModifierHealth : GameModifierBase
{
    // Keyed by Slot (not SteamID): bots share SteamID 0, so slot is the only safe key
    // for a cache that must cover bots too (matches the original's all-players scope).
    private readonly ConcurrentDictionary<int, int> _cachedOriginalMaxHealth = new();
    private Guid _spawnHookId;

    protected abstract int GetHealthValue();

    protected override void OnRegistered()
    {
        Core.Event.OnClientDisconnected += OnClientDisconnected;
    }

    protected override void OnUnregistered()
    {
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
    }

    protected override void OnEnabled()
    {
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot))
            {
                ApplyHealth(player);
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_spawnHookId);

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot))
            {
                ResetHealth(player);
            }
        }
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            ApplyHealth(player);
        }

        return HookResult.Continue;
    }

    private void ApplyHealth(IPlayer player)
    {
        if (player.PlayerPawn is not { } pawn)
        {
            return;
        }

        _cachedOriginalMaxHealth[player.Slot] = pawn.MaxHealth;

        var health = GetHealthValue();
        pawn.MaxHealth = health;
        pawn.MaxHealthUpdated();
        pawn.Health = health;
        pawn.HealthUpdated();
    }

    private void ResetHealth(IPlayer player)
    {
        if (player.PlayerPawn is not { } pawn)
        {
            return;
        }

        var originalMaxHealth = _cachedOriginalMaxHealth.TryGetValue(player.Slot, out var cached) ? cached : 100;
        pawn.MaxHealth = originalMaxHealth;
        pawn.MaxHealthUpdated();
        pawn.Health = Math.Min(pawn.Health, originalMaxHealth);
        pawn.HealthUpdated();
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        // Bug fix vs. the CSS original: unconditional TryRemove, no inverted ContainsKey guard.
        _cachedOriginalMaxHealth.TryRemove(@event.PlayerId, out _);
    }
}

public sealed class GameModifierJuggernaut : GameModifierHealth
{
    public GameModifierJuggernaut()
    {
        Name = "Juggernaut";
        Description = "Everyone's max health is set to 500";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["RandomHealth"];
    }

    protected override int GetHealthValue() => 500;
}

public sealed class GameModifierRandomHealth : GameModifierHealth
{
    public GameModifierRandomHealth()
    {
        Name = "RandomHealth";
        Description = "Everyone's health is set to a random number";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["Juggernaut"];
    }

    protected override int GetHealthValue() => Random.Shared.Next(1, 101);
}
