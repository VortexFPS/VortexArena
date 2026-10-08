using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VortexArena.Legacy.Protocol;
using VortexArena.Legacy.Server;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The server's entity encoders (sv_ents5.c, sv_ents_csqc.c), checked by reading what they write with
/// the client's own decoder (<see cref="DpServerMessageParser"/>, <see cref="DpEntityTable"/>).
/// </summary>
public class ServerEntityFrameTests
{
    // ------------------------------------------------------------------ fixtures

    private sealed class Host5 : ISvEntityFrame5Host
    {
        public int MaxClients { get; set; } = 4;
        public int MaxEdicts { get; set; } = 1024;
        public double Time { get; set; } = 10;
        public HashSet<int> SendEntity { get; } = new();
        public double Duration { get; set; }
        public bool HasSendEntity(int entityNumber) => SendEntity.Contains(entityNumber);
        public double FrameDuration(int modelIndex, int frame) => Duration;
    }

    /// <summary>The receiving end: the real parser, with a handler that understands the test's CSQC payload.</summary>
    private sealed class Client : IDpClientHandler
    {
        public Client() => Parser = new DpServerMessageParser(this);
        public DpServerMessageParser Parser { get; }
        public DpEntityTable Table => Parser.Entities;
        public List<DpEntityFrame> Frames { get; } = new();
        public Dictionary<int, int> Stats { get; } = new();
        public List<(int entity, int sendFlags, int value)> CsqcUpdates { get; } = new();
        public List<int> CsqcRemoves { get; } = new();

        public void OnEntityFrame(in DpEntityFrame frame, DpEntityTable entities) => Frames.Add(frame);
        public void OnUpdateStat(int index, int value) => Stats[index] = value;

        public DpPayloadResult OnCsqcEntityUpdate(int entity, DpMessageReader reader)
        {
            int flags = reader.ReadLong();
            int value = reader.ReadShort();
            int padding = reader.ReadByte();
            reader.ReadSpan(padding);
            CsqcUpdates.Add((entity, flags, value));
            return DpPayloadResult.Consumed;
        }

        public void OnCsqcEntityRemove(int entity) => CsqcRemoves.Add(entity);

        public DpEntityFrame Receive(DpMessageWriter msg)
        {
            int before = Frames.Count;
            Deliver(msg);
            Assert.Equal(before + 1, Frames.Count);
            return Frames[^1];
        }

        public void Deliver(DpMessageWriter msg)
        {
            Assert.False(msg.Overflowed);
            DpParseResult result = Parser.Parse(msg.ToArray());
            Assert.True(result.Status == DpParseStatus.Complete, result.ToString());
        }
    }

    private static SvEntityState Ent(int number, Vector3 origin = default, int model = 1)
    {
        var s = new SvEntityState { Net = EntityState.Default, NetCenter = origin };
        s.Net.Number = (ushort)number;
        s.Net.Active = DpProtocol.ActiveNetwork;
        s.Net.Origin = origin;
        s.Net.ModelIndex = (ushort)model;
        return s;
    }

    private static DpMessageWriter Write(SvEntityFrame5Database db, ReadOnlySpan<SvEntityState> states,
        int maxSize = 1400, int view = 1, uint move = 0, bool needEmpty = true, bool expect = true)
    {
        var msg = new DpMessageWriter();
        Assert.Equal(expect, db.WriteFrame(msg, maxSize, states, view, move, needEmpty));
        return msg;
    }

    private static void AssertSameNet(in EntityState e, in EntityState a)
    {
        Assert.Equal(e.Active, a.Active);
        Assert.Equal(e.Number, a.Number);
        Assert.Equal(e.Origin, a.Origin);
        Assert.Equal(e.Angles, a.Angles);
        Assert.Equal(e.Effects, a.Effects);
        Assert.Equal(e.ModelIndex, a.ModelIndex);
        Assert.Equal(e.Frame, a.Frame);
        Assert.Equal(e.TagEntity, a.TagEntity);
        Assert.Equal(e.TagIndex, a.TagIndex);
        Assert.Equal(e.TrailEffectNum, a.TrailEffectNum);
        Assert.Equal((e.Light0, e.Light1, e.Light2, e.Light3), (a.Light0, a.Light1, a.Light2, a.Light3));
        Assert.Equal((e.LightStyle, e.LightPFlags), (a.LightStyle, a.LightPFlags));
        Assert.Equal(e.Colormap, a.Colormap);
        Assert.Equal(e.Skin, a.Skin);
        Assert.Equal(e.Alpha, a.Alpha);
        Assert.Equal(e.Scale, a.Scale);
        Assert.Equal((e.GlowSize, e.GlowColor), (a.GlowSize, a.GlowColor));
        Assert.Equal(e.Flags, a.Flags);
        Assert.Equal((e.ColorMod0, e.ColorMod1, e.ColorMod2), (a.ColorMod0, a.ColorMod1, a.ColorMod2));
        Assert.Equal((e.GlowMod0, e.GlowMod1, e.GlowMod2), (a.GlowMod0, a.GlowMod1, a.GlowMod2));
    }

    private delegate void Mutate(ref SvEntityState s);

    /// <summary>
    /// Sends entity 20 (not a player), then the same entity changed by <paramref name="change"/>, and
    /// checks the second frame names only that entity, decodes to the changed state and has exactly
    /// <paramref name="flagBytes"/> flag bytes and <paramref name="payloadBytes"/> of fields.
    /// </summary>
    private static EntityState Delta(Mutate change, int flagBytes, int payloadBytes, Mutate? setup = null, Host5? host = null)
    {
        host ??= new Host5();
        var db = new SvEntityFrame5Database(host);
        var client = new Client();
        SvEntityState s = Ent(20, new Vector3(8, 16, 24));
        setup?.Invoke(ref s);
        DpEntityFrame first = client.Receive(Write(db, new[] { s }));
        Assert.Equal(new[] { 20 }, first.Changed);
        AssertSameNet(s.Net, client.Table.Current(20));

        change(ref s);
        DpMessageWriter msg = Write(db, new[] { s });
        // svc + framenum + movesequence, the record (number, flags, fields), the terminator
        Assert.Equal(9 + 2 + flagBytes + payloadBytes + 2, msg.Length);
        DpEntityFrame second = client.Receive(msg);
        Assert.Equal(new[] { 20 }, second.Changed);
        EntityState got = client.Table.Current(20);
        AssertSameNet(s.Net, got);
        Assert.Equal(0u, db.PendingBits(20));
        return got;
    }

    // ------------------------------------------------------------------ svc_entities

    [Fact]
    public void New_Entity_Arrives_Whole_With_The_Dp7_Header()
    {
        var db = new SvEntityFrame5Database(new Host5());
        var client = new Client();
        SvEntityState s = Ent(20, new Vector3(100.5f, -200.25f, 30), model: 7);
        s.Net.Frame = 3;
        s.Net.Skin = 2;
        s.Net.Angles = new Vector3(0, 90, 0);

        DpMessageWriter msg = Write(db, new[] { s }, move: 0xFEDCBA98);
        Assert.Equal((byte)Svc.Entities, msg.WrittenSpan[0]);
        DpEntityFrame frame = client.Receive(msg);
        Assert.Equal(1, frame.FrameNumber);
        Assert.Equal(unchecked((int)0xFEDCBA98), frame.ServerMoveSequence);
        Assert.Equal(new[] { 20 }, frame.Changed);
        Assert.Equal(1, db.LatestFrameNumber);
        AssertSameNet(s.Net, client.Table.Current(20));

        // nothing changed: no frame unless one is asked for, and then an empty one with the next number
        Write(db, new[] { s }, needEmpty: false, expect: false);
        Assert.Equal(1, db.LatestFrameNumber);
        msg = Write(db, new[] { s }, move: 5);
        Assert.Equal(11, msg.Length);
        frame = client.Receive(msg);
        Assert.Equal((2, 5), (frame.FrameNumber, frame.ServerMoveSequence));
        Assert.Empty(frame.Changed);
    }

    [Fact]
    public void A_Full_Update_Sends_Only_What_Differs_From_The_Default_State()
    {
        var db = new SvEntityFrame5Database(new Host5());
        SvEntityState s = Ent(20, Vector3.Zero, model: 0);
        // number, one flag byte (E5_FULLUPDATE alone), nothing else
        Assert.Equal(9 + 2 + 1 + 2, Write(db, new[] { s }).Length);
    }

    [Fact]
    public void Origin_Uses_Shorts_Only_For_Low_Precision_Entities_Inside_The_Box()
    {
        Mutate low = (ref SvEntityState s) => s.Net.Flags = SvEntityFrame5Database.RenderLowPrecision;
        Delta((ref SvEntityState s) => s.Net.Origin = new Vector3(-100.125f, 4095.875f, 0.5f), 1, 6, low);
        // outside what a short of eighths can hold
        Delta((ref SvEntityState s) => s.Net.Origin = new Vector3(0, 4095.9375f, 0), 2, 12, low);
        Delta((ref SvEntityState s) => s.Net.Origin = new Vector3(-4096.0625f, 0, 0), 2, 12, low);
        // not low precision: floats, whatever the value
        Delta((ref SvEntityState s) => s.Net.Origin = new Vector3(1.03125f, 2, 3), 2, 12);
        // attached, view model and exterior model entities are never low precision
        Delta((ref SvEntityState s) => s.Net.Origin = new Vector3(1, 2, 3), 2, 12,
            (ref SvEntityState s) => { low(ref s); s.ViewModelForClient = 1; });
        Delta((ref SvEntityState s) => s.Net.Origin = new Vector3(1, 2, 3), 2, 12,
            (ref SvEntityState s) => { low(ref s); s.ExteriorModelForClient = 1; });
        Delta((ref SvEntityState s) => s.Net.Origin = new Vector3(1, 2, 3), 2, 12,
            (ref SvEntityState s) => { low(ref s); s.Net.TagEntity = 3; });
        // and neither is a player
        Delta((ref SvEntityState s) => s.Net.Origin = new Vector3(1, 2, 3), 2, 12, low, new Host5 { MaxClients = 32 });
    }

    [Fact]
    public void Angles_Use_Bytes_For_Low_Precision_And_Shorts_Otherwise()
    {
        Delta((ref SvEntityState s) => s.Net.Angles = new Vector3(90, 45, -22.5f), 2, 6);
        Delta((ref SvEntityState s) => s.Net.Angles = new Vector3(90, 45, -90), 1, 3,
            (ref SvEntityState s) => s.Net.Flags = SvEntityFrame5Database.RenderLowPrecision);
    }

    [Fact]
    public void Model_Frame_And_Skin()
    {
        Delta((ref SvEntityState s) => s.Net.ModelIndex = 255, 1, 1);
        Delta((ref SvEntityState s) => s.Net.ModelIndex = 256, 2, 2);
        Delta((ref SvEntityState s) => s.Net.ModelIndex = 8191, 2, 2);
        Delta((ref SvEntityState s) => s.Net.Frame = 255, 1, 1);
        Delta((ref SvEntityState s) => s.Net.Frame = 256, 3, 2);
        Delta((ref SvEntityState s) => s.Net.Frame = 30000, 3, 2);
        Delta((ref SvEntityState s) => s.Net.Skin = 200, 1, 1);
    }

    [Fact]
    public void Effects_Take_One_Two_Or_Four_Bytes()
    {
        Delta((ref SvEntityState s) => s.Net.Effects = 0xFF, 1, 1);
        Delta((ref SvEntityState s) => s.Net.Effects = 0x100, 3, 2);
        Delta((ref SvEntityState s) => s.Net.Effects = 0xFFFF, 3, 2);
        Delta((ref SvEntityState s) => s.Net.Effects = 0x10000, 3, 4);
        Delta((ref SvEntityState s) => s.Net.Effects = unchecked((int)0x80000001), 3, 4);
        // the width follows the new value, not the old one
        Delta((ref SvEntityState s) => s.Net.Effects = 0, 1, 1, (ref SvEntityState s) => s.Net.Effects = 0x12345678);
    }

    [Fact]
    public void Alpha_Scale_Flags_And_Colormap()
    {
        Delta((ref SvEntityState s) => s.Net.Alpha = 128, 2, 1);
        Delta((ref SvEntityState s) => s.Net.Scale = 32, 2, 1);
        Delta((ref SvEntityState s) => { s.Net.Alpha = 1; s.Net.Scale = 255; }, 2, 2);
        Delta((ref SvEntityState s) => s.Net.Flags = 8 | 64, 2, 1);
        Delta((ref SvEntityState s) => s.Net.Colormap = 3, 2, 1);
    }

    [Fact]
    public void Attachment_Light_And_Glow()
    {
        Delta((ref SvEntityState s) => { s.Net.TagEntity = 300; s.Net.TagIndex = 7; }, 3, 3);
        Delta((ref SvEntityState s) => s.Net.TagIndex = 9, 3, 3, (ref SvEntityState s) => s.Net.TagEntity = 5);
        Delta((ref SvEntityState s) =>
        {
            s.Net.Light0 = 65535; s.Net.Light1 = 256; s.Net.Light2 = 1; s.Net.Light3 = 400;
            s.Net.LightStyle = 12; s.Net.LightPFlags = 3;
        }, 3, 10);
        Delta((ref SvEntityState s) => s.Net.LightPFlags = 1, 3, 10);
        Delta((ref SvEntityState s) => { s.Net.GlowSize = 40; s.Net.GlowColor = 111; }, 3, 2);
    }

    [Fact]
    public void Colormod_Glowmod_And_Trail_Effect()
    {
        Delta((ref SvEntityState s) => { s.Net.ColorMod0 = 1; s.Net.ColorMod1 = 64; s.Net.ColorMod2 = 255; }, 3, 3);
        Delta((ref SvEntityState s) => { s.Net.GlowMod0 = 0; s.Net.GlowMod1 = 16; s.Net.GlowMod2 = 200; }, 4, 3);
        Delta((ref SvEntityState s) => s.Net.TrailEffectNum = 4000, 4, 2);
    }

    [Fact]
    public void Flag_Bytes_Are_Chained_By_The_Extend_Bits()
    {
        // one byte: bits 0-6; two: up to 14; three: up to 22; four: beyond
        Delta((ref SvEntityState s) => s.Net.Skin = 1, 1, 1);
        Delta((ref SvEntityState s) => s.Net.Alpha = 1, 2, 1);
        Delta((ref SvEntityState s) => s.Net.GlowSize = 1, 3, 2);
        Delta((ref SvEntityState s) => s.Net.GlowMod0 = 1, 4, 3);
        // every group at once
        EntityState got = Delta((ref SvEntityState s) =>
        {
            s.Net.Origin = new Vector3(1, 2, 3);
            s.Net.Angles = new Vector3(45, 90, -45);
            s.Net.ModelIndex = 300; s.Net.Frame = 400; s.Net.Skin = 5; s.Net.Effects = 0x10000;
            s.Net.Flags = 64; s.Net.Alpha = 10; s.Net.Scale = 20; s.Net.Colormap = 30;
            s.Net.TagEntity = 2; s.Net.TagIndex = 1;
            s.Net.Light3 = 100; s.Net.GlowSize = 1; s.Net.ColorMod0 = 0; s.Net.GlowMod0 = 0;
            s.Net.TrailEffectNum = 9;
        }, 4, 1 + 12 + 6 + 2 + 2 + 1 + 4 + 1 + 1 + 1 + 3 + 10 + 2 + 3 + 3 + 2);
        Assert.Equal(400, got.Frame);
    }

    [Fact]
    public void Complex_Animation_Sends_The_Blended_Frame_Groups()
    {
        Mutate complex = (ref SvEntityState s) => s.Net.Flags = SvEntityFrame5Database.RenderComplexAnimation;
        static float Wire(float lerp) => (byte)(int)(lerp * 255.0f) * (1.0f / 255.0f);

        // one animation: type, frame, age
        EntityState got = Delta((ref SvEntityState s) =>
        {
            s.Net.Blend0 = new DpFrameGroupBlend { Frame = 12, Lerp = 1 };
            s.BlendStart0 = 9.5; // host time is 10
        }, 4, 1 + 2 + 2, complex);
        Assert.Equal((12, 1f, 0.5f), (got.Blend0.Frame, got.Blend0.Lerp, got.Blend0.StartAge));

        // four animations: type, 4 frames, 4 ages, 4 weights
        got = Delta((ref SvEntityState s) =>
        {
            s.Net.Blend0 = new DpFrameGroupBlend { Frame = 1, Lerp = 0.4f };
            s.Net.Blend1 = new DpFrameGroupBlend { Frame = 2, Lerp = 0.3f };
            s.Net.Blend2 = new DpFrameGroupBlend { Frame = 3, Lerp = 0.2f };
            s.Net.Blend3 = new DpFrameGroupBlend { Frame = 4, Lerp = 0.1f };
            s.BlendStart0 = 9; s.BlendStart1 = 8; s.BlendStart2 = 10; s.BlendStart3 = 11; // the last is in the future
        }, 4, 1 + 8 + 8 + 4, complex);
        Assert.Equal((1, 2, 3, 4), (got.Blend0.Frame, got.Blend1.Frame, got.Blend2.Frame, got.Blend3.Frame));
        Assert.Equal((1f, 2f, 0f, 0f), (got.Blend0.StartAge, got.Blend1.StartAge, got.Blend2.StartAge, got.Blend3.StartAge));
        Assert.Equal((Wire(0.4f), Wire(0.3f), Wire(0.2f), Wire(0.1f)), (got.Blend0.Lerp, got.Blend1.Lerp, got.Blend2.Lerp, got.Blend3.Lerp));

        // two and three
        Delta((ref SvEntityState s) => s.Net.Blend1 = new DpFrameGroupBlend { Frame = 2, Lerp = 0.5f }, 4, 1 + 4 + 4 + 2, complex);
        Delta((ref SvEntityState s) => s.Net.Blend2 = new DpFrameGroupBlend { Frame = 2, Lerp = 0.5f }, 4, 1 + 6 + 6 + 3, complex);

        // an age beyond 65.535 s is wrapped by whole loops of the animation (anim_reducetime)
        var host = new Host5 { Time = 1000, Duration = 2 };
        got = Delta((ref SvEntityState s) => s.BlendStart0 = 0.5, 4, 1 + 2 + 2, complex, host);
        Assert.Equal(65.5f, got.Blend0.StartAge, 3);

        // without RENDER_COMPLEXANIMATION the blends are not looked at
        var db = new SvEntityFrame5Database(new Host5());
        SvEntityState plain = Ent(20);
        Write(db, new[] { plain });
        plain.Net.Blend0.Frame = 5;
        Write(db, new[] { plain }, needEmpty: false, expect: false);
    }

    [Fact]
    public void Complex_Animation_Sends_A_Skeleton()
    {
        short[] pose = Enumerable.Range(1, 14).Select(i => (short)(i * 1000 - 7000)).ToArray();
        EntityState got = Delta((ref SvEntityState s) => s.Net.SkeletonPose7s = pose, 4, 1 + 2 + 1 + 28,
            (ref SvEntityState s) => { s.Net.Flags = SvEntityFrame5Database.RenderComplexAnimation; s.Net.ModelIndex = 300; });
        Assert.Equal(300, got.SkeletonModelIndex);
        Assert.Equal(pose, got.SkeletonPose7s);

        // the same array again is no change; a different pose is
        var a = Ent(20); a.Net.Flags = SvEntityFrame5Database.RenderComplexAnimation; a.Net.SkeletonPose7s = pose;
        var b = a;
        Assert.Equal(0u, SvEntityFrame5Database.DeltaBits(a, b));
        b.Net.SkeletonPose7s = (short[])pose.Clone();
        Assert.Equal(0u, SvEntityFrame5Database.DeltaBits(a, b));
        b.Net.SkeletonPose7s[3]++;
        Assert.Equal(DpProtocol.E5ComplexAnimation, SvEntityFrame5Database.DeltaBits(a, b));
    }

    [Fact]
    public void An_Oversized_Skeleton_Is_Passed_Over_Instead_Of_Stopping_The_Server()
    {
        var db = new SvEntityFrame5Database(new Host5());
        var client = new Client();
        SvEntityState big = Ent(20);
        big.Net.Flags = SvEntityFrame5Database.RenderComplexAnimation;
        big.Net.SkeletonPose7s = new short[7 * 300];
        DpEntityFrame frame = client.Receive(Write(db, new[] { big, Ent(21) }));
        Assert.Equal(new[] { 21 }, frame.Changed);
        Assert.NotEqual(0u, db.PendingBits(20));
    }

    [Fact]
    public void An_Entity_Missing_From_The_List_Is_Removed()
    {
        var db = new SvEntityFrame5Database(new Host5());
        var client = new Client();
        client.Receive(Write(db, new[] { Ent(5), Ent(20), Ent(900) }));
        Assert.True(client.Table.Current(20).IsActive);

        DpMessageWriter msg = Write(db, new[] { Ent(5) });
        Assert.Equal(9 + 2 + 2 + 2, msg.Length); // two removals of two bytes each
        DpEntityFrame frame = client.Receive(msg);
        Assert.Equal(new[] { 20, 900 }, frame.Changed);
        Assert.False(client.Table.Current(20).IsActive);
        Assert.False(client.Table.Current(900).IsActive);
        Assert.True(client.Table.Current(5).IsActive);

        // gone is gone: nothing more to say
        Write(db, new[] { Ent(5) }, needEmpty: false, expect: false);

        // and it can come back, whole
        SvEntityState back = Ent(20, new Vector3(1, 2, 3), model: 9);
        frame = client.Receive(Write(db, new[] { Ent(5), back }));
        Assert.Equal(new[] { 20 }, frame.Changed);
        AssertSameNet(back.Net, client.Table.Current(20));
    }

    [Fact]
    public void What_Does_Not_Fit_Arrives_On_Later_Frames_In_Priority_Order()
    {
        var host = new Host5 { MaxClients = 4 };
        var db = new SvEntityFrame5Database(host);
        var client = new Client();
        var far = new Vector3(5000, 0, 0);
        var states = new List<SvEntityState>();
        for (int n = 1; n <= 4; n++) states.Add(Ent(n, far));           // players, wherever they are
        for (int n = 10; n < 60; n++) states.Add(Ent(n, far));          // far from the viewer
        for (int n = 60; n < 110; n++) states.Add(Ent(n, new Vector3(n, 0, 0))); // near the viewer
        states[0] = Ent(1, Vector3.Zero);                               // the viewer
        SvEntityState[] all = states.ToArray();

        const int maxSize = 200;
        var order = new List<int>();
        int frames = 0;
        while (order.Count < all.Length)
        {
            Assert.True(++frames < 100, "entities never all arrived");
            DpMessageWriter msg = Write(db, all, maxSize);
            Assert.InRange(msg.Length, 12, maxSize);
            DpEntityFrame frame = client.Receive(msg);
            Assert.NotEmpty(frame.Changed);
            order.AddRange(frame.Changed);
        }
        Assert.True(frames > 3);

        // the viewer first (top priority), then the other players and the near entities (same level,
        // in entity order), then the far ones; waiting keeps each one's place in the queue
        var expected = new List<int> { 1, 2, 3, 4 };
        expected.AddRange(Enumerable.Range(60, 50));
        expected.AddRange(Enumerable.Range(10, 50));
        Assert.Equal(expected, order);
        foreach (SvEntityState s in all)
            AssertSameNet(s.Net, client.Table.Current(s.Net.Number));

        // all sent: nothing is pending
        Write(db, all, maxSize, needEmpty: false, expect: false);
    }

    [Fact]
    public void A_Removal_And_An_Important_Change_Outrank_An_Ordinary_One()
    {
        var db = new SvEntityFrame5Database(new Host5());
        var far = new Vector3(5000, 0, 0);
        SvEntityState[] all = { Ent(1), Ent(20, far), Ent(21, far), Ent(22, far) };
        var client = new Client();
        client.Receive(Write(db, all));

        all[1].Net.Skin = 1;        // ordinary
        all[2].Net.ModelIndex = 9;  // E5_MODEL is "more noticable"
        SvEntityState[] next = { all[0], all[1], all[2] }; // 22 is removed
        DpEntityFrame frame = client.Receive(Write(db, next));
        Assert.Equal(new[] { 22, 21, 20 }, frame.Changed);
    }

    [Fact]
    public void A_Lost_Frame_Is_Sent_Again()
    {
        var db = new SvEntityFrame5Database(new Host5());
        var client = new Client();
        SvEntityState s = Ent(20, new Vector3(1, 2, 3), model: 5);
        Write(db, new[] { s });                 // frame 1: never arrives
        client.Receive(Write(db, new[] { s })); // frame 2: empty, arrives
        Assert.False(client.Table.Current(20).IsActive);
        Assert.Equal(2, db.PendingPacketLogs);

        // sv_user.c on "clc_ackframe 2" with nothing acknowledged before it
        db.LostFrame(1);
        db.AckFrame(2);
        Assert.Equal(0, db.PendingPacketLogs);
        Assert.NotEqual(0u, db.PendingBits(20) & DpProtocol.E5FullUpdate);

        DpEntityFrame frame = client.Receive(Write(db, new[] { s }, needEmpty: false));
        Assert.Equal(3, frame.FrameNumber);
        Assert.Equal(new[] { 20 }, frame.Changed);
        AssertSameNet(s.Net, client.Table.Current(20));
    }

    [Fact]
    public void A_Lost_Change_Is_Not_Resent_When_A_Later_Frame_Carried_It()
    {
        var db = new SvEntityFrame5Database(new Host5());
        SvEntityState s = Ent(20);
        Write(db, new[] { s });
        db.AckFrame(1);

        s.Net.Skin = 1; s.Net.Alpha = 100;
        Write(db, new[] { s });   // frame 2: skin and alpha, lost
        s.Net.Skin = 2;
        Write(db, new[] { s });   // frame 3: skin again, still on its way
        db.LostFrame(2);
        // alpha has to go again; skin does not, frame 3 has the newer value
        Assert.Equal(DpProtocol.E5Alpha, db.PendingBits(20));

        // and if frame 3 is lost as well, skin comes back
        Write(db, new[] { s });   // frame 4: alpha
        db.LostFrame(3);
        db.AckFrame(4);
        Assert.Equal(DpProtocol.E5Skin, db.PendingBits(20));

        var client = new Client();
        client.Table.SetBaseline(20, Ent(20).Net);
        DpEntityFrame frame = client.Receive(Write(db, new[] { s }, needEmpty: false));
        Assert.Equal(2, client.Table.Current(20).Skin);
        Assert.Equal(5, frame.FrameNumber);
    }

    [Fact]
    public void A_Lost_Removal_Is_Sent_Again()
    {
        var db = new SvEntityFrame5Database(new Host5());
        var client = new Client();
        client.Receive(Write(db, new[] { Ent(20) }));
        db.AckFrame(1);
        Write(db, ReadOnlySpan<SvEntityState>.Empty); // frame 2: the removal, lost
        Write(db, ReadOnlySpan<SvEntityState>.Empty); // frame 3
        db.LostFrame(2);
        db.AckFrame(3);
        DpEntityFrame frame = client.Receive(Write(db, ReadOnlySpan<SvEntityState>.Empty, needEmpty: false));
        Assert.Equal(new[] { 20 }, frame.Changed);
        Assert.False(client.Table.Current(20).IsActive);
    }

    [Fact]
    public void An_Acknowledged_Frame_Is_Forgotten()
    {
        var db = new SvEntityFrame5Database(new Host5());
        SvEntityState s = Ent(20);
        Write(db, new[] { s });
        Write(db, new[] { s });
        Write(db, new[] { s });
        Assert.Equal(3, db.PendingPacketLogs);
        db.AckFrame(2);
        Assert.Equal(1, db.PendingPacketLogs);
        // a late "lost" for something already acknowledged finds no log and resends nothing
        db.LostFrame(1);
        Assert.Equal(0u, db.PendingBits(20));
        Write(db, new[] { s }, needEmpty: false, expect: false);
        db.AckFrame(3);
        Assert.Equal(0, db.PendingPacketLogs);
    }

    [Fact]
    public void A_Full_Packet_Log_Counts_Every_Unacknowledged_Frame_As_Lost()
    {
        var db = new SvEntityFrame5Database(new Host5());
        SvEntityState s = Ent(20, new Vector3(4, 5, 6));
        for (int i = 0; i < SvEntityFrame5Database.MaxPacketLogs; i++)
            Write(db, new[] { s });
        Assert.Equal(SvEntityFrame5Database.MaxPacketLogs, db.PendingPacketLogs);

        var client = new Client(); // one that received none of them
        DpEntityFrame frame = client.Receive(Write(db, new[] { s }, needEmpty: false));
        Assert.Equal(SvEntityFrame5Database.MaxPacketLogs + 1, frame.FrameNumber);
        Assert.Equal(new[] { 20 }, frame.Changed);
        AssertSameNet(s.Net, client.Table.Current(20));
        Assert.Equal(1, db.PendingPacketLogs);
    }

    [Fact]
    public void Entity_Numbers_At_The_Limits()
    {
        var host = new Host5 { MaxEdicts = DpProtocol.MaxEdicts };
        var db = new SvEntityFrame5Database(host);
        var client = new Client();
        SvEntityState first = Ent(1, new Vector3(1, 1, 1));
        SvEntityState last = Ent(DpProtocol.MaxEdicts - 1, new Vector3(2, 2, 2), model: 4);
        DpEntityFrame frame = client.Receive(Write(db, new[] { first, last }));
        Assert.Equal(new[] { 1, DpProtocol.MaxEdicts - 1 }, frame.Changed);
        AssertSameNet(last.Net, client.Table.Current(DpProtocol.MaxEdicts - 1));
        Assert.Equal(DpProtocol.MaxEdicts, db.AllocatedEdicts);

        frame = client.Receive(Write(db, new[] { first }));
        Assert.Equal(new[] { DpProtocol.MaxEdicts - 1 }, frame.Changed);
        Assert.False(client.Table.Current(DpProtocol.MaxEdicts - 1).IsActive);
    }

    [Fact]
    public void The_Database_Grows_With_The_Entities_But_Never_Past_The_Protocol_Limit()
    {
        var db = new SvEntityFrame5Database(new Host5 { MaxEdicts = 64 });
        var client = new Client();
        SvEntityState attached = Ent(2000);
        attached.Net.TagEntity = 65535; // names an entity that cannot exist
        SvEntityState world = Ent(0);   // entity 0 is never networked
        SvEntityState outOfOrder = Ent(3);
        DpEntityFrame frame = client.Receive(Write(db, new[] { world, Ent(5), outOfOrder, attached }, view: 70000));
        Assert.Equal(new[] { 5, 2000 }, frame.Changed.OrderBy(n => n));
        Assert.InRange(db.AllocatedEdicts, 2001, DpProtocol.MaxEdicts);

        // a ring of attachments ends after 256 links
        SvEntityState a = Ent(10), b = Ent(11);
        a.Net.TagEntity = 11; b.Net.TagEntity = 10;
        client.Receive(Write(db, new[] { a, b }));
        Assert.Equal(10, client.Table.Current(11).TagEntity);
    }

    [Fact]
    public void Frame_Numbers_From_A_Hostile_Client_Are_Harmless()
    {
        var db = new SvEntityFrame5Database(new Host5());
        // before anything was ever sent
        db.LostFrame(int.MaxValue); db.LostFrame(int.MinValue); db.LostFrame(0);
        db.AckFrame(int.MaxValue); db.AckFrame(int.MinValue); db.AckFrame(-1);

        SvEntityState s = Ent(20);
        Write(db, new[] { s });
        db.AckFrame(int.MinValue);
        db.LostFrame(int.MinValue);
        db.LostFrame(-5);
        Assert.Equal(1, db.PendingPacketLogs);
        Assert.Equal(0u, db.PendingBits(20));

        // claiming to have lost frames that were never sent costs the client a resend, nothing else
        db.LostFrame(int.MaxValue);
        Assert.Equal(0, db.PendingPacketLogs);
        var client = new Client();
        client.Receive(Write(db, new[] { s }, needEmpty: false));
        Assert.True(client.Table.Current(20).IsActive);

        // acknowledging the future drops the logs; nothing is resent and nothing breaks
        db.AckFrame(int.MaxValue);
        Assert.Equal(0, db.PendingPacketLogs);
        Assert.Equal(0u, db.PendingPriority(-1) + db.PendingBits(-1) + db.PendingBits(int.MaxValue));
    }

    [Fact]
    public void Changed_Stats_Ride_In_Front_Of_The_Frame_And_Are_Resent_When_Lost()
    {
        var db = new SvEntityFrame5Database(new Host5());
        var client = new Client();
        var stats = new int[DpProtocol.MaxClStats];
        stats[0] = 100; stats[10] = 255; stats[32] = 256; stats[255] = -7;
        db.UpdateStats(stats);

        DpMessageWriter msg = Write(db, ReadOnlySpan<SvEntityState>.Empty, needEmpty: false);
        Assert.Equal(3 + 3 + 6 + 6 + 11, msg.Length); // two as bytes, two as longs, the empty frame
        client.Receive(msg);
        Assert.Equal(new Dictionary<int, int> { [0] = 100, [10] = 255, [32] = 256, [255] = -7 }, client.Stats);

        // unchanged: nothing
        db.UpdateStats(stats);
        Write(db, ReadOnlySpan<SvEntityState>.Empty, needEmpty: false, expect: false);

        // lost: all four again
        db.LostFrame(1);
        var fresh = new Client();
        fresh.Receive(Write(db, ReadOnlySpan<SvEntityState>.Empty, needEmpty: false));
        Assert.Equal(client.Stats, fresh.Stats);

        // no room for a stat and the empty frame after it: the stat waits
        stats[5] = 1;
        db.UpdateStats(stats);
        var small = new DpMessageWriter();
        Assert.True(db.WriteFrame(small, 16, ReadOnlySpan<SvEntityState>.Empty, 1, 0, needEmpty: true));
        Assert.Equal(11, small.Length);
        fresh.Receive(Write(db, ReadOnlySpan<SvEntityState>.Empty, needEmpty: false));
        Assert.Equal(1, fresh.Stats[5]);
    }

    [Fact]
    public void An_Entity_Networked_By_QuakeC_Is_Not_Written()
    {
        var host = new Host5();
        host.SendEntity.Add(20);
        var db = new SvEntityFrame5Database(host);
        var client = new Client();
        DpEntityFrame frame = client.Receive(Write(db, new[] { Ent(20), Ent(21) }));
        Assert.Equal(new[] { 21 }, frame.Changed);
    }

    [Fact]
    public void Reset_Starts_The_Stream_Again()
    {
        var db = new SvEntityFrame5Database(new Host5());
        SvEntityState s = Ent(20);
        Write(db, new[] { s });
        Write(db, new[] { s });
        db.UpdateStats(new[] { 1, 2, 3 });
        db.Reset();
        Assert.Equal((0, 0, 0), (db.LatestFrameNumber, db.PendingPacketLogs, db.AllocatedEdicts));
        Assert.Equal(0, db.Stats[1]);

        var client = new Client();
        DpEntityFrame frame = client.Receive(Write(db, new[] { s }, needEmpty: false));
        Assert.Equal(1, frame.FrameNumber);
        Assert.Equal(new[] { 20 }, frame.Changed);
    }

    [Fact]
    public void A_Message_Without_Room_For_An_Empty_Frame_Is_Left_Alone()
    {
        var db = new SvEntityFrame5Database(new Host5());
        var msg = new DpMessageWriter(maxSize: 10);
        Assert.False(db.WriteFrame(msg, 1400, new[] { Ent(20) }, 1, 0, needEmpty: true));
        Assert.Equal(0, msg.Length);
        Assert.False(msg.Overflowed);
        Assert.Equal(0, db.LatestFrameNumber);
    }

    // ------------------------------------------------------------------ svc_csqcentities

    private sealed class CsqcHost : ISvCsqcEntityHost
    {
        public int MaxClients { get; set; } = 4;
        public int NumEdicts { get; set; } = 256;
        public bool RandomizeOrder { get; set; }
        public int Random { get; set; }
        public HashSet<int> SendEntity { get; } = new();
        public HashSet<int> Reject { get; } = new();
        public Dictionary<int, float> SendFlagsField { get; } = new();
        public Dictionary<int, float> VersionField { get; } = new();
        public Dictionary<int, int> Value { get; } = new();
        public int Padding { get; set; }
        public List<(int entity, int to, int sendFlags)> Calls { get; } = new();

        public int NextRandom(int exclusiveMax) => Random;
        public bool HasSendEntity(int entityNumber) => SendEntity.Contains(entityNumber);
        public float GetSendFlags(int entityNumber) => SendFlagsField.GetValueOrDefault(entityNumber);
        public void ClearSendFlags(int entityNumber) => SendFlagsField[entityNumber] = 0;
        public float GetVersion(int entityNumber) => VersionField.GetValueOrDefault(entityNumber);

        public bool CallSendEntity(int entityNumber, int toClientEntityNumber, int sendFlags, DpMessageWriter msg)
        {
            Calls.Add((entityNumber, toClientEntityNumber, sendFlags));
            msg.WriteLong(sendFlags);
            msg.WriteShort(Value.GetValueOrDefault(entityNumber));
            msg.WriteByte(Padding);
            msg.WriteBytes(new byte[Padding]);
            return !Reject.Contains(entityNumber);
        }
    }

    private const int Full = (int)SvCsqcEntityFrames.FullSendFlags;
    private const int ClientEntity = 2;

    private static ushort[] Numbers(params int[] numbers) => numbers.Select(n => (ushort)n).ToArray();

    private static DpMessageWriter WriteCsqc(SvCsqcEntityFrames frames, int frameNum, ushort[] numbers, bool expect = true, int maxSize = 1400, int startAt = 0)
    {
        var msg = new DpMessageWriter();
        msg.WriteBytes(new byte[startAt]); // svc_nop is 1, but zeros (svc_bad) are never parsed here: see callers
        Assert.Equal(expect, frames.WriteFrame(msg, maxSize, numbers, frameNum, ClientEntity));
        if (!expect)
            Assert.Equal(startAt, msg.Length); // whatever was tried was taken back out
        return msg;
    }

    private static (CsqcHost host, SvCsqcEntityFrames frames, Client client) Csqc(params int[] sendEntities)
    {
        var host = new CsqcHost();
        foreach (int n in sendEntities)
        {
            host.SendEntity.Add(n);
            host.Value[n] = n * 3;
        }
        return (host, new SvCsqcEntityFrames(host), new Client());
    }

    [Fact]
    public void Csqc_New_Entity_Is_Sent_Whole()
    {
        var (host, frames, client) = Csqc(10);
        DpMessageWriter msg = WriteCsqc(frames, 1, Numbers(10));
        Assert.Equal((byte)Svc.CsqcEntities, msg.WrittenSpan[0]);
        Assert.Equal(1 + 2 + 7 + 2, msg.Length);
        client.Deliver(msg);
        Assert.Equal(new[] { (10, Full, 30) }, client.CsqcUpdates);
        Assert.Empty(client.CsqcRemoves);
        Assert.Equal(new[] { (10, ClientEntity, Full) }, host.Calls);
        Assert.Equal(SvCsqcEntityFrames.ScopeExistedOnce | SvCsqcEntityFrames.ScopeAssumedExisting, frames.Scope(10));
        Assert.Equal(0u, frames.SendFlags(10));
        Assert.Equal(1, frames.FramesRemembered);
    }

    [Fact]
    public void Csqc_Update_Is_Sent_Only_When_SendFlags_Are_Set()
    {
        var (host, frames, client) = Csqc(10, 11);
        var versions = new SvCsqcEntityVersions();
        client.Deliver(WriteCsqc(frames, 1, Numbers(10, 11)));
        client.CsqcUpdates.Clear();
        host.Calls.Clear();

        // nothing changed: nothing written, SendEntity not even called, and no history slot spent
        WriteCsqc(frames, 2, Numbers(10, 11), expect: false);
        Assert.Empty(host.Calls);
        Assert.Equal(1, frames.FramesRemembered);

        // the QuakeC sets .SendFlags; the server collects and clears it once and tells every client
        host.SendFlagsField[11] = 5;
        foreach (int e in new[] { 10, 11 })
            frames.AddSendFlags(e, versions.CollectSendFlags(host, e));
        Assert.Equal(0f, host.SendFlagsField[11]);
        client.Deliver(WriteCsqc(frames, 3, Numbers(10, 11)));
        Assert.Equal(new[] { (11, 5, 33) }, client.CsqcUpdates);

        // flags set over several frames before the entity is visible again accumulate
        host.SendFlagsField[11] = 2;
        frames.AddSendFlags(11, versions.CollectSendFlags(host, 11));
        host.SendFlagsField[11] = 8;
        frames.AddSendFlags(11, versions.CollectSendFlags(host, 11));
        client.CsqcUpdates.Clear();
        client.Deliver(WriteCsqc(frames, 4, Numbers(10, 11)));
        Assert.Equal(new[] { (11, 10, 33) }, client.CsqcUpdates);
    }

    [Fact]
    public void Csqc_Legacy_Version_Field_Means_Send_Everything()
    {
        var host = new CsqcHost();
        var versions = new SvCsqcEntityVersions();
        host.VersionField[10] = 3;
        Assert.Equal(SvCsqcEntityFrames.FullSendFlags, versions.CollectSendFlags(host, 10)); // first sight of version 3
        Assert.Equal(0u, versions.CollectSendFlags(host, 10));                               // unchanged
        host.SendFlagsField[10] = 4;
        Assert.Equal(4u, versions.CollectSendFlags(host, 10));
        host.VersionField[10] = 4;
        Assert.Equal(SvCsqcEntityFrames.FullSendFlags, versions.CollectSendFlags(host, 10));
        versions.EdictFreed(10);
        Assert.Equal(SvCsqcEntityFrames.FullSendFlags, versions.CollectSendFlags(host, 10));

        // garbage in the fields and in the entity number
        host.SendFlagsField[11] = float.NaN;
        host.VersionField[11] = -5;
        Assert.Equal(0u, versions.CollectSendFlags(host, 11));
        host.SendFlagsField[11] = float.PositiveInfinity;
        Assert.Equal(uint.MaxValue, versions.CollectSendFlags(host, 11));
        Assert.Equal(0u, versions.CollectSendFlags(host, -1));
        Assert.Equal(0u, versions.CollectSendFlags(host, DpProtocol.MaxEdicts));
        host.VersionField[DpProtocol.MaxEdicts - 1] = 1;
        Assert.Equal(SvCsqcEntityFrames.FullSendFlags, versions.CollectSendFlags(host, DpProtocol.MaxEdicts - 1));
        versions.EdictFreed(-1);
        versions.EdictFreed(int.MaxValue);
    }

    [Fact]
    public void Csqc_Entity_Leaving_The_List_Is_Removed_Once()
    {
        var (host, frames, client) = Csqc(10, 11);
        client.Deliver(WriteCsqc(frames, 1, Numbers(10, 11)));
        client.CsqcUpdates.Clear();

        DpMessageWriter msg = WriteCsqc(frames, 2, Numbers(11));
        Assert.Equal(1 + 2 + 2, msg.Length);
        client.Deliver(msg);
        Assert.Equal(new[] { 10 }, client.CsqcRemoves);
        Assert.Empty(client.CsqcUpdates);
        Assert.Equal(SvCsqcEntityFrames.ScopeExistedOnce, frames.Scope(10));
        WriteCsqc(frames, 3, Numbers(11), expect: false);

        // back in view: sent whole again
        client.Deliver(WriteCsqc(frames, 4, Numbers(10, 11)));
        Assert.Equal(new[] { (10, Full, 30) }, client.CsqcUpdates);

        // an entity that stops having a SendEntity function is removed too, even though still listed
        host.SendEntity.Remove(11);
        client.CsqcRemoves.Clear();
        client.Deliver(WriteCsqc(frames, 5, Numbers(10, 11)));
        Assert.Equal(new[] { 11 }, client.CsqcRemoves);
    }

    [Fact]
    public void Csqc_SendEntity_Returning_False_Removes_Or_Suppresses()
    {
        var (host, frames, client) = Csqc(10, 11);
        host.Reject.Add(11);
        // never sent and rejected: its payload is rolled back, and it is not asked again
        DpMessageWriter msg = WriteCsqc(frames, 1, Numbers(10, 11));
        Assert.Equal(1 + 2 + 7 + 2, msg.Length);
        client.Deliver(msg);
        Assert.Equal(new[] { (10, Full, 30) }, client.CsqcUpdates);
        Assert.Equal(0u, frames.SendFlags(11));
        host.Calls.Clear();
        WriteCsqc(frames, 2, Numbers(10, 11), expect: false);
        Assert.Empty(host.Calls);

        // rejected alone: not even the svc byte survives
        var (host2, frames2, _) = Csqc(11);
        host2.Reject.Add(11);
        WriteCsqc(frames2, 1, Numbers(11), expect: false);

        // sent before and rejected now: a remove in place of the payload
        host.Reject.Add(10);
        frames.AddSendFlags(10, 1);
        msg = WriteCsqc(frames, 3, Numbers(10, 11));
        Assert.Equal(1 + 2 + 2, msg.Length);
        client.Deliver(msg);
        Assert.Equal(new[] { 10 }, client.CsqcRemoves);
        Assert.Equal(SvCsqcEntityFrames.ScopeExistedOnce, frames.Scope(10));
    }

    [Fact]
    public void Csqc_Lost_Frame_Resends_The_Union_Of_The_Lost_Flags()
    {
        var (_, frames, client) = Csqc(10, 11);
        ushort[] both = Numbers(10, 11);
        client.Deliver(WriteCsqc(frames, 1, both));
        client.CsqcUpdates.Clear();

        frames.AddSendFlags(10, 1);
        WriteCsqc(frames, 2, both);     // lost
        WriteCsqc(frames, 3, both, expect: false); // an svc_entities frame with no CSQC entities in it
        frames.AddSendFlags(10, 2 | 4);
        frames.AddSendFlags(11, 16);
        WriteCsqc(frames, 4, both);     // lost
        frames.AddSendFlags(10, 8);     // not yet sent
        frames.LostFrame(2);
        Assert.Equal(1u | 8u, frames.SendFlags(10));
        frames.LostFrame(3);            // carried nothing
        Assert.Equal(1u | 8u, frames.SendFlags(10));
        frames.LostFrame(4);
        Assert.Equal(15u, frames.SendFlags(10));
        Assert.Equal(16u, frames.SendFlags(11));

        client.Deliver(WriteCsqc(frames, 5, both));
        Assert.Equal(new[] { (10, 15, 30), (11, 16, 33) }, client.CsqcUpdates);
    }

    [Fact]
    public void Csqc_Lost_Flags_Already_Carried_By_A_Later_Frame_Are_Not_Resent()
    {
        var (_, frames, _) = Csqc(10);
        ushort[] one = Numbers(10);
        WriteCsqc(frames, 1, one);
        frames.AddSendFlags(10, 1 | 2);
        WriteCsqc(frames, 2, one);  // lost
        frames.AddSendFlags(10, 2);
        WriteCsqc(frames, 3, one);  // may still arrive
        frames.LostFrame(2);
        Assert.Equal(1u, frames.SendFlags(10));
    }

    [Fact]
    public void Csqc_Lost_New_Entity_And_Lost_Removal_Are_Sent_Again()
    {
        var (_, frames, client) = Csqc(10);
        WriteCsqc(frames, 1, Numbers(10)); // lost
        frames.LostFrame(1);
        client.Deliver(WriteCsqc(frames, 2, Numbers(10)));
        Assert.Equal(new[] { (10, Full, 30) }, client.CsqcUpdates);

        WriteCsqc(frames, 3, Numbers());   // the removal, lost
        WriteCsqc(frames, 4, Numbers(), expect: false);
        frames.LostFrame(3);
        client.Deliver(WriteCsqc(frames, 5, Numbers()));
        Assert.Equal(new[] { 10 }, client.CsqcRemoves);
        WriteCsqc(frames, 6, Numbers(), expect: false);
    }

    [Fact]
    public void Csqc_LostAllFrames_Resends_Everything_Ever_Sent()
    {
        var (host, frames, client) = Csqc(10, 11, 12);
        client.Deliver(WriteCsqc(frames, 1, Numbers(10, 11, 12)));
        client.Deliver(WriteCsqc(frames, 2, Numbers(10, 11))); // 12 leaves view and is removed
        client.CsqcUpdates.Clear();
        client.CsqcRemoves.Clear();
        host.SendEntity.Remove(11);                            // 11 stops being a CSQC entity...
        client.Deliver(WriteCsqc(frames, 3, Numbers(10, 11))); // ...and is removed
        Assert.Equal(new[] { 11 }, client.CsqcRemoves);
        client.CsqcRemoves.Clear();

        frames.LostAllFrames();
        client.Deliver(WriteCsqc(frames, 4, Numbers(10, 11)));
        Assert.Equal(new[] { (10, Full, 30) }, client.CsqcUpdates);
        Assert.Equal(new[] { 11 }, client.CsqcRemoves); // the remove is forced again; 12 is not in view
    }

    [Fact]
    public void Csqc_A_Loss_Older_Than_The_History_Resends_Everything()
    {
        var (_, frames, client) = Csqc(10);
        ushort[] one = Numbers(10);
        client.Deliver(WriteCsqc(frames, 100, one));
        frames.AddSendFlags(10, 1);
        client.Deliver(WriteCsqc(frames, 102, one));
        client.CsqcUpdates.Clear();

        // 101 lies between remembered frames: it had no CSQC entities, nothing to do
        frames.LostFrame(101);
        Assert.Equal(0u, frames.SendFlags(10));
        // 150 is newer than anything remembered: the same
        frames.LostFrame(150);
        Assert.Equal(0u, frames.SendFlags(10));
        Assert.Equal(0, frames.LastResetFrame);

        // 50 is older than anything remembered: no telling what it carried
        frames.LostFrame(50);
        Assert.Equal(SvCsqcEntityFrames.FullSendFlags, frames.SendFlags(10));
        Assert.Equal(-1, frames.LastResetFrame);
        frames.LostFrame(50); // ignored until the resend has gone out
        client.Deliver(WriteCsqc(frames, 103, one));
        Assert.Equal(new[] { (10, Full, 30) }, client.CsqcUpdates);
        Assert.Equal(103, frames.LastResetFrame);

        // losses from before the resend no longer matter
        frames.AddSendFlags(10, 1);
        frames.LostFrame(102);
        Assert.Equal(1u, frames.SendFlags(10));
    }

    [Fact]
    public void Csqc_Frame_Numbers_From_A_Hostile_Client_Are_Harmless()
    {
        var (_, frames, client) = Csqc(10);
        frames.LostFrame(int.MaxValue); frames.LostFrame(int.MinValue); frames.LostFrame(0); frames.LostFrame(-1);
        frames.LostAllFrames();
        client.Deliver(WriteCsqc(frames, 1, Numbers(10)));
        frames.LostFrame(int.MinValue);
        frames.LostFrame(int.MaxValue);
        frames.LostFrame(-1);
        Assert.Equal(0u, frames.SendFlags(10));
        frames.AddSendFlags(-1, 1); frames.AddSendFlags(DpProtocol.MaxEdicts, 1); frames.AddSendFlags(DpProtocol.MaxEdicts - 1, 1);
        frames.EdictFreed(-1); frames.EdictFreed(int.MaxValue);
        Assert.Equal(0, frames.Scope(-1) + frames.Scope(int.MaxValue));
        // entity numbers outside the edict range, or out of order, are skipped
        WriteCsqc(frames, 2, Numbers(0, 10, 9, 300, 65535), expect: false);
    }

    [Fact]
    public void Csqc_What_Does_Not_Fit_Is_Rolled_Back_And_Sent_Later_Players_First()
    {
        var host = new CsqcHost { MaxClients = 4, Padding = 33 }; // 2 + 40 bytes an entity
        var frames = new SvCsqcEntityFrames(host);
        var client = new Client();
        int[] ents = Enumerable.Range(1, 4).Concat(Enumerable.Range(20, 46)).ToArray();
        foreach (int n in ents) { host.SendEntity.Add(n); host.Value[n] = n; }
        ushort[] numbers = Numbers(ents);

        const int maxSize = 400;
        int frame = 0;
        var order = new List<int>();
        while (order.Count < ents.Length)
        {
            Assert.True(++frame < 100, "entities never all arrived");
            DpMessageWriter msg = WriteCsqc(frames, frame, numbers, maxSize: maxSize);
            // 24 bytes are kept back for the empty svc_entities that must share the packet
            Assert.InRange(msg.Length, 1 + 42 + 2, maxSize - 24);
            int before = client.CsqcUpdates.Count;
            client.Deliver(msg);
            Assert.True(client.CsqcUpdates.Count > before);
            order.AddRange(client.CsqcUpdates.Skip(before).Select(u => u.entity));
        }
        Assert.True(frame > 3);
        Assert.Equal(ents, order); // in entity order when the order is not randomised
        Assert.All(client.CsqcUpdates, u => Assert.Equal((Full, u.entity), (u.sendFlags, u.value)));
        Assert.Equal(frame, frames.FramesRemembered);
        WriteCsqc(frames, frame + 1, numbers, expect: false, maxSize: maxSize);

        // a message with less than 32 + 24 bytes to spare gets nothing at all
        frames.AddSendFlags(1, 1);
        WriteCsqc(frames, frame + 2, numbers, expect: false, maxSize: maxSize, startAt: maxSize - 24 - 32);
        Assert.Equal(1u, frames.SendFlags(1));
    }

    [Fact]
    public void Csqc_Random_Order_Rotates_The_Non_Players_Only()
    {
        var host = new CsqcHost { MaxClients = 4, RandomizeOrder = true, Random = 3 };
        var frames = new SvCsqcEntityFrames(host);
        var client = new Client();
        int[] ents = { 2, 3, 20, 21, 22, 23, 24, 25 };
        foreach (int n in ents) host.SendEntity.Add(n);
        client.Deliver(WriteCsqc(frames, 1, Numbers(ents)));
        // players stay in front; the others start at the fourth of them (index 3) and wrap
        Assert.Equal(new[] { 2, 3, 23, 24, 25, 20, 21, 22 }, client.CsqcUpdates.Select(u => u.entity));

        // a generator that answers out of range is not trusted with an index
        host.Random = -7;
        foreach (int n in ents) frames.AddSendFlags(n, 1);
        client.CsqcUpdates.Clear();
        client.Deliver(WriteCsqc(frames, 2, Numbers(ents)));
        Assert.Equal(ents.Length, client.CsqcUpdates.Count);
    }

    [Fact]
    public void Csqc_At_Most_256_Entities_A_Frame()
    {
        var host = new CsqcHost { MaxClients = 4, NumEdicts = 1024 };
        var frames = new SvCsqcEntityFrames(host);
        var client = new Client();
        int[] ents = Enumerable.Range(10, 300).ToArray();
        foreach (int n in ents) host.SendEntity.Add(n);
        client.Deliver(WriteCsqc(frames, 1, Numbers(ents), maxSize: 60000));
        Assert.Equal(SvCsqcEntityFrames.EntitiesPerFrame, client.CsqcUpdates.Count);
        client.Deliver(WriteCsqc(frames, 2, Numbers(ents), maxSize: 60000));
        Assert.Equal(ents, client.CsqcUpdates.Select(u => u.entity));
    }

    [Fact]
    public void Csqc_A_Payload_Larger_Than_The_Message_Is_Rolled_Back()
    {
        var host = new CsqcHost { Padding = 200 };
        host.SendEntity.Add(10);
        var frames = new SvCsqcEntityFrames(host);
        var msg = new DpMessageWriter(maxSize: 128);
        Assert.False(frames.WriteFrame(msg, 5000, Numbers(10), 1, ClientEntity));
        Assert.Equal(0, msg.Length);
        Assert.False(msg.Overflowed);
        Assert.Equal(0, frames.FramesRemembered);
    }

    [Fact]
    public void Csqc_Reset_Forgets_The_Client()
    {
        var (_, frames, client) = Csqc(10);
        client.Deliver(WriteCsqc(frames, 7, Numbers(10)));
        frames.LostFrame(3); // older than the history: full resend pending, LastResetFrame -1
        frames.Reset();
        Assert.Equal((0, 0, 0), (frames.NumEdicts, frames.FramesRemembered, frames.LastResetFrame));
        Assert.Equal(0, frames.Scope(10));
        client.CsqcUpdates.Clear();
        client.Deliver(WriteCsqc(frames, 1, Numbers(10)));
        Assert.Equal(new[] { (10, Full, 30) }, client.CsqcUpdates);
        Assert.Empty(client.CsqcRemoves);
    }

    [Fact]
    public void Both_Streams_Share_A_Packet_And_A_Frame_Number()
    {
        // sv_ents.c SV_WriteEntitiesToClient: the CSQC entities first, numbered as the svc_entities
        // frame that follows; having written any makes that frame mandatory.
        var csqcHost = new CsqcHost();
        csqcHost.SendEntity.Add(10);
        var frames = new SvCsqcEntityFrames(csqcHost);
        var host5 = new Host5();
        host5.SendEntity.Add(10);
        var db = new SvEntityFrame5Database(host5);
        var client = new Client();

        var msg = new DpMessageWriter();
        bool needEmpty = frames.WriteFrame(msg, 1400, Numbers(10), db.LatestFrameNumber + 1, ClientEntity);
        Assert.True(needEmpty);
        Assert.True(db.WriteFrame(msg, 1400, new[] { Ent(20) }, 1, 3, needEmpty));
        DpEntityFrame frame = client.Receive(msg);
        Assert.Equal(1, frame.FrameNumber);
        Assert.Equal(new[] { 20 }, frame.Changed);
        Assert.Single(client.CsqcUpdates);

        // the client acknowledges frame 3 having seen neither 1 nor 2 (sv_user.c clc_ackframe)
        msg = new DpMessageWriter();
        csqcHost.SendFlagsField[10] = 0;
        frames.AddSendFlags(10, 4);
        needEmpty = frames.WriteFrame(msg, 1400, Numbers(10), db.LatestFrameNumber + 1, ClientEntity);
        Assert.True(db.WriteFrame(msg, 1400, ReadOnlySpan<SvEntityState>.Empty, 1, 3, needEmpty)); // frame 2: lost
        Assert.True(db.WriteFrame(new DpMessageWriter(), 1400, ReadOnlySpan<SvEntityState>.Empty, 1, 4, true)); // frame 3
        for (int lost = 2; lost < 3; lost++)
        {
            db.LostFrame(lost);
            frames.LostFrame(lost);
        }
        db.AckFrame(3);
        Assert.Equal(4u, frames.SendFlags(10));
        Assert.NotEqual(0u, db.PendingBits(20)); // the removal of 20 was in frame 2
    }
}
