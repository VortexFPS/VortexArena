using System;
using System.Numerics;

namespace VortexArena.Common.Math;

/// <summary>
/// The orientation DarkPlaces gives an entity's MODEL when it draws it: the three Quake-space axes that the
/// model's local +X (forward), +Y (left) and +Z (up) are carried onto. This is the Godot-free half of the render
/// basis — the renderer only converts these three vectors with <c>Coords.ToGodot</c> and uses them as basis
/// columns, so no Euler angle (and no handedness or pitch-sign choice) is ever made on the Godot side.
///
/// <para>DarkPlaces builds the entity matrix with <c>Matrix4x4_CreateFromQuakeEntity(origin, pitch, yaw, roll)</c>
/// (matrixlib.c), whose columns are <c>AngleVectors</c>' forward, −right and up. Before that,
/// <c>CL_UpdateNetworkEntity</c> (cl_main.c) negates the pitch of every <c>mod_alias</c> model — "if model is
/// alias ... reverse pitch direction" — and the CSQC path does the same through <c>CL_GetPitchSign</c>. QuakeC
/// is written against that rule: <c>spawnfunc(misc_gamemodel)</c> flips <c>angles_x</c> precisely so the
/// engine's flip lands it back where the mapper put it (common/mapobjects/models.qc:207).</para>
///
/// <para>playtest-bugs #76 / #7: the port used to orient a yaw-only entity with the Godot Euler
/// <c>Rotation.Y = −yaw</c>, which is the MIRROR of the Quake rotation (Godot's +Y rotation carries +X toward
/// −Z, which is Quake +Y — so a Quake yaw θ is a Godot rotation of +θ, not −θ). Every map item placed at an
/// angle other than 0/180 faced the reflected direction, and the carried CTF flag counter-rotated.</para>
/// </summary>
public static class EntityRenderAxes
{
    /// <summary>
    /// True when DarkPlaces would load <paramref name="model"/> as <c>mod_alias</c> and therefore draw it with its
    /// pitch reversed. DP's alias loaders (model_alias.c) are MDL, MD2, MD3, ZYM, DPM, PSK and IQM; brush models
    /// (<c>*N</c>, <c>.bsp</c>), OBJ (<c>mod_obj</c>) and sprites (<c>mod_sprite</c>) keep their pitch.
    /// </summary>
    public static bool ReversesPitch(string? model)
    {
        if (string.IsNullOrEmpty(model) || model[0] == '*')
            return false;
        ReadOnlySpan<char> ext = System.IO.Path.GetExtension(model.AsSpan());
        return ext.Equals(".md3", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".iqm", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dpm", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mdl", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".md2", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".zym", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".psk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Quake-space images of the model's local forward (+X), left (+Y) and up (+Z) axes for an entity at
    /// <paramref name="angles"/> (pitch, yaw, roll in degrees, QC <c>.angles</c>), with DarkPlaces' alias-model
    /// pitch reversal applied when <paramref name="reversePitch"/> is set.
    /// </summary>
    public static void Axes(Vector3 angles, bool reversePitch, out Vector3 forward, out Vector3 left, out Vector3 up)
    {
        if (reversePitch)
            angles.X = -angles.X;
        QMath.AngleVectors(angles, out forward, out Vector3 right, out up);
        left = -right;
    }
}
