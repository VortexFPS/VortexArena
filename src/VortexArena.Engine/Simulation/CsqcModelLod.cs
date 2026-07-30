// Port of qcsrc/client/csqcmodel_hooks.qc — CSQCModel_LOD_Apply (lines 27-92). The level-of-detail model
// swap: at distance the engine renders a lower-poly _lod1/_lod2 variant of the model. Here the pure pieces are
// the lod-index selection math (which of 0/1/2 to use) and the "models/x.ext" -> "models/x_lodN.ext" name
// derivation; the actual model-file resolution + swap is done by the Godot glue (it has the asset pipeline),
// gated on the file existing (mirrors the QC fexists guard). Godot-free so it's unit-testable.

using System;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// The CSQC LOD selection (<c>CSQCModel_LOD_Apply</c>). <see cref="SelectLodIndex"/> reproduces the distance
/// math; <see cref="LodModelName"/> reproduces the <c>_lodN</c> filename derivation.
/// </summary>
public static class CsqcModelLod
{
    /// <summary>
    /// QC <c>CSQCModel_LOD_Apply</c> index pick (csqcmodel_hooks.qc:69-91). Returns 0 (base), 1 (_lod1) or
    /// 2 (_lod2).
    /// <para>When <paramref name="detailReduction"/> &lt;= 0 the LOD is forced by the (negative) step:
    /// &lt;=-2 → lod2, &lt;=-1 → lod1, else lod0. Otherwise it's distance-driven:
    /// <c>f = (distance * viewZoom + 100) * detailReduction</c>, then divided by
    /// <c>bound(0.01, viewQuality, 1)</c>; <c>f &gt; dist2</c> → lod2, <c>f &gt; dist1</c> → lod1, else lod0.</para>
    /// </summary>
    /// <param name="detailReduction">QC <c>cl_playerdetailreduction</c> (players, default 4) or
    /// <c>cl_modeldetailreduction</c> (default 1).</param>
    /// <param name="distance">QC <c>vlen((isplayer? origin : NearestPointOnBox(...)) - view_origin)</c>.</param>
    /// <param name="viewZoom">QC <c>current_viewzoom</c> (1 = unzoomed).</param>
    /// <param name="viewQuality">QC <c>view_quality</c> renderer global (default 1; the port passes 1).</param>
    /// <param name="dist1">Effective <c>cl_loddistance1</c> (cfg seta 1024).</param>
    /// <param name="dist2">Effective <c>cl_loddistance2</c> (cfg seta 3072).</param>
    public static int SelectLodIndex(int detailReduction, float distance, float viewZoom, float viewQuality, float dist1, float dist2)
    {
        if (detailReduction <= 0)
        {
            if (detailReduction <= -2) return 2;
            if (detailReduction <= -1) return 1;
            return 0;
        }

        float f = (distance * viewZoom + 100.0f) * detailReduction;
        f *= 1.0f / Bound(0.01f, viewQuality, 1f);
        if (f > dist2) return 2;
        if (f > dist1) return 1;
        return 0;
    }

    /// <summary>
    /// QC strcat-based <c>_lodN</c> name derivation (csqcmodel_hooks.qc:46/55):
    /// <c>strcat(substring(model, 0, strlen-4), "_lodN", substring(model, -4, 4))</c>. Inserts <c>_lodN</c>
    /// before the (3-letter, dot-prefixed) extension: <c>models/player/erebus.iqm</c> → <c>models/player/erebus_lod1.iqm</c>.
    /// FIXME (faithful to QC): only correct for 3-letter extensions (".iqm"/".md3"/".dpm"). For lod 0 returns
    /// the model unchanged.
    /// </summary>
    public static string LodModelName(string model, int lod)
    {
        if (string.IsNullOrEmpty(model) || lod <= 0)
            return model;
        if (model.Length < 4)
            return model; // QC substring(model, 0, strlen-4) would be empty/garbage; guard it
        string stem = model.Substring(0, model.Length - 4);
        string ext = model.Substring(model.Length - 4, 4); // the dot-prefixed 3-letter extension
        return stem + "_lod" + lod.ToString(System.Globalization.CultureInfo.InvariantCulture) + ext;
    }

    /// <summary>
    /// QC <c>NearestPointOnBox(ent, p)</c> for a box at <paramref name="absMin"/>..<paramref name="absMax"/>:
    /// component-wise clamp of <paramref name="p"/> into the box (the nearest surface point). Used by the
    /// non-player LOD distance (csqcmodel_hooks.qc:80) instead of the entity origin.
    /// </summary>
    public static System.Numerics.Vector3 NearestPointOnBox(System.Numerics.Vector3 absMin, System.Numerics.Vector3 absMax, System.Numerics.Vector3 p)
        => new(
            Bound(absMin.X, p.X, absMax.X),
            Bound(absMin.Y, p.Y, absMax.Y),
            Bound(absMin.Z, p.Z, absMax.Z));

    /// <summary>QC <c>bound(lo, v, hi)</c> ≡ <c>min(max(v, lo), hi)</c> (lib/math.qh). The valid LOD ranges
    /// always have lo &lt;= hi, so this is a plain clamp.</summary>
    private static float Bound(float lo, float v, float hi) => Math.Min(Math.Max(v, lo), hi);
}
