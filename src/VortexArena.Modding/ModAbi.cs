using System.Runtime.InteropServices;

namespace VortexArena.Modding;

/// <summary>
/// The names and numbers of the guest ABI, version 1. This file is the machine-readable half of
/// <c>modding-sdk/ABI.md</c>; the two must change together, and a change that is not purely additive
/// needs a new import module name (<c>vortex_2</c>) rather than an edit here, because a guest built
/// against <c>vortex_1</c> is a file on somebody's server that we cannot recompile.
/// </summary>
public static class ModAbi
{
    /// <summary>Import module every host capability lives under. The version is part of the name.</summary>
    public const string ImportModule = "vortex_1";

    /// <summary>
    /// The only other import module a guest may name. C# guests cannot avoid it: the .NET runtime that
    /// NativeAOT-LLVM links into the module imports a handful of WASI functions for its own startup.
    /// They are answered by <see cref="WasiStubs"/>, which grants nothing.
    /// </summary>
    public const string WasiModule = "wasi_snapshot_preview1";

    public const string ExportMemory = "memory";
    /// <summary>WASI "reactor" initialiser. Present in C# guests; must run before any other export.</summary>
    public const string ExportInitialize = "_initialize";
    public const string ExportInit = "mod_init";
    public const string ExportFrame = "mod_frame";
    public const string ExportEvent = "mod_event";
    public const string ExportAlloc = "mod_alloc";
    public const string ExportShutdown = "mod_shutdown";

    /// <summary>Linear memory page size, fixed by the WebAssembly specification.</summary>
    public const int PageSize = 65536;
}

/// <summary>Opcodes of the per-frame command buffer (see <see cref="ModCommandDecoder"/>).</summary>
public enum ModCommand : ushort
{
    DrawRect = 1,
    DrawPic = 2,
    DrawText = 3,
    SetClip = 4,
    ResetClip = 5,
    PlaySound = 6,
}

/// <summary>What <c>state_read</c> can be asked for. The record layouts are the structs below.</summary>
public enum ModStateKind
{
    Screen = 1,
    LocalPlayer = 2,
    Entity = 3,
    Match = 4,
}

public enum ModLogLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>What an asset id handed to the guest refers to; the namespaces are separate.</summary>
public enum ModAssetKind
{
    Picture = 1,
    Sound = 2,
    Font = 3,
}

// The state records. Little-endian, 4-byte aligned, no padding, no pointers: they are copied into
// guest memory byte for byte, so anything added goes at the END and the guest reads only as many
// bytes as it asked for.

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ModScreenState
{
    public float Width, Height;
    /// <summary>The 2D coordinate space draw commands use (DarkPlaces' vid_conwidth/vid_conheight).</summary>
    public float VirtualWidth, VirtualHeight;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ModLocalPlayerState
{
    public float OriginX, OriginY, OriginZ;
    public float VelocityX, VelocityY, VelocityZ;
    public float Pitch, Yaw, Roll;
    public float Health, Armor;
    public int Team;
    public int Flags;
    public int EntityIndex;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ModEntityState
{
    public int EntityIndex;
    public int ModelId;
    public float OriginX, OriginY, OriginZ;
    public float Pitch, Yaw, Roll;
    public int Team;
    public int Frame;
    public int Flags;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ModMatchState
{
    public float Time;
    public float TimeLimit;
    public int ScoreLimit;
    public int PlayerCount;
    public int Flags;
}
