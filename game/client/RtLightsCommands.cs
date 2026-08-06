using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using VortexArena.Formats.Lighting;
using VortexArena.Common.Config;

namespace VortexArena.Game.Client;

/// <summary>
/// Console commands for authoring a map's real-time world lights (<b>N7</b>) — the useful core of DarkPlaces'
/// <c>r_editlights</c> command set, wired to this port's world-light renderer and its in-game map editor.
///
/// <para><b>Why these matter.</b> The <c>.rtlights</c> format is one DarkPlaces itself reads and writes, and
/// every Xonotic mapper who has ever lit a map for realtime rendering knows it. Being able to <i>emit</i> one
/// means a map lit in this port stays lightable in Base, and being able to import one means the six stock maps
/// that ship the file round-trip. That interop is the whole point; a proprietary light format would have
/// neither half.</para>
///
/// <list type="bullet">
///   <item><c>rtlights_save</c> — write the currently loaded world lights to
///   <c>&lt;userdir&gt;/data/maps/&lt;map&gt;.rtlights</c>, in DP's format and its shortest-adequate line form,
///   so DarkPlaces can load the result unchanged.</item>
///   <item><c>rtlights_reload</c> — re-read the map's lights from disk, so an external edit (or a save from
///   here) takes effect without a map change.</item>
///   <item><c>rtlights_import</c> — build the light set from the map's own <c>light</c> entities and keep it,
///   which is how you bootstrap a <c>.rtlights</c> for a map that has never had one
///   (DP's <c>r_editlights_importlightentitiesfrommap</c>).</item>
///   <item><c>rtlights_status</c> — how many lights are loaded, from where, and whether the mode is on. The
///   answer to "why do I see nothing" is almost always <c>r_shadow_realtime_world 0</c>.</item>
/// </list>
///
/// <para><b>Writing goes to the user directory, never into the mounted content.</b> A save must not silently
/// modify a shipped pk3dir or drop a file the VFS then prefers over the map's own — so it lands in the
/// per-user gamedir, which is exactly where the VFS looks first anyway.</para>
/// </summary>
public static class RtLightsCommands
{
    /// <summary>The live world-light renderer, set by the client host. Null outside a match.</summary>
    public static WorldLightRenderer? Renderer { get; set; }

    /// <summary>The current map's bare name, for the filename and the reload.</summary>
    public static string MapName { get; set; } = string.Empty;

    /// <summary>The current map's BSP, for the entity import.</summary>
    public static VortexArena.Formats.Bsp.BspData? Bsp { get; set; }

    /// <summary>Register the four commands. Idempotent through the interpreter's own registration.</summary>
    public static void Register(ConfigInterpreter interp, Action<string> print)
    {
        interp.RegisterCommand("rtlights_status", _ =>
        {
            WorldLightRenderer? r = Renderer;
            if (r is null)
            {
                print("rtlights: no map loaded.");
                return;
            }
            float on = Menu.MenuState.Cvars.GetFloat("r_shadow_realtime_world");
            print($"rtlights: {r.Count} world lights for '{MapName}'. " +
                  $"r_shadow_realtime_world is {(on != 0f ? "ON" : "OFF — that is why you see no change")}.");
        }, "report the map's real-time world lights and whether they are being rendered");

        interp.RegisterCommand("rtlights_reload", _ =>
        {
            WorldLightRenderer? r = Renderer;
            if (r is null) { print("rtlights_reload: no map loaded."); return; }
            r.ForceReload(MapName, Bsp);
            print($"rtlights_reload: {r.Count} lights.");
        }, "re-read this map's .rtlights file (or its light entities) without changing map");

        interp.RegisterCommand("rtlights_import", _ =>
        {
            WorldLightRenderer? r = Renderer;
            if (r is null) { print("rtlights_import: no map loaded."); return; }
            int n = r.ImportFromEntities(Bsp);
            print(n > 0
                ? $"rtlights_import: built {n} lights from the map's light entities. " +
                  "Use rtlights_save to keep them."
                : "rtlights_import: this map declares no usable light entities.");
        }, "build world lights from the map's own light entities (DP r_editlights_importlightentitiesfrommap)");

        interp.RegisterCommand("rtlights_save", _ =>
        {
            WorldLightRenderer? r = Renderer;
            if (r is null) { print("rtlights_save: no map loaded."); return; }
            if (string.IsNullOrWhiteSpace(MapName)) { print("rtlights_save: no map name."); return; }
            IReadOnlyList<RtLightsFile.Light> lights = r.SourceLights;
            if (lights.Count == 0)
            {
                print("rtlights_save: nothing to save — try rtlights_import first.");
                return;
            }
            try
            {
                string dir = Path.Combine(UserPaths.GameDir, "maps");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, MapName + ".rtlights");
                File.WriteAllText(path, RtLightsFile.Write(lights));
                print($"rtlights_save: wrote {lights.Count} lights to {path}");
            }
            catch (Exception ex)
            {
                print($"rtlights_save failed: {ex.Message}");
            }
        }, "write this map's world lights to <userdir>/data/maps/<map>.rtlights in DarkPlaces format");
    }
}
