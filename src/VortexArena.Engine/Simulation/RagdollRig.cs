using System;
using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Math;

namespace VortexArena.Engine.Simulation;

/// <summary>
/// Maps a player skeleton onto the 15-particle corpse ragdoll and drives the bones back from the solved
/// particles. Bone names cover the stock Xonotic player rigs — every shipped model (erebus/gak/umbra/
/// seraphina/…) uses the same Blender deform set (<c>spine1..4, head, upperarm/forearm/hand_L|R,
/// upperleg/lowerleg/foot_L|R</c>, verified from the IQM string tables) — with 3ds-Max Biped
/// (<c>bip01 …</c>) fallbacks for community models. A rig that doesn't resolve the full set gets NO ragdoll
/// (TryBuild returns null) and keeps the faithful death animation.
///
/// Particle layout (index = the seeded bone's posed origin):
/// 0 pelvis, 1 chest, 2 head, 3/4 elbow L/R (forearm origin), 5/6 wrist L/R (hand origin),
/// 7/8 shoulder L/R (upperarm origin), 9/10 hip L/R (upperleg origin), 11/12 knee L/R (lowerleg origin),
/// 13/14 ankle L/R (foot origin).
///
/// Driven bones get position + a shortest-arc rotation of their SEEDED basis onto the current segment
/// direction (parallel transport — twist rides along from the death pose); every unmapped bone (spine2/3,
/// neck, shoulders, hips, hands, fingers) keeps its frozen death-pose LOCAL transform and follows its driven
/// ancestor rigidly, which keeps the chains exactly connected.
///
/// FEET ARE SPECIAL on the stock rigs: <c>foot_L/toe_L</c> are parented under the IK root <c>leg_L</c>
/// (a child of <c>master</c>), NOT under <c>lowerleg_L</c> — a frozen-local foot would float at the death
/// pose while the leg flops elsewhere. So the feet are DRIVEN bones (oriented by the knee→ankle segment)
/// and each toe is a rigid companion of its foot; the IK helpers (<c>leg_*</c>, <c>knee_*</c>) deform
/// nothing and stay frozen.
/// </summary>
public sealed class RagdollRig
{
    private const int P_Pelvis = 0, P_Chest = 1, P_Head = 2;
    private const int P_ElbowL = 3, P_ElbowR = 4, P_WristL = 5, P_WristR = 6;
    private const int P_ShoulderL = 7, P_ShoulderR = 8, P_HipL = 9, P_HipR = 10;
    private const int P_KneeL = 11, P_KneeR = 12, P_AnkleL = 13, P_AnkleR = 14;

    /// <summary>Number of solver particles this rig uses.</summary>
    public const int ParticleCount = 15;

    // Candidate names per particle slot, first match wins. Stock Blender rig first, Biped fallbacks after.
    // NOTE hand_R must precede "bip01 r hand": erebus carries BOTH (the latter is the weapon TAG bone).
    private static readonly string[][] NameCandidates =
    {
        new[] { "spine1", "bip01 pelvis", "pelvis", "hips" },                    // 0 pelvis
        new[] { "spine4", "spine3", "spine2", "bip01 spine2", "bip01 spine1", "chest" }, // 1 chest
        new[] { "head", "bip01 head" },                                          // 2 head
        new[] { "forearm_L", "bip01 l forearm" },                                // 3 elbow L
        new[] { "forearm_R", "bip01 r forearm" },                                // 4 elbow R
        new[] { "hand_L", "bip01 l hand" },                                      // 5 wrist L
        new[] { "hand_R", "bip01 r hand" },                                      // 6 wrist R
        new[] { "upperarm_L", "bip01 l upperarm" },                              // 7 shoulder L
        new[] { "upperarm_R", "bip01 r upperarm" },                              // 8 shoulder R
        new[] { "upperleg_L", "bip01 l thigh", "thigh_L" },                      // 9 hip L
        new[] { "upperleg_R", "bip01 r thigh", "thigh_R" },                      // 10 hip R
        new[] { "lowerleg_L", "bip01 l calf", "calf_L" },                        // 11 knee L
        new[] { "lowerleg_R", "bip01 r calf", "calf_R" },                        // 12 knee R
        new[] { "foot_L", "bip01 l foot" },                                      // 13 ankle L
        new[] { "foot_R", "bip01 r foot" },                                      // 14 ankle R
    };

    // The bones we write back, each oriented by a particle-pair segment: (particle slot whose bone we drive,
    // origin particle, segment from → to). Sorted by bone id before driving (parents first — IQM order).
    private static readonly (int Slot, int Origin, int DirA, int DirB)[] DrivenTable =
    {
        (P_Pelvis, P_Pelvis, P_Pelvis, P_Chest),   // spine1: the whole torso block rides this
        (P_Head, P_Head, P_Chest, P_Head),         // head flops on the chest→head segment
        (P_ShoulderL, P_ShoulderL, P_ShoulderL, P_ElbowL), // upperarm_L
        (P_ShoulderR, P_ShoulderR, P_ShoulderR, P_ElbowR),
        (P_ElbowL, P_ElbowL, P_ElbowL, P_WristL),  // forearm_L
        (P_ElbowR, P_ElbowR, P_ElbowR, P_WristR),
        (P_HipL, P_HipL, P_HipL, P_KneeL),         // upperleg_L
        (P_HipR, P_HipR, P_HipR, P_KneeR),
        (P_KneeL, P_KneeL, P_KneeL, P_AnkleL),     // lowerleg_L
        (P_KneeR, P_KneeR, P_KneeR, P_AnkleR),
        (P_AnkleL, P_AnkleL, P_KneeL, P_AnkleL),   // foot_L: IK-parented on stock rigs, must be driven
        (P_AnkleR, P_AnkleR, P_KneeR, P_AnkleR),
    };

    private const int DrivenAnkleL = 10, DrivenAnkleR = 11; // DrivenTable indices the toe companions anchor to

    // Optional rigid companions: (candidate names, the DrivenTable entry they follow). Toes share their
    // foot's IK parent on stock rigs, so they'd float exactly like the feet if left frozen.
    private static readonly (string[] Names, int DrivenIdx)[] CompanionCandidates =
    {
        (new[] { "toe_L", "bip01 l toe0" }, DrivenAnkleL),
        (new[] { "toe_R", "bip01 r toe0" }, DrivenAnkleR),
    };

    /// <summary>1-based bone number per particle slot.</summary>
    public readonly int[] BoneIds = new int[ParticleCount];

    /// <summary>Model-space particle seeds captured from the death pose (<see cref="CaptureSeed"/>).</summary>
    public readonly Vector3[] SeedPositions = new Vector3[ParticleCount];

    private readonly BoneMatrix[] _seedBasis = new BoneMatrix[DrivenTable.Length];
    private readonly Vector3[] _seedDir = new Vector3[DrivenTable.Length];
    private readonly int[] _driveOrder = new int[DrivenTable.Length]; // DrivenTable indices, bone-id ascending

    // Resolved rigid companions: bone id, the driven entry it follows, its seed offset from that entry's
    // origin particle, and its seeded basis. Written after all driven bones each drive.
    private readonly List<(int Bone, int DrivenIdx, Vector3 SeedOffset, BoneMatrix SeedBasis)> _companions = new();
    private readonly List<(int Bone, int DrivenIdx)> _companionBones = new(); // pre-CaptureSeed resolution

    private RagdollRig() { }

    /// <summary>
    /// Resolve the rig against a skeleton: every particle slot must find its bone AND the limb chains must
    /// actually be chains (child bones descend from their expected ancestors) — a community model with alien
    /// names or topology returns null and keeps animated deaths.
    /// </summary>
    public static RagdollRig? TryBuild(SkeletonManager mgr, int skel)
    {
        var rig = new RagdollRig();
        for (int slot = 0; slot < ParticleCount; slot++)
        {
            int bone = 0;
            foreach (string name in NameCandidates[slot])
            {
                bone = mgr.FindBone(skel, name);
                if (bone != 0) break;
            }
            if (bone == 0)
                return null;
            rig.BoneIds[slot] = bone;
        }
        // Distinctness (a degenerate rig could resolve chest == pelvis).
        if (rig.BoneIds[P_Chest] == rig.BoneIds[P_Pelvis])
            return null;
        // Topology: each chain child must descend from its expected ancestor. NOTE the feet are exempt —
        // stock rigs parent foot/toe under the IK root (leg_* → master), which is exactly why they're driven.
        if (!Descends(mgr, skel, rig.BoneIds[P_Chest], rig.BoneIds[P_Pelvis]) ||
            !Descends(mgr, skel, rig.BoneIds[P_Head], rig.BoneIds[P_Chest]) ||
            !Descends(mgr, skel, rig.BoneIds[P_ElbowL], rig.BoneIds[P_ShoulderL]) ||
            !Descends(mgr, skel, rig.BoneIds[P_ElbowR], rig.BoneIds[P_ShoulderR]) ||
            !Descends(mgr, skel, rig.BoneIds[P_WristL], rig.BoneIds[P_ElbowL]) ||
            !Descends(mgr, skel, rig.BoneIds[P_WristR], rig.BoneIds[P_ElbowR]) ||
            !Descends(mgr, skel, rig.BoneIds[P_KneeL], rig.BoneIds[P_HipL]) ||
            !Descends(mgr, skel, rig.BoneIds[P_KneeR], rig.BoneIds[P_HipR]))
            return null;

        // Optional rigid companions (toes): resolved by name; absent is fine.
        foreach ((string[] names, int drivenIdx) in CompanionCandidates)
        {
            foreach (string name in names)
            {
                int bone = mgr.FindBone(skel, name);
                if (bone != 0) { rig._companionBones.Add((bone, drivenIdx)); break; }
            }
        }

        // Drive order: bone-id ascending == parents before children (IQM guarantees parent < child).
        for (int i = 0; i < rig._driveOrder.Length; i++) rig._driveOrder[i] = i;
        Array.Sort(rig._driveOrder, (x, y) =>
            rig.BoneIds[DrivenTable[x].Slot].CompareTo(rig.BoneIds[DrivenTable[y].Slot]));
        return rig;
    }

    private static bool Descends(SkeletonManager mgr, int skel, int bone, int ancestor)
    {
        for (int b = mgr.GetBoneParent(skel, bone); b != 0; b = mgr.GetBoneParent(skel, b))
            if (b == ancestor)
                return true;
        return false;
    }

    /// <summary>
    /// Capture the death pose as the rig's reference: particle seed positions (model space) and each driven
    /// bone's basis + segment direction. Call once, right after the final animated <c>FromFrames</c>.
    /// </summary>
    public void CaptureSeed(Func<int, BoneMatrix> boneAbs)
    {
        for (int slot = 0; slot < ParticleCount; slot++)
            SeedPositions[slot] = boneAbs(BoneIds[slot]).Origin;
        for (int d = 0; d < DrivenTable.Length; d++)
        {
            (int slot, _, int a, int b) = DrivenTable[d];
            _seedBasis[d] = boneAbs(BoneIds[slot]);
            Vector3 dir = SeedPositions[b] - SeedPositions[a];
            _seedDir[d] = dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : new Vector3(0, 0, 1);
        }
        _companions.Clear();
        foreach ((int bone, int drivenIdx) in _companionBones)
        {
            BoneMatrix abs = boneAbs(bone);
            Vector3 anchor = SeedPositions[DrivenTable[drivenIdx].Origin];
            _companions.Add((bone, drivenIdx, abs.Origin - anchor, abs));
        }
    }

    /// <summary>
    /// Wire the corpse constraint network into a solver whose particles were seeded (in any rigid transform
    /// of <see cref="SeedPositions"/> — lengths are read back from the solver itself): the rigid torso clique,
    /// limb segment sticks, and the Jakobsen joint stops (hyperextension caps as fractions of the SEGMENT SUM,
    /// fold/scissor floors as fractions of the seeded spacing).
    /// </summary>
    public void Wire(RagdollSolver s)
    {
        // Rigid torso block: full clique over pelvis/chest/shoulders/hips (15 sticks holds it square).
        Span<int> torso = stackalloc int[] { P_Pelvis, P_Chest, P_ShoulderL, P_ShoulderR, P_HipL, P_HipR };
        for (int i = 0; i < torso.Length; i++)
            for (int j = i + 1; j < torso.Length; j++)
                s.AddStick(torso[i], torso[j]);

        // Neck: rigid chest→head, flop bounded against pelvis and shoulders.
        s.AddStick(P_Chest, P_Head);
        s.AddLimit(P_Head, P_Pelvis, 0.8f, 1.15f);
        s.AddLimit(P_Head, P_ShoulderL, 0.6f, 1.5f);
        s.AddLimit(P_Head, P_ShoulderR, 0.6f, 1.5f);

        // Limbs: segment sticks + shoulder→wrist / hip→ankle stops (no hyperextension past ~straight, no
        // folding through themselves). The straight cap uses the SUM of the two segments, not the seeded
        // end-to-end distance — the death pose may already be bent.
        WireLimb(s, P_ShoulderL, P_ElbowL, P_WristL, foldMin: 0.25f);
        WireLimb(s, P_ShoulderR, P_ElbowR, P_WristR, foldMin: 0.25f);
        WireLimb(s, P_HipL, P_KneeL, P_AnkleL, foldMin: 0.35f);
        WireLimb(s, P_HipR, P_KneeR, P_AnkleR, foldMin: 0.35f);

        // Anti-scissor floors so the legs can't cross through each other.
        s.AddLimit(P_KneeL, P_KneeR, 0.3f, 4f);
        s.AddLimit(P_AnkleL, P_AnkleR, 0.25f, 5f);
    }

    private void WireLimb(RagdollSolver s, int root, int mid, int tip, float foldMin)
    {
        s.AddStick(root, mid);
        s.AddStick(mid, tip);
        float sum = Vector3.Distance(s.Position(root), s.Position(mid))
                  + Vector3.Distance(s.Position(mid), s.Position(tip));
        s.AddLimitAbsolute(root, tip, sum * foldMin, sum * 0.985f);
    }

    /// <summary>
    /// Write the solved pose onto the skeleton: for each driven bone, position = its origin particle and
    /// rotation = the seeded basis carried by the shortest arc from the seeded segment direction to the
    /// current one. <paramref name="particles"/> must already be in MODEL space (the driver inverts the
    /// entity transform); bones are written parents-first via <paramref name="setBoneAbs"/>.
    /// </summary>
    public void DriveBones(ReadOnlySpan<Vector3> particles, Action<int, BoneMatrix> setBoneAbs)
    {
        Span<Quaternion> arcs = stackalloc Quaternion[DrivenTable.Length];
        foreach (int d in _driveOrder)
        {
            (int slot, int origin, int a, int b) = DrivenTable[d];
            Vector3 dir = particles[b] - particles[a];
            Vector3 cur = dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : _seedDir[d];
            Quaternion q = arcs[d] = ArcQuat(_seedDir[d], cur);
            setBoneAbs(BoneIds[slot], Rotated(_seedBasis[d], q, particles[origin]));
        }
        // Rigid companions (toes) follow their driven anchor's rotation; written after every driven bone so
        // all ancestor rels are final (their parents are frozen IK helpers anyway on the stock rigs).
        foreach ((int bone, int drivenIdx, Vector3 seedOffset, BoneMatrix seedBasis) in _companions)
        {
            Quaternion q = arcs[drivenIdx];
            Vector3 origin = particles[DrivenTable[drivenIdx].Origin] + Vector3.Transform(seedOffset, q);
            setBoneAbs(bone, Rotated(seedBasis, q, origin));
        }
    }

    /// <summary>
    /// The seeded basis carried by the shortest-arc rotation taking <paramref name="from"/> to
    /// <paramref name="to"/> (both unit), re-based at <paramref name="origin"/>. Antiparallel input rotates
    /// 180° about any axis perpendicular to <paramref name="from"/>. Public for the unit tests.
    /// </summary>
    public static BoneMatrix ArcAlign(Vector3 from, Vector3 to, in BoneMatrix seedBasis, Vector3 origin)
        => Rotated(seedBasis, ArcQuat(from, to), origin);

    private static BoneMatrix Rotated(in BoneMatrix basis, Quaternion q, Vector3 origin)
        => new(Vector3.Transform(basis.Fwd, q),
               Vector3.Transform(basis.Left, q),
               Vector3.Transform(basis.Up, q),
               origin);

    /// <summary>The shortest-arc rotation taking unit <paramref name="from"/> to unit <paramref name="to"/>.</summary>
    private static Quaternion ArcQuat(Vector3 from, Vector3 to)
    {
        float d = Vector3.Dot(from, to);
        if (d > 0.99999f)
            return Quaternion.Identity;
        if (d < -0.99999f)
        {
            Vector3 axis = Vector3.Cross(from, new Vector3(1f, 0f, 0f));
            if (axis.LengthSquared() < 1e-6f)
                axis = Vector3.Cross(from, new Vector3(0f, 1f, 0f));
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        Vector3 c = Vector3.Cross(from, to);
        float s = MathF.Sqrt((1f + d) * 2f);
        return Quaternion.Normalize(new Quaternion(c.X / s, c.Y / s, c.Z / s, s * 0.5f));
    }
}
