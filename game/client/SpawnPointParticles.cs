using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// Idle particle glow at every spawn point — the port of CSQC <c>Spawn_Draw</c>
/// (qcsrc/client/spawnpoints.qc:20): while <c>cl_spawn_point_particles</c> is on, each spawn point
/// continuously emits the <c>EFFECT_SPAWNPOINT</c> burst (effectinfo <c>spawn_point_neutral</c>) so players
/// can read spawn locations at a glance. Upstream emits per draw frame via <c>boxparticles</c>; here a small
/// repeat timer feeds <see cref="EffectSystem.Spawn"/> — the faithful backend's fractional accumulator
/// shapes the per-point rate exactly like DP's count scaling.
///
/// Spawn points are discovered from the ambient entity table (<c>info_player_deathmatch</c> /
/// <c>info_player_start</c> / <c>info_player_team*</c> — the same classnames SpawnSystem searches), lazily
/// and re-scanned occasionally so late entity registration is picked up. Distance-culled to the camera so a
/// big map doesn't burn spawns nobody can see (upstream culls via the render loop reaching Spawn_Draw).
/// </summary>
public sealed partial class SpawnPointParticles : Node3D
{
    /// <summary>Seconds between emission pulses per point. Upstream emits per draw frame with pcount =
    /// bound(0, frametime, 0.1); each pulse passes the covered interval as the count so the average rate
    /// matches (spawn_point_* count 37.5 → ~37.5 particles/sec/point, NOT 37.5 per pulse — passing count 1
    /// here was a 5× overspawn).</summary>
    [Export] public float PulseInterval { get; set; } = 0.2f;

    /// <summary>Unused legacy fallback (kept for scene compatibility). The live cull range is the
    /// <c>cl_spawn_point_dist_max</c> cvar, registered in ClientSettings with the Base default 1200
    /// (0 ⇒ no distance cull).</summary>
    [Export] public float DrawDistance { get; set; } = 2000f;

    /// <summary>The effect player (EffectSystem) — wired by ClientWorld.</summary>
    public EffectSystem? Effects { get; set; }

    private static readonly string[] SpawnClasses =
    {
        "info_player_deathmatch", "info_player_start",
        "info_player_team1", "info_player_team2", "info_player_team3", "info_player_team4",
    };

    /// <summary>Discovered spawn points: world origin (Quake space) + the spot's team (0/neutral or 1..4).</summary>
    private readonly List<(NVec3 Origin, int Team)> _points = new();
    private float _pulseTimer;
    private float _rescanTimer;

    /// <summary>Port of CSQC <c>Team_ColorRGB(team - 1)</c> (= <c>colormapPaletteColor(team-1)</c>): the per-team
    /// glow tint. The spot's team carries the port's CSQC encoding (Teams.Red/Blue/Yellow/Pink = 4/13/12/9), so
    /// switch on those — not 1/2/3/4 — exactly like <c>Ctf.TeamRadarColor</c>. Neutral / unteamed spots read white.</summary>
    private static Color TeamColorRgb(int team) => team switch
    {
        Teams.Red => new Color(1f, 0.0625f, 0.0625f),    // red
        Teams.Blue => new Color(0.0625f, 0.0625f, 1f),   // blue
        Teams.Yellow => new Color(1f, 1f, 0.0625f),      // yellow
        Teams.Pink => new Color(1f, 0.0625f, 1f),        // pink
        _ => new Color(1f, 1f, 1f),
    };

    public override void _Process(double delta)
    {
        using var _prof = FrameProfiler.Scope("clientmisc");
        if (Effects is null || Api.Services is null)
            return;
        if (VortexArena.Game.Menu.MenuState.Cvars.GetFloat("cl_spawn_point_particles") == 0f)
            return;

        // Lazy + periodic rescan (entities register during load; gametype filters can add/remove).
        _rescanTimer -= (float)delta;
        if (_points.Count == 0 || _rescanTimer <= 0f)
        {
            _rescanTimer = 5f;
            _points.Clear();
            foreach (string cls in SpawnClasses)
                foreach (Entity e in Api.Entities.FindByClass(cls))
                    _points.Add((e.Origin, (int)e.Team));
        }
        if (_points.Count == 0)
            return;

        // #30 slowmo/pause: the glow pulse cadence is CSQC-time-driven in Base (Spawn_Draw runs per CSQC frame) —
        // scale so a paused game stops emitting new pulses. (The rescan timer above stays on the raw delta: it's
        // port-internal entity-registry housekeeping, not an animation.)
        _pulseTimer -= VortexArena.Game.Client.ClientRenderTime.ScaleDelta((float)delta);
        if (_pulseTimer > 0f)
            return;
        _pulseTimer = PulseInterval;

        // Draw-distance cull range = cl_spawn_point_dist_max (upstream Spawn_Draw); 0 disables the distance
        // cull entirely (Base only computes vdist when the cvar is non-zero). Registered in ClientSettings
        // with the Base default 1200 (xonotic-client.cfg:77) — registration matters: an UNSET cvar reads 0,
        // which silently meant "no cull" and rendered the glow at every spawn point map-wide.
        float distMax = VortexArena.Game.Menu.MenuState.Cvars.GetFloat("cl_spawn_point_dist_max");
        bool cull = distMax > 0f;

        // Camera position for the draw-distance cull (Quake space).
        NVec3 eye = default;
        if (cull)
        {
            Camera3D? cam = GetViewport()?.GetCamera3D();
            if (cam is not null)
                eye = Coords.ToQuake(cam.GlobalTransform.Origin);
        }

        float dd2 = distMax * distMax;
        foreach ((NVec3 origin, int team) in _points)
        {
            if (cull)
            {
                NVec3 d = origin - eye;
                if (NVec3.Dot(d, d) > dd2)
                    continue;
            }
            // Per-team tint (Team_ColorRGB(team-1)); neutral spots read white.
            // Slight lift so the glow sits above the floor (upstream uses the spawnpoint bbox).
            // count = the interval this pulse covers — upstream passes bound(0, frametime, 0.1) per frame,
            // so one 0.2s pulse carries 0.2 worth of count (37.5 count → 7.5 particles/pulse = 37.5/sec).
            Effects.Spawn("SPAWNPOINT", origin + new NVec3(0f, 0f, 8f), count: PulseInterval, color: TeamColorRgb(team));
        }
    }
}
