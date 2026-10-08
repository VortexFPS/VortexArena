// The C# guest library for the Vortex mod interface, version 1. Reference: ../../ABI.md.
// This code runs INSIDE the sandbox, compiled to WebAssembly by NativeAOT-LLVM. It is not part of the
// game's own build (the host project excludes modding-sdk/ from compilation).
using System.Runtime.InteropServices;
using System.Text;

namespace Vortex.Modding;

/// <summary>The raw imports. Prefer <see cref="Mod"/> and <see cref="Draw"/>.</summary>
public static unsafe partial class Native
{
    private const string Module = "vortex_1";

    [DllImport(Module, EntryPoint = "log"), WasmImportLinkage]
    public static extern void Log(int level, byte* ptr, int len);

    [DllImport(Module, EntryPoint = "commands"), WasmImportLinkage]
    public static extern void Commands(byte* ptr, int len);

    [DllImport(Module, EntryPoint = "state_read"), WasmImportLinkage]
    public static extern int StateRead(int kind, int index, void* ptr, int capacity);

    [DllImport(Module, EntryPoint = "entity_count"), WasmImportLinkage]
    public static extern int EntityCount();

    [DllImport(Module, EntryPoint = "cvar_get"), WasmImportLinkage]
    public static extern int CvarGet(byte* name, int nameLen, byte* output, int capacity);

    [DllImport(Module, EntryPoint = "asset_id"), WasmImportLinkage]
    public static extern int AssetId(int kind, byte* ptr, int len);

    [DllImport(Module, EntryPoint = "text_width"), WasmImportLinkage]
    public static extern float TextWidth(int font, float size, byte* ptr, int len);

    [DllImport(Module, EntryPoint = "time_now"), WasmImportLinkage]
    public static extern double TimeNow();

    [DllImport(Module, EntryPoint = "send_to_server"), WasmImportLinkage]
    public static extern int SendToServer(byte* ptr, int len);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ScreenState
{
    public float Width, Height, VirtualWidth, VirtualHeight;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LocalPlayerState
{
    public float OriginX, OriginY, OriginZ;
    public float VelocityX, VelocityY, VelocityZ;
    public float Pitch, Yaw, Roll;
    public float Health, Armor;
    public int Team, Flags, EntityIndex;

    /// <summary>Horizontal speed in Quake units per second - what a speedometer shows.</summary>
    public readonly float Speed => MathF.Sqrt(VelocityX * VelocityX + VelocityY * VelocityY);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct EntityState
{
    public int EntityIndex, ModelId;
    public float OriginX, OriginY, OriginZ;
    public float Pitch, Yaw, Roll;
    public int Team, Frame, Flags;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MatchState
{
    public float Time, TimeLimit;
    public int ScoreLimit, PlayerCount, Flags;
}

public enum AssetKind { Picture = 1, Sound = 2, Font = 3 }

public static class Color
{
    public const uint White = 0xFFFFFFFF;
    public const uint Black = 0x000000FF;
    public static uint Rgba(byte r, byte g, byte b, byte a = 255) => (uint)(r << 24 | g << 16 | b << 8 | a);
}

public static class Fonts
{
    /// <summary>Font id 0 is the client's default HUD font.</summary>
    public const int Default = 0;
}

/// <summary>State, logging, assets and messaging.</summary>
public static unsafe class Mod
{
    public static ScreenState Screen => Read<ScreenState>(1, 0, out _);
    public static LocalPlayerState LocalPlayer => Read<LocalPlayerState>(2, 0, out _);
    public static MatchState Match => Read<MatchState>(4, 0, out _);
    public static int EntityCount => Native.EntityCount();
    public static double Time => Native.TimeNow();

    public static bool TryGetEntity(int index, out EntityState entity)
    {
        entity = Read<EntityState>(3, index, out int written);
        return written > 0;
    }

    private static T Read<T>(int kind, int index, out int written) where T : unmanaged
    {
        T value = default;
        written = Native.StateRead(kind, index, &value, sizeof(T));
        return value;
    }

    public static void Log(string message, int level = 0)
    {
        // Stack space for short lines; anything longer than the host's 4096-byte string limit would
        // disable the mod, so it is truncated here instead.
        Span<byte> utf8 = stackalloc byte[1024];
        int length = Encode(message, utf8);
        fixed (byte* p = utf8) Native.Log(level, p, length);
    }

    /// <summary>Resolves a path inside this mod's packs. Call once at start-up and keep the id.</summary>
    public static int Asset(AssetKind kind, string path)
    {
        Span<byte> utf8 = stackalloc byte[512];
        int length = Encode(path, utf8);
        fixed (byte* p = utf8) return Native.AssetId((int)kind, p, length);
    }

    public static bool TryGetCvar(string name, out string value)
    {
        Span<byte> nameUtf8 = stackalloc byte[256];
        Span<byte> output = stackalloc byte[1024];
        int nameLength = Encode(name, nameUtf8);
        int length;
        fixed (byte* n = nameUtf8)
        fixed (byte* o = output)
            length = Native.CvarGet(n, nameLength, o, output.Length);
        value = length < 0 ? "" : Encoding.UTF8.GetString(output[..Math.Min(length, output.Length)]);
        return length >= 0;
    }

    public static bool SendToServer(ReadOnlySpan<byte> payload)
    {
        fixed (byte* p = payload) return Native.SendToServer(p, payload.Length) != 0;
    }

    internal static int Encode(ReadOnlySpan<char> text, Span<byte> destination)
    {
        // Encode as much as fits. GetBytes throws when the destination is too small, so trim the input
        // to a length that cannot overflow it (UTF-8 is at most 3 bytes per UTF-16 unit).
        int maxChars = destination.Length / 3;
        if (text.Length > maxChars) text = text[..maxChars];
        return Encoding.UTF8.GetBytes(text, destination);
    }

    /// <summary>
    /// The host calls this to obtain a buffer for an event payload. Export it from your mod if you handle
    /// <c>mod_event</c>: <c>[UnmanagedCallersOnly(EntryPoint = "mod_alloc")] static int Alloc(int n) => Mod.Alloc(n);</c>
    /// </summary>
    public static int Alloc(int size) => (int)(nint)NativeMemory.Alloc((nuint)Math.Max(size, 1));

    public static void Free(int pointer) => NativeMemory.Free((void*)(nint)pointer);
}

/// <summary>
/// The per-frame command buffer. Calls here only write into this module's own memory;
/// <see cref="Flush"/> is the single call that crosses the sandbox boundary.
/// </summary>
public static unsafe class Draw
{
    // Native memory rather than a managed array: the pointer handed to the host must not move, and
    // nothing here should give the garbage collector work during a frame.
    private static byte* s_buffer;
    private static int s_capacity;
    private static int s_length;

    public static void Rect(float x, float y, float w, float h, uint rgba)
    {
        byte* p = Begin(1, 20);
        *(float*)p = x; *(float*)(p + 4) = y; *(float*)(p + 8) = w; *(float*)(p + 12) = h; *(uint*)(p + 16) = rgba;
    }

    public static void Pic(int assetId, float x, float y, float w, float h, uint rgba = Color.White)
    {
        byte* p = Begin(2, 24);
        *(int*)p = assetId; *(float*)(p + 4) = x; *(float*)(p + 8) = y; *(float*)(p + 12) = w; *(float*)(p + 16) = h; *(uint*)(p + 20) = rgba;
    }

    public static void Text(int fontId, float x, float y, float size, uint rgba, ReadOnlySpan<char> text)
    {
        Span<byte> utf8 = stackalloc byte[1536];
        int length = Mod.Encode(text, utf8);
        int padded = (length + 3) & ~3;
        byte* p = Begin(3, 24 + padded);
        *(int*)p = fontId; *(float*)(p + 4) = x; *(float*)(p + 8) = y; *(float*)(p + 12) = size; *(uint*)(p + 16) = rgba; *(uint*)(p + 20) = (uint)length;
        utf8[..length].CopyTo(new Span<byte>(p + 24, length));
        new Span<byte>(p + 24 + length, padded - length).Clear();
    }

    public static void SetClip(float x, float y, float w, float h)
    {
        byte* p = Begin(4, 16);
        *(float*)p = x; *(float*)(p + 4) = y; *(float*)(p + 8) = w; *(float*)(p + 12) = h;
    }

    public static void ResetClip() => Begin(5, 0);

    public static void Sound(int assetId, int channel = 0, float volume = 1f, float pitch = 1f)
    {
        byte* p = Begin(6, 16);
        *(int*)p = assetId; *(int*)(p + 4) = channel; *(float*)(p + 8) = volume; *(float*)(p + 12) = pitch;
    }

    /// <summary>Sends everything written since the last flush to the host. Call once at the end of <c>mod_frame</c>.</summary>
    public static void Flush()
    {
        if (s_length > 0) Native.Commands(s_buffer, s_length);
        s_length = 0;
    }

    private static byte* Begin(ushort opcode, int payload)
    {
        int size = 4 + payload;
        if (s_length + size > s_capacity)
        {
            s_capacity = Math.Max(Math.Max(s_capacity * 2, 64 * 1024), s_length + size);
            s_buffer = (byte*)NativeMemory.Realloc(s_buffer, (nuint)s_capacity);
        }
        byte* record = s_buffer + s_length;
        *(ushort*)record = opcode;
        *(ushort*)(record + 2) = (ushort)size;
        s_length += size;
        return record + 4;
    }
}
