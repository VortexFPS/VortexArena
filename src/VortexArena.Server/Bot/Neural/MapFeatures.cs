using System;
using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;

namespace VortexArena.Server.Bot.Neural;

/// <summary>The kinds of map furniture the policy is told about, one-hot in the observation.</summary>
public enum MapFeatureKind : byte
{
    None = 0,
    /// <summary><c>trigger_push</c> / <c>trigger_push_velocity</c>: a launch, with a known landing point.</summary>
    JumpPad = 1,
    /// <summary><c>trigger_teleport</c>: an instant relocation, with a known exit.</summary>
    Teleporter = 2,
    /// <summary>A warpzone plane: continuous space, so the exit direction matters more than the exit point.</summary>
    Warpzone = 3,
    /// <summary><c>trigger_hurt</c>: a volume to avoid, or to escape from if already inside.</summary>
    Hurt = 4,
    /// <summary><c>func_plat</c> / <c>func_door</c> / <c>func_train</c>: a floor that moves.</summary>
    Mover = 5,
    /// <summary><c>func_ladder</c> / <c>func_water</c>: gravity-free climbing.</summary>
    Ladder = 6,
}

/// <summary>
/// One piece of map furniture, reduced to what the policy needs: where it is, what it does, and
/// <b>where it puts you</b>.
///
/// <para>That last part is the whole point. A jump pad the policy can only see is a thing to avoid; a jump
/// pad whose landing point is in the observation is a route. The same for a teleporter's exit. Baking the
/// outcome is the difference between the network learning "stay off the blue glow" and learning to plan
/// through it.</para>
/// </summary>
public struct MapFeature
{
    public MapFeatureKind Kind;

    /// <summary>Volume the feature occupies (world AABB).</summary>
    public Vector3 Mins, Maxs;

    /// <summary>Centre of the volume, precomputed because every query uses it.</summary>
    public Vector3 Centre;

    /// <summary>
    /// Where entering this feature puts you. For a jump pad, the solved landing point; for a teleporter,
    /// the destination origin; for a warpzone, a point just past the exit plane. Equal to
    /// <see cref="Centre"/> for features that do not move you (hurt volumes, ladders).
    /// </summary>
    public Vector3 Exit;

    /// <summary>
    /// Seconds between entering and arriving at <see cref="Exit"/>. Jump-pad flight time; ~0 for a
    /// teleporter. Lets the policy weigh a 2.5 s pad ride against running around.
    /// </summary>
    public float TransitTime;

    /// <summary>
    /// Live state, refreshed per think for the movers: 0..1 travel phase (0 = at Pos1, 1 = at Pos2). Zero
    /// for static features.
    /// </summary>
    public float State;

    /// <summary>Damage per application for <see cref="MapFeatureKind.Hurt"/>; 0 elsewhere.</summary>
    public float Damage;

    /// <summary>The entity this was derived from, so <see cref="State"/> can be refreshed.</summary>
    public Entity? Source;

    public readonly bool Contains(Vector3 p)
        => p.X >= Mins.X && p.X <= Maxs.X && p.Y >= Mins.Y && p.Y <= Maxs.Y && p.Z >= Mins.Z && p.Z <= Maxs.Z;

    /// <summary>Squared distance from <paramref name="p"/> to the volume (0 when inside).</summary>
    public readonly float DistanceSquared(Vector3 p)
    {
        float dx = p.X < Mins.X ? Mins.X - p.X : p.X > Maxs.X ? p.X - Maxs.X : 0f;
        float dy = p.Y < Mins.Y ? Mins.Y - p.Y : p.Y > Maxs.Y ? p.Y - Maxs.Y : 0f;
        float dz = p.Z < Mins.Z ? Mins.Z - p.Z : p.Z > Maxs.Z ? p.Z - Maxs.Z : 0f;
        return dx * dx + dy * dy + dz * dz;
    }
}

/// <summary>
/// The map's furniture list, built once from the entity table and queried per think for the K nearest.
///
/// <para><b>Built from entities, not waypoints.</b> Parity finding D3 records that the port only creates
/// teleporter and jumppad waypoints on the no-file fallback path, so every shipped map reports
/// <c>teleportWps = 0</c> and the whole teleport-traversal machinery is live code that never fires. Reading
/// the entities directly (what Base does at <c>jumppads.qc:720</c> and <c>teleporters.qc:260</c>) sidesteps
/// that bug rather than inheriting it.</para>
/// </summary>
public sealed class MapFeatures
{
    /// <summary>How many nearest features the observation carries.</summary>
    public const int ObservedCount = 4;

    /// <summary>Floats per observed feature: direction (3), log distance (1), kind one-hot (7), exit dir (3), transit (1), state (1).</summary>
    public const int FloatsPerFeature = 16;

    /// <summary>Total floats this channel contributes.</summary>
    public const int ObservationFloats = ObservedCount * FloatsPerFeature;

    /// <summary>Beyond this range a feature is not worth an observation slot.</summary>
    public const float RelevanceRadius = 1400f;

    private readonly List<MapFeature> _features = new();
    private readonly int[] _nearestScratch = new int[ObservedCount];
    private readonly float[] _nearestDist = new float[ObservedCount];

    public IReadOnlyList<MapFeature> All => _features;
    public int Count => _features.Count;

    /// <summary>
    /// Scan an entity list and build the feature set. Safe to call again after the map's entities settle;
    /// the previous set is discarded.
    /// </summary>
    public void Build(IEnumerable<Entity> entities)
    {
        _features.Clear();
        foreach (Entity e in entities)
        {
            if (e.IsFreed) continue;
            MapFeatureKind kind = Classify(e.ClassName);
            if (kind == MapFeatureKind.None) continue;

            Vector3 mins = e.AbsMin, maxs = e.AbsMax;
            if (mins == maxs) { mins = e.Origin + e.Mins; maxs = e.Origin + e.Maxs; }
            if (mins == maxs)
            {
                // A point entity (a target_position destination used as a ladder marker, say). Give it a
                // nominal box so distance queries behave.
                mins = e.Origin - new Vector3(16f, 16f, 16f);
                maxs = e.Origin + new Vector3(16f, 16f, 16f);
            }

            Vector3 centre = (mins + maxs) * 0.5f;
            var f = new MapFeature
            {
                Kind = kind,
                Mins = mins,
                Maxs = maxs,
                Centre = centre,
                Exit = centre,
                TransitTime = 0f,
                State = 0f,
                Damage = kind == MapFeatureKind.Hurt ? e.Dmg : 0f,
                Source = e,
            };

            ResolveExit(ref f, e);
            _features.Add(f);
        }
    }

    private static MapFeatureKind Classify(string cn) => cn switch
    {
        "trigger_push" or "trigger_push_velocity" => MapFeatureKind.JumpPad,
        "trigger_teleport" or "target_teleporter" => MapFeatureKind.Teleporter,
        "trigger_warpzone" or "func_warpzone" => MapFeatureKind.Warpzone,
        "func_ladder" or "func_water" => MapFeatureKind.Ladder,
        _ =>
            cn.StartsWith("trigger_hurt", StringComparison.Ordinal) ? MapFeatureKind.Hurt
            : cn.StartsWith("func_plat", StringComparison.Ordinal)
              || cn.StartsWith("func_door", StringComparison.Ordinal)
              || cn.StartsWith("func_train", StringComparison.Ordinal)
              || cn.StartsWith("func_bobbing", StringComparison.Ordinal) ? MapFeatureKind.Mover
            : MapFeatureKind.None,
    };

    /// <summary>
    /// Work out where the feature deposits whoever enters it. Jump pads get the real destination and a
    /// ballistic flight time; teleporters get their destination entity's origin.
    /// </summary>
    private static void ResolveExit(ref MapFeature f, Entity e)
    {
        switch (f.Kind)
        {
            case MapFeatureKind.JumpPad:
            {
                Entity? dest = e.Enemy;
                if (dest is null && !string.IsNullOrEmpty(e.Target))
                {
                    foreach (Entity d in MapMover.FindByTargetName(e.Target)) { dest = d; break; }
                }
                if (dest is null) return;
                f.Exit = dest.Origin;
                // QC trigger_push_calculatevelocity solves an arc that apexes `height` above the higher of
                // the two endpoints; the flight time is the fall from that apex to the destination. Height 0
                // means "use the default arc", which the solver treats as a 100 qu rise.
                float rise = e.Height > 0f ? e.Height : 100f;
                float gravity = MathF.Max(1f, Cvars.FloatOr("sv_gravity", 800f));
                float apex = MathF.Max(f.Centre.Z, dest.Origin.Z) + rise;
                float upTime = MathF.Sqrt(MathF.Max(0f, 2f * (apex - f.Centre.Z) / gravity));
                float downTime = MathF.Sqrt(MathF.Max(0f, 2f * (apex - dest.Origin.Z) / gravity));
                f.TransitTime = upTime + downTime;
                break;
            }
            case MapFeatureKind.Teleporter:
            {
                if (string.IsNullOrEmpty(e.Target)) return;
                foreach (Entity d in MapMover.FindByTargetName(e.Target)) { f.Exit = d.Origin; break; }
                f.TransitTime = 0f;
                break;
            }
            case MapFeatureKind.Mover:
            {
                // The exit of a lift is the top of its travel: that is where riding it gets you.
                f.Exit = e.Pos2 != Vector3.Zero ? e.Pos2 : f.Centre;
                break;
            }
        }
    }

    /// <summary>
    /// Refresh the per-think live state of the movers (their travel phase). Everything else is static, so
    /// this touches only the handful of entities whose height changes.
    /// </summary>
    public void RefreshState()
    {
        for (int i = 0; i < _features.Count; i++)
        {
            MapFeature f = _features[i];
            if (f.Kind != MapFeatureKind.Mover || f.Source is not { IsFreed: false } src) continue;
            Vector3 p1 = src.Pos1, p2 = src.Pos2;
            float span = (p2 - p1).Length();
            f.State = span > 1f ? Math.Clamp((src.Origin - p1).Length() / span, 0f, 1f) : 0f;
            // The volume moves with the platform, so the AABB has to follow or the distance query points at
            // where the lift used to be.
            Vector3 mins = src.AbsMin, maxs = src.AbsMax;
            if (mins != maxs)
            {
                f.Mins = mins; f.Maxs = maxs; f.Centre = (mins + maxs) * 0.5f;
            }
            _features[i] = f;
        }
    }

    /// <summary>
    /// Write the K nearest features to <paramref name="origin"/> into <paramref name="dest"/>, egocentric
    /// and rotated into the frame given by <paramref name="forward"/>. Unused slots are zeroed, which reads
    /// as "no feature" because the kind one-hot is all-zero there.
    /// </summary>
    public void WriteObservation(Vector3 origin, Vector3 forward, Span<float> dest)
    {
        if (dest.Length < ObservationFloats)
            throw new ArgumentException($"need {ObservationFloats} floats", nameof(dest));
        dest[..ObservationFloats].Clear();

        for (int i = 0; i < ObservedCount; i++) { _nearestScratch[i] = -1; _nearestDist[i] = float.MaxValue; }

        float maxSq = RelevanceRadius * RelevanceRadius;
        for (int i = 0; i < _features.Count; i++)
        {
            float d = _features[i].DistanceSquared(origin);
            if (d > maxSq) continue;
            // Insertion into a K=4 sorted list. A heap would be asymptotically better and slower at this size.
            for (int k = 0; k < ObservedCount; k++)
            {
                if (d >= _nearestDist[k]) continue;
                for (int j = ObservedCount - 1; j > k; j--)
                {
                    _nearestDist[j] = _nearestDist[j - 1];
                    _nearestScratch[j] = _nearestScratch[j - 1];
                }
                _nearestDist[k] = d;
                _nearestScratch[k] = i;
                break;
            }
        }

        // Frame basis: forward flattened, plus its left perpendicular. Same frame the NavField ring uses, so
        // "ahead" means one thing across the whole observation.
        float fx = forward.X, fy = forward.Y;
        float flen = MathF.Sqrt(fx * fx + fy * fy);
        if (flen < 1e-4f) { fx = 1f; fy = 0f; } else { fx /= flen; fy /= flen; }

        for (int k = 0; k < ObservedCount; k++)
        {
            int idx = _nearestScratch[k];
            if (idx < 0) continue;
            MapFeature f = _features[idx];
            int w = k * FloatsPerFeature;

            WriteLocalDir(dest, ref w, f.Centre - origin, fx, fy);
            dest[w++] = MathF.Log(1f + MathF.Sqrt(_nearestDist[k])) * 0.2f;

            // Kind one-hot. Seven slots covers None plus the six real kinds, so adding a kind is a constant
            // bump here and a retrain, not a silent reinterpretation of an existing column.
            for (int t = 0; t < 7; t++) dest[w + t] = 0f;
            dest[w + (int)f.Kind] = 1f;
            w += 7;

            WriteLocalDir(dest, ref w, f.Exit - origin, fx, fy);
            dest[w++] = MathF.Min(f.TransitTime, 8f) * 0.25f;
            dest[w++] = f.Kind == MapFeatureKind.Hurt ? MathF.Min(f.Damage / 100f, 2f) : f.State;
        }
    }

    /// <summary>
    /// Write a world offset as (forward, right, up) in the goal frame, each scaled so a typical arena
    /// distance lands inside [-1,1]. 512 qu is roughly a room. Same handedness as
    /// <c>NeuralObservation.ToFrame</c>.
    /// </summary>
    private static void WriteLocalDir(Span<float> dest, ref int w, Vector3 delta, float fx, float fy)
    {
        const float scale = 1f / 512f;
        dest[w++] = Math.Clamp((delta.X * fx + delta.Y * fy) * scale, -4f, 4f);
        dest[w++] = Math.Clamp((delta.X * fy - delta.Y * fx) * scale, -4f, 4f);
        dest[w++] = Math.Clamp(delta.Z * scale, -4f, 4f);
    }

    /// <summary>
    /// The feature the given point is standing inside, if any. Used by the reward (entering a hurt volume)
    /// and by the training env's episode-abort test.
    /// </summary>
    public bool TryFind(Vector3 point, MapFeatureKind kind, out MapFeature found)
    {
        for (int i = 0; i < _features.Count; i++)
        {
            if (_features[i].Kind != kind || !_features[i].Contains(point)) continue;
            found = _features[i];
            return true;
        }
        found = default;
        return false;
    }
}
