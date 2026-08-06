using System;
using Godot;
using VortexArena.Game.Menu;

namespace VortexArena.Game.Client;

/// <summary>
/// Camera motion blur — the port of DarkPlaces' <c>r_motionblur</c>, which the Effects tab has always had a
/// checkbox and a strength slider for and which nothing read until now.
///
/// <para><b>What DarkPlaces does, and what this does instead.</b> DP's motion blur is an <i>accumulation</i>
/// blur (<c>R_MotionBlurView</c>): it keeps the previous frame and blends it over the current one, weighted by
/// <c>r_motionblur</c> and normalised against frame time so the effect does not change with framerate. That
/// needs a persistent full-resolution colour buffer and a copy every frame. This is a <b>directional</b> blur
/// instead: it measures how far the view moved since the last frame and smears the screen along that vector,
/// sampling the screen texture a handful of times. No persistent buffer, no per-frame copy, and — the part
/// that actually matters — it reads the same way to a player, because what you notice is the smear when you
/// flick the mouse.</para>
///
/// <para>Two visible differences, stated rather than hidden: DP smears a moving <i>object</i> against a still
/// camera (accumulation catches that; a camera-velocity blur cannot), and DP's trail is a true exponential
/// history where this is a straight-line smear over one frame's worth of motion.</para>
///
/// <para><b>Framerate independence matters more here than it looks.</b> Blurring by "pixels moved since the
/// last frame" would make the effect vanish at high framerates and smear violently at low ones — the setting
/// would mean something different on every machine. The offset is therefore scaled to a reference frame time,
/// so <c>r_motionblur 0.4</c> looks like 0.4 at 60 fps and at 300 fps.</para>
///
/// <para><b>Cvars.</b> <c>r_motionblur</c> is both the switch and the strength (0 = off; the menu's checkbox
/// writes 0.4, its slider 0.1..1) — that is DP's own arrangement, and the reason the tab has a checkbox and a
/// slider bound to one cvar. Off by default, and off in every effects preset.</para>
/// </summary>
public sealed partial class MotionBlurOverlay : CanvasLayer
{
    /// <summary>Frame time the strength is normalised against (~60 fps). See the framerate note above.</summary>
    private const float ReferenceFrameTime = 1f / 60f;

    /// <summary>Hard cap on the smear in UV units, so a teleport or a respawn cannot streak the whole screen.</summary>
    private const float MaxOffset = 0.05f;

    private static readonly StringName OffsetUniform = "blur_offset";

    private ColorRect _rect = null!;
    private ShaderMaterial _mat = null!;

    private Camera3D? _camera;
    private Transform3D _lastXform = Transform3D.Identity;
    private bool _hasLast;
    private bool _everEngaged;

    /// <summary>The camera whose motion drives the blur. Set by the client host each map.</summary>
    public Camera3D? Camera
    {
        get => _camera;
        set { _camera = value; _hasLast = false; }
    }

    public override void _Ready()
    {
        // Above the 3-D scene but BELOW the HUD: a blurred crosshair or scoreboard would be a bug, not an
        // effect. ViewEffects sits at -1 (under everything); the net HUD is at 5.
        Layer = 1;

        _mat = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        _rect = new ColorRect
        {
            Name = "MotionBlur",
            Material = _mat,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_rect);
    }

    public override void _Process(double delta)
    {
        using var _prof = FrameProfiler.Scope("motionblur");

        float strength = Cvar("r_motionblur", 0f);
        Camera3D? cam = _camera;
        if (strength <= 0f || cam is null || !GodotObject.IsInstanceValid(cam))
        {
            _rect.Visible = false;
            _hasLast = false;
            return;
        }

        Transform3D now = cam.GlobalTransform;
        if (!_hasLast)
        {
            _lastXform = now;
            _hasLast = true;
            _rect.Visible = false;
            return;
        }

        // Screen-space motion from the two poses. Rotation dominates what a player perceives (a mouse flick),
        // so it is measured directly as an angle; translation is converted to an angle at a nominal distance
        // so a strafe smears too, without needing per-pixel depth.
        Vector3 fwdNow = -now.Basis.Z, fwdPrev = -_lastXform.Basis.Z;
        float yaw = Mathf.Atan2(fwdNow.X, fwdNow.Z) - Mathf.Atan2(fwdPrev.X, fwdPrev.Z);
        yaw = Mathf.Wrap(yaw, -Mathf.Pi, Mathf.Pi);
        float pitch = Mathf.Asin(Mathf.Clamp(fwdNow.Y, -1f, 1f)) - Mathf.Asin(Mathf.Clamp(fwdPrev.Y, -1f, 1f));

        Vector3 move = now.Origin - _lastXform.Origin;
        // Sideways/vertical translation, expressed as an angle at 512 units - roughly "how far a mid-distance
        // wall slid across the screen". Forward motion is deliberately ignored: it produces a zoom blur, which
        // at these speeds reads as a rendering fault rather than as motion.
        float sideAngle = now.Basis.X.Dot(move) / 512f;
        float upAngle = now.Basis.Y.Dot(move) / 512f;

        _lastXform = now;

        // Normalise to the reference frame time so the setting means the same thing at any framerate.
        float dt = MathF.Max((float)delta, 1e-4f);
        float norm = ReferenceFrameTime / dt;
        var offset = new Vector2((yaw + sideAngle) * norm, (pitch + upAngle) * norm) * strength * 0.5f;

        if (offset.Length() < 0.0005f)
        {
            _rect.Visible = false;   // standing still: skip the pass entirely rather than blur by ~zero
            return;
        }
        if (offset.Length() > MaxOffset)
            offset = offset.Normalized() * MaxOffset;

        _mat.SetShaderParameter(OffsetUniform, offset);
        if (!_rect.Visible)
        {
            _rect.Visible = true;
            if (!_everEngaged)
            {
                _everEngaged = true;
                // Once per session, on the first frame the blur actually draws. "Is motion blur on?" is
                // otherwise unanswerable without a video: the pass hides itself whenever the view is still,
                // so a screenshot of a stationary player looks identical either way.
                VortexArena.Common.Diagnostics.Log.Info(
                    $"[motionblur] engaged (r_motionblur {Cvar("r_motionblur", 0f)}).");
            }
        }
    }

    private const string ShaderCode = @"// VortexArena camera motion blur (r_motionblur). Generated in C#.
shader_type canvas_item;
// The screen as it stands before this overlay draws. filter_linear so the taps between texels do not alias.
uniform sampler2D screen : hint_screen_texture, filter_linear;
// Smear vector in UV units, from the camera's motion since the previous frame.
uniform vec2 blur_offset = vec2(0.0);

void fragment() {
    // Symmetric taps about the current pixel: a one-sided smear drags the whole image in the direction of
    // travel, which reads as the view lagging rather than as motion.
    const int TAPS = 5;
    vec3 sum = vec3(0.0);
    for (int i = 0; i < TAPS; i++) {
        float t = float(i) / float(TAPS - 1) - 0.5;   // -0.5 .. +0.5
        sum += texture(screen, SCREEN_UV + blur_offset * t).rgb;
    }
    COLOR = vec4(sum / float(TAPS), 1.0);
}
";

    private static float Cvar(string name, float fallback)
    {
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(name);
    }
}
