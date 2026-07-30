using SwiftlyS2.Shared.Natives;

namespace CSRoll.Services.Interfaces;

/// <summary>
/// Re-derivation of CS2-GameModifiers-Plugin's ThirdParty/NavMesh.cs under SwiftlyS2's memory
/// primitives - same raw x86-64 signature-scan/struct-offset technique, same fragility. This is
/// the single most binary-version-locked piece of the whole plugin: the signature bytes and
/// struct offsets are facts about the current CS2 server binary, not about either framework,
/// and must be re-verified against the running server. If the scan fails - or
/// CSRollConfig.EnableNavMeshTeleports is off - <see cref="IsAvailable"/> is false and
/// <see cref="GetRandomPosition"/> always returns null, rather than the plugin crashing. The
/// three dependent modifiers (GameModifierTeleportNavMesh.cs) still register in that case and
/// fall back to a random spawn point instead of a random nav-mesh area.
/// </summary>
public interface INavMeshService
{
    void Install();
    void Uninstall();

    bool IsAvailable { get; }

    /// <summary>Returns a random position on the current map's nav mesh that's reachable from a spawn point, or null if unavailable/no reachable point was found within maxAttempts.</summary>
    Vector? GetRandomPosition(int maxAttempts = 10);
}
