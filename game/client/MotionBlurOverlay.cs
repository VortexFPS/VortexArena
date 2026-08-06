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
    private static readonly StringName OffsetUniform = "blur_offset";

    private ColorRect? _rect;
    private ShaderMaterial? _mat;

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
        // The ColorRect is built LAZILY, on the first frame r_motionblur is non-zero - see EnsureRect.
    }

    /// <summary>
    /// Build the overlay on first use, and never if the feature stays off.
    ///
    /// <para><b>This is not a micro-optimisation.</b> The shader samples <c>hint_screen_texture</c>, and merely
    /// having such a material in the tree makes Godot maintain the 2-D back-buffer copy path for the viewport -
    /// a full-screen copy per frame, whether or not the rect is visible. Creating it eagerly cost roughly 6x on
    /// map load, because the load spends most of its time rendering frames for pipeline warm
    /// (render.setup, precache.weapons, gpu.warm-items) and every one of those frames paid the copy.</para>
    /// </summary>
    private void EnsureRect()
    {
        if (_rect is not null)
            return;
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
            if (_rect is not null)
                _rect!.Visible = false;
            _hasLast = false;
            return;
        }
        EnsureRect();

        Transform3D now = cam.GlobalTransform;
        if (!_hasLast)
        {
            _lastXform = now;
            _hasLast = true;
            _rect!.Visible = false;
            return;
        }

        // The arithmetic lives in VortexArena.Engine.Rendering.MotionBlurMath so it can be unit-tested: the
        // first version of this was correctly signed and about a tenth as strong as it needed to be, which no
        // screenshot could catch (the pass hides itself whenever the view is still). See MotionBlurMathTests.
        Vector3 fwdNow = -now.Basis.Z, fwdPrev = -_lastXform.Basis.Z;
        Vector3 move = now.Origin - _lastXform.Origin;
        _lastXform = now;

        System.Numerics.Vector2 o = VortexArena.Engine.Rendering.MotionBlurMath.Offset(
            N(fwdPrev), N(fwdNow), N(now.Basis.X), N(now.Basis.Y), N(move), (float)delta, strength);
        var offset = new Vector2(o.X, o.Y);

        if (offset.Length() < VortexArena.Engine.Rendering.MotionBlurMath.MinOffset)
        {
            _rect!.Visible = false;   // standing still: skip the pass entirely rather than blur by ~zero
            return;
        }

        _mat!.SetShaderParameter(OffsetUniform, offset);
        if (!_rect!.Visible)
        {
            _rect.Visible = true;
            if (!_everEngaged)
            {
                _everEngaged = true;
                // Once per session, on the first frame the blur actually draws. "Is motion blur on?" is
                // otherwise unanswerable without a video: the pass hides itself whenever the view is still,
                // so a screenshot of a stationary player looks identical either way.
                // Report the MEASURED smear, not just "on": the question a player actually has is whether it
                // is doing anything, and a percentage of screen width answers that where a boolean does not.
                VortexArena.Common.Diagnostics.Log.Info(
                    $"[motionblur] engaged: r_motionblur {Cvar("r_motionblur", 0f):0.##}, "
                    + $"smear {offset.Length() * 100f:0.0}% of screen width "
                    + $"(cap {VortexArena.Engine.Rendering.MotionBlurMath.MaxOffset * 100f:0}%).");
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
    const int TAPS = 9;
    vec3 sum = vec3(0.0);
    for (int i = 0; i < TAPS; i++) {
        float t = float(i) / float(TAPS - 1) - 0.5;   // -0.5 .. +0.5
        sum += texture(screen, SCREEN_UV + blur_offset * t).rgb;
    }
    COLOR = vec4(sum / float(TAPS), 1.0);
}
";

    /// <summary>Godot vector to System.Numerics, for the shared (Godot-free, testable) math above.</summary>
    private static System.Numerics.Vector3 N(Vector3 v) => new(v.X, v.Y, v.Z);

    private static float Cvar(string name, float fallback)
    {
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(name);
    }
}
