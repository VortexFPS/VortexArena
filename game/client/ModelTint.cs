using System.Collections.Generic;
using Godot;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using VortexArena.Game.Loaders;

namespace VortexArena.Game.Client;

/// <summary>
/// Drives the per-entity tint instance-uniforms of <see cref="PlayerSkinShader"/> on every
/// <see cref="MeshInstance3D"/> under a model root — the Godot successor to Darkplaces' per-entity
/// <c>colormod</c>/<c>glowmod</c> and the shirt/pants colormap (<c>gl_rmain.c</c>).
///
/// <para>The colors are <b>instance shader parameters</b>, so the shared (cached) skin material is reused
/// while each model instance carries its own colors. Surfaces using a plain <see cref="StandardMaterial3D"/>
/// (a model with no <c>_shirt</c>/<c>_pants</c>/<c>_reflect</c> siblings) simply have no such uniforms and
/// ignore the call — harmless, since colormod/glowmod default to white there anyway.</para>
///
/// <para><see cref="ApplyAppearance"/> ports the GLOWMOD + death-fade tail of
/// <c>CSQCPlayer_ModelAppearance_Apply</c> (qcsrc/client/csqcmodel_hooks.qc:331-360): glowmod from the full
/// <c>colormapPaletteColor</c> palette, the <c>cl_deathglow</c> fade while dead, the respawn-ghost zeroing
/// (<c>cl_respawn_ghosts_keepcolors</c>), and the <c>r_hdr_glowintensity</c> divide. The pure math lives in
/// <see cref="CsqcModelAppearance"/> (engine, unit-tested); this reads the cvars + clock.</para>
/// </summary>
public static class ModelTint
{
    /// <summary>Identity tint (no change): white colormod/glowmod.</summary>
    public static readonly Color White = new(1f, 1f, 1f);

    /// <summary>No team contribution: black shirt/pants (the additive masks vanish).</summary>
    public static readonly Color Black = new(0f, 0f, 0f);

    /// <summary>
    /// Set the four tint instance-uniforms (<c>colormod</c>/<c>glowmod</c>/<c>shirt_color</c>/<c>pants_color</c>)
    /// on every mesh under <paramref name="root"/>.
    /// </summary>
    public static void Apply(Node root, Color colormod, Color glowmod, Color shirt, Color pants)
    {
        if (root is null)
            return;
        foreach (MeshInstance3D mi in Meshes(root))
        {
            mi.SetInstanceShaderParameter(PlayerSkinShader.ColormodUniform, colormod);
            mi.SetInstanceShaderParameter(PlayerSkinShader.GlowmodUniform, glowmod);
            mi.SetInstanceShaderParameter(PlayerSkinShader.ShirtColorUniform, shirt);
            mi.SetInstanceShaderParameter(PlayerSkinShader.PantsColorUniform, pants);
        }
    }

    /// <summary>
    /// Set the four tint instance-uniforms on an already-flattened, cached mesh list — the per-frame appearance
    /// path. Same body as <see cref="Apply(Node,Color,Color,Color,Color)"/> but skips the recursive
    /// <see cref="Meshes"/> tree-walk (the list comes pre-built from <see cref="CsqcModelEffects.GetCachedMeshes"/>,
    /// 3.2-2). The list is non-null by construction; an empty list loops zero times (matches today's no-mesh case).
    /// </summary>
    public static void Apply(IReadOnlyList<MeshInstance3D> meshes, Color colormod, Color glowmod, Color shirt, Color pants)
    {
        for (int i = 0; i < meshes.Count; i++)
        {
            MeshInstance3D mi = meshes[i];
            mi.SetInstanceShaderParameter(PlayerSkinShader.ColormodUniform, colormod);
            mi.SetInstanceShaderParameter(PlayerSkinShader.GlowmodUniform, glowmod);
            mi.SetInstanceShaderParameter(PlayerSkinShader.ShirtColorUniform, shirt);
            mi.SetInstanceShaderParameter(PlayerSkinShader.PantsColorUniform, pants);
        }
    }

    /// <summary>
    /// Push the lightgrid model-lighting instance-uniforms (playtest r14 B/C) on every mesh under
    /// <paramref name="root"/>: <c>grid_lit</c> switches the skin shader onto the DP grid-lit branch,
    /// <c>grid_ambient</c>/<c>grid_diffuse</c> are the sample's RGB terms (DP scale, 1.0 ≈ byte 128,
    /// may exceed 1 — q3map overbright), <c>grid_dir</c> the baked light direction (normalized, GODOT
    /// world axes, pointing AT the light). Same harmless-on-StandardMaterial contract as
    /// <see cref="Apply(Node,Color,Color,Color,Color)"/>. Pass <paramref name="on"/> false to return the
    /// model to the PBR path (the no-grid-map fallback).
    /// </summary>
    public static void ApplyGridLight(Node root, bool on, Vector3 ambient, Vector3 diffuse, Vector3 dir)
    {
        if (root is null)
            return;
        foreach (MeshInstance3D mi in Meshes(root))
        {
            mi.SetInstanceShaderParameter(PlayerSkinShader.GridLitUniform, on ? 2f : 0f);
            mi.SetInstanceShaderParameter(PlayerSkinShader.GridAmbientUniform, ambient);
            mi.SetInstanceShaderParameter(PlayerSkinShader.GridDiffuseUniform, diffuse);
            mi.SetInstanceShaderParameter(PlayerSkinShader.GridDirUniform, dir);
        }
    }

    /// <summary>
    /// Switch every mesh under <paramref name="root"/> onto the DP grid-lit branch with NO per-entity light
    /// terms - the F1-B default for players, items, gibs and props on a map that has a GPU light grid bound.
    /// Lobe 1 (the per-pixel grid sample) does all the work; lobe 2 stays at its zero default until the
    /// dynamic-light probe pushes something into it.
    ///
    /// <para>Call this instead of <see cref="ApplyGridLight"/> when there is nothing per-entity to say. It is
    /// a no-op on surfaces that compiled to a plain <see cref="StandardMaterial3D"/> (no such uniform), which
    /// after the ResolveModelMaterial change should be nothing that counts as model geometry.</para>
    /// </summary>
    public static void EnableGridLight(Node root, bool on)
    {
        if (root is null)
            return;
        foreach (MeshInstance3D mi in Meshes(root))
            mi.SetInstanceShaderParameter(PlayerSkinShader.GridLitUniform, on ? 1f : 0f);
    }

    /// <summary>
    /// Override ONLY the <c>colormod</c> instance-uniform on a cached mesh list, leaving glow/shirt/pants as a
    /// prior <see cref="ApplyAppearance(IReadOnlyList{MeshInstance3D},int,bool,float,bool)"/> set them. Used by the
    /// frozen overlay to multiply a player's whole model toward icy-blue without disturbing its team colors (QC
    /// ENT_CLIENT_STATUSEFFECTS frozen tint). Pair with invalidating the appearance change-gate so the model
    /// repaints its real colormod the frame it thaws.
    /// </summary>
    public static void SetColormod(IReadOnlyList<MeshInstance3D> meshes, Color colormod)
    {
        for (int i = 0; i < meshes.Count; i++)
            meshes[i].SetInstanceShaderParameter(PlayerSkinShader.ColormodUniform, colormod);
    }

    /// <summary>
    /// Apply a player's team/colormap tint: shirt (high nibble) + pants (low nibble) masks AND the glow
    /// (DP sets glowmod from the pants color — QC <c>weaponentity_glowmod</c>'s fallback). A plain team
    /// nibble (1..4) keeps the team mapping; a packed colormap (≥1024 / non-zero high nibble, #43) paints
    /// the full <c>colormapPaletteColor</c> palette — the FFA profile colors. Colorless leaves the model
    /// untinted with a white — i.e. native — glow. Colormod stays white (no per-entity darkening here).
    /// </summary>
    public static void ApplyColormap(Node root, int colormap)
    {
        ColormapColors(colormap, out Color shirt, out Color pants, out bool hasColor);
        Apply(root, White,
            hasColor ? pants : White,
            hasColor ? shirt : Black,
            hasColor ? pants : Black);
    }

    /// <summary>
    /// Port of the GLOWMOD + death-fade + respawn-ghost tail of <c>CSQCPlayer_ModelAppearance_Apply</c>
    /// (csqcmodel_hooks.qc:331-360). Computes the glowmod from the colormap's low (pants) nibble via the full
    /// <c>colormapPaletteColor</c> palette, fades it while the model is dead (<c>cl_deathglow</c>), and divides
    /// by <c>r_hdr_glowintensity</c> when &gt;1. A respawn ghost with <c>cl_respawn_ghosts_keepcolors</c>=0
    /// zeroes the color and glow. The shirt/pants masks still ride the (team) colormap like
    /// <see cref="ApplyColormap"/>. <paramref name="deathTime"/> is the client-observed death timestamp (the
    /// wire doesn't carry the server <c>death_time</c>) — see the T58 parity note.
    /// </summary>
    public static void ApplyAppearance(Node root, int colormap, bool isDead, float deathTime, bool isRespawnGhost)
    {
        if (root is null)
            return;
        // One-shot (attach-time) callers route through the Node walk; the per-frame caller uses the cached-list
        // overload below so it never re-walks the tree. Flatten once, then share the identical math.
        var meshes = new List<MeshInstance3D>();
        foreach (MeshInstance3D mi in Meshes(root))
            meshes.Add(mi);
        ApplyAppearance(meshes, colormap, isDead, deathTime, isRespawnGhost);
    }

    /// <summary>
    /// Cached-list overload of <see cref="ApplyAppearance(Node,int,bool,float,bool)"/> for the per-frame
    /// appearance pass: identical glowmod + death-fade + respawn-ghost math, but it tints an already-flattened
    /// mesh list (from <see cref="CsqcModelEffects.GetCachedMeshes"/>) instead of re-walking the tree every frame
    /// (3.2-2). Bit-identical output to the Node overload for the same meshes/cvars. The list is non-null by
    /// construction; an empty list tints nothing (matches today's no-mesh case).
    /// </summary>
    public static void ApplyAppearance(IReadOnlyList<MeshInstance3D> meshes, int colormap, bool isDead, float deathTime, bool isRespawnGhost)
    {
        ComputeAppearance(colormap, isDead, deathTime, isRespawnGhost,
            out Color colormod, out Color glow, out Color shirt, out Color pants);
        Apply(meshes, colormod, glow, shirt, pants);
    }

    /// <summary>Last-applied tint snapshot for the per-frame change gate (§11 R8). Held by the caller's
    /// per-entity state; <see cref="ApplyAppearance(IReadOnlyList{MeshInstance3D},int,bool,float,bool,ref TintCache)"/>
    /// skips the 4×meshes <c>SetInstanceShaderParameter</c> interop when neither the computed colors nor the
    /// mesh list changed (the common case — colors only move while dead-fading or on a rainbow palette nibble).</summary>
    public struct TintCache
    {
        public Color Colormod, Glowmod, Shirt, Pants;
        public int MeshCount;
        public ulong FirstMeshId;
        public bool Valid;
    }

    /// <summary>Change-gated overload of the per-frame appearance pass (§11 R8): identical output colors,
    /// but the instance-uniform pushes only happen when the colors or the mesh list actually changed.</summary>
    public static void ApplyAppearance(IReadOnlyList<MeshInstance3D> meshes, int colormap, bool isDead,
        float deathTime, bool isRespawnGhost, ref TintCache cache)
    {
        ComputeAppearance(colormap, isDead, deathTime, isRespawnGhost,
            out Color colormod, out Color glow, out Color shirt, out Color pants);
        // Mesh-list identity: count + first id catches a model swap / placeholder→real rebuild (the new
        // meshes need the uniforms seeded even when the colors are unchanged).
        ulong firstId = meshes.Count > 0 ? meshes[0].GetInstanceId() : 0;
        if (cache.Valid && cache.MeshCount == meshes.Count && cache.FirstMeshId == firstId
            && cache.Colormod == colormod && cache.Glowmod == glow && cache.Shirt == shirt && cache.Pants == pants)
            return;
        Apply(meshes, colormod, glow, shirt, pants);
        cache = new TintCache
        {
            Colormod = colormod, Glowmod = glow, Shirt = shirt, Pants = pants,
            MeshCount = meshes.Count, FirstMeshId = firstId, Valid = true,
        };
    }

    /// <summary>The appearance math shared by both overloads (QC CSQCPlayer_ModelAppearance_Apply tail).</summary>
    private static void ComputeAppearance(int colormap, bool isDead, float deathTime, bool isRespawnGhost,
        out Color colormod, out Color glow, out Color shirtOut, out Color pantsOut)
    {
        colormod = White;

        // RESPAWNGHOST early-out (csqcmodel_hooks.qc:331-336): (csqcmodel_effects & CSQCMODEL_EF_RESPAWNGHOST)
        // && !cl_respawn_ghosts_keepcolors → glowmod '0 0 0', no shirt/pants, colormap 0. Decision via the shared
        // CsqcModelAppearance helper so the live render path and the unit-tested math are one source of truth.
        if (CsqcModelAppearance.RespawnGhostClearsColors(
                isRespawnGhost, keepColors: Cvar("cl_respawn_ghosts_keepcolors", 1f) != 0f))
        {
            glow = Black;            // glowmod '0 0 0', no shirt/pants, colormap 0
            shirtOut = Black;
            pantsOut = Black;
            return;
        }

        // Resolve the shirt/pants/glow colors from the colormap. A PLAIN networked team id (0..4) keeps the
        // team mapping (TeamColor); a packed/forced colormap (>=1024, or any non-zero high nibble — e.g. the
        // FORCECOLORS / unique-color combos) is decoded through the full colormapPaletteColor palette so
        // cl_forceplayercolors / cl_forcemyplayercolors / cl_forceuniqueplayercolors paint correctly.
        ColormapColors(colormap, out Color shirt, out Color pants, out bool hasColor);

        // GLOWMOD base (csqcmodel_hooks.qc:340-342): colormapPaletteColor(colormap & 0x0F /* pants */, true) when
        // colormap>0, else '1 1 1'. The pants color we just resolved IS colormapPaletteColor(lo, isPants:true), so
        // feed the shared BaseGlowmod helper its low nibble for the colored case and let it return white otherwise.
        (float gr, float gg, float gb) = CsqcModelAppearance.BaseGlowmod(hasColor, colormap & 0x0F, Now());
        glow = hasColor ? pants : new Color(gr, gg, gb);

        // DEATH FADING: scale glowmod by the death-fade factor while dead (cl_deathglow drives the rate).
        float deathglow = Cvar("cl_deathglow", 0f); // engine cvar default 0; xonotic-client.cfg sets 2 at runtime
        if (deathglow > 0f && isDead)
        {
            float minFactor = Cvar("cl_deathglow_min", 0.5f);
            float factor = CsqcModelAppearance.DeathGlowFactor(deathglow, minFactor, hasColor: colormap > 0,
                isDead: true, now: Now(), deathTime: deathTime);
            // QC glowmod-zero guard (csqcmodel_hooks.qc:353-354): a fully-black post-fade glowmod is nudged to
            // x=0.000001 so the engine doesn't treat it as "no glowmod". Via the shared GuardGlowmodZero helper.
            (float fr, float fg, float fb) = CsqcModelAppearance.GuardGlowmodZero(
                (glow.R * factor, glow.G * factor, glow.B * factor));
            glow = new Color(fr, fg, fb);
        }

        // Don't let the engine increase the player's glowmod (HDR, csqcmodel_hooks.qc:359-360): glowmod /=
        // r_hdr_glowintensity when >1. Via the shared ApplyHdrGlowClamp helper.
        (float hr, float hg, float hb) = CsqcModelAppearance.ApplyHdrGlowClamp(
            (glow.R, glow.G, glow.B), Cvar("r_hdr_glowintensity", 0f));
        glow = new Color(hr, hg, hb);

        // Shirt = high-nibble color, pants = low-nibble color (distinct for a packed/forced colormap; equal for a
        // plain team id; FFA/untinted = black masks).
        shirtOut = hasColor ? shirt : Black;
        pantsOut = hasColor ? pants : Black;
    }

    /// <summary>
    /// Map VortexArena's networked colormap/team value (the low nibble: 1=red, 2=blue, 3=yellow, 4=pink — see
    /// <c>RadarPanel.TeamColor</c>) to its team color, using Xonotic's canonical <c>colormapPaletteColor</c>
    /// RGBs (<c>lib/color.qh</c>). <paramref name="hasTeam"/> is false for FFA / unknown.
    /// </summary>
    public static Color TeamColor(int colormap, out bool hasTeam)
    {
        int team = colormap & 0x0F;
        hasTeam = team is >= 1 and <= 4;
        return team switch
        {
            1 => new Color(1f, 0f, 0f),       // red    (palette code 4)
            2 => new Color(0f, 0.333f, 1f),   // blue   (palette code 13)
            3 => new Color(1f, 1f, 0f),       // yellow (palette code 12)
            4 => new Color(1f, 0f, 0.5f),     // pink   (palette code 10)
            _ => White,
        };
    }

    /// <summary>
    /// Resolve a colormap's shirt (high nibble) + pants (low nibble) colors, mirroring QC's
    /// <c>colormapPaletteColor(nibble, isPants)</c> use. A PLAIN networked team id (high nibble 0, low nibble
    /// 1..4 — what the VortexArena wire carries in <c>Entity.Team</c>) keeps the team mapping (<see cref="TeamColor"/>),
    /// so team rendering is unchanged. A packed/forced colormap (any non-zero high nibble, e.g. the FORCECOLORS /
    /// unique-color combos which are <c>1024 + (shirt&lt;&lt;4) + pants</c>) is decoded through the full 0..15
    /// <c>colormapPaletteColor</c> palette so forced colors paint their real shirt/pants. <paramref name="hasColor"/>
    /// is false only for a truly colorless map (both nibbles 0 → FFA/untinted).
    /// </summary>
    private static void ColormapColors(int colormap, out Color shirt, out Color pants, out bool hasColor)
    {
        int lo = colormap & 0x0F;
        int hi = (colormap >> 4) & 0x0F;
        if (hi == 0 && lo is >= 1 and <= 4)
        {
            // Plain networked team id — preserve the existing team mapping for both masks.
            Color team = TeamColor(colormap, out hasColor);
            shirt = pants = team;
            return;
        }
        if (lo == 0 && hi == 0)
        {
            shirt = pants = White;
            hasColor = false;
            return;
        }
        // Packed/forced colormap → full palette (shirt = high nibble, pants = low nibble).
        float t = Now();
        (float sr, float sg, float sb) = CsqcModelAppearance.ColormapPaletteColor(hi, isPants: false, t);
        (float pr, float pg, float pb) = CsqcModelAppearance.ColormapPaletteColor(lo, isPants: true, t);
        shirt = new Color(sr, sg, sb);
        pants = new Color(pr, pg, pb);
        hasColor = true;
    }

    /// <summary>
    /// Read a float cvar live, honoring an explicit <c>0</c> (only an UNSET cvar — empty string — falls back).
    /// Mirrors <c>ViewEffects.Cvar</c>: so a user setting <c>cl_deathglow 0</c> disables the fade rather than
    /// being reinterpreted as "use the default 2". Returns <paramref name="fallback"/> when no services are wired.
    /// </summary>
    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>The simulation clock time (QC <c>time</c>), for the animated rainbow palette + death fade.</summary>
    private static float Now() => Api.Services?.Clock?.Time ?? 0f;

    /// <summary>Depth-first walk of every <see cref="MeshInstance3D"/> at or under <paramref name="node"/>.</summary>
    private static IEnumerable<MeshInstance3D> Meshes(Node node)
    {
        if (node is MeshInstance3D mi)
            yield return mi;
        foreach (Node child in node.GetChildren())
            foreach (MeshInstance3D m in Meshes(child))
                yield return m;
    }
}
