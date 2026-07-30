using System;
using VortexArena.Formats.Materials;

namespace VortexArena.Game.Loaders;

/// <summary>
/// Resolves a Q3 shader's <c>surfaceparm</c> set into the gameplay/collision side-channel the
/// BSP and model builders consume.
///
/// In a real Q3 map the per-face content/surface bits live in the BSP texture lump
/// (<c>Q3SURFACEFLAG_*</c> / <c>SUPERCONTENTS_*</c>), and that is the authority for collision. But a
/// great many Xonotic surfaces declare their gameplay intent only in the <c>.shader</c> via
/// <c>surfaceparm</c> (e.g. <c>nodraw</c>, <c>nonsolid</c>, <c>playerclip</c>, <c>lava</c>,
/// <c>nolightmap</c>), and model shaders have no BSP entry at all. <see cref="SurfaceFlags"/> turns
/// those parms into the same flag vocabulary the engine uses (<see cref="VortexArena.Engine.Collision.Q3SurfaceFlags"/>
/// and <see cref="VortexArena.Engine.Collision.SuperContents"/>) so a builder can OR them onto whatever the
/// BSP supplied — these are <b>gameplay</b> bits and must never be dropped (asset-pipeline.md §4).
///
/// The mapping mirrors q3map2 / Darkplaces <c>Mod_Q3BSP_LoadShaders</c> surfaceparm handling:
/// content parms (lava/slime/water/playerclip/…) set <c>SUPERCONTENTS</c> bits; rendering/movement
/// parms (slick/ladder/nodraw/nonsolid/nomarks/sky) set <c>Q3SURFACEFLAG</c> bits; and a parm that is
/// purely a renderer hint with no BSP analogue (e.g. <c>nolightmap</c>, <c>trans</c>) is surfaced as a
/// boolean on <see cref="SurfaceInfo"/>.
/// </summary>
public static class SurfaceFlags
{
    // Local aliases for the engine flag tables (VortexArena.Engine.Collision.*). Re-declared as constants
    // here so this file has no compile-time edge into the engine's internal layout beyond the two
    // public static classes; the values are the canonical Q3/DP bit definitions.
    // SUPERCONTENTS_* (Brush.cs SuperContents):
    private const int ContentsSolid       = 0x00000001;
    private const int ContentsWater       = 0x00000010;
    private const int ContentsSlime       = 0x00000020;
    private const int ContentsLava        = 0x00000040;
    private const int ContentsSky         = 0x00000080;
    private const int ContentsPlayerClip  = 0x00000100;
    private const int ContentsMonsterClip = 0x00000200;

    // Q3SURFACEFLAG_* (Brush.cs Q3SurfaceFlags):
    private const int SurfNoDamage = 0x0001;
    private const int SurfSlick    = 0x0002;
    private const int SurfSky      = 0x0004;
    private const int SurfLadder   = 0x0008;
    private const int SurfNoImpact = 0x0010;
    private const int SurfNoMarks  = 0x0020;
    private const int SurfFlesh    = 0x0040;
    private const int SurfNoDraw   = 0x0080;
    private const int SurfNoSteps  = 0x2000;
    private const int SurfNonSolid = 0x4000;

    /// <summary>
    /// The resolved gameplay/collision properties of a surface. <see cref="ContentFlags"/> and
    /// <see cref="Q3Flags"/> use the engine's <c>SUPERCONTENTS_*</c> / <c>Q3SURFACEFLAG_*</c> bit values
    /// (so a builder can OR them straight onto a BSP brush/face); the booleans expose the renderer-only
    /// hints the bit tables don't carry. A surface with no relevant parms resolves to
    /// <see cref="ContentFlags"/> == <see cref="ContentsSolid"/> (the Q3 default: a normal solid wall).
    /// </summary>
    public readonly struct SurfaceInfo
    {
        /// <summary>OR of <c>SUPERCONTENTS_*</c> content bits (solid/water/slime/lava/sky/clip/…).</summary>
        public int ContentFlags { get; init; }

        /// <summary>OR of <c>Q3SURFACEFLAG_*</c> surface bits (slick/ladder/nodraw/nonsolid/nomarks/sky/…).</summary>
        public int Q3Flags { get; init; }

        /// <summary>The mesh builder should emit no geometry for this surface (<c>surfaceparm nodraw</c>).</summary>
        public bool NoDraw { get; init; }

        /// <summary>The surface has no collision (<c>surfaceparm nonsolid</c> or it is a non-clip nodraw caulk-less hint).</summary>
        public bool NonSolid { get; init; }

        /// <summary>This is a sky surface (<c>surfaceparm sky</c>/<c>skyParms</c>): draw the skybox, no lightmap.</summary>
        public bool Sky { get; init; }

        /// <summary>Suppress the lightmap pass (<c>surfaceparm nolightmap</c>/sky/liquid/fog). NOT set by
        /// <c>trans</c> — a translucent surface still uses whatever lightmap the BSP face carries (DP keys the
        /// lightmap off the face's <c>lightmapindex</c>, not the surfaceparms).</summary>
        public bool NoLightmap { get; init; }

        /// <summary>The surface is translucent (<c>surfaceparm trans</c>): sort-order / vis hint only.</summary>
        public bool Translucent { get; init; }

        /// <summary>Low-friction floor (<c>surfaceparm slick</c>): physics applies reduced ground friction.</summary>
        public bool Slick { get; init; }

        /// <summary>Climbable surface (<c>surfaceparm ladder</c>): player movement treats it as a ladder.</summary>
        public bool Ladder { get; init; }

        /// <summary>Player-blocking clip volume (<c>surfaceparm playerclip</c>): invisible, blocks players only.</summary>
        public bool PlayerClip { get; init; }

        /// <summary>Monster/bot-blocking clip (<c>surfaceparm monsterclip</c>/<c>botclip</c>).</summary>
        public bool MonsterClip { get; init; }

        /// <summary>Liquid content (<c>surfaceparm water</c>/<c>slime</c>/<c>lava</c>) — see <see cref="ContentFlags"/>.</summary>
        public bool Liquid { get; init; }

        /// <summary>Convenience: any content bit beyond plain solid is set (water/slime/lava/clip/sky).</summary>
        public bool HasSpecialContent => (ContentFlags & ~ContentsSolid) != 0;
    }

    /// <summary>
    /// Resolve a parsed <see cref="ShaderDef"/> to its <see cref="SurfaceInfo"/>. Null resolves to a
    /// plain solid surface (the Q3 default for an unknown/textureless name).
    /// </summary>
    public static SurfaceInfo Resolve(ShaderDef? def)
    {
        if (def == null)
            return new SurfaceInfo { ContentFlags = ContentsSolid };

        int contents = 0;
        int surf = 0;
        bool noDraw = false, nonSolid = false, sky = false, noLightmap = false, translucent = false;
        bool slick = false, ladder = false, playerClip = false, monsterClip = false, liquid = false;
        bool sawStructuralContent = false; // a parm that establishes a non-default content type

        foreach (string raw in def.SurfaceParms)
        {
            // SurfaceParms is already lower-cased; switch on it directly.
            switch (raw)
            {
                // ---- content types ----
                case "water":
                    contents |= ContentsWater; liquid = true; noLightmap = true; sawStructuralContent = true; break;
                case "slime":
                    contents |= ContentsSlime; liquid = true; noLightmap = true; sawStructuralContent = true; break;
                case "lava":
                    contents |= ContentsLava; liquid = true; noLightmap = true; sawStructuralContent = true; break;

                case "playerclip":
                    contents |= ContentsPlayerClip; playerClip = true; noDraw = true; sawStructuralContent = true; break;
                case "monsterclip":
                case "botclip":
                    contents |= ContentsMonsterClip; monsterClip = true; noDraw = true; sawStructuralContent = true; break;
                case "clip":
                    // Q3 "clip" blocks everything: player + monster. Invisible.
                    contents |= ContentsPlayerClip | ContentsMonsterClip;
                    playerClip = true; monsterClip = true; noDraw = true; sawStructuralContent = true; break;

                // ---- sky ----
                case "sky":
                    contents |= ContentsSky; surf |= SurfSky; sky = true; noLightmap = true; sawStructuralContent = true; break;

                // ---- rendering / movement surface flags ----
                case "nodraw":
                    surf |= SurfNoDraw; noDraw = true; break;
                case "nonsolid":
                    surf |= SurfNonSolid; nonSolid = true; break;
                case "nomarks":
                case "nomarker":
                    surf |= SurfNoMarks; break;
                case "noimpact":
                    surf |= SurfNoImpact; break;
                case "nodamage":
                    surf |= SurfNoDamage; break;
                case "nosteps":
                    surf |= SurfNoSteps; break;
                case "slick":
                    surf |= SurfSlick; slick = true; break;
                case "ladder":
                    surf |= SurfLadder; ladder = true; break;
                case "flesh":
                    surf |= SurfFlesh; break;

                // ---- renderer-only hints (no BSP bit, but gameplay/visual relevant) ----
                case "nolightmap":
                    noLightmap = true; break;
                case "trans":
                    // Sort/vis hint ONLY — NOT a lightmap suppressor. DP keys a face's lightmap purely off the
                    // BSP lightmapindex (Mod_Q3BSP_LoadFaces), and q3map2 happily lightmaps trans surfaces:
                    // alpha-masked grates (exx/floor-grate01) and lit glass carry a real lightmap + a {$lightmap}
                    // modulate stage. Forcing noLightmap here dropped them to the unlit shader path → fullbright.
                    translucent = true; break;
                case "fog":
                    // Volumetric fog brush: never solid, no lightmap. Treated like nonsolid translucent.
                    nonSolid = true; translucent = true; noLightmap = true; surf |= SurfNonSolid; break;

                // ---- parms with no collision/render consequence we model (detail/structural/etc.) ----
                default:
                    break;
            }
        }

        // A surface that establishes no special content but is also not explicitly nonsolid/nodraw is a
        // normal solid wall. Clip/nodraw/sky/liquid surfaces are NOT additionally solid.
        if (!sawStructuralContent && !nonSolid)
            contents |= ContentsSolid;

        // nodraw without a content type still typically wants to remain solid (caulk): the structural
        // brush blocks movement even though it isn't drawn. Only the explicit clip/nonsolid/liquid/sky
        // parms above clear the solid bit.
        if (noDraw && !sawStructuralContent && !nonSolid)
            contents |= ContentsSolid;

        return new SurfaceInfo
        {
            ContentFlags = contents,
            Q3Flags = surf,
            NoDraw = noDraw,
            NonSolid = nonSolid,
            Sky = sky,
            NoLightmap = noLightmap,
            Translucent = translucent,
            Slick = slick,
            Ladder = ladder,
            PlayerClip = playerClip,
            MonsterClip = monsterClip,
            Liquid = liquid,
        };
    }

    /// <summary>Convenience: resolve straight from a surfaceparm set (e.g. when only the parms are at hand).</summary>
    public static SurfaceInfo Resolve(System.Collections.Generic.IEnumerable<string>? surfaceParms)
    {
        if (surfaceParms == null)
            return new SurfaceInfo { ContentFlags = ContentsSolid };
        var tmp = new ShaderDef();
        foreach (string p in surfaceParms)
            if (!string.IsNullOrEmpty(p))
                tmp.SurfaceParms.Add(p.ToLowerInvariant());
        return Resolve(tmp);
    }
}
