using Godot;
using VortexArena.Common.Framework;       // MoveFilter
using VortexArena.Common.Services;        // Api (trace)
using VortexArena.Game.Client;            // ClientWorld
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Net;

/// <summary>
/// Dev/CI visual-capture helpers that ride on a live <see cref="ClientWorld"/> — the survivors of GameDemo's
/// inline dev flags after the GameDemo→NetGame consolidation.
///
/// <para>Currently just <c>--fx-demo [effect]</c>: repeatedly bursts a named effect on the floor and at eye
/// level in front of the player so a windowed <c>--screenshot</c> always lands on a live burst (and its
/// <c>type decal</c> block accumulates a visible scorch), for effect-parity checks
/// (planning/weapon-effects-parity.md). The host (<see cref="NetGame"/>) drives it each frame with the
/// predicted eye + aim. Inert (zero work) unless an effect is configured, so it's safe to leave attached on
/// every listen-server boot.</para>
///
/// <para>GameDemo's other dev flags were dropped as obsolete by the consolidation — each has a better successor
/// on the real net path: <c>--weapon N</c> (real weapons switch on a listen server via the weapon binds),
/// <c>--proj-demo</c> (just fire the real weapon for a real projectile + trail), <c>--skeleton-smoke</c>
/// (the CPU posing is unit-tested in <c>SkeletonTests</c>, and the model renders via the <c>--model</c> viewer),
/// and the sample-model spawn (replaced wholesale by <c>--model</c> / <see cref="ModelViewer"/>).</para>
/// </summary>
public sealed partial class DevHarness : Node
{
    /// <summary>The live render world whose effect system the capture modes drive.</summary>
    public ClientWorld Render { get; init; } = null!;

    /// <summary>The effect name to burst for <c>--fx-demo</c>, or null when that mode is off.</summary>
    public string? FxDemoEffect { get; init; }

    private float _fxTimer;

    /// <summary>True when any capture mode is active, so the host can skip the per-frame <see cref="Drive"/> call.</summary>
    public bool Active => FxDemoEffect is not null;

    /// <summary>
    /// Drive the active capture mode for this frame. <paramref name="eyeQuake"/> is the player's eye and
    /// <paramref name="forwardQuake"/> the aim direction, both in Quake space (the host computes them from the
    /// predicted origin + view angles).
    /// </summary>
    public void Drive(float dt, NVec3 eyeQuake, NVec3 forwardQuake)
    {
        if (FxDemoEffect is null)
            return;

        _fxTimer -= dt;
        if (_fxTimer > 0f)
            return;
        _fxTimer = 0.12f; // re-burst cadence — frequent enough that a fresh burst is always on screen

        // Aim a point ~120u ahead, then drop to the floor under it so the burst rises into view and any scorch
        // decal lands on a real surface; fall back to the forward point on an open drop.
        NVec3 ahead = eyeQuake + forwardQuake * 120f;
        var down = Api.Trace.Trace(
            ahead, NVec3.Zero, NVec3.Zero, ahead - new NVec3(0f, 0f, 400f),
            MoveFilter.WorldOnly, null);
        NVec3 floor = down.Fraction < 1f ? down.EndPos + new NVec3(0f, 0f, 6f) : ahead;

        // Floor burst (scorch decal lands here) + an eye-level burst centered in view (fireball always visible
        // regardless of facing) + a bullet-impact burst (impact sprite/decal path).
        Render.Effects.Spawn(FxDemoEffect, floor);
        Render.Effects.Spawn(FxDemoEffect, eyeQuake + forwardQuake * 110f + new NVec3(0f, 0f, 45f));
        Render.Effects.Spawn("machinegun_impact", floor + forwardQuake * 30f, -forwardQuake);

        // If the configured effect is a TRAIL/beam (nex_beam, arc_beam, TR_ROCKET…), also sweep it across the
        // view as a line so a capture can verify the trail-along-the-segment path (the vortex beam line).
        VortexArena.Common.Gameplay.Effect? eff =
            VortexArena.Common.Gameplay.Effects.ByEffectInfoName(FxDemoEffect)
            ?? VortexArena.Common.Gameplay.Effects.ByName(FxDemoEffect);
        if (eff?.IsTrail == true)
            Render.Effects.Spawn(FxDemoEffect, eyeQuake + forwardQuake * 20f + new NVec3(0f, 0f, 10f),
                eyeQuake + forwardQuake * 600f + new NVec3(0f, 0f, 10f));
    }
}
