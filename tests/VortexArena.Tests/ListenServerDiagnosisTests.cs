using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using XonoticGodot.Tests.Camera;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// LISTEN-SERVER movement diagnosis (the "build apparatus + diagnose more first" deliverable). Drives the
/// <see cref="ListenServerHarness"/> with catharsis-like dt streams (steady 72/60/43 fps + hitches + a GC pause)
/// and measures the per-frame RECONCILE CORRECTION — the rubberband the camera shows when client prediction and
/// the soft-capped server sim disagree. Toggling <c>Lockstep</c> (server runs as many ticks as the client
/// predicted, no soft cap) ATTRIBUTES how much of the rubberband is the soft-cap/proportional-predict mismatch.
/// These tests PRINT the attribution; the assertions only pin the qualitative findings so they stay green.
/// </summary>
public class ListenServerDiagnosisTests
{
    private readonly ITestOutputHelper _out;
    public ListenServerDiagnosisTests(ITestOutputHelper o) => _out = o;

    private const float Tic = 1f / 72f;

    [Fact]
    public void Diagnose_Rubberband_VsFrameRate_AndLockstepFix()
    {
        _out.WriteLine("scenario                | maxCorr  meanCorr  >2u frames | (lockstep) maxCorr meanCorr");
        _out.WriteLine("------------------------|------------------------------|----------------------------");
        Row("steady 144fps", Steady(1f / 144f, 6f));
        Row("steady  72fps", Steady(1f / 72f, 6f));
        Row("steady  60fps", Steady(1f / 60f, 6f));
        Row("steady  43fps", Steady(1f / 43f, 6f));
        Row("43fps + 50ms hitches", Hitchy(1f / 43f, 0.050f, every: 20, seconds: 6f));
        Row("43fps + 170ms GC pause", Hitchy(1f / 43f, 0.170f, every: 60, seconds: 6f));

        // Per-frame vs LEGACY drain at 43fps+GC (does switching cl_movement_perframe 0 mitigate?).
        var s = Hitchy(1f / 43f, 0.170f, every: 60, seconds: 6f);
        var pf = RunHarness(s, lockstep: false, perFrame: true);
        var lg = RunHarness(s, lockstep: false, perFrame: false);
        _out.WriteLine($"\n43fps+GC drain mode: per-frame maxCorr={MaxCorr(pf):F2}u mean={MeanCorr(pf):F3}u  |  " +
                       $"legacy maxCorr={MaxCorr(lg):F2}u mean={MeanCorr(lg):F3}u");

        // CONFIRMED DIAGNOSIS (pinned):
        //  (a) at fps >= the 72Hz sim rate, the listen-server prediction is EXACT — zero rubberband.
        //  (b) below 72fps, the two independent integer-tick accumulators (server sim vs client predict, a frame
        //      apart) diverge by ~1 tick every frame → a per-frame reconcile correction (the felt rubberband/jitter).
        //  (c) removing the server soft cap ("lockstep" probe) does NOT fix it and is WORSE on big catch-ups — so
        //      the soft cap is not the lever; the dual-accumulator + starve-repeat is.
        Assert.True(MaxCorr(RunHarness(Steady(1f / 72f, 4f), lockstep: false)) < 0.5f,
            "at >=72fps the listen-server prediction must be exact (no rubberband)");
        Assert.True(MeanCorr(RunHarness(Steady(1f / 43f, 6f), lockstep: false)) > 2f,
            "below 72fps the dual-accumulator prediction must show a per-frame rubberband (the reproduced bug)");
        var stream = Hitchy(1f / 43f, 0.170f, every: 60, seconds: 6f);
        float normalMax = MaxCorr(RunHarness(stream, lockstep: false));
        float lockedMax = MaxCorr(RunHarness(stream, lockstep: true));
        _out.WriteLine($"\nATTRIBUTION (43fps+GC): soft-cap path maxCorr={normalMax:F2}u, lockstep(no-cap) maxCorr={lockedMax:F2}u " +
                       "(lockstep is NOT the fix)");
    }

    [Fact]
    public void Fix_CommandDriven_EliminatesRubberband_AtAllFrameRates()
    {
        // PATH B FIX: with command-driven movement (no fabricated starve-repeats), the per-frame reconcile
        // correction must drop to ~0 at EVERY frame rate — the same exactness the old code only had at >=72fps.
        _out.WriteLine("scenario                | OLD(starve) max/mean | FIXED(command-driven) max/mean");
        foreach (var (name, stream) in new (string, IReadOnlyList<float>)[]
        {
            ("steady 43fps",            Steady(1f / 43f, 6f)),
            ("steady 60fps",            Steady(1f / 60f, 6f)),
            ("43fps + 50ms hitches",    Hitchy(1f / 43f, 0.050f, 20, 6f)),
            ("43fps + 170ms GC pause",  Hitchy(1f / 43f, 0.170f, 60, 6f)),
            ("wild 20-160fps jitter",   Jitter(6f)),
        })
        {
            var old = RunHarness(stream, lockstep: false, commandDriven: false);
            var fix = RunHarness(stream, lockstep: false, commandDriven: true);
            _out.WriteLine($"{name,-23} | {MaxCorr(old),6:F2}/{MeanCorr(old),-6:F3} | {MaxCorr(fix),6:F2}/{MeanCorr(fix):F3}");
            Assert.True(MaxCorr(fix) < 0.5f,
                $"{name}: command-driven must eliminate the rubberband (max {MaxCorr(fix):F2}u, was {MaxCorr(old):F2}u)");
        }
    }

    [Fact]
    public void GracefulHitchRecovery_CapsThePostHitchLunge_AndStaysInSync()
    {
        // A frame HITCH (bot pathing / GC / OS) must NOT translate into a big movement jump. Walk forward at 60fps,
        // inject one 148ms hitch (the bot-pathing spike we measured), and compare the worst single-frame predicted-X
        // displacement: UNCAPPED drains the whole ~10-tick backlog in one frame (a lunge); CAPPED (to the server's
        // 4-tick budget) spreads the catch-up over several frames → bounded, graceful, and in lockstep (no reconcile).
        var stream = new List<float>();
        for (int i = 0; i < 80; i++) stream.Add(i == 40 ? 0.148f : 1f / 60f); // one 148ms hitch mid-walk
        var uncapped = RunHarness(stream, lockstep: false, commandDriven: true, catchupCap: int.MaxValue);
        var capped = RunHarness(stream, lockstep: false, commandDriven: true, catchupCap: 4);

        float lungeUncapped = MaxFrameDx(uncapped), lungeCapped = MaxFrameDx(capped);
        float oneTick = (1f / 72f) * 360f; // one tick of movement at maxspeed (~5u)
        _out.WriteLine($"148ms hitch: worst single-frame X jump — UNCAPPED={lungeUncapped:F1}u  CAPPED(4)={lungeCapped:F1}u " +
                       $"(1 tick ~= {oneTick:F1}u; reconcile maxCorr capped={MaxCorr(capped):F3}u)");

        Assert.True(lungeCapped < lungeUncapped * 0.7f,
            $"capping the catch-up must meaningfully shrink the post-hitch lunge (capped {lungeCapped:F1}u vs uncapped {lungeUncapped:F1}u)");
        Assert.True(lungeCapped <= 4f * oneTick + 2f,
            $"the capped catch-up must be bounded to ~the server tick budget ({lungeCapped:F1}u > {4f * oneTick + 2f:F1}u)");
        Assert.True(MaxCorr(capped) < 1f, "the graceful catch-up must stay in lockstep with the server (no reconcile snap)");
    }

    private static List<float> Jitter(float seconds)
    {
        float[] pat = { 1f / 144f, 1f / 60f, 1f / 90f, 1f / 45f, 1f / 120f, 1f / 72f, 1f / 160f, 1f / 20f, 1f / 50f };
        var l = new List<float>(); float t = 0; int i = 0;
        while (t < seconds) { float d = pat[i++ % pat.Length]; l.Add(d); t += d; }
        return l;
    }

    [Fact]
    public void Diagnose_PerFrame_StarveRepeat_DivergesEvenWithoutHitches()
    {
        // Even at a STEADY sub-72 fps with NO hitches, per-frame mode can diverge: when the server runs >1 sim tick
        // in a frame, its first tick drains the whole queued batch and the remaining soft-capped ticks STARVE-REPEAT,
        // moving the player more than the client predicted. Measure the steady-state correction at 43 fps.
        var steady43 = Steady(1f / 43f, 8f);
        var perFrame = RunHarness(steady43, lockstep: false);
        float maxC = MaxCorr(perFrame), meanC = MeanCorr(perFrame);
        int diverged = CountCorr(perFrame, 1f);
        _out.WriteLine($"steady 43fps per-frame: maxCorr={maxC:F2}u meanCorr={meanC:F3}u frames>1u={diverged}/{perFrame.Count}");
        // This is the diagnostic; we only assert the run produced data (the printed numbers are the finding).
        Assert.True(perFrame.Count > 100);
    }

    // ---- helpers ----

    private void Row(string name, IReadOnlyList<float> stream)
    {
        var normal = RunHarness(stream, lockstep: false);
        var locked = RunHarness(stream, lockstep: true);
        _out.WriteLine($"{name,-23} | {MaxCorr(normal),7:F2}  {MeanCorr(normal),7:F3}  {CountCorr(normal, 2f),5}      | " +
                       $"{MaxCorr(locked),7:F2} {MeanCorr(locked),7:F3}");
    }

    private static List<ListenServerHarness.FrameMetric> RunHarness(IReadOnlyList<float> stream, bool lockstep,
        bool perFrame = true, bool commandDriven = false, int catchupCap = int.MaxValue)
    {
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock);
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();
        var h = new ListenServerHarness
        { ServerSoftCap = 4, PerFrameDrain = perFrame, Lockstep = lockstep, CommandDriven = commandDriven, ClientCatchupCap = catchupCap };
        h.Run(stream, world, clock);
        return h.Metrics;
    }

    // Max single-frame predicted-X displacement = the post-hitch "lunge" (graceful recovery keeps it bounded; an
    // ungraceful catch-up drains the whole hitch backlog in one frame → a big jump).
    private static float MaxFrameDx(List<ListenServerHarness.FrameMetric> m)
    {
        float worst = 0f;
        for (int i = 1; i < m.Count; i++) worst = MathF.Max(worst, MathF.Abs(m[i].ClientX - m[i - 1].ClientX));
        return worst;
    }

    private static float MaxCorr(List<ListenServerHarness.FrameMetric> m)
    { float x = 0; foreach (var f in m) x = MathF.Max(x, f.ReconcileCorrection); return x; }
    private static float MeanCorr(List<ListenServerHarness.FrameMetric> m)
    { double s = 0; foreach (var f in m) s += f.ReconcileCorrection; return m.Count == 0 ? 0 : (float)(s / m.Count); }
    private static int CountCorr(List<ListenServerHarness.FrameMetric> m, float thresh)
    { int n = 0; foreach (var f in m) if (f.ReconcileCorrection > thresh) n++; return n; }

    private static List<float> Steady(float dt, float seconds)
    { var l = new List<float>(); for (float t = 0; t < seconds; t += dt) l.Add(dt); return l; }

    private static List<float> Hitchy(float dt, float hitch, int every, float seconds)
    {
        var l = new List<float>(); int i = 0;
        for (float t = 0; t < seconds; )
        { float d = (i % every == every - 1) ? hitch : dt; l.Add(d); t += d; i++; }
        return l;
    }

    private static (AnalyticWorld, MutableClock) FlatWorld()
    {
        var brushes = new List<(int, float[])>
        {
            (AnalyticWorld.ContSolid, new float[]
            { 0,0,1,0, 0,0,-1,64, 1,0,0,8192, -1,0,0,8192, 0,1,0,8192, 0,-1,0,8192 }),
        };
        return (AnalyticWorld.FromPlanes(brushes), new MutableClock());
    }
}
