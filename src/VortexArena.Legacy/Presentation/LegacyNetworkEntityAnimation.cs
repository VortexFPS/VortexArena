// Port of the parts of Base/darkplaces/cl_main.c CL_UpdateNetworkEntity that decide which poses of a model
// an entity the SERVER networks is drawn in (the frame lerp), and of prvm_cmds.c
// VM_FrameBlendFromFrameGroupBlend, which turns that into poses - for one or two frame groups, which is all
// a network entity has. An entity the client program draws itself goes through FormatLegacyModels instead,
// from its own .frame/.lerpfrac fields.
using VortexArena.Legacy.Csqc;

namespace VortexArena.Legacy.Presentation;

/// <summary>
/// The animation state DarkPlaces keeps for one network entity (<c>entity_render_t.framegroupblend[0..1]</c>):
/// the frame it shows and when that began, and the frame it showed before. A Quake monster's server sets
/// <c>.frame</c> ten times a second; the client blends from the previous frame to the new one over the time
/// the previous one lasted, at most a tenth of a second, which is what makes a ten-frames-a-second walk
/// smooth.
/// </summary>
public struct LegacyNetworkEntityAnimation
{
    /// <summary>cl_lerpanim_maxdelta_server and cl_lerpanim_maxdelta_framegroups, both 0.1 by default.</summary>
    public const float DefaultMaxDelta = 0.1f;

    private int _frame0, _frame1;
    private double _start0, _start1;
    private float _lerp0, _lerp1;

    /// <summary>
    /// cl_parse.c CL_MoveLerpEntityStates, "reset all persistent stuff if this is a new entity" (it was not
    /// active in the previous server frame, or its model changed) and the same reset when EF_TELEPORT_BIT
    /// toggles: the entity shows <paramref name="frame"/> since now, with nothing to blend from.
    /// </summary>
    public void Reset(int frame, double time)
    {
        _frame0 = _frame1 = frame;
        _start0 = _start1 = time;
        _lerp0 = 1;
        _lerp1 = 0;
    }

    /// <summary>
    /// CL_MoveLerpEntityStates when EF_RESTARTANIM_BIT toggles: the frame begins again now, blending from
    /// what was shown - even if it is the same frame number (a looping attack played twice).
    /// </summary>
    public void Restart(int frame, double time)
    {
        _frame1 = _frame0;
        _start1 = _start0;
        _lerp1 = 1;
        _frame0 = frame;
        _start0 = time;
        _lerp0 = 0;
    }

    /// <summary>
    /// One frame of CL_UpdateNetworkEntity's "animation lerp" for an entity without complex animation.
    /// </summary>
    /// <param name="model">The entity's model, or null.</param>
    /// <param name="frame">state_current.frame, already replaced by 0 when it is not below the model's frame count.</param>
    /// <param name="time">cl.time.</param>
    /// <param name="serverFrameDelta">cl.mtime[0] - cl.mtime[1]: the time between the last two server frames.</param>
    public void Update(LegacyModel? model, int frame, double time, double serverFrameDelta,
        float maxDeltaServer = DefaultMaxDelta, float maxDeltaFrameGroups = DefaultMaxDelta)
    {
        if (_frame0 == frame)
        {
            // "update frame lerp fraction"
            _lerp0 = 1;
            _lerp1 = 0;
            if (_start0 > _start1)
            {
                // "make sure frame lerp won't last longer than 100ms (this mainly helps with models that use
                // framegroups and switch between them infrequently)"
                float maxDelta = maxDeltaServer;
                if (model?.Scenes is { } scenes && (FrameCount(scenes, _frame0) > 1 || FrameCount(scenes, _frame1) > 1))
                    maxDelta = maxDeltaFrameGroups;
                double limit = System.Math.Max(maxDelta, serverFrameDelta);
                double lerp = (time - _start0) / System.Math.Min(_start0 - _start1, limit);
                _lerp0 = (float)System.Math.Clamp(double.IsNaN(lerp) ? 1 : lerp, 0, 1);
                _lerp1 = 1 - _lerp0;
            }
        }
        else
        {
            // "begin a new frame lerp"
            _frame1 = _frame0;
            _start1 = _start0;
            _lerp1 = 1;
            _frame0 = frame;
            _start0 = time;
            _lerp0 = 0;
        }

        static int FrameCount(LegacyAnimScene[] scenes, int frame) => (uint)frame < (uint)scenes.Length ? scenes[frame].FrameCount : 1;
    }

    /// <summary>
    /// The two strongest poses of the state: pose <paramref name="poseA"/> blended towards
    /// <paramref name="poseB"/> by <paramref name="lerp"/> (0 = all A). False for a model without frames.
    /// </summary>
    /// <param name="noLerp">r_lerpmodels 0 (or r_lerpsprites 0 for a sprite, the default): only the first frame group counts, and no pose is blended.</param>
    public readonly bool Poses(LegacyModel? model, double time, bool noLerp, out int poseA, out int poseB, out float lerp)
    {
        System.Span<LegacyFrameGroupBlend> groups = stackalloc LegacyFrameGroupBlend[2];
        groups[0] = new LegacyFrameGroupBlend(_frame0, _lerp0, _start0);
        groups[1] = new LegacyFrameGroupBlend(_frame1, _lerp1, _start1);
        return LegacyFrameBlend.Strongest(model, groups, time, noLerp, out poseA, out poseB, out lerp);
    }
}

/// <summary>framegroupblend_t: a frame number (an animscene), its weight, and when it began.</summary>
public readonly record struct LegacyFrameGroupBlend(int Frame, float Lerp, double Start);

/// <summary>VM_FrameBlendFromFrameGroupBlend, for callers that have the frame groups in hand rather than in an edict.</summary>
public static class LegacyFrameBlend
{
    /// <summary>MAX_FRAMEBLENDS.</summary>
    public const int MaxBlends = 8;

    /// <summary>
    /// Fills <paramref name="poses"/> / <paramref name="weights"/> (both at least <see cref="MaxBlends"/> long)
    /// with the weighted poses of up to four frame groups; unused entries have weight 0. Returns how many
    /// were written.
    /// </summary>
    public static int FromGroups(LegacyModel? model, System.ReadOnlySpan<LegacyFrameGroupBlend> groups, double time, bool noLerp,
        System.Span<int> poses, System.Span<float> weights)
    {
        poses[..MaxBlends].Clear();
        weights[..MaxBlends].Clear();
        if (model is null)
        {
            weights[0] = 1;
            return 1;
        }

        bool first = true;
        LegacyAnimScene[]? scenes = model.Scenes;
        int numFrames = model.NumFrames;
        for (int k = 0; k < groups.Length && k < 4; k++)
        {
            int f = groups[k].Frame;
            if ((uint)f >= (uint)numFrames) f = 0;
            double share = groups[k].Lerp, d = share;
            if (!(share > 0)) continue;
            if (noLerp)
            {
                if (!first) continue;
                d = share = 1;
                first = false;
            }
            if (scenes is not null && f < scenes.Length)
            {
                LegacyAnimScene scene = scenes[f];
                f = scene.FirstFrame;
                if (scene.FrameCount > 1)
                {
                    double subLerp = scene.FrameRate * (time - groups[k].Start);
                    double whole = System.Math.Floor(subLerp);
                    subLerp -= whole;
                    int sub1 = whole >= int.MaxValue - 1 ? int.MaxValue - 1 : whole <= int.MinValue ? int.MinValue : (int)whole;
                    int sub2 = sub1 + 1;
                    if (subLerp < 1.0 / 65536.0) subLerp = 0;
                    if (subLerp > 65535.0 / 65536.0) subLerp = 1;
                    if (noLerp) subLerp = 0;
                    if (scene.Loop)
                    {
                        sub1 %= scene.FrameCount;
                        sub2 %= scene.FrameCount;
                    }
                    f = System.Math.Clamp(sub1, 0, scene.FrameCount - 1) + scene.FirstFrame;
                    sub2 = System.Math.Clamp(sub2, 0, scene.FrameCount - 1) + scene.FirstFrame;
                    d = subLerp * share;
                    if (d > 0) Add(poses, weights, sub2, (float)d);
                    d = (1 - subLerp) * share;
                }
            }
            if (d > 0) Add(poses, weights, f, (float)d);
        }
        int count = 0;
        while (count < MaxBlends && weights[count] > 0) count++;
        return count;

        static void Add(System.Span<int> poses, System.Span<float> weights, int pose, float weight)
        {
            for (int i = 0; i < MaxBlends; i++)
                if (weights[i] <= 0 || poses[i] == pose)
                {
                    poses[i] = pose;
                    weights[i] += weight;
                    return;
                }
        }
    }

    /// <summary>
    /// The two heaviest poses of <see cref="FromGroups"/>, as a renderer that morphs between two frames wants
    /// them: <paramref name="poseA"/> towards <paramref name="poseB"/> by <paramref name="lerp"/>.
    /// </summary>
    public static bool Strongest(LegacyModel? model, System.ReadOnlySpan<LegacyFrameGroupBlend> groups, double time, bool noLerp,
        out int poseA, out int poseB, out float lerp)
    {
        poseA = poseB = 0;
        lerp = 0;
        if (model?.Scenes is null) return false;
        System.Span<int> poses = stackalloc int[MaxBlends];
        System.Span<float> weights = stackalloc float[MaxBlends];
        int count = FromGroups(model, groups, time, noLerp, poses, weights);
        int best = -1, second = -1;
        for (int i = 0; i < count; i++)
        {
            if (best < 0 || weights[i] > weights[best]) { second = best; best = i; }
            else if (second < 0 || weights[i] > weights[second]) second = i;
        }
        if (best < 0) return false;
        poseA = poses[best];
        poseB = second >= 0 ? poses[second] : poseA;
        float total = weights[best] + (second >= 0 ? weights[second] : 0);
        lerp = second >= 0 && total > 0 ? weights[second] / total : 0;
        return true;
    }
}

/// <summary>
/// The trail state DarkPlaces keeps for one network entity (<c>entity_persistent_t.trail_origin</c>,
/// <c>trail_allowed</c>) and CL_UpdateNetworkEntityTrail's rule for it: an entity whose effects ask for a
/// trail leaves one from where it was last frame to where it is now - but not on the first frame it is seen,
/// nor across a teleport, where "last frame" is somewhere else entirely.
/// </summary>
public struct LegacyNetworkEntityTrail
{
    private bool _allowed;
    private QuakeC.QcVector _origin;

    /// <summary>The entity was removed or teleported (state_current.active just became true): no trail to here.</summary>
    public void Reset() => _allowed = false;

    /// <summary>
    /// Records where the entity is this frame. True when a trail segment is to be drawn, from
    /// <paramref name="from"/> to <paramref name="origin"/>; the first frame after a <see cref="Reset"/>
    /// only records.
    /// </summary>
    public bool Step(QuakeC.QcVector origin, bool wantsTrail, out QuakeC.QcVector from)
    {
        from = _origin;
        bool draw = wantsTrail && _allowed;
        _allowed = true;
        _origin = origin;
        return draw;
    }
}
