using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests the snapshot delta-compression spine: the per-entity change-mask codec (<see cref="EntityStateCodec"/>)
/// and the server/client baseline-ack history (<see cref="ServerSnapshotHistory"/> /
/// <see cref="ClientSnapshotHistory"/>) that only sends spawned/changed/removed entities, plus movevar replication.
/// </summary>
public class SnapshotDeltaTests
{
    private static NetEntityState Player(int id, Vector3 origin, int health) => new()
    {
        EntNum = id, Kind = NetEntityKind.Player, ModelIndex = 7, Origin = origin, Health = health,
        // [W14a] match the producer + the Empty baseline so the wepent group bit stays clear for a plain player.
        SwitchWeapon = -1, SwitchingWeapon = -1,
    };

    [Fact]
    public void EntityCodec_RoundTrips_AnimAction_And_Wepent()
    {
        // [W14a] the new upper-body action overlay + exterior-weapon block round-trip through the delta codec.
        var baseline = Player(10, new Vector3(0, 0, 0), 100);
        var cur = baseline;
        cur.UpperAction = 3;            // some SHOOT/PAIN action id
        cur.AnimActionTime = 12.5f;     // raw float start time (death_time-class divergence)
        cur.SwitchWeapon = 5;
        cur.SwitchingWeapon = 4;
        cur.WepPhase = 2;               // WS_DROP
        cur.ViewmodelSkin = 1;
        cur.WepAlpha = 128;             // mid fade
        cur.GunAlign = 3;

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, cur);
        Assert.Equal(EntityField.AnimAction | EntityField.Wepent, mask);

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);
        Assert.Equal((byte)3, got.UpperAction);
        Assert.Equal(12.5f, got.AnimActionTime);
        Assert.Equal(5, got.SwitchWeapon);
        Assert.Equal(4, got.SwitchingWeapon);
        Assert.Equal((byte)2, got.WepPhase);
        Assert.Equal((byte)1, got.ViewmodelSkin);
        Assert.Equal((byte)128, got.WepAlpha);
        Assert.Equal((byte)3, got.GunAlign);
    }

    [Fact]
    public void EntityCodec_Plain_Player_Leaves_Wepent_And_AnimAction_Clear()
    {
        // [W14a] a plain player (no action, switch ids at the -1 sentinel) must NOT set the new group bits — they
        // cost nothing on the wire until a producer changes them.
        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, NetEntityState.Empty(10), Player(10, Vector3.Zero, 100));
        Assert.Equal(EntityField.None, mask & (EntityField.AnimAction | EntityField.Wepent));
    }

    [Fact]
    public void Diff_AnimAction_Bit_Set_Iff_UpperAction_Or_AnimActionTime_Differs()
    {
        // [W14a] the AnimAction bit (1<<18) must track UpperAction OR AnimActionTime, and NOTHING else.
        var baseline = Player(10, Vector3.Zero, 100);

        // identical → clear
        Assert.Equal(EntityField.None, NetEntityState.Diff(baseline, baseline) & EntityField.AnimAction);

        // only UpperAction differs → set
        var a = baseline; a.UpperAction = 4;
        Assert.Equal(EntityField.AnimAction, NetEntityState.Diff(baseline, a) & EntityField.AnimAction);

        // only AnimActionTime differs → set
        var b = baseline; b.AnimActionTime = 7.25f;
        Assert.Equal(EntityField.AnimAction, NetEntityState.Diff(baseline, b) & EntityField.AnimAction);

        // a wepent-only change must NOT spuriously set the AnimAction bit
        var c = baseline; c.WepPhase = 1;
        Assert.Equal(EntityField.None, NetEntityState.Diff(baseline, c) & EntityField.AnimAction);
    }

    [Fact]
    public void Diff_Wepent_Bit_Set_Iff_Any_Wepent_Field_Differs()
    {
        // [W14a] the Wepent bit (1<<19) must track ANY of the six exterior-weapon fields, and nothing outside them.
        var baseline = Player(10, Vector3.Zero, 100);

        Assert.Equal(EntityField.None, NetEntityState.Diff(baseline, baseline) & EntityField.Wepent);

        void AssertField(System.Func<NetEntityState, NetEntityState> mutate)
        {
            var cur = mutate(baseline);
            Assert.Equal(EntityField.Wepent, NetEntityState.Diff(baseline, cur) & EntityField.Wepent);
        }

        AssertField(s => { s.SwitchWeapon = 2; return s; });       // -1 → 2
        AssertField(s => { s.SwitchingWeapon = 3; return s; });     // -1 → 3
        AssertField(s => { s.WepPhase = 1; return s; });
        AssertField(s => { s.ViewmodelSkin = 2; return s; });
        AssertField(s => { s.WepAlpha = 64; return s; });
        AssertField(s => { s.GunAlign = 4; return s; });

        // an anim-only change must NOT spuriously set the Wepent bit
        var anim = baseline; anim.UpperAction = 5;
        Assert.Equal(EntityField.None, NetEntityState.Diff(baseline, anim) & EntityField.Wepent);
    }

    [Fact]
    public void EntityCodec_UnchangedField_LeavesItsMaskBitClear_AcrossTheNewGroups()
    {
        // [W14a] change ONLY UpperAction: AnimAction rides the wire, Wepent must stay clear (and round-trip the
        // untouched wepent fields from the baseline). The mirror case — change only a wepent field — is below.
        var baseline = Player(10, Vector3.Zero, 100);
        baseline.WepAlpha = 200; baseline.GunAlign = 1; // a non-default baseline so "carried from baseline" is meaningful

        var animOnly = baseline; animOnly.UpperAction = 6;
        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, animOnly);
        Assert.Equal(EntityField.AnimAction, mask & (EntityField.AnimAction | EntityField.Wepent));

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);
        Assert.Equal((byte)6, got.UpperAction);
        Assert.Equal((byte)200, got.WepAlpha);  // carried from baseline — bit was clear
        Assert.Equal((byte)1, got.GunAlign);    // carried from baseline — bit was clear

        // mirror: change only a wepent field → Wepent set, AnimAction clear.
        var wepOnly = baseline; wepOnly.WepPhase = 2;
        var w2 = new BitWriter();
        EntityField mask2 = EntityStateCodec.WriteDelta(w2, baseline, wepOnly);
        Assert.Equal(EntityField.Wepent, mask2 & (EntityField.AnimAction | EntityField.Wepent));

        var r2 = new BitReader(w2.WrittenSpan);
        NetEntityState got2 = EntityStateCodec.ReadDelta(ref r2, baseline);
        Assert.False(r2.BadRead);
        Assert.Equal((byte)2, got2.WepPhase);
        Assert.Equal((byte)0, got2.UpperAction); // carried from baseline (idle) — AnimAction bit was clear
    }

    [Fact]
    public void EntityCodec_WepAlpha_OpaqueSentinel_And_SwitchWeapon_None_RoundTrip()
    {
        // [W14a] the WepAlpha opaque sentinel (0, same convention as the body Alpha — opaque, "not networked")
        // and SwitchWeapon = -1 ("none") must survive the wire when SOMETHING else in the wepent group changed.
        // Here the gun goes from a faded baseline back to fully opaque (WepAlpha → 0) while the switch id stays -1.
        var baseline = Player(10, Vector3.Zero, 100);
        baseline.WepAlpha = 128;           // mid-fade baseline
        baseline.SwitchWeapon = -1;        // none
        baseline.SwitchingWeapon = -1;     // none

        var cur = baseline;
        cur.WepAlpha = 0;                  // restored to opaque (the sentinel value)
        // SwitchWeapon / SwitchingWeapon stay at the -1 "none" sentinel.

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, cur);
        Assert.Equal(EntityField.Wepent, mask & EntityField.Wepent); // WepAlpha changed → group rides

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);
        Assert.Equal((byte)0, got.WepAlpha);     // opaque sentinel round-trips
        Assert.Equal(-1, got.SwitchWeapon);      // -1 "none" round-trips through the signed short
        Assert.Equal(-1, got.SwitchingWeapon);
    }

    [Fact]
    public void EntityCodec_WepAlpha_HiddenSentinel_RoundTrips()
    {
        // [W14a] the 255 = QC -1 "don't render" hidden sentinel (Running Guns hides the player but keeps the gun;
        // here the gun's own alpha is the hidden marker) is DISTINCT from 0 = opaque and must survive intact.
        var baseline = Player(10, Vector3.Zero, 100);
        var cur = baseline; cur.WepAlpha = 255;

        var w = new BitWriter();
        EntityStateCodec.WriteDelta(w, baseline, cur);
        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);
        Assert.Equal((byte)255, got.WepAlpha);
    }

    [Fact]
    public void EntityCodec_RoundTrips_A_Spawn_From_Empty()
    {
        var w = new BitWriter();
        NetEntityState cur = Player(10, new Vector3(64, 0, 16), 100);
        EntityStateCodec.WriteDelta(w, NetEntityState.Empty(10), cur);

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, NetEntityState.Empty(10));
        Assert.False(r.BadRead);
        Assert.Equal(NetEntityKind.Player, got.Kind);
        Assert.Equal(7, got.ModelIndex);
        Assert.Equal(100, got.Health);
        Assert.True((got.Origin - cur.Origin).Length() < 0.5f, "origin round-trips within quantization");
    }

    [Fact]
    public void EntityCodec_Only_Encodes_Changed_Fields()
    {
        var baseline = Player(10, new Vector3(64, 0, 16), 100);
        var moved = baseline; moved.Health = 75; // only health changed

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, moved);
        Assert.Equal(EntityField.Health, mask); // exactly one field on the wire

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.Equal(75, got.Health);
        Assert.Equal(baseline.ModelIndex, got.ModelIndex); // carried from baseline
    }

    [Fact]
    public void Snapshot_RoundTrip_Carries_Unchanged_And_Applies_Changes()
    {
        var server = new ServerSnapshotHistory();
        var client = new ClientSnapshotHistory();
        var w = new BitWriter();

        // frame 1: two entities, full snapshot (no baseline yet).
        var f1 = new Dictionary<int, NetEntityState>
        {
            [10] = Player(10, new Vector3(0, 0, 0), 100),
            [20] = Player(20, new Vector3(128, 0, 0), 50),
        };
        w.Reset(); server.EncodeSnapshot(w, f1, 1);
        var r = new BitReader(w.WrittenSpan);
        var d1 = client.DecodeSnapshot(ref r);
        Assert.NotNull(d1);
        Assert.Equal(2, d1!.Count);
        Assert.Equal(1, (int)client.LastDecodedSeq);

        // client acks → server can now delta against frame 1.
        server.Ack(client.LastDecodedSeq);

        // frame 2: entity 10 moves, 20 unchanged. Only 10 should be on the wire.
        var f2 = new Dictionary<int, NetEntityState>
        {
            [10] = Player(10, new Vector3(72, 0, 0), 100),
            [20] = f1[20], // identical
        };
        w.Reset(); server.EncodeSnapshot(w, f2, 2);
        int deltaBytes = w.Length;

        var r2 = new BitReader(w.WrittenSpan);
        var d2 = client.DecodeSnapshot(ref r2);
        Assert.NotNull(d2);
        Assert.Equal(2, d2!.Count);                                   // 20 carried from the baseline
        Assert.True((d2[10].Origin - f2[10].Origin).Length() < 0.5f); // 10's new origin applied
        Assert.Equal(50, d2[20].Health);                             // 20 unchanged, carried correctly

        // A full re-send of both entities would be much larger; the delta is small (header + one entity).
        Assert.True(deltaBytes < 40, $"delta should be compact, was {deltaBytes} bytes");
    }

    [Fact]
    public void Snapshot_Removes_Entities_Gone_Since_Baseline()
    {
        var server = new ServerSnapshotHistory();
        var client = new ClientSnapshotHistory();
        var w = new BitWriter();

        var f1 = new Dictionary<int, NetEntityState> { [10] = Player(10, Vector3.Zero, 100), [20] = Player(20, Vector3.One, 50) };
        w.Reset(); server.EncodeSnapshot(w, f1, 1);
        var r1 = new BitReader(w.WrittenSpan); client.DecodeSnapshot(ref r1);
        server.Ack(client.LastDecodedSeq);

        // frame 2: entity 20 removed.
        var f2 = new Dictionary<int, NetEntityState> { [10] = Player(10, Vector3.Zero, 100) };
        w.Reset(); server.EncodeSnapshot(w, f2, 2);
        var r2 = new BitReader(w.WrittenSpan);
        var d2 = client.DecodeSnapshot(ref r2);
        Assert.NotNull(d2);
        Assert.True(d2!.ContainsKey(10));
        Assert.False(d2.ContainsKey(20)); // removed
    }

    /// <summary>
    /// (§12.8) Locks the contract the DP-faithful networking PVS cull (sv_cullentities_pvs) relies on: filtering
    /// an entity out of a client's per-tick set (it left that client's PVS) removes it via the delta, and putting
    /// it back (re-entered the PVS) RE-SPAWNS it carrying its CURRENT state — not the stale pre-cull value — since
    /// the client's baseline no longer holds it. This is why per-client filtering is safe: the existing delta
    /// history handles appear/disappear with no special casing.
    /// </summary>
    [Fact]
    public void Snapshot_Culled_Entity_Removes_Then_Respawns_With_Current_State()
    {
        var server = new ServerSnapshotHistory();
        var client = new ClientSnapshotHistory();
        var w = new BitWriter();

        var both = new Dictionary<int, NetEntityState>
        {
            [10] = Player(10, Vector3.Zero, 100),
            [20] = Player(20, new Vector3(500, 0, 0), 50),
        };
        w.Reset(); server.EncodeSnapshot(w, both, 1);
        var r1 = new BitReader(w.WrittenSpan); var d1 = client.DecodeSnapshot(ref r1);
        Assert.Equal(2, d1!.Count);
        server.Ack(client.LastDecodedSeq);

        // frame 2: entity 20 left the recipient's PVS → filtered out of THIS client's set.
        var culled = new Dictionary<int, NetEntityState> { [10] = both[10] };
        w.Reset(); server.EncodeSnapshot(w, culled, 2);
        var r2 = new BitReader(w.WrittenSpan); var d2 = client.DecodeSnapshot(ref r2);
        Assert.True(d2!.ContainsKey(10));
        Assert.False(d2.ContainsKey(20)); // culled → removed from the client's view
        server.Ack(client.LastDecodedSeq);

        // frame 3: entity 20 re-enters the PVS, having moved + taken damage while the client couldn't see it.
        var back = new Dictionary<int, NetEntityState>
        {
            [10] = both[10],
            [20] = Player(20, new Vector3(470, 0, 0), 35),
        };
        w.Reset(); server.EncodeSnapshot(w, back, 3);
        var r3 = new BitReader(w.WrittenSpan); var d3 = client.DecodeSnapshot(ref r3);
        Assert.True(d3!.ContainsKey(20));                                 // respawned
        Assert.Equal(35, d3[20].Health);                                 // CURRENT state, not the stale 50
        Assert.True((d3[20].Origin - back[20].Origin).Length() < 0.5f);  // current origin
    }

    [Fact]
    public void Snapshot_Without_Ack_Stays_A_Full_Resend()
    {
        // If the client never acks (total packet loss on the C2S path), the server keeps sending full snapshots
        // (baselineSeq 0) so a freshly-connected/desynced client can always decode without prior state.
        var server = new ServerSnapshotHistory();
        var client = new ClientSnapshotHistory();
        var w = new BitWriter();
        var f = new Dictionary<int, NetEntityState> { [10] = Player(10, Vector3.Zero, 100) };

        w.Reset(); server.EncodeSnapshot(w, f, 1);
        // no Ack(): a second client connecting cold can still decode this from scratch.
        var fresh = new ClientSnapshotHistory();
        var r = new BitReader(w.WrittenSpan);
        var d = fresh.DecodeSnapshot(ref r);
        Assert.NotNull(d);
        Assert.True(d!.ContainsKey(10));
    }

    [Fact]
    public void MoveVars_RoundTrip_And_Apply_To_Client_Cvars()
    {
        var serverCvars = new CvarService();
        serverCvars.Set("sv_maxspeed", "400");      // an XPM/overkill-style override
        serverCvars.Set("sv_gravity", "800");
        serverCvars.Set("sv_jumpvelocity", "270");

        float[] vals = MoveVarsBlock.Capture(serverCvars);
        var w = new BitWriter();
        MoveVarsBlock.Serialize(w, vals);

        var r = new BitReader(w.WrittenSpan);
        float[] got = MoveVarsBlock.Deserialize(ref r);
        Assert.Equal(vals.Length, got.Length);
        Assert.Equal(MoveVarsBlock.Hash(vals), MoveVarsBlock.Hash(got));

        var clientCvars = new CvarService();
        MoveVarsBlock.Apply(clientCvars, got);
        Assert.Equal(400f, clientCvars.GetFloat("sv_maxspeed")); // the server's physics now drives client prediction
        Assert.Equal(270f, clientCvars.GetFloat("sv_jumpvelocity"));
    }

    [Fact]
    public void MoveVars_Has49Entries_WithTheVersionTailsAppended()
    {
        // v7 (T54): the block grew 40 → 46 (T54 breadth), then 46 → 48 (the step-up velocity-limiter port
        // extension); v8: 48 → 49 (sv_gameplayfix_q2airaccelerate — the air-strafe accel limiter, replicated
        // so prediction agrees with the server on the accel step, like Base's MOVEFLAG_Q2AIRACCELERATE).
        // APPEND-only (prefix-stable Apply/FromValues across versions).
        Assert.Equal(49, MoveVarsBlock.Count);
        string[] tail =
        {
            "g_movement_highspeed", "g_movement_highspeed_q3_compat", "sv_gameplayfix_nudgeoutofsolid",
            "sv_wallclip", "sv_nostep", "sv_slick_applygravity",
            "sv_step_upspeed_scale", "sv_step_upspeed_max",
            "sv_gameplayfix_q2airaccelerate",
        };
        for (int i = 0; i < tail.Length; i++)
            Assert.Equal(tail[i], MoveVarsBlock.MovementCvars[40 + i]);
        Assert.Equal("sv_maxspeed", MoveVarsBlock.MovementCvars[0]); // prefix untouched
        Assert.Equal("sv_wallfriction", MoveVarsBlock.MovementCvars[39]);
    }

    [Fact]
    public void MoveVars_Capture_SpecialSemantics_OnAnEmptyStore()
    {
        // An UNSET store must capture the engine defaults for the unset→non-zero names, or replication would
        // silently turn the features off for remote clients (g_movement_highspeed 1, nudgeoutofsolid ON), and
        // the jumpspeedcaps must capture the NaN "disabled" sentinel rather than a real 0 cap.
        var empty = new CvarService();
        float[] vals = MoveVarsBlock.Capture(empty);
        int IndexOf(string name) => System.Array.IndexOf(MoveVarsBlock.MovementCvars, name);
        Assert.Equal(1f, vals[IndexOf("g_movement_highspeed")]);
        Assert.Equal(1f, vals[IndexOf("sv_gameplayfix_nudgeoutofsolid")]);
        // v8: q2airaccelerate must capture its ON default (QC autocvar inline 1) from an empty store — a bare
        // 0 on the wire would flip the client's air-strafe accel step to the Q1 behavior and desync prediction.
        Assert.Equal(1f, vals[IndexOf("sv_gameplayfix_q2airaccelerate")]);
        Assert.True(float.IsNaN(vals[IndexOf("sv_jumpspeedcap_min")]));
        Assert.True(float.IsNaN(vals[IndexOf("sv_jumpspeedcap_max")]));
        Assert.Equal(0f, vals[IndexOf("sv_maxspeed")]); // plain entries still read raw (Apply/FromValues default them)

        // …and explicit values win: a real 0 jumpspeedcap (xdf-style) and highspeed 2 survive capture.
        var set = new CvarService();
        set.Set("g_movement_highspeed", "2");
        set.Set("sv_jumpspeedcap_min", "0");
        set.Set("sv_jumpspeedcap_max", "0.5");
        float[] vals2 = MoveVarsBlock.Capture(set);
        Assert.Equal(2f, vals2[IndexOf("g_movement_highspeed")]);
        Assert.Equal(0f, vals2[IndexOf("sv_jumpspeedcap_min")]);
        Assert.Equal(0.5f, vals2[IndexOf("sv_jumpspeedcap_max")]);
    }

    [Fact]
    public void MoveVars_NaN_SurvivesTheWire_AndHashIsStable()
    {
        var cvars = new CvarService();
        cvars.Set("sv_jumpspeedcap_max", "nan");
        float[] vals = MoveVarsBlock.Capture(cvars);
        var w = new BitWriter();
        MoveVarsBlock.Serialize(w, vals);
        var r = new BitReader(w.WrittenSpan);
        float[] got = MoveVarsBlock.Deserialize(ref r);
        int idx = System.Array.IndexOf(MoveVarsBlock.MovementCvars, "sv_jumpspeedcap_max");
        Assert.True(float.IsNaN(got[idx]));
        Assert.Equal(MoveVarsBlock.Hash(vals), MoveVarsBlock.Hash(got)); // NaN bits hash deterministically
    }

    [Fact]
    public void MoveVars_EmptyBlock_RoundTrips_AsTheOverrideClearSentinel()
    {
        // v7: a count-0 resolved block is the "clear the per-client physics override" sentinel.
        var w = new BitWriter();
        MoveVarsBlock.Serialize(w, System.Array.Empty<float>());
        var r = new BitReader(w.WrittenSpan);
        float[] got = MoveVarsBlock.Deserialize(ref r);
        Assert.False(r.BadRead);
        Assert.Empty(got);
    }

    [Fact]
    public void SequenceWraparound_Comparison_Is_Correct()
    {
        Assert.True(ServerSnapshotHistory.IsNewer(2, 1));
        Assert.True(ServerSnapshotHistory.IsNewer(1, 65535));   // wrapped: 1 is newer than 65535
        Assert.False(ServerSnapshotHistory.IsNewer(65535, 1));
    }
}
