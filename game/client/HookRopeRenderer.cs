// Port of the CSQC grappling-hook chain rendering (qcsrc/common/weapons/weapon/hook.qc:Draw_GrapplingHook,
// drawn via the ENT_CLIENT_HOOK entity stream GrapplingHookSend). Base draws ONE view-facing quad
// (Draw_CylindricLine, lib/draw.qh) 8 units thick from the hook end back to the firer's gun, textured with the
// chain-link picture particles/hook_<team> (white outside teams), tinted by the firer's pants colour, at alpha
// cl_grapplehook_alpha, for the whole life of the chain (in flight AND once latched). The texture repeats every
// thickness/aspect = 8/0.25 = 32 units, starting at the hook end.
//
// The port has no dedicated hook entity stream; instead — exactly like LaserRenderer (Draw_Laser) and
// PortoTrajectoryPreview (Porto_Draw) — this is a persistent self-driving client node that scans the AMBIENT
// entity facade (Api.Entities.FindByClass("grapplinghook")). On the listen-server/demo path Api.Services IS the
// live server world, so the chain entity AND its Owner (the firing player) are the authoritative server edicts.
// A pure --connect client has no entity facade yet, so this idles there — the established ambient-renderer seam
// (the hook HEAD still draws there: it is networked as a projectile, ProjectileCatalog.GrapplingHook).
//
// The gun end: for the LOCAL player Base anchors at the rendered view, view_origin + view_forward*vs.x +
// view_right*-vs.y + view_up*vs.z (vs = hook_shotorigin for the player's gun alignment), or at the player's eye
// in chase view; for everyone else at the server's reel origin (hook_end). Both use Hook.ShotOriginFor, the
// port of hook_shotorigin[W_GunAlign - 1].
//
// What this replaced (playtest #88): an untextured, team-tinted "+" cross ribbon (two crossed quads) anchored at
// the server player's eye + a fixed '8 8 -12', tinted through ModelTint.TeamColor with the raw team NUMBER
// (4/13/12/9) where that helper expects 1..4 — so red teams drew pink and the other teams white — and no chain
// texture at all: a flat 8-unit bar.

using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Math;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>The grappling-hook chain renderer (the Draw_GrapplingHook successor). Hosted by
/// <see cref="ClientWorld"/>; self-driving ambient-facade scanner — draws each live chain from its hook end back to
/// the firer's gun. Idle on a pure --connect client (no entity facade).</summary>
public sealed partial class HookRopeRenderer : Node3D
{
    // QC Draw_GrapplingHook_trace_callback: Draw_CylindricLine(hit, start, 8, tex, 0.25, rnd, rgb, alpha, NORMAL, vorg).
    private const float RopeWidth = 8f;          // QC thickness 8
    private const float RopeAspect = 0.25f;      // QC aspect 0.25 → one texture repeat per 32 units
    private const float DefaultAlpha = 1f;       // cl_grapplehook_alpha
    private const float RescanInterval = 0.5f;   // facade re-scan cadence (hooks fire/expire continually)

    /// <summary>Host-injected shared cylindric-line node (the Draw_CylindricLine successor). Each live rope
    /// acquires one <see cref="CylindricLine.Segment"/> from it and draws into it in place each frame. Until the
    /// host wires this (e.g. on a pure --connect client before the facade exists) the renderer simply idles.</summary>
    public CylindricLine Lines { get; set; } = null!;

    /// <summary>Host texture loader (AssetSystem.LoadTexture) for particles/hook_*. Late-bindable; without it the
    /// chain draws untextured.</summary>
    public Func<string, Texture2D?>? TextureLoader { get; set; }

    /// <summary>The local player's server entity on a listen host (NetGame.LocalServerPlayer): its chain starts at
    /// the rendered view's gun instead of the server-side eye. Null = treat every chain as someone else's.</summary>
    public Func<Entity?>? LocalPlayerProvider { get; set; }

    /// <summary>QC <c>autocvar_chase_active</c> for the local view: in chase view the local chain starts at the
    /// player's eye (csqcplayer.origin + view_ofs), as Base does.</summary>
    public Func<bool>? ChaseActiveProvider { get; set; }

    // One persistent cylindric-line segment per live hook chain (keyed by the chain entity), updated in place.
    private sealed class Rope
    {
        public Entity Hook = null!;                  // the grapplinghook chain edict
        public CylindricLine.Segment Seg = null!;    // the Draw_CylindricLine quad for this chain
    }

    private readonly Dictionary<Entity, Rope> _ropes = new();
    private readonly List<Entity> _dead = new();
    private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.Ordinal);
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

        Camera3D? cam = GetViewport()?.GetCamera3D();
        if (cam is not null && !GodotObject.IsInstanceValid(cam))
            cam = null;
        Entity? local = LocalPlayerProvider?.Invoke();
        foreach (Rope r in _ropes.Values)
            UpdateRope(r, cam, local);
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
            _ropes[e] = new Rope { Hook = e, Seg = Lines.AcquireSegment() };
        }

        // Drop ropes whose chain was removed (RemoveHook / RemoveGrapplingHooks / shoot-down / expiry / sky).
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

    // =================================================================================================
    //  Per-frame rope update (Draw_GrapplingHook)
    // =================================================================================================

    private void UpdateRope(Rope r, Camera3D? cam, Entity? local)
    {
        Entity hook = r.Hook;
        if (hook.IsFreed)
            return;
        // No firer (shouldn't happen for a live chain) → hide until the next rescan sweeps it.
        if (hook.Owner is not { IsFreed: false } owner || cam is null)
        {
            r.Seg.Hide();
            return;
        }

        NVec3 vs = Hook.ShotOriginFor(owner); // QC hook_shotorigin[W_GunAlign(...) - 1]
        NVec3 viewOrg = Coords.ToQuake(cam.GlobalPosition);
        NVec3 gun;
        if (local is not null && ReferenceEquals(owner, local))
        {
            if (ChaseActiveProvider?.Invoke() == true)
            {
                gun = owner.Origin + owner.ViewOfs; // QC: csqcplayer.origin + csqcplayer.view_ofs
            }
            else
            {
                // QC: view_origin + view_forward * vs.x + view_right * -vs.y + view_up * vs.z (camera: -Z fwd, +X right, +Y up).
                Basis b = cam.GlobalBasis;
                NVec3 vf = Coords.ToQuake(-b.Z);
                NVec3 vr = Coords.ToQuake(b.X);
                NVec3 vu = Coords.ToQuake(b.Y);
                gun = viewOrg + vf * vs.X + vr * -vs.Y + vu * vs.Z;
            }
        }
        else
        {
            // QC hook_end, set by GrapplingHookThink: owner.origin + view_ofs + vs rotated by the owner's v_angle.
            NVec3 viewAngles = owner.ViewAngles == NVec3.Zero ? owner.Angles : owner.ViewAngles;
            QMath.AngleVectors(viewAngles, out NVec3 vf, out NVec3 vr, out NVec3 vu);
            gun = owner.Origin + owner.ViewOfs + vf * vs.X + vr * -vs.Y + vu * vs.Z;
        }

        // QC: tex by entcs_GetTeamColor (hook_red/blue/yellow/pink, else hook_white); rgb = the firer's PANTS colour
        // (colormapPaletteColor(clientcolors & 0x0F, true)), in FFA too — the white chain takes the player's colour.
        int team = (int)owner.Team;
        string tex = team switch
        {
            Teams.Red => "particles/hook_red",
            Teams.Blue => "particles/hook_blue",
            Teams.Yellow => "particles/hook_yellow",
            Teams.Pink => "particles/hook_pink",
            _ => "particles/hook_white",
        };
        int colors = owner is Player p ? p.ClientColors : owner.Colors;
        (float cr, float cg, float cb) = CsqcModelAppearance.ColormapPaletteColor(colors & 0x0F, isPants: true,
            Api.Clock.Time);

        // QC draws from the hook end (hit) back to the gun (start), U starting at 0 at the hook end.
        r.Seg.UpdateFacing(hook.Origin, gun, RopeWidth, viewOrg, Texture(tex), RopeAspect, 0f,
            new Color(cr, cg, cb, Alpha()), BaseMaterial3D.BlendModeEnum.Mix);
    }

    private Texture2D? Texture(string name)
    {
        if (_textures.TryGetValue(name, out Texture2D? t))
            return t;
        t = TextureLoader?.Invoke(name);
        if (TextureLoader is not null)
            _textures[name] = t; // cache misses too, once a loader exists
        return t;
    }

    /// <summary>QC autocvar_cl_grapplehook_alpha (default 1) — the chain's draw alpha. Base draws ceil(alpha)
    /// passes; one pass at min(alpha, 1) is the same picture up to 1. An unset cvar falls back to 1.</summary>
    private static float Alpha()
    {
        string s = Api.Cvars.GetString("cl_grapplehook_alpha");
        return string.IsNullOrWhiteSpace(s) ? DefaultAlpha : Mathf.Clamp(Api.Cvars.GetFloat("cl_grapplehook_alpha"), 0f, 1f);
    }
}
