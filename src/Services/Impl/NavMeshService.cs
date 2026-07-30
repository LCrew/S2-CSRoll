using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Memory;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;

using CSRoll.Config;
using CSRoll.Core;
using CSRoll.Services.Interfaces;

namespace CSRoll.Services.Impl;

/// <summary>
/// See INavMeshService's doc comment - this is a direct re-derivation of the original CS2
/// plugin's ThirdParty/NavMesh.cs signature bytes and struct offsets under SwiftlyS2's
/// Core.Memory/Core.GameData APIs instead of CounterStrikeSharp's MemoryFunctionWithReturn.
/// The bytes/offsets themselves are unchanged - they describe the CS2 server binary, not either
/// framework - and must be re-verified against whatever CS2 build actually runs this.
/// </summary>
public sealed unsafe class NavMeshService : INavMeshService
{
    // "55 48 89 E5 41 57 41 56 41 55 49 89 CD 41 54 49 89 D4 53 48 89 FB 48 8D 3D" - NavAreaBuildPath.
    // Find by searching for the symbol directly, or via any of the nav_pathfind_* convars.
    private const string NavAreaBuildPathSignature = "55 48 89 E5 41 57 41 56 41 55 49 89 CD 41 54 49 89 D4 53 48 89 FB 48 8D 3D";

    // "0F B6 05 ? ? ? 01 84 C0 74 1D 80 3D ? ? ? 01 00 74 07 C6 05 ? ? ? 01 00" - NavPathCost.
    // Find via nav_avoid_obstacles convar usage, or the 5th argument to a NavAreaBuildPath call.
    private const string NavPathCostSignature = "0F B6 05 ? ? ? 01 84 C0 74 1D 80 3D ? ? ? 01 00 74 07 C6 05 ? ? ? 01 00";

    // "48 8D 05 ? ? ? ? 48 83 38 00 0F 95 C0" - a `lea rax, [rip+rel32]` loading the address of
    // the CNavMesh singleton pointer slot (not a callable function - only its address matters).
    private const string IsValidNavMeshSignature = "48 8D 05 ? ? ? ? 48 83 38 00 0F 95 C0";

    private delegate nint NavAreaBuildPathDelegate(nint startArea, nint goalArea, nint startPos, nint goalPos, nint pathCost, nint scratch, float f1, float f2, nint distanceOut);
    private delegate nint NavPathCostDelegate();

    private readonly ISwiftlyCore _core;
    private readonly CSRollConfig _config;

    private IUnmanagedFunction<NavAreaBuildPathDelegate>? _navAreaBuildPath;
    private IUnmanagedFunction<NavPathCostDelegate>? _navPathCost;
    private nint _navMeshPtrAddress;

    public bool IsAvailable { get; private set; }

    public NavMeshService(ISwiftlyCore core, CSRollConfig config)
    {
        _core = core;
        _config = config;
    }

    public void Install()
    {
        if (!_config.EnableNavMeshTeleports)
        {
            _core.Logger.LogInformation("[CSRoll] NavMesh teleports disabled via config (EnableNavMeshTeleports=false) - RandomSpawn/TeleportOnReload/TeleportOnHit will not register.");
            IsAvailable = false;
            return;
        }

        try
        {
            var buildPathAddress = _core.Memory.GetAddressBySignature(Library.Server, NavAreaBuildPathSignature);
            var pathCostAddress = _core.Memory.GetAddressBySignature(Library.Server, NavPathCostSignature);
            var isValidNavMeshAddress = _core.Memory.GetAddressBySignature(Library.Server, IsValidNavMeshSignature);

            if (buildPathAddress is null || pathCostAddress is null || isValidNavMeshAddress is null)
            {
                _core.Logger.LogWarning("[CSRoll] NavMesh signature scan failed to find one or more targets - RandomSpawn/TeleportOnReload/TeleportOnHit will not register. This is expected if the CS2 server binary has updated since these signatures were derived.");
                IsAvailable = false;
                return;
            }

            _navAreaBuildPath = _core.Memory.GetUnmanagedFunctionByAddress<NavAreaBuildPathDelegate>(buildPathAddress.Value);
            _navPathCost = _core.Memory.GetUnmanagedFunctionByAddress<NavPathCostDelegate>(pathCostAddress.Value);
            _navMeshPtrAddress = Rel(isValidNavMeshAddress.Value, 3);

            IsAvailable = true;
            _core.Logger.LogInformation("[CSRoll] NavMesh signatures resolved successfully.");
        }
        catch (Exception ex)
        {
            // Catches managed-level failures (e.g. a bad Marshal read). A genuinely corrupted
            // pointer dereference is not guaranteed to be catchable at all - the config flag
            // above is the real safety net, this is a best-effort second layer.
            _core.Logger.LogError(ex, "[CSRoll] NavMesh signature resolution threw - RandomSpawn/TeleportOnReload/TeleportOnHit will not register.");
            IsAvailable = false;
        }
    }

    public void Uninstall()
    {
        IsAvailable = false;
        _navAreaBuildPath = null;
        _navPathCost = null;
        _navMeshPtrAddress = 0;
    }

    public Vector? GetRandomPosition(int maxAttempts = 10)
    {
        if (!IsAvailable)
        {
            return null;
        }

        try
        {
            return GetRandomPositionCore(maxAttempts);
        }
        catch (Exception ex)
        {
            _core.Logger.LogError(ex, "[CSRoll] NavMesh lookup threw at runtime - disabling NavMesh teleports for the rest of this session.");
            IsAvailable = false;
            return null;
        }
    }

    private Vector? GetRandomPositionCore(int maxAttempts)
    {
        var startPosition = CSRollUtils.GetSpawnLocation(_core, Team.T) ?? CSRollUtils.GetSpawnLocation(_core, Team.CT);
        if (startPosition is not { } start)
        {
            return null;
        }

        var navMeshAddress = Marshal.ReadIntPtr(_navMeshPtrAddress);
        if (navMeshAddress == 0)
        {
            return null;
        }

        var areaCount = Marshal.ReadInt32(navMeshAddress + 8);
        if (areaCount <= 0)
        {
            return null;
        }

        var areaArrayPtr = Marshal.ReadIntPtr(navMeshAddress + 16);
        var startArea = GetClosestArea(areaArrayPtr, areaCount, start);
        if (startArea == 0)
        {
            return null;
        }

        for (var i = 0; i < maxAttempts; i++)
        {
            var candidateArea = Marshal.ReadIntPtr(areaArrayPtr + Random.Shared.Next(areaCount) * 8);
            var blockedTeam = Marshal.ReadByte(candidateArea + 92);
            if (blockedTeam != 0)
            {
                continue;
            }

            if (IsAreaAccessible(startArea, candidateArea))
            {
                return ReadVector(candidateArea + 12);
            }
        }

        return null;
    }

    private static nint GetClosestArea(nint areaArrayPtr, int areaCount, Vector position)
    {
        var closestDistance = float.MaxValue;
        nint closest = 0;

        for (var i = 0; i < areaCount; i++)
        {
            var area = Marshal.ReadIntPtr(areaArrayPtr + i * 8);
            var center = ReadVector(area + 12);
            var distance = position.Distance(center);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closest = area;
            }
        }

        return closest;
    }

    private bool IsAreaAccessible(nint startArea, nint goalArea)
    {
        if (_navAreaBuildPath is null || _navPathCost is null)
        {
            return false;
        }

        var pathCost = _navPathCost.Call();
        var scratch = stackalloc byte[32];
        float distance;

        _navAreaBuildPath.Call(startArea, goalArea, 0, 0, pathCost, (nint)scratch, -1.0f, -1.0f, (nint)(&distance));
        return distance >= 0;
    }

    private static Vector ReadVector(nint address)
    {
        var x = ReadFloat(address);
        var y = ReadFloat(address + 4);
        var z = ReadFloat(address + 8);
        return new Vector(x, y, z);
    }

    private static float ReadFloat(nint address)
    {
        var bytes = new byte[4];
        Marshal.Copy(address, bytes, 0, 4);
        return BitConverter.ToSingle(bytes, 0);
    }

    /// <summary>Resolves a `lea reg, [rip+rel32]`-style RIP-relative reference at address+offset to its absolute target.</summary>
    private static nint Rel(nint address, int offset)
    {
        var relativeOffset = Marshal.ReadInt32(address + offset);
        return address + relativeOffset + offset + sizeof(int);
    }
}
