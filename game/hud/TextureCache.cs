using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Hud;

/// <summary>
/// A tiny load-once texture cache for HUD art (weapon icons, crosshair pics, powerup/notify icons) — the
/// modernized stand-in for QuakeC's <c>precache_pic</c> + the <c>drawpic</c> string-keyed image table
/// (Base/.../qcsrc/client/draw.qh). QC referenced HUD images by a virtual path ("gfx/hud/.../weapon…")
/// and the engine kept them in a cache keyed by that path; we do the same with Godot
/// <see cref="Texture2D"/> resources keyed by their <c>res://</c> (or absolute) path.
///
/// The HUD only ever has a handful of icons on screen, but the same icon is requested every frame in
/// immediate-mode <c>_Draw</c>, so caching avoids re-hitting the resource loader 60×/second. A path that
/// fails to load is remembered as <c>null</c> so we don't retry it each frame (the caller then draws its
/// colored-box fallback). Call <see cref="Clear"/> on a resource-reload if hot-reloading art.
///
/// This is presentation-only and lives under <c>game/hud</c>; nothing in the sim references it.
/// </summary>
public static class TextureCache
{
    // Path -> texture (or null = known-missing). One process-wide cache; HUD textures are immutable art.
    private static readonly Dictionary<string, Texture2D?> _cache = new();

    /// <summary>
    /// Resolves a bare VFS art base name (extension-agnostic, e.g. <c>"gfx/hud/default/weaponvortex"</c> or
    /// <c>"gfx/crosshair3"</c>) to a Godot <see cref="Texture2D"/> from the mounted game data, or null. The
    /// host wires this to the asset pipeline (<c>AssetLoader.LoadTexture</c>, which does the
    /// <c>ResolveImage</c> extension search + TGA/PNG/JPG decode). This is what lets the HUD draw the REAL
    /// Xonotic weapon icons / crosshairs from the pk3 tree instead of the colored-box fallback. Null until the
    /// host wires it (then HUD art simply falls back, as before).
    /// </summary>
    public static System.Func<string, Texture2D?>? VfsResolver { get; set; }

    /// <summary>
    /// Load (and cache) the texture at <paramref name="path"/>, or return <c>null</c> if it cannot be
    /// loaded. A Godot resource/absolute path (<c>res://…</c>, <c>user://…</c>, <c>C:/…</c>) goes through the
    /// resource loader; any other (a bare, extension-agnostic name like <c>gfx/hud/default/weaponvortex</c>)
    /// is resolved from the mounted game data via <see cref="VfsResolver"/>. An empty/blank path returns
    /// <c>null</c>. Subsequent calls with the same path are served from the dictionary (including the miss).
    /// </summary>
    public static Texture2D? Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_cache.TryGetValue(path, out Texture2D? cached)) return cached;

        Texture2D? tex;
        if (IsEnginePath(path))
        {
            // ResourceLoader.Exists avoids error spam for the (expected) missing-art case; guard the Load too.
            tex = null;
            if (ResourceLoader.Exists(path))
            {
                try { tex = ResourceLoader.Load<Texture2D>(path); }
                catch { tex = null; }
            }
        }
        else
        {
            // A bare VFS art base name → resolve through the mounted game data (real Xonotic art).
            try { tex = VfsResolver?.Invoke(path); }
            catch { tex = null; }
        }
        _cache[path] = tex;
        return tex;
    }

    /// <summary>True for a Godot resource path (res://, user://) or an absolute filesystem path.</summary>
    private static bool IsEnginePath(string p)
        => p.StartsWith("res://", System.StringComparison.Ordinal)
        || p.StartsWith("user://", System.StringComparison.Ordinal)
        || p.StartsWith("/", System.StringComparison.Ordinal)
        || (p.Length > 1 && p[1] == ':'); // Windows drive letter (C:/...)

    /// <summary>
    /// Try the supplied candidate paths in order and return the first that loads (QC fall-through from a
    /// skin-specific pic to the default). Returns <c>null</c> if none resolve.
    /// </summary>
    public static Texture2D? GetFirst(params string?[] paths)
    {
        foreach (string? p in paths)
        {
            Texture2D? t = Get(p);
            if (t is not null) return t;
        }
        return null;
    }

    /// <summary>Whether <paramref name="path"/> resolves to a real texture (cached).</summary>
    public static bool Has(string? path) => Get(path) is not null;

    /// <summary>Drop the cache (e.g. on a HUD-skin change or art hot-reload).</summary>
    public static void Clear() => _cache.Clear();
}
