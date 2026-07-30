using System.Numerics;
using VortexArena.Common;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The PREDICTED warpzone crossing (<see cref="TriggerTouch.PredictWarpzonesAmbient"/>) and its per-replay-chain
/// budget (<see cref="TriggerTouch.PredictedWarpBudget"/>). The budget exists to block the bogus within-chain
/// round trip through BOTH paired zones (T_B∘T_A == identity), but its SCOPE must be one replay chain — the
/// client arms it at the start of every chain (ClientNet: the snapshot reconcile AND each fresh-input predict
/// are separate full replays from the acked seed). Regression for the warpzone "view drops/raises briefly"
/// report: the budget was armed once per RENDER FRAME, so while the crossing tick was still unacked the frame's
/// first chain consumed it and the later chains were STARVED of the crossing — the frame's final predict left
/// the carrier on the NEAR side, bouncing the camera back through the seam for a frame, and the return leg (no
/// teleport pulse) let the stair smoother GLIDE the paired windows' height difference into a visible dip.
///
/// Mutates Api.Services / the MapMover index / WarpzoneTrace.AmbientManager, so it runs in the serialized
/// GlobalState collection.
/// </summary>
[Collection("GlobalState")]
public sealed class WarpzonePredictionTests
{
    private sealed class TestFacade : IEngineServices
    {
        public EngineServices Inner { get; }
        public MutableClock GameClock { get; } = new();
        public TestFacade() { Inner = new EngineServices(new CollisionWorld()); }
        public ITraceService Trace => Inner.Trace;
        public IEntityService Entities => Inner.Entities;
        public ICvarService Cvars => Inner.Cvars;
        public ISoundService Sound => Inner.Sound;
        public IModelService Models => Inner.Models;
        public IGameClock Clock => GameClock;
    }

    private static void Boot()
    {
        var f = new TestFacade();
        GameInit.Boot(f);
        MapMover.ClearIndex();
        MapObjectsState.Reset();
        f.GameClock.Time = 1f;
        f.GameClock.FrameTime = 1f / 72f;
        TriggerTouch.PredictionSeq = 0;
        TriggerTouch.LastPredictedWarpSeq = 0;
    }

    /// <summary>Restore the statics this suite arms so later tests see the headless defaults.</summary>
    private static void Cleanup()
    {
        WarpzoneTrace.AmbientManager = null;
        TriggerTouch.PredictedWarpBudget = int.MaxValue;
    }

    /// <summary>
    /// The reported repro geometry: a linked pair whose windows sit at DIFFERENT heights — the IN plane at the
    /// origin facing +X, the partner 400u away and 300u HIGHER facing back (−X). Published as the ambient
    /// manager the predictor resolves (what TraceService.SetWarpzoneManager does per match).
    /// </summary>
    private static void SpawnHeightOffsetPair()
    {
        var mgr = new WarpzoneManager();
        mgr.Spawn(new Vector3(0, 0, 0), new Vector3(0, 0, 0), "wzA", "wzB", new Vector3(-8, -64, -64), new Vector3(8, 64, 64));
        mgr.Spawn(new Vector3(400, 0, 300), new Vector3(0, 180, 0), "wzB", "wzA", new Vector3(-8, -64, -64), new Vector3(8, 64, 64));
        mgr.Link();
        Assert.All(mgr.Zones, z => Assert.True(z.Linked));
        WarpzoneTrace.AmbientManager = mgr;
    }

    /// <summary>A SOLID_NOT prediction-carrier client whose EYE has just crossed the IN plane (the QC
    /// WarpZone_PlaneDist &lt; 0 gate, probed at origin + view_ofs), moving into the seam.</summary>
    private static Entity CrossingCarrier()
    {
        Entity c = Api.Entities.Spawn();
        c.ClassName = "player";
        c.Flags = EntFlags.Client | EntFlags.OnGround;
        c.Solid = Solid.Not;
        c.Mins = new Vector3(-16, -16, -24);
        c.Maxs = new Vector3(16, 16, 45);
        c.Size = c.Maxs - c.Mins;
        c.ViewOfs = new Vector3(0, 0, 35);
        ReseedPreWarp(c);
        return c;
    }

    /// <summary>Load the pre-warp state back onto the carrier — what a reconcile replay chain does when it
    /// re-seeds from a server ack captured BEFORE the crossing tick (PredictedState carries origin/velocity/
    /// onground; the angles come from the replayed input).</summary>
    private static void ReseedPreWarp(Entity c)
    {
        c.Velocity = new Vector3(-200, 0, 0);
        c.Angles = new Vector3(0, 180, 0);
        c.Flags |= EntFlags.OnGround;
        Api.Entities.SetOrigin(c, new Vector3(-1, 0, 0));
        c.OldOrigin = c.Origin;
    }

    private static void AssertFarSide(Entity c)
        => Assert.True(c.Origin.X > 350f && c.Origin.Z > 250f,
            $"carrier should have crossed to the exit window (≈399, 0, 300); is at {c.Origin}");

    private static void AssertNearSide(Entity c)
        => Assert.True(c.Origin.X < 50f && c.Origin.Z < 50f,
            $"carrier should still be at the entry window (≈−1, 0, 0); is at {c.Origin}");

    [Fact]
    public void EveryReplayChain_MayCross_WhileTheCrossingTickIsUnacked()
    {
        Boot();
        try
        {
            SpawnHeightOffsetPair();
            Entity carrier = CrossingCarrier();

            // Chain 1 — the snapshot reconcile replay: seeded pre-warp, replays the crossing tick.
            TriggerTouch.PredictedWarpBudget = 1;
            TriggerTouch.PredictWarpzonesAmbient(carrier);
            AssertFarSide(carrier);
            Assert.Equal(0, TriggerTouch.PredictedWarpBudget); // the crossing consumed this chain's budget

            // Chain 2, SAME frame — the fresh-input predict: a full replay from the SAME pre-warp seed, so it
            // must re-cross too. ClientNet arms the budget at the start of every chain; under the old per-frame
            // arming this chain was starved and the frame rendered the carrier on the NEAR side (the bounce).
            ReseedPreWarp(carrier);
            TriggerTouch.PredictedWarpBudget = 1;
            TriggerTouch.PredictWarpzonesAmbient(carrier);
            AssertFarSide(carrier);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void ExhaustedBudget_BlocksTheCrossing_TheOldPerFrameArmingStarvedLaterChains()
    {
        Boot();
        try
        {
            SpawnHeightOffsetPair();
            Entity carrier = CrossingCarrier();

            // One armed budget, two chains — the OLD per-render-frame model. Chain 1 crosses...
            TriggerTouch.PredictedWarpBudget = 1;
            TriggerTouch.PredictWarpzonesAmbient(carrier);
            AssertFarSide(carrier);

            // ...and chain 2 (re-seeded pre-warp, budget already spent) is starved: the carrier stays on the
            // NEAR side. This is the same guard that (correctly) blocks a second crossing WITHIN one chain —
            // the bogus T_B∘T_A round trip — which is why the fix re-arms per chain rather than removing it.
            ReseedPreWarp(carrier);
            TriggerTouch.PredictWarpzonesAmbient(carrier);
            AssertNearSide(carrier);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void PredictedCrossing_StampsTheSmoothingPulse_NotTheViewSnap()
    {
        Boot();
        try
        {
            SpawnHeightOffsetPair();
            Entity carrier = CrossingCarrier();
            Assert.Equal(0f, carrier.LastTeleportTime);

            TriggerTouch.PredictedWarpBudget = 1;
            TriggerTouch.PredictWarpzonesAmbient(carrier);
            AssertFarSide(carrier);

            // The TELEPORT PULSE for the view smoothing (QC .lastteleporttime): the camera's one-shot consume
            // snaps the stair/eye-height glide across the seam instead of gliding the windows' height difference.
            Assert.Equal(1f, carrier.LastTeleportTime); // the booted clock's time
            // The view SNAP stays authoritative-only (WarpzoneManager.Teleport stamps it server-side): the
            // predicted crossing must NOT stamp .fixangle — a replay can spuriously re-cross a zone the server
            // never did, and a wrong predicted snap visibly yanks the view (see PredictWarpzonesAmbient).
            Assert.False(carrier.FixAngle);
            // QC UNSET_ONGROUND — airborne through the seam until the next ground trace.
            Assert.False(carrier.OnGround);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void FaithfulSmoothing_TeleportPulse_SnapsTheHeightDifference_NoDip()
    {
        // The view-level half of the story: a grounded crossing to a window 300u higher. WITHOUT the teleport
        // pulse the stair glide treats the jump as terrain — the rendered eye lags the live Z by the full
        // step-height clamp (31u) and catches up at cl_stairsmoothspeed (~150ms) = the reported dip/raise.
        var glided = new FaithfulViewSmoothing();
        glided.Apply(0f, 1f / 72f, onground: true, viewOfsZ: 35f);
        FaithfulViewSmoothing.Result lag = glided.Apply(300f, 1f / 72f, onground: true, viewOfsZ: 35f);
        Assert.True(lag.StairZ <= 300f - 25f, $"without the pulse the glide should lag the live Z; got {lag.StairZ}");

        // WITH the pulse (the carrier's LastTeleportTime consume) the smoother snaps — no dip at any height gap.
        var snapped = new FaithfulViewSmoothing();
        snapped.Apply(0f, 1f / 72f, onground: true, viewOfsZ: 35f);
        FaithfulViewSmoothing.Result snap = snapped.Apply(300f, 1f / 72f, onground: true, viewOfsZ: 35f, teleported: true);
        Assert.Equal(300f, snap.StairZ);
    }
}
