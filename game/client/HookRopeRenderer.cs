// Port of the CSQC grappling-hook rope rendering (qcsrc/common/weapons/weapon/hook.qc:Draw_GrapplingHook,
// drawn via the ENT_CLIENT_HOOK entity stream GrapplingHookSend). Base draws a thick (width 8) team-coloured
// cylindric line (Draw_CylindricLine, particles/hook_<team>) from the firer's gun muzzle to the latched hook
// end every frame, alpha cl_grapplehook_alpha, for the whole life of the chain (in flight AND once latched).
//
// The port has no dedicated hook entity stream; instead — exactly like LaserRenderer (Draw_Laser) and
// PortoTrajectoryPreview (Porto_Draw) — this is a persistent self-driving client node that scans the AMBIENT
// entity facade (Api.Entities.FindByClass("grapplinghook")). On the listen-server/demo path Api.Services IS the
// live server world, so the chain entity AND its Owner (the firing player) are the authoritative server edicts:
// the rope endpoints are read straight off them, so the line matches the server's reel geometry. A pure
// --connect client has no entity facade yet, so this idles there — the established ambient-renderer seam.
//
// One CylindricLine.Segment per live hook (the shared Draw_CylindricLine cross-ribbon successor, owned by the
// host-injected CylindricLine node), updated IN PLACE each frame and tinted by the firer's team colour
// (ModelTint.TeamColor; the port's stand-in for the particles/hook_<team> texture set, white for FFA / team 0).
// The muzzle end mirrors Hook.GrapplingHookThink's reel origin (owner.origin + view_ofs + hook_shotorigin
// '8 8 -12' rotated by the owner's view angles), so the rope is anchored where the reel pulls toward; the far
// end is the chain entity's origin. The line draws while the hook is in flight (complementing the Hookbomb-spike
// head ProjectileCatalog renders) and persists after the latch (when the MoveType.None head drops out of the
// snapshot), closing the "never a rope" presentation gap.

using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Math;
using VortexArena.Common.Services;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>The grappling-hook rope renderer (the Draw_GrapplingHook successor). Hosted by
/// <see cref="ClientWorld"/>; self-driving ambient-facade scanner — draws a team-coloured line from each live
/// firer's gun muzzle to its latched/flying hook end. Idle on a pure --connect client (no entity facade).</summary>
public sealed partial class HookRopeRenderer : Node3D
{
    // QC Draw_GrapplingHook: Draw_CylindricLine(..., 8, ...) at alpha autocvar_cl_grapplehook_alpha (default 1).
    private const float RopeWidth = 8f;          // QC thickness 8
    private const float DefaultAlpha = 1f;       // cl_grapplehook_alpha
    private const float RescanInterval = 0.5f;   // facade re-scan cadence (hooks fire/expire continually)

    /// <summary>Host-injected shared cylindric-line node (the Draw_CylindricLine successor). Each live rope
    /// acquires one <see cref="CylindricLine.Segment"/> from it and draws into it in place each frame. Until the
    /// host wires this (e.g. on a pure --connect client before the facade exists) the renderer simply idles.</summary>
    public CylindricLine Lines { get; set; } = null!;

    // One persistent cylindric-line segment per live hook chain (keyed by the chain entity), updated in place.
    private sealed class Rope
    {
        public Entity Hook = null!;                  // the grapplinghook chain edict
        public CylindricLine.Segment Seg = null!;    // the Draw_CylindricLine cross-ribbon for this chain
    }

    private readonly Dictionary<Entity, Rope> _ropes = new();
    private readonly List<Entity> _dead = new();
    private float _rescanIn;

    public override void _Process(double delta)
    {
        if (Api.Services is null || Lines is null)
            return;

        using var _prof = FrameProfiler.Scope("clientmisc");

        _rescanIn -= (float)delta;
        if (_rescanIn <= 0f)
        {
            Rescan();
            _rescanIn = RescanInterval;
        }

        foreach (Rope r in _ropes.Values)
            UpdateRope(r);
    }

    // =================================================================================================
    //  Facade scan / segment lifecycle (mirrors LaserRenderer.Rescan)
    // =================================================================================================

    private void Rescan()
    {
        foreach (Entity e in Api.Entities.FindByClass("grapplinghook"))
        {
            if (e.IsFreed || _ropes.ContainsKey(e))
                continue;
            _ropes[e] = BuildRope(e);
        }

        // Drop ropes whose chain was removed (RemoveHook / RemoveGrapplingHooks / shoot-down / expiry).
        _dead.Clear();
        foreach (var kv in _ropes)
            if (kv.Key.IsFreed)
                _dead.Add(kv.Key);
        foreach (Entity e in _dead)
        {
            _ropes[e].Seg.Free();
            _ropes.Remove(e);
        }
    }

    private Rope BuildRope(Entity hook)
    {
        // One cylindric-line segment from the shared host node — the Draw_CylindricLine cross-ribbon stand-in.
        return new Rope { Hook = hook, Seg = Lines.AcquireSegment() };
    }

    // =================================================================================================
    //  Per-frame rope update (Draw_GrapplingHook)
    // =================================================================================================

    private void UpdateRope(Rope r)
    {
        Entity hook = r.Hook;
        if (hook.IsFreed)
            return;
        // No firer (shouldn't happen for a live chain) → hide until the next rescan sweeps it.
        if (hook.Owner is not { IsFreed: false } owner)
        {
            r.Seg.Hide();
            return;
        }

        // QC Draw_GrapplingHook draws from the firer's gun muzzle to the hook end. Mirror Hook.GrapplingHookThink's
        // reel origin: owner.origin + view_ofs + hook_shotorigin('8 8 -12') rotated by the owner's view angles. Use
        // the SAME view-angle source the server reel uses (ViewAngles, falling back to Angles when unset) so the
        // muzzle anchor matches the reel geometry exactly. The v_right*-vs.y term matches the port's
        // v_right = -Left convention (BoneMatrix.cs), same as the reel math.
        NVec3 viewAngles = owner.ViewAngles == NVec3.Zero ? owner.Angles : owner.ViewAngles;
        QMath.AngleVectors(viewAngles, out NVec3 vf, out NVec3 vr, out NVec3 vu);
        NVec3 vs = vf * Hook.HookShotOrigin.X + vr * -Hook.HookShotOrigin.Y + vu * Hook.HookShotOrigin.Z;
        NVec3 muzzle = owner.Origin + owner.ViewOfs + vs;
        NVec3 end = hook.Origin;

        // QC team-coloured rope (particles/hook_<team>); the port tints a white ribbon by the firer's team colour
        // (ModelTint.TeamColor — the stand-in used by ProjectileRenderer.ApplyTeamColormod). Team 0 (FFA) → white.
        Color tint = ModelTint.TeamColor((int)owner.Team, out bool hasTeam);
        if (!hasTeam)
            tint = Colors.White;

        // QC Draw_CylindricLine width 8 at DRAWFLAG_NORMAL (Mix). The segment owns the muzzle→hook geometry,
        // length-gating and the cross-ribbon basis (its degenerate-length guard hides the ribbon itself).
        r.Seg.Update(muzzle, end, RopeWidth, new Color(tint, Alpha()), BaseMaterial3D.BlendModeEnum.Mix);
    }

    /// <summary>QC autocvar_cl_grapplehook_alpha (default 1) — the rope's draw alpha. An explicit value is honoured;
    /// an unset cvar falls back to 1 (mirrors ModelTint.Cvar / PortoTrajectoryPreview.PortoSecondary).</summary>
    private static float Alpha()
    {
        string s = Api.Cvars.GetString("cl_grapplehook_alpha");
        return string.IsNullOrWhiteSpace(s) ? DefaultAlpha : Mathf.Clamp(Api.Cvars.GetFloat("cl_grapplehook_alpha"), 0f, 1f);
    }
}
