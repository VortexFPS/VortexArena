using System.Security.Cryptography;

namespace VortexArena.Modding;

/// <summary>
/// One connection's worth of server-offered mod on the client: the offer protocol
/// (<see cref="ModOfferClient"/>) joined to the sandbox (<see cref="WasmModSandbox"/>). It is what turns
/// "the files are verified" into "the mod is running", and what stops the mod again - on a fault, a
/// budget overrun, a level change, a withdrawn offer, <c>cl_allow_mods 0</c>, or the end of the
/// connection.
///
/// The host (game/modding) owns the pixels and the socket and nothing else: it passes frames in, sends
/// frames out, shows <see cref="ModOfferClient.PendingConsent"/> to the player, and calls
/// <see cref="Update"/> and <see cref="Frame"/> once a rendered frame. It supplies the
/// <see cref="IModHost"/> the guest draws through; this class wraps it so that the two things a
/// manifest must declare before a mod can do them - talk to the server, play sounds - are refused
/// when it did not.
///
/// Nothing here throws at the caller for anything a server or a guest did.
/// </summary>
public sealed class ModClientSession : IDisposable
{
    /// <summary>Server messages handed to the mod per rendered frame. Each one is a guest call with its own frame budget.</summary>
    public const int MaxEventsPerFrame = 4;

    private readonly IModHost _host;
    private readonly Action<string> _log;
    private readonly GatedHost _gate;
    private ModLoadPlan? _loadedPlan;
    private double _now;

    public ModClientSession(ModClientOptions options, ModCache cache, ModConsentStore consent, IModHost host, Action<string>? log = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _log = log ?? (_ => { });
        Offer = new ModOfferClient(options, cache, consent);
        Cache = cache;
        _gate = new GatedHost(this);
    }

    public ModOfferClient Offer { get; }
    public ModCache Cache { get; }
    /// <summary>The running mod, or null. Null too for an assets-only mod, which has no code.</summary>
    public WasmModSandbox? Sandbox { get; private set; }
    /// <summary>The plan whose module (and packs) the host currently has in use, or null.</summary>
    public ModLoadPlan? LoadedPlan => _loadedPlan;

    /// <summary>Raised after a plan's module started (or, for an assets-only mod, was accepted). The host mounts the packs here.</summary>
    public event Action<ModLoadPlan>? Loaded;
    /// <summary>Raised when the loaded plan stops being in use, for any reason. The host unmounts and clears what it drew.</summary>
    public event Action<ModLoadPlan>? Unloaded;

    public void HandleFrame(ReadOnlySpan<byte> frame, double now)
    {
        _now = now;
        Offer.HandleFrame(frame, now);
        Reconcile();
    }

    public bool TryDequeueOutbound(out byte[] frame) => Offer.TryDequeueOutbound(out frame);

    /// <summary>Timeouts, and loading a mod whose files have just been verified. Call once a frame, before <see cref="Frame"/>.</summary>
    public void Update(double now)
    {
        _now = now;
        Offer.Update(now);
        Reconcile();
    }

    /// <summary>
    /// Runs one frame of the mod: pending server messages first, then <c>mod_frame</c>. Returns false
    /// when no mod code ran. A mod that faults here is unloaded and the server is told.
    /// </summary>
    public bool Frame(float deltaSeconds, double now)
    {
        _now = now;
        Reconcile();
        if (Sandbox is null || Offer.State != ModClientState.Active) return false;

        for (int i = 0; i < MaxEventsPerFrame && Offer.TryDequeueEvent(out int id, out byte[] payload); i++)
        {
            // False without being disabled only means the mod exports no mod_event: the message is dropped.
            if (!Sandbox.Event(id, payload) && Sandbox.State == ModSandboxState.Disabled) break;
        }
        if (Sandbox.State == ModSandboxState.Running) Sandbox.Frame(deltaSeconds);

        if (Sandbox.State == ModSandboxState.Disabled)
        {
            string reason = Sandbox.DisabledReason ?? "disabled";
            _log($"mod '{Sandbox.Name}' disabled: {reason}");
            Offer.ReportFailure(ModDeclineReason.Faulted, reason);
            Reconcile();
            return false;
        }
        return true;
    }

    public void ResolveConsent(ModConsentDecision decision)
    {
        Offer.ResolveConsent(decision, _now);
        Reconcile();
    }

    public void Cancel()
    {
        Offer.Cancel();
        Reconcile();
    }

    /// <summary>Stops the server's mod for the rest of this connection (the player's <c>mod_unload</c>).</summary>
    public void StopForConnection()
    {
        Offer.StopForConnection();
        Reconcile();
    }

    /// <summary>Follows <c>cl_allow_mods</c>. Switching it off stops a running mod at once.</summary>
    public void SetAllowMods(bool allow)
    {
        Offer.AllowMods = allow;
        Reconcile();
    }

    /// <summary>The level changed: stop the mod. The server offers again and a fresh instance starts.</summary>
    public void LevelChanged()
    {
        Offer.LevelChanged();
        Reconcile();
    }

    public void Disconnected()
    {
        Offer.Disconnected();
        Reconcile();
    }

    public void Dispose()
    {
        Offer.Dispose();
        Unload();
    }

    // ------------------------------------------------------------------------------------------------

    /// <summary>Makes what is loaded match what the offer flow says should be: unload first, then load.</summary>
    private void Reconcile()
    {
        if (_loadedPlan is not null && !ReferenceEquals(_loadedPlan, Offer.Plan)) Unload();
        if (_loadedPlan is null && Offer.State == ModClientState.ReadyToLoad && Offer.Plan is { } plan) Load(plan);
    }

    private void Load(ModLoadPlan plan)
    {
        string name = plan.Manifest.ModId;
        WasmModSandbox? sandbox = null;
        if (plan.ModulePath is not null)
        {
            ModArtifact artifact = plan.Manifest.ClientModule!;
            if (!TryReadVerified(plan.ModulePath, artifact, out byte[] module))
            {
                // The cache held the right bytes when they were stored; if it does not now, someone
                // else changed the file. Drop it so the next offer downloads it again.
                Cache.Remove(artifact);
                Fail(ModDeclineReason.LoadFailed, "the cached module no longer matches its SHA-256");
                return;
            }

            try { sandbox = WasmModSandbox.Load(name, module, _gate, plan.Limits); }
            catch (ModLoadException e)
            {
                Fail(ModDeclineReason.LoadFailed, e.Message);
                return;
            }
            if (!sandbox.Init())
            {
                string reason = sandbox.DisabledReason ?? "failed to start";
                sandbox.Dispose();
                Fail(ModDeclineReason.LoadFailed, reason);
                return;
            }
        }

        Sandbox = sandbox;
        _loadedPlan = plan;
        Offer.ReportLoaded();
        _log($"mod '{name}' {plan.Manifest.ModVersion} running");
        Loaded?.Invoke(plan);

        void Fail(ModDeclineReason reason, string text)
        {
            _log($"mod '{name}' not started: {text}");
            Offer.ReportFailure(reason, text);
        }
    }

    private void Unload()
    {
        ModLoadPlan? plan = _loadedPlan;
        WasmModSandbox? sandbox = Sandbox;
        _loadedPlan = null;
        Sandbox = null;
        if (sandbox is not null)
        {
            sandbox.Shutdown();
            sandbox.Dispose();
        }
        if (plan is not null) Unloaded?.Invoke(plan);
    }

    /// <summary>
    /// Reads a cached file back, refusing one that is not exactly the size the manifest declared, and
    /// hashes it again. Bounded by the artifact's size, which the manifest parser already capped.
    /// </summary>
    private static bool TryReadVerified(string path, ModArtifact artifact, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != artifact.SizeBytes || artifact.SizeBytes > int.MaxValue) return false;
            byte[] buffer = new byte[(int)artifact.SizeBytes];
            stream.ReadExactly(buffer);
            if (Convert.ToHexString(SHA256.HashData(buffer)).ToLowerInvariant() != artifact.Sha256) return false;
            bytes = buffer;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The <see cref="IModHost"/> the guest actually gets. Everything is passed straight to the real
    /// host except the two things a manifest has to declare first: messages to the server, which go
    /// through the offer flow's size and rate limits and are refused without the "net" capability, and
    /// sounds, which are dropped without the "sound" capability. It adds no capability; it can only
    /// remove one.
    /// </summary>
    private sealed class GatedHost : IModHost
    {
        private readonly ModClientSession _session;
        public GatedHost(ModClientSession session) => _session = session;

        private IModHost Host => _session._host;
        private ModManifest? Manifest => _session.Offer.Manifest;

        public bool SendToServer(ReadOnlySpan<byte> payload) => _session.Offer.TrySendToServer(payload, _session._now);

        public void PlaySound(int assetId, int channel, float volume, float pitch)
        {
            if (Manifest is { } manifest && manifest.HasCapability(ModCapabilities.Sound)) Host.PlaySound(assetId, channel, volume, pitch);
        }

        public void Log(ModLogLevel level, string message) => Host.Log(level, message);
        public int ReadState(ModStateKind kind, int index, Span<byte> destination) => Host.ReadState(kind, index, destination);
        public int EntityCount => Host.EntityCount;
        public bool TryGetCvar(string name, out string value) => Host.TryGetCvar(name, out value);
        public int ResolveAsset(ModAssetKind kind, string path) =>
            kind == ModAssetKind.Sound && Manifest is { } manifest && !manifest.HasCapability(ModCapabilities.Sound) ? 0 : Host.ResolveAsset(kind, path);
        public float MeasureText(int fontId, float size, string text) => Host.MeasureText(fontId, size, text);
        public double Time => Host.Time;
        public void DrawRect(float x, float y, float width, float height, uint rgba) => Host.DrawRect(x, y, width, height, rgba);
        public void DrawPic(int assetId, float x, float y, float width, float height, uint rgba) => Host.DrawPic(assetId, x, y, width, height, rgba);
        public void DrawText(int fontId, float x, float y, float size, uint rgba, ReadOnlySpan<byte> utf8) => Host.DrawText(fontId, x, y, size, rgba, utf8);
        public void SetClip(float x, float y, float width, float height) => Host.SetClip(x, y, width, height);
        public void ResetClip() => Host.ResetClip();
    }
}
