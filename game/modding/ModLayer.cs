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
///
/// Two ways a mod gets here. The developer one: <c>mod_load</c> runs a module from the player's own
/// mods/ folder. The real one: a server offers a mod over the game connection; the net layer calls
/// <see cref="BeginServerSession"/>, passes mod frames to <see cref="HandleServerFrame"/>, and this
/// class asks the player (<see cref="ModConsentPrompt"/>), then lets <see cref="ModClientSession"/>
/// download, verify, load and run it. Both end in the same recording and replay.
///
/// STATUS 2026-10-08: the server session wiring, the consent prompt, the clip segments
/// (<see cref="ModDrawSegment"/>) and sound were written WITHOUT building the Godot host. They
/// type-check against GodotSharp 4.6.3 in a scratch project with stand-ins for the host classes used
/// here; they have never been built into the host and never run. Nothing in game/net calls the session
/// entry points yet (planning/specs/modding.md, section 9.6).
/// </summary>
public partial class ModLayer : Control, IModHost
{
    /// <summary>Height of the 2D space guests draw in. The width follows the window's aspect ratio.</summary>
    public const float VirtualHeight = 600f;

    // A guest that floods the command buffer costs itself its frame budget; this caps what it can cost
    // the renderer. Past it, commands are dropped for the frame.
    private const int MaxDrawOpsPerFrame = 16384;
    private const int MaxAssets = 4096;

    // A clip rectangle costs a canvas item (see ModDrawSegment). Past this many in one frame the rest of
    // the frame's commands are not drawn - dropped rather than drawn under the wrong clip.
    private const int MaxSegments = 64;
    private const int SoundVoices = 8;
    private const int MaxSoundsPerFrame = 4;

    private readonly List<ModDrawOp> _ops = new(1024);
    private readonly List<ModDrawSegment> _segments = new();
    private readonly List<Texture2D?> _pictures = new();
    private readonly Dictionary<string, int> _pictureIds = new(StringComparer.Ordinal);
    private readonly List<AudioStream?> _sounds = new();
    private readonly Dictionary<string, int> _soundIds = new(StringComparer.Ordinal);
    private readonly List<(int AssetId, int Channel, float Volume, float Pitch)> _pendingSounds = new();
    private readonly AudioStreamPlayer?[] _voices = new AudioStreamPlayer?[SoundVoices];
    private int _nextVoice;
    private WasmModSandbox? _sandbox;
    private CvarService? _cvars;
    private Action<string> _print = _ => { };

    // The server-offered mod, when connected to a server that has one (or had one refused).
    private ModClientSession? _session;
    private Action<byte[]>? _sendToServer;
    private ModCache? _cache;
    private ModConsentStore? _consent;
    private ModConsentPrompt? _prompt;
    private ModConsentRequest? _promptFor;
    private ModClientState _reportedState = ModClientState.Idle;

    /// <summary>
    /// Loads a sound sample by virtual-filesystem path (the game's <c>AssetLoader.LoadSound</c>). Until the
    /// shell sets it, mods cannot resolve sounds and so cannot play any.
    /// </summary>
    public Func<string, AudioStream?>? SoundLoader { get; set; }

    /// <summary>Fills the local player's record; returns false when there is no local player (menu, spectating nothing).</summary>
    public Func<ModLocalPlayerState?>? LocalPlayerProvider { get; set; }
    public Func<int, ModEntityState?>? EntityProvider { get; set; }
    public Func<int>? EntityCountProvider { get; set; }
    public Func<ModMatchState?>? MatchProvider { get; set; }
    /// <summary>Game time in seconds. Falls back to time since start when no match supplies one.</summary>
    public Func<double>? TimeProvider { get; set; }

    public bool IsRunning => (_session?.Sandbox ?? _sandbox) is { State: ModSandboxState.Running };

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    private bool ModsAllowed => _cvars is not null && _cvars.GetFloat("cl_allow_mods") != 0f;

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
        interp.RegisterCommand("mod_unload", _ =>
        {
            // A server's mod is stopped by refusing it for the rest of the connection; mods/ ones are just unloaded.
            if (_session?.Sandbox is not null) _session.StopForConnection();
            Unload("unloaded by command");
        }, "stop the running mod");
        interp.RegisterCommand("mod_status", _ => print(Describe()), "show the mod sandbox's state");

        cvars.Register("cl_mod_download_max_mb", "256", VortexArena.Common.Services.CvarFlags.Save,
            "largest download, in megabytes, accepted for one server-offered mod");
        interp.RegisterCommand("mod_allow", a => Answer(a.Count > 1 ? a[1] : "once", allow: true),
            "answer a server's mod offer with yes: mod_allow [once|server|everywhere]");
        interp.RegisterCommand("mod_deny", a => Answer(a.Count > 1 ? a[1] : "once", allow: false),
            "answer a server's mod offer with no: mod_deny [once|mod|server]");
        interp.RegisterCommand("mod_consent_list", _ => ListConsent(), "list the remembered answers about server-offered mods");
        interp.RegisterCommand("mod_consent_forget", a =>
        {
            if (a.Count < 2)
            {
                print("usage: mod_consent_forget all | <server address>");
                return;
            }
            int removed = Consent().Forget(a[1] == "all" ? null : a[1]);
            print($"forgot {removed} remembered answer(s)");
        }, "forget remembered answers about server mods: mod_consent_forget all | <server address>");
    }

    // ---- the server-offered mod ----------------------------------------------------------------------

    private ModConsentStore Consent() => _consent ??= new ModConsentStore(Path.Combine(UserPaths.BaseDir, "mod-consent.json"));

    private static double Clock => Time.GetTicksMsec() / 1000.0;

    /// <summary>
    /// Called by the net layer when the game connection to a Vortex server is accepted.
    /// <paramref name="serverKey"/> is the address the player connected to (what remembered consent is
    /// keyed by); <paramref name="sendReliable"/> sends one mod frame to the server on the reliable channel.
    /// Nothing is sent unless the server offers a mod.
    /// </summary>
    public void BeginServerSession(string serverKey, uint baseProtocol, Action<byte[]> sendReliable)
    {
        EndServerSession();
        if (_cvars is null) return;

        if (_cache is null)
        {
            _cache = new ModCache(Path.Combine(UserPaths.BaseDir, "modcache"));
            _cache.CleanPartials(TimeSpan.FromDays(1));
        }
        long maxDownload = (long)Math.Clamp(_cvars.GetFloat("cl_mod_download_max_mb"), 0f, 4096f) * 1024 * 1024;
        ModClientOptions options = new()
        {
            AllowMods = ModsAllowed,
            SandboxAvailable = WasmModSandbox.IsAvailable,
            ServerKey = serverKey,
            BaseProtocol = baseProtocol,
            MaxDownloadBytes = maxDownload,
        };
        _sendToServer = sendReliable;
        _session = new ModClientSession(options, _cache, Consent(), this, _print);
        _session.Loaded += OnServerModLoaded;
        _session.Unloaded += OnServerModUnloaded;
        _reportedState = ModClientState.Idle;
    }

    /// <summary>One mod frame from the server (the bytes after the game protocol's own message id).</summary>
    public void HandleServerFrame(ReadOnlySpan<byte> frame)
    {
        if (_session is null) return;
        _session.HandleFrame(frame, Clock);
        FlushToServer();
    }

    /// <summary>The level changed: the running server mod stops; the server offers again.</summary>
    public void OnLevelChanged()
    {
        _session?.LevelChanged();
        ClosePrompt();
    }

    /// <summary>The connection ended, for any reason.</summary>
    public void EndServerSession()
    {
        if (_session is null) return;
        _session.Disconnected();
        _session.Dispose();
        _session = null;
        _sendToServer = null;
        ClosePrompt();
        ClearPresentation();
    }

    private void OnServerModLoaded(ModLoadPlan plan)
    {
        // One mod at a time: the server's replaces a developer-loaded one. The asset tables are left
        // alone - the server's mod has already run its mod_init by now and may hold ids from them.
        Unload("replaced by the server's mod", clearPresentation: false);
        // TODO(MS-6): mount plan.Packs in a scope of their own. They are downloaded and verified, but
        // until the virtual filesystem has per-mod scopes they are not mounted, so a server mod can
        // only draw rectangles, text and pictures the base game already has.
    }

    private void OnServerModUnloaded(ModLoadPlan plan) => ClearPresentation();

    private void FlushToServer()
    {
        if (_session is null || _sendToServer is null) return;
        while (_session.TryDequeueOutbound(out byte[] frame)) _sendToServer(frame);
    }

    private void UpdateServerSession(double delta)
    {
        ModClientSession session = _session!;
        double now = Clock;
        session.SetAllowMods(ModsAllowed);
        session.Update(now);

        ModOfferClient offer = session.Offer;
        if (offer.PendingConsent is { } request)
        {
            if (!ReferenceEquals(request, _promptFor)) OpenPrompt(request);
        }
        else if (_promptFor is not null)
        {
            ClosePrompt();
        }

        if (offer.State != _reportedState)
        {
            _reportedState = offer.State;
            switch (offer.State)
            {
                case ModClientState.Downloading:
                    _print($"downloading the server's mod ({offer.DownloadTotalBytes / 1024} KiB)");
                    break;
                case ModClientState.Declined when offer.DeclineReason != ModDeclineReason.ModsDisabled:
                    _print($"server mod not running: {offer.DeclineReason}{(offer.DeclineText.Length > 0 ? " - " + offer.DeclineText : "")}");
                    break;
            }
        }

        if (session.Sandbox is not null)
        {
            _ops.Clear();
            _pendingSounds.Clear();
            session.Frame((float)delta, now);
            Present();
        }
        FlushToServer();
    }

    private void OpenPrompt(ModConsentRequest request)
    {
        if (_prompt is null)
        {
            _prompt = new ModConsentPrompt { Name = "ModConsent" };
            _prompt.Answered += decision =>
            {
                _promptFor = null;
                _session?.ResolveConsent(decision);
                FlushToServer();
            };
            AddChild(_prompt);
        }
        _promptFor = request;
        _prompt.Open(request);
        _print($"this server offers the mod '{HudText.Strip(request.Manifest.ModId)}' - answer the prompt, or use mod_allow / mod_deny");
    }

    private void ClosePrompt()
    {
        _promptFor = null;
        _prompt?.Close();
    }

    private void Answer(string scope, bool allow)
    {
        if (_session is not { } session || session.Offer.PendingConsent is null)
        {
            _print("no mod offer is waiting for an answer");
            return;
        }
        ModConsentDecision? decision = (allow, scope) switch
        {
            (true, "once") => ModConsentDecision.AllowOnce,
            (true, "server") => ModConsentDecision.AllowOnThisServer,
            (true, "everywhere") => ModConsentDecision.AllowEverywhere,
            (false, "once") => ModConsentDecision.DenyOnce,
            (false, "mod") => ModConsentDecision.DenyThisMod,
            (false, "server") => ModConsentDecision.DenyThisServer,
            _ => null,
        };
        if (decision is not { } answer)
        {
            _print(allow ? "usage: mod_allow [once|server|everywhere]" : "usage: mod_deny [once|mod|server]");
            return;
        }
        ClosePrompt();
        session.ResolveConsent(answer);
        FlushToServer();
    }

    private void ListConsent()
    {
        IReadOnlyList<ModConsentEntry> entries = Consent().Entries;
        if (entries.Count == 0) _print("no remembered answers about server mods");
        foreach (ModConsentEntry entry in entries)
            _print($"{(entry.Allow ? "allow" : "deny ")} {(entry.Server.Length > 0 ? entry.Server : "(any server)")} {(entry.Sha256.Length > 0 ? entry.Sha256[..12] : "(any mod)")} {entry.Label}");
    }

    private void ClearPresentation()
    {
        _ops.Clear();
        _pendingSounds.Clear();
        _pictures.Clear();
        _pictureIds.Clear();
        _sounds.Clear();
        _soundIds.Clear();
        foreach (AudioStreamPlayer? voice in _voices) voice?.Stop();
        Present();
    }

    private string Describe()
    {
        if (!WasmModSandbox.IsAvailable) return $"mod sandbox unavailable on this platform: {WasmModSandbox.UnavailableReason}";
        if (_session is { } session && session.Offer.State != ModClientState.Idle)
        {
            ModOfferClient offer = session.Offer;
            string what = offer.Manifest is { } manifest ? $"server mod '{HudText.Strip(manifest.ModId)}'" : "server mod";
            return offer.State switch
            {
                ModClientState.AwaitingConsent => $"{what}: waiting for your answer (mod_allow / mod_deny)",
                ModClientState.Downloading => $"{what}: downloading, {offer.DownloadedBytes / 1024} of {offer.DownloadTotalBytes / 1024} KiB",
                ModClientState.Declined => $"{what}: not running ({offer.DeclineReason}{(offer.DeclineText.Length > 0 ? " - " + offer.DeclineText : "")})",
                _ when session.Sandbox is { } running => $"{what} {running.State}, {running.MemoryBytes / 1024} KiB of guest memory, {_ops.Count} draw commands last frame",
                _ => $"{what}: {offer.State}",
            };
        }
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
        if (_session?.Sandbox is not null)
        {
            _print("mod_load: this server's mod is running; one mod at a time");
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

    /// <summary>Stops the developer-loaded mod (<c>mod_load</c>). A server's mod is stopped through its session.</summary>
    public void Unload(string? reason, bool clearPresentation = true)
    {
        if (_sandbox is null) return;
        string name = _sandbox.Name;
        _sandbox.Shutdown();
        _sandbox.Dispose();
        _sandbox = null;
        if (clearPresentation) ClearPresentation();
        if (reason is not null) _print($"mod '{name}' {reason}");
    }

    public override void _ExitTree()
    {
        EndServerSession();
        Unload(null);
    }

    public override void _Process(double delta)
    {
        if (_session is null && _sandbox is null) return;
        using var _scope = FrameProfiler.Scope("mods");

        if (_session is not null) UpdateServerSession(delta);
        if (_sandbox is null) return;

        // The developer-loaded mod. cl_allow_mods 0 stops it too, not only server mods.
        if (!ModsAllowed)
        {
            Unload("stopped (cl_allow_mods 0)");
            return;
        }
        _ops.Clear();
        _pendingSounds.Clear();
        if (!_sandbox.Frame((float)delta))
        {
            _print($"mod '{_sandbox.Name}' disabled: {_sandbox.DisabledReason}");
            Unload(null);
            return;
        }
        Present();
    }

    /// <summary>
    /// Hands the frame's recorded commands to the canvas: the draw list is cut into stretches that share
    /// a clip rectangle, one <see cref="ModDrawSegment"/> each, and the frame's sounds are started. Runs
    /// after the guest call has returned, so no renderer or audio code runs inside the guest's budget.
    /// </summary>
    private void Present()
    {
        float unitScale = Size.Y > 0f ? Size.Y / VirtualHeight : 1f;
        int used = 0, start = 0;
        Rect2? clip = null;

        for (int i = 0; i <= _ops.Count; i++)
        {
            bool atEnd = i == _ops.Count;
            if (!atEnd && _ops[i].Kind is not (ModDrawKind.SetClip or ModDrawKind.ResetClip)) continue;

            // A clip rectangle with no area shows nothing, so that stretch gets no canvas item at all.
            if (i > start && (clip is not { } area || area.HasArea()))
            {
                if (used >= MaxSegments) break;
                if (used == _segments.Count)
                {
                    ModDrawSegment segment = new() { Name = $"Segment{used}" };
                    AddChild(segment);
                    // Segments stay in list order, and under the consent prompt.
                    MoveChild(segment, used);
                    _segments.Add(segment);
                }
                _segments[used++].Assign(_ops, start, i, clip, unitScale, Size);
            }
            if (atEnd) break;
            clip = _ops[i].Kind == ModDrawKind.SetClip ? _ops[i].Rect : null;
            start = i + 1;
        }
        for (int i = used; i < _segments.Count; i++) _segments[i].Release();

        foreach ((int assetId, int channel, float volume, float pitch) in _pendingSounds) StartSound(assetId, channel, volume, pitch);
        _pendingSounds.Clear();
    }

    private void StartSound(int assetId, int channel, float volume, float pitch)
    {
        if (assetId <= 0 || assetId > _sounds.Count || _sounds[assetId - 1] is not { } stream) return;
        // A channel from 0 to 7 names a voice, so a mod can replace its own sound; anything else takes the next one in turn.
        int index = channel is >= 0 and < SoundVoices ? channel : _nextVoice++ % SoundVoices;
        AudioStreamPlayer voice = _voices[index] ??= NewVoice(index);
        voice.Stream = stream;
        voice.VolumeDb = Mathf.LinearToDb(Math.Clamp(volume, 0f, 1f));
        voice.PitchScale = Math.Clamp(pitch, 0.5f, 2f);
        voice.Play();
    }

    private AudioStreamPlayer NewVoice(int index)
    {
        AudioStreamPlayer voice = new() { Name = $"ModVoice{index}", Bus = "SFX" };
        AddChild(voice);
        return voice;
    }

    // ---- IModCommandSink: record only -----------------------------------------------------------------

    private bool Record(in ModDrawOp op)
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
        Record(new ModDrawOp { Kind = ModDrawKind.Rect, Rect = SaneRect(x, y, width, height), Color = ToColor(rgba) });

    void IModCommandSink.DrawPic(int assetId, float x, float y, float width, float height, uint rgba)
    {
        if (assetId <= 0 || assetId > _pictures.Count) return; // an id this mod was never given
        Record(new ModDrawOp { Kind = ModDrawKind.Pic, Rect = SaneRect(x, y, width, height), Color = ToColor(rgba), Texture = _pictures[assetId - 1] });
    }

    void IModCommandSink.DrawText(int fontId, float x, float y, float size, uint rgba, ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return;
        // One string per text command per frame. Acceptable for a HUD's worth of labels; if mods start
        // drawing thousands, cache by content here rather than asking guests to draw less.
        Record(new ModDrawOp
        {
            Kind = ModDrawKind.Text, Rect = SaneRect(x, y, 0f, 0f), Color = ToColor(rgba),
            Text = Encoding.UTF8.GetString(utf8), Size = Math.Clamp(size, 1f, 512f),
        });
    }

    void IModCommandSink.SetClip(float x, float y, float width, float height) =>
        Record(new ModDrawOp { Kind = ModDrawKind.SetClip, Rect = SaneRect(x, y, width, height) });

    void IModCommandSink.ResetClip() => Record(new ModDrawOp { Kind = ModDrawKind.ResetClip });

    // Recorded like a draw command and started after the guest call returns (Present). The id must be one
    // this mod was given by ResolveAsset; the per-frame cap keeps a mod from stacking a wall of noise.
    void IModCommandSink.PlaySound(int assetId, int channel, float volume, float pitch)
    {
        if (assetId <= 0 || assetId > _sounds.Count || _pendingSounds.Count >= MaxSoundsPerFrame) return;
        if (!float.IsFinite(volume) || !float.IsFinite(pitch)) return;
        _pendingSounds.Add((assetId, channel, volume, pitch));
    }

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
        if (!IsSafeAssetPath(path)) return 0;
        if (kind == ModAssetKind.Sound) return ResolveSound(path);
        if (kind != ModAssetKind.Picture) return 0;
        if (_pictureIds.TryGetValue(path, out int id)) return id;
        if (_pictures.Count >= MaxAssets) return 0;

        // TODO(MS-6): resolve inside the mod's own packs only. Until the virtual filesystem has per-mod
        // scopes this searches all mounted game data, which is every file the game itself may draw.
        Texture2D? texture = TextureCache.Get(path);
        if (texture is null) return 0;
        _pictures.Add(texture);
        return _pictureIds[path] = _pictures.Count;
    }

    // TODO(MS-6): like pictures, this searches all mounted game data until per-mod scopes exist.
    private int ResolveSound(string path)
    {
        if (SoundLoader is null) return 0;
        if (_soundIds.TryGetValue(path, out int id)) return id;
        if (_sounds.Count >= MaxAssets) return 0;
        AudioStream? stream = SoundLoader(path);
        if (stream is null) return 0;
        _sounds.Add(stream);
        return _soundIds[path] = _sounds.Count;
    }

    float IModHost.MeasureText(int fontId, float size, string text)
    {
        Font font = HudPanel.HudFont ?? ThemeDB.FallbackFont;
        return font.GetStringSize(text, HorizontalAlignment.Left, -1f, Math.Clamp((int)MathF.Round(size), 1, 512)).X;
    }

    // A developer-loaded mod has no server half to talk to. A server-offered mod never reaches this
    // member: ModClientSession answers send_to_server itself, through the offer flow's size and rate
    // limits, and only when the manifest declared the "net" capability.
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
