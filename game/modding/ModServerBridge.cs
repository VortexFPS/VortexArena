// NOT BUILT IN THE GODOT HOST as of 2026-10-08, never run, and NOT YET CALLED from game/net. It type-checks
// against GodotSharp 4.6.3 in a scratch project with stand-ins for the host classes it uses. The wiring it
// needs is listed in planning/specs/modding.md, section 9.6.
using System;
using System.Collections.Generic;
using System.IO;
using VortexArena.Engine.Simulation;
using VortexArena.Modding;

namespace VortexArena.Game.Modding;

/// <summary>
/// The server's side of offering a mod, as the game server sees it: the <c>sv_mod_*</c> cvars that
/// describe the mod, and a <see cref="ModOfferHub"/> built from them.
///
/// A server with <c>sv_mod_module</c> and <c>sv_mod_packs</c> both empty - the default - has no bridge
/// (<see cref="TryCreate"/> returns null), creates no peers and sends no mod frames, so its traffic is
/// byte for byte what it was before this existed.
/// </summary>
public sealed class ModServerBridge : IDisposable
{
    private readonly ModOfferHub _hub;

    private ModServerBridge(ModOfferHub hub) => _hub = hub;

    public ModOffer Offer => _hub.Offer;

    public static void RegisterCvars(CvarService cvars)
    {
        const VortexArena.Common.Services.CvarFlags none = VortexArena.Common.Services.CvarFlags.None;
        cvars.Register("sv_mod_module", "", none, "path of the client mod module (.wasm) this server offers to players; empty = none");
        cvars.Register("sv_mod_packs", "", none, "semicolon-separated paths of asset packs (.pk3) offered with the mod");
        cvars.Register("sv_mod_id", "", none, "short id of the offered mod (letters, digits, '.', '-', '_')");
        cvars.Register("sv_mod_version", "1", none, "version of the offered mod, shown to players");
        cvars.Register("sv_mod_title", "", none, "title shown to players when they are asked to accept the mod");
        cvars.Register("sv_mod_description", "", none, "description shown to players when they are asked to accept the mod");
        cvars.Register("sv_mod_author", "", none, "author shown to players when they are asked to accept the mod");
        cvars.Register("sv_mod_capabilities", "", none, "what the mod uses beyond drawing: any of \"net\" (messages to and from this server) and \"sound\", space-separated");
        cvars.Register("sv_mod_required", "0", none, "1 = disconnect players who do not end up running the mod; 0 = they play without it");
    }

    /// <summary>
    /// Builds the offer from the cvars. Returns null when the server offers no mod, or when the mod it
    /// describes is one no client would accept - which is printed, and the server then runs without a mod
    /// rather than failing to start.
    /// </summary>
    public static ModServerBridge? TryCreate(CvarService cvars, uint baseProtocol, Action<string> print)
    {
        string module = cvars.GetString("sv_mod_module").Trim();
        string[] packs = cvars.GetString("sv_mod_packs").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (module.Length == 0 && packs.Length == 0) return null;

        string id = cvars.GetString("sv_mod_id").Trim();
        ModOfferDescription description = new()
        {
            ModId = id,
            ModVersion = cvars.GetString("sv_mod_version").Trim(),
            BaseProtocol = baseProtocol,
            Consent = new ModConsent
            {
                Title = cvars.GetString("sv_mod_title") is { Length: > 0 } title ? title : id,
                Description = cvars.GetString("sv_mod_description"),
                Author = cvars.GetString("sv_mod_author"),
            },
            Required = cvars.GetFloat("sv_mod_required") != 0f,
            Capabilities = cvars.GetString("sv_mod_capabilities").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };

        try
        {
            ModOffer offer = ModOffer.FromFiles(description, module.Length > 0 ? module : null, packs);
            print($"offering mod '{offer.Manifest.ModId}' {offer.Manifest.ModVersion} to clients ({offer.Manifest.TotalBytes / 1024} KiB, manifest {offer.ManifestSha256[..12]})");
            return new ModServerBridge(new ModOfferHub(offer));
        }
        catch (Exception e) when (e is ModManifestException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            print($"sv_mod_*: this server's mod cannot be offered ({e.Message}); running without it");
            return null;
        }
    }

    /// <summary>A client passed the handshake.</summary>
    public void PeerAccepted(int peerId, double now) => _hub.PeerJoined(peerId, now);

    public void PeerDisconnected(int peerId) => _hub.PeerLeft(peerId);

    /// <summary>One mod frame from a client (the bytes after the game protocol's own message id).</summary>
    public void HandleFrame(int peerId, ReadOnlySpan<byte> frame, double now) => _hub.HandleFrame(peerId, frame, now);

    public void LevelChanged(double now) => _hub.LevelChanged(now);

    /// <summary>
    /// False while a client of a server with a REQUIRED mod is not running it yet: the server should keep
    /// that client out of the match (spectating, no input) until this turns true or the hub drops it.
    /// </summary>
    public bool MayPlay(int peerId) => _hub.MayPlay(peerId);

    /// <summary>Once per server tick: send what is due and drop who must go.</summary>
    public void Tick(double now, Action<int, byte[]> sendReliable, Action<int, string> disconnect) => _hub.Pump(now, sendReliable, disconnect);

    /// <summary>Server half of the mod to one client's mod. False when that client is not running the mod.</summary>
    public bool SendToClient(int peerId, int eventId, ReadOnlySpan<byte> payload) =>
        _hub.Peer(peerId) is { } peer && peer.TrySendToClient(eventId, payload);

    /// <summary>The next message a client's mod sent to the server half, already size- and rate-limited.</summary>
    public bool TryReceive(int peerId, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        return _hub.Peer(peerId) is { } peer && peer.TryDequeueMessage(out payload);
    }

    public void Dispose() => _hub.Dispose();
}
