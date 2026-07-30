using System;
using System.Collections.Generic;
using VortexArena.Common.Gameplay;
using VortexArena.Formats;
using VortexArena.Formats.Sidecars;
using VortexArena.Formats.Vfs;

namespace VortexArena.Game.Loaders;

/// <summary>
/// Host-side implementation of QuakeC's <c>LoadPlayerSounds</c> / <c>UpdatePlayerSounds</c>
/// (common/effects/qc/globalsound.qc): resolve a player body-sound / voice id (e.g. <c>jump</c>,
/// <c>pain50</c>, <c>taunt</c>) to its real on-disk sample path by parsing the model's <c>.sounds</c>
/// manifest, falling back to the stock <c>sound/player/default.sounds</c> pack.
///
/// <para>The headless gameplay layer (<see cref="Sounds.PlayerSoundSample"/>) cannot read files, so it
/// exposes the <see cref="Sounds.ModelSoundResolver"/> seam; the host installs this resolver at boot once it
/// has a <see cref="VirtualFileSystem"/>. Without it, <c>PlayerSoundSample(null,"jump")</c> resolved to the
/// bogus path <c>sound/player/default.sounds/jump</c> (the <c>.sounds</c> file is a manifest, not a directory),
/// so EVERY per-model voice — the wall-jump grunt and all pain/death voices — was silent.</para>
///
/// <para>QC <c>LoadPlayerSounds</c> loads <c>default.sounds</c> first, then the model pack overrides any ids it
/// defines; an id absent from both yields the default. We mirror that: a model id wins, else the default pack,
/// else null (caller falls back to the raw concat). Variant counts (<c>N&gt;0</c> =&gt; random pick of
/// <c>{path}1..{path}N</c>) are honored as in Base.</para>
/// </summary>
public static class PlayerSoundResolver
{
    // Parsed manifests, keyed by their VFS vpath (default.sounds + each model's .sounds). null = parsed-but-absent.
    private static readonly Dictionary<string, Dictionary<string, ModelSound>?> _cache =
        new(StringComparer.Ordinal);

    private static VirtualFileSystem? _vfs;
    private static readonly Random _rng = new();

    private const string DefaultManifest = "sound/player/default.sounds";

    /// <summary>
    /// Install the resolver onto <see cref="Sounds.ModelSoundResolver"/>, reading manifests from
    /// <paramref name="vfs"/>. Idempotent / safe to re-call (e.g. on map change): the manifest cache is reset
    /// so a re-mounted VFS is re-read. A null VFS clears the seam (revert to the raw concat fallback).
    /// </summary>
    public static void Install(VirtualFileSystem? vfs)
    {
        _vfs = vfs;
        _cache.Clear();
        Sounds.ModelSoundResolver = vfs is null ? null : Resolve;
    }

    /// <summary>
    /// Resolve a player-sound <paramref name="id"/> for the model whose <c>.sounds</c> manifest is at
    /// <paramref name="modelSoundsFile"/> (QC <c>get_model_datafilename</c>; null/empty =&gt; default pack only).
    /// Returns the sample path (no extension; the audio loader probes .ogg/.wav), or null to fall back.
    /// </summary>
    private static string? Resolve(string? modelSoundsFile, string id)
    {
        // Model pack overrides the default (QC loads default first, then the model pack on top).
        if (!string.IsNullOrEmpty(modelSoundsFile)
            && Lookup(modelSoundsFile, id) is { } modelSample)
            return modelSample;

        return Lookup(DefaultManifest, id);
    }

    private static string? Lookup(string manifestVpath, string id)
    {
        Dictionary<string, ModelSound>? map = GetManifest(manifestVpath);
        if (map is null || !map.TryGetValue(id, out ModelSound s))
            return null;

        // QC: a count of 0 is a single file at the exact path; N>0 picks randomly among {path}1..{path}N.
        if (s.IsSingle)
            return s.Path;
        int n = _rng.Next(1, s.VariantCount + 1);
        return s.Path + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, ModelSound>? GetManifest(string vpath)
    {
        if (_cache.TryGetValue(vpath, out Dictionary<string, ModelSound>? cached))
            return cached;

        Dictionary<string, ModelSound>? map = null;
        VirtualFileSystem? vfs = _vfs;
        if (vfs is not null && vfs.Exists(vpath))
        {
            try
            {
                map = new Dictionary<string, ModelSound>(StringComparer.Ordinal);
                foreach (ModelSound entry in ModelSounds.ParseEntries(vfs.ReadText(vpath)))
                    map[entry.Id] = entry; // last definition wins, as QC strcpy overwrites
            }
            catch (AssetParseException)
            {
                map = null; // unreadable manifest: treat as absent
            }
        }

        _cache[vpath] = map;
        return map;
    }
}
