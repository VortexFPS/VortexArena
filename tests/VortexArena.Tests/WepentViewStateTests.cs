using System;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Guards the per-player exterior-weapon VIEW block (QC the networked <c>wepent.*</c> charge/clip/load/heat
/// fields that drive OTHER players' / spectatees' crosshair rings — the owner's own rings ride the separate
/// <see cref="OwnerWeaponRings"/> owner block, which is UNCHANGED). The new channel is
/// <see cref="EntityField.WepentView"/> (bit 20, the new tail of the entity delta) carrying a fixed
/// <see cref="WepentViewState"/> block.
///
/// <para>This is the <see cref="OwnerWeaponRingsTests"/> pattern applied to the entity-stream path: a single
/// <see cref="WepentViewState"/> Write/Read pair is the source of truth for the block layout, and the new mask
/// bit is appended AFTER the existing <see cref="EntityField.Wepent"/> block in
/// <see cref="EntityStateCodec.WriteDelta"/>/<see cref="EntityStateCodec.ReadDelta"/>, so the prior fields stay
/// byte-aligned and only the new tail is added. The desync class these lock down: a field added to Write but not
/// Read (the same regression OwnerWeaponRings warns about) would shift every block after it for a real remote
/// client.</para>
///
/// <para>Intended divergence (documented on the block): Base codes some pools at ×16; the port uses ×255
/// uniformly for all [0,1] charge/pool/heat values — the same precision class as the existing 13i-coord /
/// ×255-alpha divergences — so each of those fields round-trips to within 1/255.</para>
///
/// <para>The Phase-2 producer-fill tests (the <c>WepentResolver_*</c> facts at the bottom) drive the REAL
/// <see cref="WepentResolver.Resolve"/> off an authoritative <see cref="Player"/> holding a registered weapon,
/// so they boot the process-global weapon registries + <see cref="Api.Services"/> and therefore run in the
/// <c>GlobalState</c> collection. The codec / <c>Diff</c> facts above touch no globals.</para>
/// </summary>
[Collection("GlobalState")]
public class WepentViewStateTests : IDisposable
{
    public void Dispose() => MutatorActivation.DeactivateAll();

    /// <summary>1/255 quantization tolerance for the ×255→byte→/255 [0,1] charge/pool/heat fields.</summary>
    private const float ChargeTol = 1f / 255f;

    /// <summary>A fully-populated sample exercising every field with a distinct, non-default value.</summary>
    private static WepentViewState Sample => new()
    {
        VortexCharge = 0.25f,
        OknexCharge = 0.50f,
        VortexChargePool = 0.75f,
        OknexChargePool = 0.125f,
        ClipLoad = 7,
        ClipSize = 12,
        HagarLoad = 3,
        MinelayerMines = 2,
        ArcHeat = 0.8f,
        ViewmodelFrame = 4,
        BeamState = 0b101, // arc beam active + electro beam active
    };

    // ---- (1) Write -> Read round-trips every field within quantization tolerance --------------------------

    [Fact]
    public void Write_Read_RoundTrips_EveryField_WithinQuantizationTolerance()
    {
        var w = new BitWriter();
        WepentViewCodec.Write(w, Sample);

        var r = new BitReader(w.WrittenSpan);
        WepentViewState got = WepentViewCodec.Read(ref r);

        Assert.False(r.BadRead);
        // [0,1] charge/pool/heat: ×255 → byte → /255, so within 1/255.
        Assert.InRange(got.VortexCharge, 0.25f - ChargeTol, 0.25f + ChargeTol);
        Assert.InRange(got.OknexCharge, 0.50f - ChargeTol, 0.50f + ChargeTol);
        Assert.InRange(got.VortexChargePool, 0.75f - ChargeTol, 0.75f + ChargeTol);
        Assert.InRange(got.OknexChargePool, 0.125f - ChargeTol, 0.125f + ChargeTol);
        Assert.InRange(got.ArcHeat, 0.8f - ChargeTol, 0.8f + ChargeTol);
        // shorts / bytes: exact within their integer range.
        Assert.Equal(7, got.ClipLoad);
        Assert.Equal(12, got.ClipSize);
        Assert.Equal(3, got.HagarLoad);
        Assert.Equal(2, got.MinelayerMines);
        Assert.Equal(4, got.ViewmodelFrame);
        Assert.Equal(0b101, got.BeamState);
    }

    [Fact]
    public void Write_Read_PreservesTheClipLoadNeedsReloadSentinel()
    {
        // Base clip_load is SIGNED: -1 = "needs reload" — it must survive the signed-short coding intact, distinct
        // from ClipSize 0 = "weapon has no clip → ring hidden".
        var s = Sample;
        s.ClipLoad = -1;   // needs-reload sentinel
        s.ClipSize = 0;    // no-clip weapon

        var w = new BitWriter();
        WepentViewCodec.Write(w, s);
        var r = new BitReader(w.WrittenSpan);
        WepentViewState got = WepentViewCodec.Read(ref r);

        Assert.False(r.BadRead);
        Assert.Equal(-1, got.ClipLoad);
        Assert.Equal(0, got.ClipSize);
    }

    /// <summary>The desync guard (the OwnerWeaponRings sentinel pattern): write a sentinel, the block, then a
    /// second sentinel, and confirm the reader lands EXACTLY on the trailing sentinel after the block — i.e. Read
    /// consumes precisely the 13 bytes Write produced. Misaligned by even one field and the trailing sentinel is
    /// garbage, exactly as the live entity stream would desync.</summary>
    [Fact]
    public void Read_ConsumesExactlyWhatWriteProduced_KeepingTheStreamAligned()
    {
        var w = new BitWriter();
        w.WriteUShort(0xBEEF);
        int before = w.Length;
        WepentViewCodec.Write(w, Sample);
        int blockBytes = w.Length - before;
        w.WriteUShort(0xD00D);

        var r = new BitReader(w.WrittenSpan);
        Assert.Equal(0xBEEF, r.ReadUShort());
        WepentViewState got = WepentViewCodec.Read(ref r);
        Assert.Equal(0xD00D, r.ReadUShort()); // misaligned by even one field and this is garbage
        Assert.False(r.BadRead);

        Assert.Equal(13, blockBytes); // 4 bytes + 2 shorts (4) + 5 bytes = 13 bytes when present
        Assert.Equal(7, got.ClipLoad);
        // The Phase-2 trailing two bytes (ViewmodelFrame, BeamState) are the LAST fields of the block, so they
        // are the most sensitive to a Write/Read misalignment: if either were dropped, the 0xD00D sentinel above
        // would land short and read garbage. NON-ZERO values must survive at the exact tail (Sample stamps
        // ViewmodelFrame=4 / BeamState=0b101) — a producer that now fills them can't silently desync the stream.
        Assert.Equal(4, got.ViewmodelFrame);
        Assert.Equal(0b101, got.BeamState);
    }

    /// <summary>The 13-byte alignment guard, re-run with the Phase-2 ViewmodelFrame/BeamState bytes carrying the
    /// MAXIMUM non-zero values (255 / 255). These trailing bytes were always present but produced 0 before the
    /// resolver started filling them; this proves the codec already round-trips arbitrary non-zero tail bytes at
    /// the byte-precision they are coded — i.e. the producer change is value-only, NOT a layout change.</summary>
    [Fact]
    public void Read_PreservesNonZeroTrailingViewmodelFrameAndBeamState()
    {
        var s = Sample;
        s.ViewmodelFrame = 255; // full byte range
        s.BeamState = 0b111;    // all three beam bits set

        var w = new BitWriter();
        w.WriteUShort(0xBEEF);
        int before = w.Length;
        WepentViewCodec.Write(w, s);
        int blockBytes = w.Length - before;
        w.WriteUShort(0xD00D);

        var r = new BitReader(w.WrittenSpan);
        Assert.Equal(0xBEEF, r.ReadUShort());
        WepentViewState got = WepentViewCodec.Read(ref r);
        Assert.Equal(0xD00D, r.ReadUShort()); // tail aligned: the two new bytes consumed exactly
        Assert.False(r.BadRead);

        Assert.Equal(13, blockBytes); // unchanged — still 13 bytes with the trailing two non-zero
        Assert.Equal(255, got.ViewmodelFrame);
        Assert.Equal(0b111, got.BeamState);
    }

    // ---- (2) NetEntityState.Diff sets WepentView iff WepentView changed -----------------------------------

    private static NetEntityState Player(int id) => new()
    {
        EntNum = id, Kind = NetEntityKind.Player, ModelIndex = 7, Origin = Vector3.Zero, Health = 100,
        // match the producer + the Empty baseline so the wepent group bits stay clear for a plain player.
        SwitchWeapon = -1, SwitchingWeapon = -1,
    };

    [Fact]
    public void Diff_WepentView_Bit_Set_Iff_WepentView_Changed()
    {
        var baseline = Player(10);

        // two identical states → bit clear (the "idle costs one zero mask" contract).
        Assert.Equal(EntityField.None, NetEntityState.Diff(baseline, baseline) & EntityField.WepentView);

        // any change to the WepentView block → bit set.
        var cur = baseline;
        cur.WepentView = Sample;
        Assert.Equal(EntityField.WepentView, NetEntityState.Diff(baseline, cur) & EntityField.WepentView);
    }

    [Fact]
    public void Diff_WepentView_Tracks_EachField_AndNothingOutsideTheBlock()
    {
        var baseline = Player(10);
        baseline.WepentView = Sample; // a non-default baseline so each per-field flip is meaningful

        void AssertField(System.Func<WepentViewState, WepentViewState> mutate)
        {
            var cur = baseline;
            cur.WepentView = mutate(baseline.WepentView);
            Assert.Equal(EntityField.WepentView, NetEntityState.Diff(baseline, cur) & EntityField.WepentView);
        }

        AssertField(v => { v.VortexCharge = 0.9f; return v; });
        AssertField(v => { v.OknexCharge = 0.1f; return v; });
        AssertField(v => { v.VortexChargePool = 0.0f; return v; });
        AssertField(v => { v.OknexChargePool = 0.6f; return v; });
        AssertField(v => { v.ClipLoad = 1; return v; });
        AssertField(v => { v.ClipSize = 30; return v; });
        AssertField(v => { v.HagarLoad = 1; return v; });
        AssertField(v => { v.MinelayerMines = 1; return v; });
        AssertField(v => { v.ArcHeat = 0.0f; return v; });
        AssertField(v => { v.ViewmodelFrame = 9; return v; });
        AssertField(v => { v.BeamState = 0b010; return v; });

        // a non-wepentview change (the older Wepent group) must NOT spuriously set the WepentView bit.
        var wep = baseline; wep.WepPhase = 2;
        Assert.Equal(EntityField.None, NetEntityState.Diff(baseline, wep) & EntityField.WepentView);
    }

    [Fact]
    public void Diff_PlainPlayer_LeavesWepentView_Clear()
    {
        // an all-default player (no charge, WepentView at its default) must NOT set the new bit — it costs nothing
        // on the wire until a producer sets the block.
        EntityField m = NetEntityState.Diff(NetEntityState.Empty(10), Player(10));
        Assert.Equal(EntityField.None, m & EntityField.WepentView);
    }

    // ---- (3) Codec carries the block only when the bit is set, prior fields stay aligned -------------------

    [Fact]
    public void EntityCodec_RoundTrips_WepentView_WhenSet()
    {
        var baseline = Player(10);
        var cur = baseline;
        cur.WepentView = Sample;

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, cur);
        Assert.Equal(EntityField.WepentView, mask & EntityField.WepentView);

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);

        Assert.InRange(got.WepentView.VortexCharge, 0.25f - ChargeTol, 0.25f + ChargeTol);
        Assert.InRange(got.WepentView.ArcHeat, 0.8f - ChargeTol, 0.8f + ChargeTol);
        Assert.Equal(7, got.WepentView.ClipLoad);
        Assert.Equal(12, got.WepentView.ClipSize);
        Assert.Equal(2, got.WepentView.MinelayerMines);
        Assert.Equal(0b101, got.WepentView.BeamState);
    }

    [Fact]
    public void EntityCodec_WepentView_AbsentWhenUnchanged_CarriesBaselineBlock()
    {
        // change ONLY a body field (Health): WepentView must stay clear and the block carries from the baseline.
        var baseline = Player(10);
        baseline.WepentView = Sample; // a non-default baseline so "carried from baseline" is meaningful

        var moved = baseline; moved.Health = 75;
        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, moved);
        Assert.Equal(EntityField.None, mask & EntityField.WepentView);

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);
        Assert.Equal(75, got.Health);
        Assert.Equal(7, got.WepentView.ClipLoad);   // carried from baseline — bit was clear
        Assert.Equal(12, got.WepentView.ClipSize);
    }

    /// <summary>
    /// The alignment guard the OwnerWeaponRings desync class warns about: changing ONLY WepentView must leave the
    /// PRIOR delta fields intact. Here a non-default baseline carries an AnimAction overlay and a Wepent block; the
    /// current state flips only the WepentView block. Both prior groups must round-trip unchanged from the baseline
    /// (their bits clear), and the new WepentView tail must decode — proving the new bit is appended AFTER the
    /// existing Wepent block in identical order on both halves.
    /// </summary>
    [Fact]
    public void EntityCodec_WepentViewOnlyChange_LeavesPriorWepentAndAnimActionFieldsIntact()
    {
        var baseline = Player(10);
        // prior groups at non-default baseline values (carried, since only WepentView changes):
        baseline.UpperAction = 3;            // AnimAction group
        baseline.AnimActionTime = 12.5f;
        baseline.WepPhase = 2;               // Wepent group
        baseline.ViewmodelSkin = 1;
        baseline.WepAlpha = 128;
        baseline.GunAlign = 3;
        baseline.SwitchWeapon = 5;
        baseline.SwitchingWeapon = 4;
        baseline.WepentView = Sample;

        var cur = baseline;
        var view = Sample;
        view.VortexCharge = 0.6f;   // the ONLY change is inside the WepentView block
        cur.WepentView = view;

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, cur);
        // exactly the WepentView bit, none of the prior groups.
        Assert.Equal(EntityField.WepentView, mask);

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);

        // prior fields stay aligned — carried verbatim from the baseline.
        Assert.Equal((byte)3, got.UpperAction);
        Assert.Equal(12.5f, got.AnimActionTime);
        Assert.Equal((byte)2, got.WepPhase);
        Assert.Equal((byte)1, got.ViewmodelSkin);
        Assert.Equal((byte)128, got.WepAlpha);
        Assert.Equal((byte)3, got.GunAlign);
        Assert.Equal(5, got.SwitchWeapon);
        Assert.Equal(4, got.SwitchingWeapon);

        // the new tail decoded.
        Assert.InRange(got.WepentView.VortexCharge, 0.6f - ChargeTol, 0.6f + ChargeTol);
    }

    [Fact]
    public void EntityCodec_TwoIdenticalStates_CostOneZeroMask()
    {
        // the "idle entity costs one zero mask" contract: identical states → empty mask → just the 32-bit mask.
        var s = Player(10);
        s.WepentView = Sample;

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, s, s);
        Assert.Equal(EntityField.None, mask);
        Assert.Equal(4, w.Length); // the 32-bit zero mask, nothing else
    }

    // ---- (4) WepentResolver.Resolve returns None for a dead / observer player -----------------------------

    [Fact]
    public void WepentResolver_DeadPlayer_ResolvesToNone()
    {
        var p = new Player { DeadState = DeadFlag.Dead };
        WepentViewState got = WepentResolver.Resolve(p, _ => 0, now: 10f);
        AssertIsNone(got);
    }

    [Fact]
    public void WepentResolver_ObserverPlayer_ResolvesToNone()
    {
        var p = new Player { IsObserver = true };
        WepentViewState got = WepentResolver.Resolve(p, _ => 0, now: 10f);
        AssertIsNone(got);
    }

    /// <summary>The None block: all charge/clip/load/heat fields default (no rings drawn for this player).</summary>
    private static void AssertIsNone(in WepentViewState got)
    {
        WepentViewState none = WepentViewState.None;
        Assert.True(none.Equals(got),
            "a dead/observer player must resolve to WepentViewState.None (no rings for this player)");
    }

    // ---- (5) Producer fill: WepentResolver stamps the two Phase-2 trailing fields ------------------------
    //
    // These drive the REAL resolver off an authoritative Player holding a registered weapon (so the producer's
    // WeaponFireState -> ViewmodelFrame map and BeamBursting -> BeamState bit1 actually run), then round-trip the
    // produced block through the codec to confirm the non-zero trailing bytes survive end-to-end. Together with
    // the alignment facts above this is the full producer→wire→reader path for the two new bytes.

    /// <summary>Boot the process-global weapon registries + Api.Services so a Player can hold a REGISTERED weapon
    /// and <see cref="Inventory.CurrentWeapon"/> resolves it (the resolver's producer branches key off
    /// <c>active.NetName</c>). Mirrors the HlacCrouchSpreadTests boot.</summary>
    private static EngineServices Boot()
    {
        var facade = new EngineServices(new CollisionWorld());
        Api.Services = facade;
        VehicleCommon.GameStopped = false;
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        GameRegistries.Bootstrap();
        Combat.System = new DamageSystem();
        MutatorActivation.Apply();
        return facade;
    }

    /// <summary>A live, alive, non-observer player holding the named registered weapon, with the slot 0 fire
    /// state under the caller's control. Returns the player and its slot-0 scratch for direct stamping.</summary>
    private static (Player p, WeaponSlotState st) ArmedPlayer(EngineServices f, string netName)
    {
        Weapon? w = Weapons.ByName(netName);
        Assert.NotNull(w); // the requested weapon must be registered by Bootstrap

        var p = new Player();
        p.ActiveWeaponId = w!.RegistryId;     // Inventory.CurrentWeapon(p) now resolves to `w`
        WeaponSlotState st = p.WeaponState(new WeaponSlot(0));
        return (p, st);
    }

    [Theory]
    [InlineData(WeaponFireState.Ready, 0f, 0)]  // idle (READY) -> frame 0
    [InlineData(WeaponFireState.Clear, 0f, 0)]  // empty hands -> frame 0
    [InlineData(WeaponFireState.Raise, 0f, 3)]  // WS_RAISE -> raise frame 3
    [InlineData(WeaponFireState.Drop, 0f, 4)]   // WS_DROP -> drop frame 4
    [InlineData(WeaponFireState.InUse, 20f, 1)] // WS_INUSE + refire gate still closed (AttackFinished > now) -> fire 1
    [InlineData(WeaponFireState.InUse, 5f, 0)]  // WS_INUSE but refire gate already open (AttackFinished <= now) -> idle 0
    public void WepentResolver_StampsViewmodelFrame_FromWeaponFireState(WeaponFireState state, float attackFinished, int expectedFrame)
    {
        // hagar holds a clip; ClipSize stays 0 here so the reload-priority branch (ClipSize>0 && ClipLoad<0) is
        // NOT taken — this isolates the fire-state machine selector.
        var (p, st) = ArmedPlayer(Boot(), "hagar");
        st.ClipSize = 0;
        st.ClipLoad = 0;
        st.State = state;
        st.AttackFinished = attackFinished;

        WepentViewState got = WepentResolver.Resolve(p, _ => 0, now: 10f);
        Assert.Equal(expectedFrame, got.ViewmodelFrame);
    }

    [Fact]
    public void WepentResolver_StampsViewmodelFrame_ReloadSentinelTakesPriority()
    {
        // The QC WFRAME_RELOAD sentinel: a slot with a clip whose load went negative (-1 "needs reload" / mid-
        // reload). The reload frame (2) must win even while the fire-state machine says WS_INUSE+firing.
        var (p, st) = ArmedPlayer(Boot(), "hagar");
        st.ClipSize = 12;     // has a clip
        st.ClipLoad = -1;     // the reload sentinel
        st.State = WeaponFireState.InUse;
        st.AttackFinished = 20f; // would otherwise select fire (1)

        WepentViewState got = WepentResolver.Resolve(p, _ => 0, now: 10f);
        Assert.Equal(2, got.ViewmodelFrame); // WFRAME_RELOAD takes priority over fire
    }

    [Theory]
    [InlineData(true, 0b11)]   // bursting -> bit0 (active, BeamHeat>0) | bit1 (burst)
    [InlineData(false, 0b01)]  // not bursting -> bit0 only
    public void WepentResolver_StampsBeamState_Bit1_FromBeamBursting(bool bursting, int expectedBeamState)
    {
        var (p, st) = ArmedPlayer(Boot(), "arc");
        st.BeamHeat = 1f;          // bit0: arc beam active
        st.BeamBursting = bursting; // bit1: arc beam burst (the NEW Phase-2 bit)

        WepentViewState got = WepentResolver.Resolve(p, _ => 0, now: 10f);
        Assert.Equal(expectedBeamState, got.BeamState);
    }

    [Fact]
    public void WepentResolver_BeamState_NonArcWeapon_StaysZero()
    {
        // bit1 is arc-only: a non-arc weapon with BeamBursting set (impossible in practice, but it must not leak
        // a burst bit) produces BeamState 0.
        var (p, st) = ArmedPlayer(Boot(), "hagar");
        st.BeamHeat = 1f;
        st.BeamBursting = true;

        WepentViewState got = WepentResolver.Resolve(p, _ => 0, now: 10f);
        Assert.Equal(0, got.BeamState);
    }

    [Fact]
    public void WepentResolver_ProducedNonZeroTrailingBytes_RoundTripThroughCodec()
    {
        // End-to-end: the resolver fills non-zero ViewmodelFrame + BeamState (arc, bursting, mid-raise), and the
        // 13-byte codec preserves both at the exact tail — producer → wire → reader for the two new bytes.
        var (p, st) = ArmedPlayer(Boot(), "arc");
        st.BeamHeat = 1f;
        st.BeamBursting = true;          // BeamState -> 0b11
        st.State = WeaponFireState.Raise; // ViewmodelFrame -> 3
        st.ClipSize = 0;
        st.ClipLoad = 0;

        WepentViewState produced = WepentResolver.Resolve(p, _ => 0, now: 10f);
        Assert.Equal(3, produced.ViewmodelFrame);
        Assert.Equal(0b11, produced.BeamState);

        var w = new BitWriter();
        WepentViewCodec.Write(w, produced);
        var r = new BitReader(w.WrittenSpan);
        WepentViewState got = WepentViewCodec.Read(ref r);

        Assert.False(r.BadRead);
        Assert.Equal(13, w.Length);            // still 13 bytes — value-only, not a layout change
        Assert.Equal(3, got.ViewmodelFrame);   // non-zero frame survived the round-trip
        Assert.Equal(0b11, got.BeamState);     // non-zero beam bitfield survived the round-trip
    }
}
