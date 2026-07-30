using System.Reflection;
using Godot;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Hud;

/// <summary>
/// Presentation-side helpers that bridge a sim-side <see cref="Weapon"/> descriptor to the HUD's art and
/// per-weapon attributes — the modernized stand-in for the bits of QuakeC's weapon HUD code that read
/// <c>it.model2</c> (the weapon icon), <c>wep.ammo_type</c> and the per-weapon crosshair cvars
/// (Base/.../qcsrc/client/hud/panel/weapons.qc + crosshair.qc).
///
/// Two impedance mismatches are resolved here, both because the panels live in <c>game/hud</c> and may
/// only consult the sim through its public surface:
///   * The base <see cref="Weapon"/> exposes no <c>ammo_type</c> / <c>charge</c> field — those live on the
///     concrete subclasses (e.g. <c>Vortex.AmmoType</c>, <c>Vortex.Cvars.Charge</c>). We read them
///     reflectively (they're plain public fields) and cache nothing mutable, so the panels stay sim-agnostic.
///   * The QC weapon "icon" (<c>model2</c>, the <c>g_*.md3</c> pickup model) has no PNG yet in this port,
///     so <see cref="IconPath"/> derives the conventional Xonotic HUD icon path from the weapon NetName and
///     <see cref="TextureCache"/> resolves it (returning null until the art lands → colored-box fallback).
/// </summary>
public static class WeaponHud
{
    /// <summary>The HUD skin whose art is preferred (QC <c>hud_skin</c>). The default-skin art is the fallback.</summary>
    public static string HudSkin { get; set; } = "luma";

    /// <summary>
    /// Candidate texture paths for a weapon's HUD icon (QC <c>model2</c> = <c>gfx/hud/&lt;skin&gt;/weapon&lt;icon&gt;</c>),
    /// best-first. The bare (extension-agnostic) names resolve from the mounted game data via
    /// <see cref="TextureCache"/>'s VFS resolver — the REAL Xonotic weapon icons; a <c>res://</c> project
    /// override and the colored-box fallback follow. The icon suffix is the weapon's Xonotic art name, which
    /// differs from the NetName for several weapons (see <see cref="IconName"/>).
    /// </summary>
    public static string[] IconPaths(string netName)
    {
        if (string.IsNullOrEmpty(netName)) return System.Array.Empty<string>();
        string icon = IconName(netName);
        return new[]
        {
            $"gfx/hud/{HudSkin}/weapon{icon}",   // VFS: preferred skin
            $"gfx/hud/default/weapon{icon}",     // VFS: default-skin fallback
            $"res://art/hud/weapons/{netName}.png", // project art override
        };
    }

    /// <summary>
    /// Map a weapon NetName to its Xonotic HUD-art icon name (<c>weapon&lt;icon&gt;.tga</c>). Most match the
    /// NetName; the legacy-named ones don't (the art predates several weapon renames) — mortar's icon is
    /// "grenadelauncher", vortex's is "nex", vaporizer's "minstanex", machinegun's "uzi", blaster's "laser",
    /// devastator's "rocketlauncher".
    /// </summary>
    public static string IconName(string netName) => netName switch
    {
        "mortar" => "grenadelauncher",
        "devastator" => "rocketlauncher",
        "vortex" => "nex",
        "vaporizer" => "minstanex",
        "machinegun" => "uzi",
        "blaster" => "laser",
        _ => netName,
    };

    /// <summary>Resolve the weapon's HUD icon texture, or null if no art is present yet (QC model2 pic).</summary>
    public static Texture2D? Icon(Weapon w) =>
        w is null ? null : TextureCache.GetFirst(IconPaths(w.NetName));

    /// <summary>
    /// The ammo <see cref="ResourceType"/> a weapon consumes. Reads the concrete weapon's public
    /// <c>AmmoType</c> field (QC <c>wep.ammo_type</c>) by reflection; weapons without one (Blaster, Tuba,
    /// Porto, Hook in some modes) report <see cref="ResourceType.None"/>.
    /// </summary>
    public static ResourceType AmmoType(Weapon w)
    {
        if (w is null) return ResourceType.None;
        FieldInfo? f = w.GetType().GetField("AmmoType", BindingFlags.Public | BindingFlags.Instance);
        if (f is not null && f.FieldType == typeof(ResourceType) && f.GetValue(w) is ResourceType rt)
            return rt;
        return ResourceType.None;
    }

    /// <summary>
    /// Whether a weapon supports a charge mechanic (QC <c>g_balance_&lt;wep&gt;_charge</c>) — true for the
    /// Vortex/Vaporizer rail family. Detected by a public <c>bool Charge</c> on the weapon or a
    /// <c>Charge</c> field on its nested balance struct (<c>Cvars</c>/<c>Primary</c>).
    /// </summary>
    public static bool IsChargeWeapon(Weapon w)
    {
        if (w is null) return false;
        System.Type t = w.GetType();

        // direct: public bool Charge;
        FieldInfo? direct = t.GetField("Charge", BindingFlags.Public | BindingFlags.Instance);
        if (direct is not null && direct.FieldType == typeof(bool) && direct.GetValue(w) is bool b0)
            return b0;

        // nested balance block: Cvars.Charge / Primary.Charge (a public struct field on the weapon).
        foreach (string blockName in new[] { "Cvars", "Primary", "Secondary" })
        {
            FieldInfo? block = t.GetField(blockName, BindingFlags.Public | BindingFlags.Instance);
            if (block is null) continue;
            object? blockVal = block.GetValue(w);
            if (blockVal is null) continue;
            FieldInfo? charge = block.FieldType.GetField("Charge", BindingFlags.Public | BindingFlags.Instance);
            if (charge is not null && charge.FieldType == typeof(bool) && charge.GetValue(blockVal) is bool b1 && b1)
                return true;
        }
        return false;
    }

    /// <summary>Convert a weapon's sim-side <see cref="System.Numerics.Vector3"/> color to a Godot color.</summary>
    public static Color ColorOf(Weapon w, float alpha = 1f) =>
        w is null ? new Color(1f, 1f, 1f, alpha) : new Color(w.Color.X, w.Color.Y, w.Color.Z, alpha);
}
