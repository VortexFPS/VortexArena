// Port of the CSQC Porto_Draw trajectory preview (qcsrc/common/weapons/weapon/porto.qc:17-88) — the
// reflecting red/blue cylindric-line polyline that, while the local player holds the Port-O-Launch, predicts
// where the in/out portals would land.
//
// Base Porto_Draw early-returns unless the player is alive, not spectating, not at intermission, AND
// g_balance_porto_secondary is FALSE (the non-default combined-shot mode). It then traces the shot path from
// the eye along view_forward with up to 2 portal placements, extending the trace through slick/playerclip
// faces (reflect + continue) and stopping at noimpact/oversize faces, building a 16-point polyline; the
// segments BEFORE the first portal point are drawn red, the rest blue (Draw_CylindricLine width 4).
//
// Like LaserRenderer this is a persistent self-driving client node hosted by ClientWorld. It draws each
// polyline segment through the shared CylindricLine segment pool (the proper Draw_CylindricLine stand-in),
// acquiring one pooled segment per leg and updating it in place each frame, and reads the local player's
// eye/aim from the host-supplied providers (camera + view angles + active weapon id). It draws nothing
// unless a porto is held.

using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Math;
using VortexArena.Common.Services;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>The Porto_Draw portal-aim trajectory preview (red/blue reflecting polyline). Hosted by
/// <see cref="ClientWorld"/>; idle unless the local player is alive and holding the Port-O-Launch in the
/// non-default combined-shot mode (g_balance_porto_secondary 0).</summary>
public sealed partial class PortoTrajectoryPreview : Node3D
{
    /// <summary>Host provider for the local player's view angles (DEGREES, Quake convention). Null = idle.</summary>
    public Func<NVec3?>? ViewAnglesProvider { get; set; }

    /// <summary>Host provider for the local player's active weapon registry id (QC activeweapon). -1 = none.</summary>
    public Func<int>? ActiveWeaponProvider { get; set; }

    /// <summary>Host predicate: true while the local player is dead / spectating / at intermission (suppresses the
    /// preview, QC Porto_Draw:19 spectatee_status || intermission || STAT(HEALTH) &lt;= 0).</summary>
    public Func<bool>? SuppressedProvider { get; set; }

    /// <summary>Host-injected shared cylindric-line segment pool (the Draw_CylindricLine stand-in). Null = idle;
    /// the preview acquires one pooled <see cref="CylindricLine.Segment"/> per polyline leg.</summary>
    public CylindricLine? Lines { get; set; }

    // QC Porto_Draw constants.
    private const int PolylineLength = 16;          // QC polyline_length
    private const float LineWidth = 4f;             // QC Draw_CylindricLine width 4
    private const float TraceDist = 65536f;         // QC traceline(pos, pos + 65536 * dir)
    private const int Q3SurfaceFlagSlick = 0x2;     // Q3SURFACEFLAG_SLICK
    private const int Q3SurfaceFlagNoImpact = 0x10; // Q3SURFACEFLAG_NOIMPACT
    private const int DpContentsPlayerClip = 256;   // DPCONTENTS_PLAYERCLIP
    private const float WireframeBox = 96f;         // QC CheckWireframeBox(... 96 * v_right/up/forward)

    private static readonly Color Red = new(1f, 0f, 0f);
    private static readonly Color Blue = new(0f, 0f, 1f);

    // One pooled CylindricLine segment per polyline leg (acquired from the shared pool, updated in place).
    private readonly List<CylindricLine.Segment> _segments = new();

    public override void _Process(double delta)
    {
        // QC Porto_Draw early-returns unless a porto is held in the non-secondary mode and the player is alive.
        if (Api.Services is null || ViewAnglesProvider is null || ActiveWeaponProvider is null || Lines is null)
        {
            HideAll();
            return;
        }
        if (SuppressedProvider?.Invoke() == true || PortoSecondary())
        {
            HideAll();
            return;
        }
        int wepId = ActiveWeaponProvider.Invoke();
        if (wepId < 0 || wepId >= Registry<Weapon>.Count || Registry<Weapon>.ById(wepId) is not Porto)
        {
            HideAll();
            return;
        }
        NVec3? va = ViewAnglesProvider.Invoke();
        if (va is null)
        {
            HideAll();
            return;
        }

        using var _prof = FrameProfiler.Scope("clientmisc");
        BuildPolyline(va.Value);
    }

    /// <summary>QC WEP_CVAR(WEP_PORTO, secondary): the default-1 secondary mode hides the preview entirely.</summary>
    private static bool PortoSecondary()
    {
        string s = Api.Cvars.GetString("g_balance_porto_secondary");
        // Unset → Xonotic default 1 (secondary mode on → no preview). Only an explicit 0 enables the preview.
        return string.IsNullOrWhiteSpace(s) || Api.Cvars.GetFloat("g_balance_porto_secondary") != 0f;
    }

    // QC Porto_Draw body (porto.qc:41-86): trace + reflect, build the 16-point polyline, draw red-then-blue.
    private void BuildPolyline(NVec3 viewAngles)
    {
        // Eye + aim straight along view_forward (the non-secondary mode never holds an aim angle on the port).
        NVec3? eyeOpt = EyeOrigin();
        if (eyeOpt is null) { HideAll(); return; }
        QMath.AngleVectors(viewAngles, out NVec3 forward, out _, out _);

        NVec3 pos = eyeOpt.Value;
        NVec3 dir = QMath.Normalize(forward);

        var poly = new NVec3[PolylineLength];
        poly[0] = pos;

        int portalNumber = 0, portal1Idx = 1;
        const int portalMax = 2;
        int n = 1 + 2; // QC: 2 lines == 3 points
        int idx = 0;
        while (idx < n && idx < PolylineLength - 1)
        {
            // QC traceline(pos, pos + 65536 * dir, true, this) — worldonly (MOVE_WORLDONLY).
            NVec3 end = pos + dir * TraceDist;
            TraceResult tr = Api.Trace.Trace(pos, NVec3.Zero, NVec3.Zero, end, MoveFilter.WorldOnly, null);
            NVec3 norm = tr.PlaneNormal.LengthSquared() > 0.0001f ? QMath.Normalize(tr.PlaneNormal) : -dir;

            dir = Reflect(dir, norm);     // QC dir = reflect(dir, trace_plane_normal)
            pos = tr.EndPos;              // QC pos = trace_endpos
            poly[++idx] = pos;

            // QC: slick OR playerclip face -> extend the trace one more point and keep going (no portal here).
            if ((tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagSlick) != 0
                || (tr.DpHitContents & DpContentsPlayerClip) != 0)
            {
                ++n;
                continue;
            }
            // QC: noimpact face -> stop (no portal would land).
            if ((tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagNoImpact) != 0)
            {
                n = System.Math.Max(2, idx);
                break;
            }
            // QC size check: a 96^3 box must fit at the hit (CheckWireframeBox), else stop.
            if (!CheckWireframeBox(pos, norm, dir))
            {
                n = System.Math.Max(2, idx);
                break;
            }
            ++portalNumber;
            if (portalNumber >= portalMax)
                break;
            if (portalNumber == 1)
                portal1Idx = idx;
        }

        // QC draw loop: segments before portal1_idx are red (in-portal leg), the rest blue (out-portal leg).
        int wanted = System.Math.Max(0, n - 1);
        EnsureSegments(wanted);
        NVec3 viewUp = ViewUp(viewAngles);
        for (int s = 0; s < _segments.Count; ++s)
        {
            CylindricLine.Segment seg = _segments[s];
            if (s >= wanted)
            {
                seg.Hide();
                continue;
            }
            NVec3 p = poly[s];
            NVec3 q = poly[s + 1];
            if (s == 0)
                p -= viewUp * 16f; // QC: "line from player" (drop the first point below the eye)
            Color rgb = s < portal1Idx ? Red : Blue;
            // QC Draw_CylindricLine width 4 at alpha 0.5, additive (DRAWFLAG draws over the world).
            seg.Update(p, q, LineWidth * 0.0625f, new Color(rgb, 0.5f), BaseMaterial3D.BlendModeEnum.Add);
        }
    }

    // QC reflect: v - 2*n*(v·n).
    private static NVec3 Reflect(NVec3 v, NVec3 n) => v - 2f * n * QMath.Dot(v, n);

    private static NVec3 ViewUp(NVec3 viewAngles)
    {
        QMath.AngleVectors(viewAngles, out _, out _, out NVec3 up);
        return up;
    }

    /// <summary>The local eye in Quake space — the rendered camera position (the view origin Porto_Draw reads).</summary>
    private NVec3? EyeOrigin()
    {
        Camera3D? cam = GetViewport()?.GetCamera3D();
        if (cam is null || !GodotObject.IsInstanceValid(cam))
            return null;
        return Coords.ToQuake(cam.GlobalPosition);
    }

    /// <summary>
    /// QC CheckWireframeBox(this, org, v_right*96, v_up*96, v_forward*96): a portal needs a flat 96^3 surface to
    /// fit. Base traces the box's edges against the world. We approximate it with a single box-trace of the
    /// 96^3 extent flush against the hit face (offset out along the normal so it doesn't start in solid): if the
    /// box can sit at the wall without immediately hitting geometry across its span, the surface is large enough.
    /// </summary>
    private bool CheckWireframeBox(NVec3 hit, NVec3 normal, NVec3 dir)
    {
        const float half = WireframeBox * 0.5f;
        NVec3 mins = new(-half, -half, -half);
        NVec3 maxs = new(half, half, half);
        // Sit the box just off the wall (centre pushed out by half its depth + a small skin) so the trace doesn't
        // start embedded in the surface it just hit.
        NVec3 centre = hit + normal * (half + 1f);
        TraceResult tr = Api.Trace.Trace(centre, mins, maxs, centre, MoveFilter.WorldOnly, null);
        return !tr.StartSolid; // box fits clear at the wall => surface large/flat enough for a portal
    }

    // =================================================================================================
    //  Pooled segment lifecycle (the Draw_CylindricLine stand-in, backed by the shared CylindricLine pool)
    // =================================================================================================

    /// <summary>Grow the local segment list to <paramref name="wanted"/> by acquiring more pooled segments.</summary>
    private void EnsureSegments(int wanted)
    {
        if (Lines is null)
            return;
        while (_segments.Count < wanted)
            _segments.Add(Lines.AcquireSegment());
    }

    private void HideAll()
    {
        foreach (CylindricLine.Segment s in _segments)
            s.Hide();
    }
}
