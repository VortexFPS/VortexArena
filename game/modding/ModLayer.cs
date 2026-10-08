using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Godot;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Game.Client;
using VortexArena.Game.Hud;
using VortexArena.Modding;

namespace VortexArena.Game.Modding;

/// <summary>
/// The Godot side of the mod sandbox (planning/specs/modding.md): owns the running
/// <see cref="WasmModSandbox"/>, is its <see cref="IModHost"/>, and draws what the guest asked for.
///
/// The guest's draw commands arrive during <see cref="_Process"/> (inside the sandbox call) and are only
/// RECORDED there; <see cref="_Draw"/> replays them. Godot allows canvas drawing only inside _Draw, and
/// the split also means nothing the guest does can run renderer code while it holds the frame budget.
///
/// Everything in this class that answers the guest is part of the security boundary. Read
/// <see cref="IModHost"/>'s remarks before adding to it.
/// </summary>
public partial class ModLayer : Control, IModHost
{
    /// <summary>Height of the 2D space guests draw in. The width follows the window's aspect ratio.</summary>
    public const float VirtualHeight = 600f;

    // A guest that floods the command buffer costs itself its frame budget; this caps what it can cost
    // the renderer. Past it, commands are dropped for the frame.
    private const int MaxDrawOpsPerFrame = 16384;
    private const int MaxAssets = 4096;

    private enum OpKind : byte { Rect, Pic, Text, SetClip, ResetClip }

    private struct DrawOp
    {
        public OpKind Kind;
        public Rect2 Rect;
        public Color Color;
        public Texture2D? Texture;
        public string? Text;
        public float Size;
    }

    private readonly List<DrawOp> _ops = new(1024);
    private readonly List<Texture2D?> _pictures = new();
    private readonly Dictionary<string, int> _pictureIds = new(StringComparer.Ordinal);
    private WasmModSandbox? _sandbox;
    private CvarService? _cvars;
    private Action<string> _print = _ => { };

    /// <summary>Fills the local player's record; returns false when there is no local player (menu, spectating nothing).</summary>
    public Func<ModLocalPlayerState?>? LocalPlayerProvider { get; set; }
    public Func<int, ModEntityState?>? EntityProvider { get; set; }
    public Func<int>? EntityCountProvider { get; set; }
    public Func<ModMatchState?>? MatchProvider { get; set; }
    /// <summary>Game time in seconds. Falls back to time since start when no match supplies one.</summary>
    public Func<double>? TimeProvider { get; set; }

    public bool IsRunning => _sandbox is { State: ModSandboxState.Running };

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>Registers <c>cl_allow_mods</c> and the developer commands. Called once by the shell.</summary>
    public void Initialize(ConfigInterpreter interp, CvarService cvars, Action<string> print)
    {
        _cvars = cvars;
        _print = print;
        cvars.Register("cl_allow_mods", "0", VortexArena.Common.Services.CvarFlags.Save,
            "allow client mods to run in the WebAssembly sandbox (0 = never load one)");

        interp.RegisterCommand("mod_load", a =>
        {
            if (a.Count < 2) print("usage: mod_load <name>   (loads <userdir>/mods/<name>.wasm)");
            else LoadFromUserDir(a[1]);
        }, "developer: run a mod module from the user directory's mods/ folder");
        interp.RegisterCommand("mod_unload", _ => Unload("unloaded by command"), "stop the running mod");
        interp.RegisterCommand("mod_status", _ => print(Describe()), "show the mod sandbox's state");
    }

    private string Describe()
    {
        if (!WasmModSandbox.IsAvailable) return $"mod sandbox unavailable on this platform: {WasmModSandbox.UnavailableReason}";
        if (_sandbox is null) return "no mod loaded";
        return _sandbox.State == ModSandboxState.Disabled
            ? $"mod '{_sandbox.Name}' disabled: {_sandbox.DisabledReason}"
            : $"mod '{_sandbox.Name}' {_sandbox.State}, {_sandbox.MemoryBytes / 1024} KiB of guest memory, {_ops.Count} draw commands last frame";
    }

    /// <summary>
    /// The developer entry point: a module the player put in their own mods/ folder. Server-pushed mods
    /// (manifest, consent, download) are specified but not built; when they are, they end in the same
    /// <see cref="Start"/> call.
    /// </summary>
    private void LoadFromUserDir(string name)
    {
        if (_cvars is null || _cvars.GetFloat("cl_allow_mods") == 0f)
        {
            _print("mods are off (cl_allow_mods 0)");
            return;
        }
        if (!IsSafeName(name))
        {
            _print("mod_load: the name may contain only letters, digits, '-' and '_'");
            return;
        }
        string path = Path.Combine(UserPaths.BaseDir, "mods", name + ".wasm");
        if (!File.Exists(path))
        {
            _print($"mod_load: {path} does not exist");
            return;
        }
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _print($"mod_load: cannot read {path}: {e.Message}");
            return;
        }
        Start(name, bytes, ModLimits.Default);
    }

    /// <summary>Replaces any running mod with <paramref name="wasm"/>. Never throws; failures are printed.</summary>
    public bool Start(string name, byte[] wasm, ModLimits limits)
    {
        Unload("replaced");
        try
        {
            _sandbox = WasmModSandbox.Load(name, wasm, this, limits);
        }
        catch (ModLoadException e)
        {
            _print($"mod '{name}' refused: {e.Message}");
            return false;
        }

        if (!_sandbox.Init())
        {
            _print($"mod '{name}' failed to start: {_sandbox.DisabledReason}");
            Unload(null);
            return false;
        }
        _print($"mod '{name}' running");
        return true;
    }

    public void Unload(string? reason)
    {
        if (_sandbox is null) return;
        string name = _sandbox.Name;
        _sandbox.Shutdown();
        _sandbox.Dispose();
        _sandbox = null;
        _ops.Clear();
        _pictures.Clear();
        _pictureIds.Clear();
        QueueRedraw();
        if (reason is not null) _print($"mod '{name}' {reason}");
    }

    public override void _ExitTree() => Unload(null);

    public override void _Process(double delta)
    {
        if (_sandbox is null) return;
        using var _scope = FrameProfiler.Scope("mods");

        _ops.Clear();
        if (!_sandbox.Frame((float)delta))
        {
            _print($"mod '{_sandbox.Name}' disabled: {_sandbox.DisabledReason}");
            Unload(null);
            return;
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_ops.Count == 0) return;
        float scale = Size.Y / VirtualHeight;
        DrawSetTransform(Vector2.Zero, 0f, new Vector2(scale, scale));
        Font font = HudPanel.HudFont ?? ThemeDB.FallbackFont;

        // Canvas drawing has no immediate-mode scissor, so the guest's clip rectangle is applied here:
        // exactly for rectangles and pictures (the picture's source region is cut to match), and for
        // text by dropping a string whose origin lies outside the clip. A string that starts inside and
        // runs past the edge is not cut - the one approximation in this replay.
        Rect2? clip = null;
        foreach (DrawOp op in _ops)
        {
            switch (op.Kind)
            {
                case OpKind.SetClip: clip = op.Rect; break;
                case OpKind.ResetClip: clip = null; break;
                case OpKind.Rect:
                {
                    Rect2 rect = clip is { } c ? op.Rect.Intersection(c) : op.Rect;
                    if (rect.HasArea()) DrawRect(rect, op.Color);
                    break;
                }
                case OpKind.Pic when op.Texture is not null:
                {
                    Rect2 rect = clip is { } c ? op.Rect.Intersection(c) : op.Rect;
                    if (!rect.HasArea()) break;
                    Vector2 texSize = op.Texture.GetSize();
                    Rect2 source = new(
                        (rect.Position - op.Rect.Position) / op.Rect.Size * texSize,
                        rect.Size / op.Rect.Size * texSize);
                    DrawTextureRectRegion(op.Texture, rect, source, op.Color);
                    break;
                }
                case OpKind.Text when op.Text is not null:
                {
                    if (clip is { } c && !c.HasPoint(op.Rect.Position)) break;
                    int size = Math.Max(1, (int)MathF.Round(op.Size));
                    Vector2 baseline = op.Rect.Position + new Vector2(0f, font.GetAscent(size));
                    DrawString(font, baseline, op.Text, HorizontalAlignment.Left, -1f, size, op.Color);
                    break;
                }
            }
        }
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    // ---- IModCommandSink: record only -----------------------------------------------------------------

    private bool Record(in DrawOp op)
    {
        if (_ops.Count >= MaxDrawOpsPerFrame) return false;
        _ops.Add(op);
        return true;
    }

    private static Color ToColor(uint rgba) =>
        new((rgba >> 24) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);

    // Sizes can be anything finite; a negative or absurd one is clamped rather than handed to the renderer.
    private static Rect2 SaneRect(float x, float y, float w, float h) =>
        new(Math.Clamp(x, -65536f, 65536f), Math.Clamp(y, -65536f, 65536f), Math.Clamp(w, 0f, 65536f), Math.Clamp(h, 0f, 65536f));

    void IModCommandSink.DrawRect(float x, float y, float width, float height, uint rgba) =>
        Record(new DrawOp { Kind = OpKind.Rect, Rect = SaneRect(x, y, width, height), Color = ToColor(rgba) });

    void IModCommandSink.DrawPic(int assetId, float x, float y, float width, float height, uint rgba)
    {
        if (assetId <= 0 || assetId > _pictures.Count) return; // an id this mod was never given
        Record(new DrawOp { Kind = OpKind.Pic, Rect = SaneRect(x, y, width, height), Color = ToColor(rgba), Texture = _pictures[assetId - 1] });
    }

    void IModCommandSink.DrawText(int fontId, float x, float y, float size, uint rgba, ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return;
        // One string per text command per frame. Acceptable for a HUD's worth of labels; if mods start
        // drawing thousands, cache by content here rather than asking guests to draw less.
        Record(new DrawOp
        {
            Kind = OpKind.Text, Rect = SaneRect(x, y, 0f, 0f), Color = ToColor(rgba),
            Text = Encoding.UTF8.GetString(utf8), Size = Math.Clamp(size, 1f, 512f),
        });
    }

    void IModCommandSink.SetClip(float x, float y, float width, float height) =>
        Record(new DrawOp { Kind = OpKind.SetClip, Rect = SaneRect(x, y, width, height) });

    void IModCommandSink.ResetClip() => Record(new DrawOp { Kind = OpKind.ResetClip });

    // Not built: sound ids are never handed out (ResolveAsset returns 0 for them), so there is nothing a
    // guest could legitimately name here yet.
    void IModCommandSink.PlaySound(int assetId, int channel, float volume, float pitch) { }

    // ---- IModHost -------------------------------------------------------------------------------------

    void IModHost.Log(ModLogLevel level, string message) =>
        _print($"[mod{(level == ModLogLevel.Info ? "" : " " + level.ToString().ToLowerInvariant())}] {HudText.Strip(message)}");

    int IModHost.EntityCount => Math.Max(0, EntityCountProvider?.Invoke() ?? 0);

    double IModHost.Time => TimeProvider?.Invoke() ?? Time.GetTicksMsec() / 1000.0;

    int IModHost.ReadState(ModStateKind kind, int index, Span<byte> destination)
    {
        switch (kind)
        {
            case ModStateKind.Screen:
            {
                Vector2 size = Size;
                float aspect = size.Y > 0f ? size.X / size.Y : 4f / 3f;
                return Copy(new ModScreenState { Width = size.X, Height = size.Y, VirtualWidth = VirtualHeight * aspect, VirtualHeight = VirtualHeight }, destination);
            }
            case ModStateKind.LocalPlayer:
                return LocalPlayerProvider?.Invoke() is { } player ? Copy(player, destination) : -1;
            case ModStateKind.Entity:
                return index >= 0 && EntityProvider?.Invoke(index) is { } entity ? Copy(entity, destination) : -1;
            case ModStateKind.Match:
                return MatchProvider?.Invoke() is { } match ? Copy(match, destination) : -1;
            default:
                return -1;
        }
    }

    private static int Copy<T>(T record, Span<byte> destination) where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref record, 1));
        int count = Math.Min(bytes.Length, destination.Length);
        bytes[..count].CopyTo(destination);
        return count;
    }

    /// <summary>
    /// The cvars a mod may read: presentation settings only. An allow-list, because the store also holds
    /// things no downloaded code should see (rcon_password, the player's saved server passwords).
    /// </summary>
    bool IModHost.TryGetCvar(string name, out string value)
    {
        value = "";
        if (_cvars is null || !IsReadableCvar(name) || !_cvars.Has(name)) return false;
        value = _cvars.GetString(name);
        return true;
    }

    private static bool IsReadableCvar(string name) =>
        name.StartsWith("hud_", StringComparison.Ordinal)
        || name.StartsWith("crosshair", StringComparison.Ordinal)
        || name is "fov" or "vid_conwidth" or "vid_conheight" or "cl_allow_mods";

    int IModHost.ResolveAsset(ModAssetKind kind, string path)
    {
        if (kind != ModAssetKind.Picture || !IsSafeAssetPath(path)) return 0;
        if (_pictureIds.TryGetValue(path, out int id)) return id;
        if (_pictures.Count >= MaxAssets) return 0;

        // TODO(MS-6): resolve inside the mod's own packs only. Until the virtual filesystem has per-mod
        // scopes this searches all mounted game data, which is every file the game itself may draw.
        Texture2D? texture = TextureCache.Get(path);
        if (texture is null) return 0;
        _pictures.Add(texture);
        return _pictureIds[path] = _pictures.Count;
    }

    float IModHost.MeasureText(int fontId, float size, string text)
    {
        Font font = HudPanel.HudFont ?? ThemeDB.FallbackFont;
        return font.GetStringSize(text, HorizontalAlignment.Left, -1f, Math.Clamp((int)MathF.Round(size), 1, 512)).X;
    }

    // Not built: there is no mod channel in the native protocol yet (specs/modding.md section 9).
    bool IModHost.SendToServer(ReadOnlySpan<byte> payload) => false;

    // ---- validation of names that come from outside ---------------------------------------------------

    private static bool IsSafeName(string name)
    {
        if (name.Length is 0 or > 64) return false;
        foreach (char c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        return true;
    }

    /// <summary>
    /// A virtual-filesystem path and nothing else. <see cref="TextureCache.Get"/> treats res://, user://,
    /// a leading slash and a drive letter as real filesystem paths, so every one of those must be refused
    /// here or a guest could name a file on the player's disk.
    /// </summary>
    private static bool IsSafeAssetPath(string path)
    {
        if (path.Length is 0 or > 200 || path[0] is '/' or '.') return false;
        foreach (char c in path)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.')) return false;
        return !path.Contains("..", StringComparison.Ordinal) && !path.Contains("//", StringComparison.Ordinal);
    }
}
