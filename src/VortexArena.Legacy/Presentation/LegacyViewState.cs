// Port of Base/darkplaces/clvm_cmds.c VM_CL_R_ClearScene (the reset of the view), CSQC_R_RecalcView,
// VM_CL_R_SetView (both directions: #303 setproperty and #309 getproperty), VM_CL_project and
// VM_CL_unproject; and cl_screen.c SCR_DrawScreen's "frustum_y = tan(scr_fov * pi / 360) * 3/4 * viewzoom"
// block, which is the view the engine hands the program before the program changes it.
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Presentation;

/// <summary>
/// r_refdef.view as a client program sees it: the values behind every VF_* key of setproperty /
/// getproperty, the reset clearscene performs, and the two projection builtins that are computed from
/// them. No renderer is involved - a renderer reads the result (<see cref="Origin"/>,
/// <see cref="Angles"/>, <see cref="VerticalFovDegrees"/>, the viewport) when renderscene is called.
///
/// DarkPlaces keeps the field of view as two tangents (frustum_x, frustum_y) and, separately, the
/// angles they came from (ortho_x, ortho_y, "abused as angle by VM_CL_R_SetView"). Xonotic's "fov"
/// cvar is the HORIZONTAL angle of a 4:3 picture; the engine default below converts it the way
/// SCR_DrawScreen does, and a program that sets VF_FOV itself supplies both angles.
/// </summary>
public sealed class LegacyViewState
{
    // csprogs.h
    public const int VfMin = 1, VfMinX = 2, VfMinY = 3, VfSize = 4, VfSizeX = 5, VfSizeY = 6, VfViewport = 7,
        VfFov = 8, VfFovX = 9, VfFovY = 10, VfOrigin = 11, VfOriginX = 12, VfOriginY = 13, VfOriginZ = 14,
        VfAngles = 15, VfAnglesX = 16, VfAnglesY = 17, VfAnglesZ = 18, VfDrawWorld = 19, VfDrawEngineSbar = 20,
        VfDrawCrosshair = 21, VfPerspective = 200, VfClearScreen = 201, VfFogDensity = 202, VfFogColor = 203,
        VfFogColorR = 204, VfFogColorG = 205, VfFogColorB = 206, VfFogAlpha = 207, VfFogStart = 208, VfFogEnd = 209,
        VfFogHeight = 210, VfFogFadeDepth = 211, VfMainView = 400, VfMinFpsQuality = 401;

    // What the engine supplies each frame (csqc_original_r_refdef_view, cl.csqc_vieworiginfromengine).
    private float _engineWidth = 1024, _engineHeight = 768, _engineFrustumX = 1, _engineFrustumY = 0.75f;
    private QcVector _engineOrigin, _engineAngles;

    /// <summary>The viewport in window pixels (r_refdef.view.x / y / width / height).</summary>
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Width { get; private set; } = 1024;
    public int Height { get; private set; } = 768;
    /// <summary>Tangents of half the horizontal and vertical field of view.</summary>
    public float FrustumX { get; private set; } = 1;
    public float FrustumY { get; private set; } = 0.75f;
    /// <summary>The angles last given for the field of view, in degrees (what getproperty answers).</summary>
    public float FovX { get; private set; } = 90;
    public float FovY { get; private set; } = 73.74f;
    /// <summary>cl.csqc_vieworigin / cl.csqc_viewangles.</summary>
    public QcVector Origin { get; private set; }
    public QcVector Angles { get; private set; }
    public bool DrawWorld { get; private set; } = true;
    public bool DrawEngineSbar { get; private set; }
    public bool DrawCrosshair { get; private set; }
    public bool UsePerspective { get; private set; } = true;
    /// <summary>r_refdef.view.isoverlay: VF_CLEARSCREEN 0 draws over what is there instead of clearing it.</summary>
    public bool IsOverlay { get; private set; }
    /// <summary>r_refdef.view.ismain: set by VF_MAINVIEW, read back by getproperty.</summary>
    public bool IsMainView { get; private set; }
    public float Quality { get; private set; } = 1;
    public float FogDensity { get; private set; }
    public QcVector FogColor { get; private set; }
    public float FogAlpha { get; private set; } = 1;
    public float FogStart { get; private set; }
    public float FogEnd { get; private set; } = 16384;
    public float FogHeight { get; private set; } = 1 << 30;
    public float FogFadeDepth { get; private set; } = 128;
    /// <summary>Whether the program changed any fog key since the last reset (a renderer then overrides the map's fog).</summary>
    public bool FogTouched { get; private set; }

    /// <summary>vid_conwidth / vid_conheight: the 2D coordinate space cs_project answers in.</summary>
    public float ConWidth { get; set; } = 800;
    public float ConHeight { get; set; } = 600;
    /// <summary>r_drawworld.</summary>
    public bool EngineDrawWorld { get; set; } = true;

    /// <summary>The vertical field of view a perspective camera needs, in degrees.</summary>
    public float VerticalFovDegrees => MathF.Atan(FrustumY) * (360f / MathF.PI);

    /// <summary>
    /// SCR_DrawScreen: frustum_y = tan(fov/2) * 3/4 * viewzoom, frustum_x = frustum_y * width / height.
    /// <paramref name="fov"/> is Xonotic's "fov" cvar, a horizontal angle at 4:3; the result is the
    /// vertical angle of a picture <paramref name="width"/> by <paramref name="height"/> pixels.
    /// </summary>
    public static void EngineFrustum(float fov, float viewZoom, float width, float height, out float frustumX, out float frustumY)
    {
        frustumY = MathF.Tan(fov * MathF.PI / 360f) * 0.75f * viewZoom;
        frustumX = height > 0 ? frustumY * width / height : frustumY;
    }

    /// <summary>The vertical field of view, in degrees, for Xonotic's horizontal-at-4:3 <paramref name="fov"/>.</summary>
    public static float VerticalFov(float fov, float viewZoom = 1)
    {
        EngineFrustum(fov, viewZoom, 4, 3, out _, out float frustumY);
        return MathF.Atan(frustumY) * (360f / MathF.PI);
    }

    /// <summary>
    /// The view the engine computed for this frame, before the program runs: what clearscene restores.
    /// </summary>
    /// <param name="width">vid.mode.width / height: the window in pixels.</param>
    /// <param name="origin">The eye of the view entity (V_CalcRefdef's result).</param>
    /// <param name="fov">scr_fov ("fov"), clamped to DarkPlaces' 1..170.</param>
    /// <param name="viewZoom">cl.viewzoom: STAT_VIEWZOOM / 255.</param>
    public void SetEngineView(float width, float height, QcVector origin, QcVector angles, float fov, float viewZoom)
    {
        _engineWidth = Sane(width, 1, 16384, 1024);
        _engineHeight = Sane(height, 1, 16384, 768);
        _engineOrigin = origin;
        _engineAngles = angles;
        EngineFrustum(Sane(fov, 1, 170, 90), Sane(viewZoom, 1f / 256, 16, 1), _engineWidth, _engineHeight, out _engineFrustumX, out _engineFrustumY);
    }

    private static float Sane(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    /// <summary>VM_CL_R_ClearScene: "restore the view settings to the values that VM_CL_UpdateView received from the client code".</summary>
    public void Reset()
    {
        X = Y = 0;
        Width = (int)_engineWidth;
        Height = (int)_engineHeight;
        FrustumX = _engineFrustumX;
        FrustumY = _engineFrustumY;
        FovX = MathF.Atan(FrustumX) * (360f / MathF.PI);
        FovY = MathF.Atan(FrustumY) * (360f / MathF.PI);
        Origin = _engineOrigin;
        Angles = _engineAngles;
        DrawWorld = EngineDrawWorld;
        DrawEngineSbar = DrawCrosshair = false;
        UsePerspective = true;
        IsOverlay = false;
        IsMainView = false;
        FogTouched = false;
    }

    private static int Whole(float value) => float.IsFinite(value) ? (int)Math.Clamp(value, -65536f, 65536f) : 0;

    /// <summary>#303 setproperty. False for a key DarkPlaces does not know ("unknown parm").</summary>
    public bool Set(int property, QcVector a, QcVector b)
    {
        float k = a.X;
        QcVector v;
        switch (property)
        {
            case VfMin: X = Whole(a.X); Y = Whole(a.Y); break;
            case VfMinX: X = Whole(k); break;
            case VfMinY: Y = Whole(k); break;
            case VfSize: Width = Whole(a.X); Height = Whole(a.Y); break;
            case VfSizeX: Width = Whole(k); break;
            case VfSizeY: Height = Whole(k); break;
            case VfViewport: X = Whole(a.X); Y = Whole(a.Y); Width = Whole(b.X); Height = Whole(b.Y); break;
            case VfFov:
                FrustumX = MathF.Tan(a.X * MathF.PI / 360f); FovX = a.X;
                FrustumY = MathF.Tan(a.Y * MathF.PI / 360f); FovY = a.Y;
                break;
            case VfFovX: FrustumX = MathF.Tan(k * MathF.PI / 360f); FovX = k; break;
            case VfFovY: FrustumY = MathF.Tan(k * MathF.PI / 360f); FovY = k; break;
            case VfOrigin: Origin = a; break;
            case VfOriginX: v = Origin; v.X = k; Origin = v; break;
            case VfOriginY: v = Origin; v.Y = k; Origin = v; break;
            case VfOriginZ: v = Origin; v.Z = k; Origin = v; break;
            case VfAngles: Angles = a; break;
            case VfAnglesX: v = Angles; v.X = k; Angles = v; break;
            case VfAnglesY: v = Angles; v.Y = k; Angles = v; break;
            case VfAnglesZ: v = Angles; v.Z = k; Angles = v; break;
            case VfDrawWorld: DrawWorld = k != 0 && EngineDrawWorld; break;
            case VfDrawEngineSbar: DrawEngineSbar = k != 0; break;
            case VfDrawCrosshair: DrawCrosshair = k != 0; break;
            case VfPerspective: UsePerspective = k != 0; break;
            case VfClearScreen: IsOverlay = k == 0; break;
            case VfMainView: IsMainView = k != 0; break;
            case VfFogDensity: FogDensity = k; FogTouched = true; break;
            case VfFogColor: FogColor = a; FogTouched = true; break;
            case VfFogColorR: v = FogColor; v.X = k; FogColor = v; FogTouched = true; break;
            case VfFogColorG: v = FogColor; v.Y = k; FogColor = v; FogTouched = true; break;
            case VfFogColorB: v = FogColor; v.Z = k; FogColor = v; FogTouched = true; break;
            case VfFogAlpha: FogAlpha = k; FogTouched = true; break;
            case VfFogStart: FogStart = k; FogTouched = true; break;
            case VfFogEnd: FogEnd = k; FogTouched = true; break;
            case VfFogHeight: FogHeight = k; FogTouched = true; break;
            case VfFogFadeDepth: FogFadeDepth = k; FogTouched = true; break;
            case VfMinFpsQuality: Quality = k; break;
            default: return false;
        }
        return true;
    }

    /// <summary>#309 getproperty. A float answer is in X. False for an unknown key; VF_VIEWPORT
    /// "can't be retrieved" and answers zero, as the C does after its warning.</summary>
    public bool Get(int property, out QcVector value)
    {
        value = default;
        switch (property)
        {
            case VfMin: value = new QcVector(X, Y, 0); break;
            case VfMinX: value.X = X; break;
            case VfMinY: value.X = Y; break;
            case VfSize: value = new QcVector(Width, Height, 0); break;
            case VfSizeX: value.X = Width; break;
            case VfSizeY: value.X = Height; break;
            case VfViewport: break;
            case VfFov: value = new QcVector(FovX, FovY, 0); break;
            case VfFovX: value.X = FovX; break;
            case VfFovY: value.X = FovY; break;
            case VfOrigin: value = Origin; break;
            case VfOriginX: value.X = Origin.X; break;
            case VfOriginY: value.X = Origin.Y; break;
            case VfOriginZ: value.X = Origin.Z; break;
            case VfAngles: value = Angles; break;
            case VfAnglesX: value.X = Angles.X; break;
            case VfAnglesY: value.X = Angles.Y; break;
            case VfAnglesZ: value.X = Angles.Z; break;
            case VfDrawWorld: value.X = DrawWorld ? 1 : 0; break;
            case VfDrawEngineSbar: value.X = DrawEngineSbar ? 1 : 0; break;
            case VfDrawCrosshair: value.X = DrawCrosshair ? 1 : 0; break;
            case VfPerspective: value.X = UsePerspective ? 1 : 0; break;
            case VfClearScreen: value.X = IsOverlay ? 1 : 0; break;
            case VfMainView: value.X = IsMainView ? 1 : 0; break;
            case VfFogDensity: value.X = FogDensity; break;
            case VfFogColor: value = FogColor; break;
            // The C writes only one component of the return vector for these three; the first is what a float read sees.
            case VfFogColorR: value.X = FogColor.X; break;
            case VfFogColorG: value.Y = FogColor.Y; break;
            case VfFogColorB: value.Z = FogColor.Z; break;
            case VfFogAlpha: value.X = FogAlpha; break;
            case VfFogStart: value.X = FogStart; break;
            case VfFogEnd: value.X = FogEnd; break;
            case VfFogHeight: value.X = FogHeight; break;
            case VfFogFadeDepth: value.X = FogFadeDepth; break;
            case VfMinFpsQuality: value.X = Quality; break;
            default: return false;
        }
        return true;
    }

    // r_refdef.view.matrix (Matrix4x4_CreateFromQuakeEntity of the origin and angles, scale 1): its
    // columns are forward, left and up.
    private void Axes(out QcVector forward, out QcVector left, out QcVector up)
    {
        QcCoreBuiltins.AngleVectors(Angles, out forward, out QcVector right, out up);
        left = new QcVector(-right.X, -right.Y, -right.Z);
    }

    /// <summary>
    /// #311 cs_project: a world point to virtual-screen pixels, with the distance along the view in Z.
    /// A point in the plane of the eye divides by zero, as it does in the C; the program tests Z first.
    /// </summary>
    public QcVector Project(QcVector world)
    {
        Axes(out QcVector forward, out QcVector left, out QcVector up);
        float dx = world.X - Origin.X, dy = world.Y - Origin.Y, dz = world.Z - Origin.Z;
        // Matrix4x4_Invert_Full of a rotation and a translation: the offset along each axis.
        float v0 = dx * forward.X + dy * forward.Y + dz * forward.Z;
        float v1 = dx * left.X + dy * left.Y + dz * left.Z;
        float v2 = dx * up.X + dy * up.Y + dz * up.Z;
        return new QcVector(
            ConWidth * (0.5f * (1.0f + v1 / v0 / -FrustumX)),
            ConHeight * (0.5f * (1.0f + v2 / v0 / -FrustumY)),
            v0);
    }

    /// <summary>#310 cs_unproject: virtual-screen pixels and a distance along the view, to a world point.</summary>
    public QcVector Unproject(QcVector screen)
    {
        Axes(out QcVector forward, out QcVector left, out QcVector up);
        float t0 = screen.Z;
        float t1 = (-1.0f + 2.0f * (screen.X / ConWidth)) * screen.Z * -FrustumX;
        float t2 = (-1.0f + 2.0f * (screen.Y / ConHeight)) * screen.Z * -FrustumY;
        return new QcVector(
            Origin.X + t0 * forward.X + t1 * left.X + t2 * up.X,
            Origin.Y + t0 * forward.Y + t1 * left.Y + t2 * up.Y,
            Origin.Z + t0 * forward.Z + t1 * left.Z + t2 * up.Z);
    }
}
