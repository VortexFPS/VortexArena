using System.Numerics;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Physics;

/// <summary>
/// The push-apart half of soft player collision (PORT EXTENSION <c>sv_player_softcollision</c> — the
/// pass-through half is <c>PlayerPhysics.PlayerClipFilter</c>/<see cref="MoveFilter.NoPlayers"/>). With
/// player-vs-player movement clipping off, bodies can overlap; once per server tick — GameWorld.OnEndFrame,
/// after every player has moved — this pass walks the live player pairs and slides overlapping hulls apart
/// POSITIONALLY (a gentle constant-speed drift, not an impulse) until they rest side by side:
///   * crossing paths at speed barely registers — the overlap lasts a tick or two, a few units of drift;
///   * walking into someone standing still nudges BOTH bodies away, symmetrically;
///   * fully co-located bodies (a telefrag-style overlap) resolve outward in a fraction of a second and
///     settle adjacent — the push scales down to exactly the remaining penetration, so there is no
///     oscillation at the contact edge.
/// Displacement is horizontal-only (overlap can never boost anyone vertically) and trace-guarded
/// (<see cref="MoveFilter.NoPlayers"/>) so nobody is squeezed through world geometry. Frozen statues
/// (freezetag) stay put — the free body absorbs the full push. Server-authoritative and unpredicted: the
/// client sees the nudge as a small reconcile error that the existing error smoothing absorbs (push speeds
/// are well under walk speed).
/// </summary>
public static class PlayerSeparation
{
    /// <summary>Total pair separation speed, u/s (cvar <c>sv_player_softcollision_pushspeed</c>, unset →
    /// this). Split evenly across the movable bodies of a pair. 0 disables the pass (pure pass-through).</summary>
    public const float DefaultPushSpeed = 150f;

    /// <summary>Run one separation tick over the live roster. <paramref name="players"/> is the server's
    /// client list (players + bots); non-playing entries (spectators, dead, vehicles' transmuted solids)
    /// filter out on the SOLID_SLIDEBOX + FL_CLIENT + alive gate. O(n²) pairs over ≤ tens of players.
    /// <paramref name="relink"/> re-registers a moved body in the area-grid broadphase (SV_LinkEdict —
    /// pass <c>EngineServices.LinkEdict</c>) so post-EndFrame traces see the nudged footprint, not the
    /// pre-nudge one.</summary>
    public static void Run(System.Collections.Generic.IReadOnlyList<Entity> players, float dt,
        System.Action<Entity>? relink = null)
    {
        _relink = relink;
        if (Api.Services is null || dt <= 0f || players.Count < 2)
            return;
        if (Api.Cvars.GetString("sv_player_softcollision") == "0")
            return;
        float speed = PushSpeed();
        if (speed <= 0f)
            return;

        using var _ = Prof.Sample("sim.playersep");
        for (int i = 0; i < players.Count; i++)
        {
            Entity a = players[i];
            if (!Eligible(a)) continue;
            for (int j = i + 1; j < players.Count; j++)
            {
                Entity b = players[j];
                if (!Eligible(b)) continue;
                SeparatePair(a, b, i, j, speed, dt);
            }
        }
    }

    /// <summary>Unset → <see cref="DefaultPushSpeed"/>; an explicit 0 is honored (pass-through without push).</summary>
    private static float PushSpeed()
    {
        string s = Api.Cvars.GetString("sv_player_softcollision_pushspeed");
        return s.Length == 0 ? DefaultPushSpeed : Api.Cvars.GetFloat("sv_player_softcollision_pushspeed");
    }

    private static bool Eligible(Entity p)
        => !p.IsFreed && p.Solid == Solid.SlideBox && (p.Flags & EntFlags.Client) != 0
           && p.DeadState == DeadFlag.No;

    private static void SeparatePair(Entity a, Entity b, int i, int j, float speed, float dt)
    {
        // Hull overlap (STRICT — touching faces don't count, so settled bodies rest adjacent without jitter).
        Vector3 aMin = a.Origin + a.Mins, aMax = a.Origin + a.Maxs;
        Vector3 bMin = b.Origin + b.Mins, bMax = b.Origin + b.Maxs;
        float penX = MathF.Min(aMax.X, bMax.X) - MathF.Max(aMin.X, bMin.X);
        float penY = MathF.Min(aMax.Y, bMax.Y) - MathF.Max(aMin.Y, bMin.Y);
        float penZ = MathF.Min(aMax.Z, bMax.Z) - MathF.Max(aMin.Z, bMin.Z);
        if (penX <= 0f || penY <= 0f || penZ <= 0f)
            return;

        // Push direction: b away from a along the horizontal origin delta (never vertical).
        float dx = b.Origin.X - a.Origin.X, dy = b.Origin.Y - a.Origin.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.25f)
        {
            // Co-axial overlap (telefrag-style): a deterministic pair-keyed direction — the same roster pair
            // always resolves the same way, and no RNG stream advances (the shared Prandom must stay untouched).
            float ang = ((i * 61 + j * 17) & 63) * (MathF.PI / 32f);
            dx = MathF.Cos(ang); dy = MathF.Sin(ang);
        }
        else
        {
            dx /= len; dy /= len;
        }

        // Distance along ±dir until the hulls no longer overlap: the first horizontal axis to clear ends the
        // overlap, so take the min of the per-axis exits (guard the division for near-axis-aligned pushes).
        float need = MathF.Min(
            penX / MathF.Max(MathF.Abs(dx), 1e-4f),
            penY / MathF.Max(MathF.Abs(dy), 1e-4f));

        // This tick's closing budget, capped at the remaining penetration (the ease-out: the final tick moves
        // exactly to contact). Frozen statues stay put — the free body absorbs the full push.
        bool aMoves = !PlayerPhysics.IsFrozen(a), bMoves = !PlayerPhysics.IsFrozen(b);
        if (!aMoves && !bMoves)
            return;
        float step = MathF.Min(speed * dt, need);
        var dir = new Vector3(dx, dy, 0f);
        if (aMoves && bMoves)
        {
            Nudge(a, dir * (-0.5f * step));
            Nudge(b, dir * (0.5f * step));
        }
        else if (aMoves) Nudge(a, dir * -step);
        else Nudge(b, dir * step);
    }

    // The current Run's relink seam (server-tick single-threaded; prediction never calls Run).
    private static System.Action<Entity>? _relink;

    /// <summary>Trace-guarded positional slide: world/movers block (endpos short of the wall), other players
    /// don't (<see cref="MoveFilter.NoPlayers"/> — a third body must not wedge the push). A start-solid body
    /// (embedded in world geometry) is left for NudgeOutOfSolid on its next move.</summary>
    private static void Nudge(Entity p, Vector3 delta)
    {
        TraceResult tr = Api.Trace.Trace(p.Origin, p.Mins, p.Maxs, p.Origin + delta, MoveFilter.NoPlayers, p);
        if (tr.StartSolid)
            return;
        p.Origin = tr.EndPos;
        _relink?.Invoke(p);
    }
}
