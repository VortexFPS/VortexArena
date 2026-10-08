using System;
using System.Collections.Generic;
using System.Numerics;
using VortexArena.Legacy.Protocol;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>svc_entities for DP7 (cl_ents5.c), one flag at a time, from hand-built bytes.</summary>
public class EntityFrame5Tests
{
    private static DpMessageWriter Frame(int frameNumber = 100, int serverMove = 7)
    {
        var w = new DpMessageWriter();
        w.WriteLong(frameNumber);
        w.WriteLong(serverMove);
        return w;
    }

    // Writes the 1-4 flag bytes the way the server does: an extend bit for every further byte needed.
    private static void Bits(DpMessageWriter w, uint bits)
    {
        if (bits >= 1u << 24) bits |= DpProtocol.E5Extend1 | DpProtocol.E5Extend2 | DpProtocol.E5Extend3;
        else if (bits >= 1u << 16) bits |= DpProtocol.E5Extend1 | DpProtocol.E5Extend2;
        else if (bits >= 1u << 8) bits |= DpProtocol.E5Extend1;
        w.WriteByte((int)(bits & 0xFF));
        if ((bits & DpProtocol.E5Extend1) != 0) w.WriteByte((int)((bits >> 8) & 0xFF));
        if ((bits & DpProtocol.E5Extend2) != 0) w.WriteByte((int)((bits >> 16) & 0xFF));
        if ((bits & DpProtocol.E5Extend3) != 0) w.WriteByte((int)((bits >> 24) & 0xFF));
    }

    private static (DpEntityTable table, DpEntityFrame frame, DpMessageReader reader) Read(DpMessageWriter w, DpEntityTable? table = null)
    {
        w.WriteShort(0x8000);
        table ??= new DpEntityTable();
        var r = new DpMessageReader(w.ToArray());
        Assert.True(table.ReadFrame(r, 0, out DpEntityFrame frame, out string? error), error);
        Assert.False(r.BadRead);
        Assert.Equal(0, r.Remaining);
        return (table, frame, r);
    }

    [Fact]
    public void Default_State_Matches_Protocol_C()
    {
        EntityState d = EntityState.Default;
        Assert.Equal(255, d.Alpha);
        Assert.Equal(16, d.Scale);
        Assert.Equal(254, d.GlowColor);
        Assert.Equal((32, 32, 32), (d.ColorMod0, d.ColorMod1, d.ColorMod2));
        Assert.Equal((32, 32, 32), (d.GlowMod0, d.GlowMod1, d.GlowMod2));
        Assert.Equal(DpProtocol.ActiveNot, d.Active);
        Assert.Equal(0, d.ModelIndex + d.Frame + d.Effects + d.Skin + d.Colormap + d.Flags + d.GlowSize);
        Assert.False(d.IsActive);

        var table = new DpEntityTable();
        Assert.Equal(255, table.Current(5).Alpha);
        Assert.Equal(255, table.Current(40000).Alpha); // out of range reads are the default, not an exception
        Assert.Equal(255, table.Current(-1).Alpha);
    }

    [Fact]
    public void Empty_Frame_Reads_Header_And_Terminator()
    {
        var (_, frame, _) = Read(Frame(1234, 99));
        Assert.Equal(1234, frame.FrameNumber);
        Assert.Equal(99, frame.ServerMoveSequence);
        Assert.Empty(frame.Changed);
    }

    [Fact]
    public void Full_Update_Resets_To_Default_And_Activates()
    {
        var w = Frame();
        w.WriteShort(5);
        Bits(w, DpProtocol.E5FullUpdate | DpProtocol.E5Model | DpProtocol.E5Origin);
        // order on the wire: origin (13i), then model
        w.WriteShort(80); w.WriteShort(-160); w.WriteShort(8);
        w.WriteByte(42);
        var (table, frame, _) = Read(w);
        Assert.Equal(new[] { 5 }, frame.Changed);
        EntityState s = table.Current(5);
        Assert.True(s.IsActive);
        Assert.Equal(5, s.Number);
        Assert.Equal(new Vector3(10, -20, 1), s.Origin);
        Assert.Equal(42, s.ModelIndex);
        Assert.Equal(255, s.Alpha);
        Assert.Equal(16, s.Scale);
        Assert.Equal(6, table.Count);
        Assert.False(table.Previous(5).IsActive); // state before the update
    }

    [Fact]
    public void Delta_Changes_Only_The_Flagged_Fields()
    {
        var table = new DpEntityTable();
        var w = Frame();
        w.WriteShort(3);
        Bits(w, DpProtocol.E5FullUpdate | DpProtocol.E5Model | DpProtocol.E5Frame | DpProtocol.E5Skin);
        w.WriteByte(10); w.WriteByte(20); w.WriteByte(30);
        Read(w, table);

        w = Frame(101);
        w.WriteShort(3);
        Bits(w, DpProtocol.E5Frame);
        w.WriteByte(21);
        Read(w, table);
        EntityState s = table.Current(3);
        Assert.Equal((10, 21, 30), (s.ModelIndex, s.Frame, s.Skin));
        Assert.True(s.IsActive);
        Assert.Equal(20, table.Previous(3).Frame);
    }

    [Fact]
    public void Remove_Restores_The_Default_State()
    {
        var table = new DpEntityTable();
        var w = Frame();
        w.WriteShort(9);
        Bits(w, DpProtocol.E5FullUpdate | DpProtocol.E5Model);
        w.WriteByte(1);
        Read(w, table);
        Assert.True(table.Current(9).IsActive);

        w = Frame(101);
        w.WriteShort(9 | 0x8000);
        var (_, frame, _) = Read(w, table);
        Assert.Equal(new[] { 9 }, frame.Changed);
        EntityState s = table.Current(9);
        Assert.False(s.IsActive);
        Assert.Equal(0, s.ModelIndex);
        Assert.Equal(9, s.Number);          // "fix the number"
        Assert.True(table.Previous(9).IsActive);
    }

    [Fact]
    public void Origin32_And_Angles16_Use_The_Wide_Forms()
    {
        var w = Frame();
        w.WriteShort(1);
        Bits(w, DpProtocol.E5Origin | DpProtocol.E5Origin32 | DpProtocol.E5Angles | DpProtocol.E5Angles16);
        w.WriteFloat(1.25f); w.WriteFloat(-2.5f); w.WriteFloat(4096.125f);
        w.WriteAngle16i(90); w.WriteAngle16i(-45); w.WriteAngle16i(180);
        EntityState s = Read(w).table.Current(1);
        Assert.Equal(new Vector3(1.25f, -2.5f, 4096.125f), s.Origin);
        Assert.Equal(new Vector3(90, -45, -180), s.Angles); // 0x8000 reads back as -180
    }

    [Fact]
    public void Angles_Without_Angles16_Are_Bytes()
    {
        var w = Frame();
        w.WriteShort(1);
        Bits(w, DpProtocol.E5Angles);
        w.WriteByte(64); w.WriteByte(192); w.WriteByte(32);
        Assert.Equal(new Vector3(90, -90, 45), Read(w).table.Current(1).Angles);
    }

    [Fact]
    public void Model16_Frame16_And_Effects_Widths()
    {
        var w = Frame();
        w.WriteShort(1);
        Bits(w, DpProtocol.E5Model | DpProtocol.E5Model16 | DpProtocol.E5Frame | DpProtocol.E5Frame16 | DpProtocol.E5Effects | DpProtocol.E5Effects16);
        w.WriteShort(0x1234); // model
        w.WriteShort(0xFEDC); // frame
        w.WriteShort(0x8001); // effects16, unsigned
        EntityState s = Read(w).table.Current(1);
        Assert.Equal(0x1234, s.ModelIndex);
        Assert.Equal(0xFEDC, s.Frame);
        Assert.Equal(0x8001, s.Effects);

        w = Frame();
        w.WriteShort(1);
        Bits(w, DpProtocol.E5Effects);
        w.WriteByte(0xF0);
        Assert.Equal(0xF0, Read(w).table.Current(1).Effects);

        // EFFECTS32 wins over EFFECTS16 when both are set
        w = Frame();
        w.WriteShort(1);
        Bits(w, DpProtocol.E5Effects | DpProtocol.E5Effects32 | DpProtocol.E5Effects16);
        w.WriteLong(unchecked((int)0x80000100));
        Assert.Equal(unchecked((int)0x80000100), Read(w).table.Current(1).Effects);
    }

    [Fact]
    public void Second_Byte_Fields_Flags_Alpha_Scale_Colormap()
    {
        var w = Frame();
        w.WriteShort(2);
        Bits(w, DpProtocol.E5Flags | DpProtocol.E5Alpha | DpProtocol.E5Scale | DpProtocol.E5Colormap | DpProtocol.E5Skin);
        // wire order: flags, skin, alpha, scale, colormap
        w.WriteByte(0x24); w.WriteByte(3); w.WriteByte(128); w.WriteByte(32); w.WriteByte(7);
        EntityState s = Read(w).table.Current(2);
        Assert.Equal((0x24, 3, 128, 32, 7), (s.Flags, s.Skin, s.Alpha, s.Scale, s.Colormap));
    }

    [Fact]
    public void Third_Byte_Fields_Attachment_Light_Glow_ColorMod()
    {
        var w = Frame();
        w.WriteShort(2);
        Bits(w, DpProtocol.E5Attachment | DpProtocol.E5Light | DpProtocol.E5Glow | DpProtocol.E5ColorMod);
        w.WriteShort(300); w.WriteByte(4);                                               // attachment
        w.WriteShort(1); w.WriteShort(2); w.WriteShort(3); w.WriteShort(60000);         // light
        w.WriteByte(5); w.WriteByte(6);                                                  // lightstyle, pflags
        w.WriteByte(9); w.WriteByte(10);                                                 // glow
        w.WriteByte(11); w.WriteByte(12); w.WriteByte(13);                               // colormod
        EntityState s = Read(w).table.Current(2);
        Assert.Equal((300, 4), (s.TagEntity, s.TagIndex));
        Assert.Equal((1, 2, 3, 60000), (s.Light0, s.Light1, s.Light2, s.Light3));
        Assert.Equal((5, 6), (s.LightStyle, s.LightPFlags));
        Assert.Equal((9, 10), (s.GlowSize, s.GlowColor));
        Assert.Equal((11, 12, 13), (s.ColorMod0, s.ColorMod1, s.ColorMod2));
    }

    [Fact]
    public void Fourth_Byte_Fields_GlowMod_And_TrailEffect()
    {
        var w = Frame();
        w.WriteShort(2);
        Bits(w, DpProtocol.E5GlowMod | DpProtocol.E5TrailEffectNum);
        w.WriteByte(1); w.WriteByte(2); w.WriteByte(3);
        w.WriteShort(0xABCD);
        EntityState s = Read(w).table.Current(2);
        Assert.Equal((1, 2, 3), (s.GlowMod0, s.GlowMod1, s.GlowMod2));
        Assert.Equal(0xABCD, s.TrailEffectNum);
    }

    [Fact]
    public void Extend_Bytes_Are_Read_Only_When_Chained()
    {
        // E5_EXTEND2 in the second byte is what announces the third; without EXTEND1 the first byte
        // stands alone and the next byte is already field data.
        var w = Frame();
        w.WriteShort(4);
        w.WriteByte((int)DpProtocol.E5Skin); // one flag byte, no extend
        w.WriteByte(0x80);                   // skin value that looks like an extend bit
        EntityState s = Read(w).table.Current(4);
        Assert.Equal(0x80, s.Skin);

        w = Frame();
        w.WriteShort(4);
        w.WriteByte((int)(DpProtocol.E5Extend1 | DpProtocol.E5Skin));
        w.WriteByte((int)(DpProtocol.E5Alpha >> 8)); // second byte, no EXTEND2
        w.WriteByte(0xFF);                            // skin
        w.WriteByte(0x40);                            // alpha
        s = Read(w).table.Current(4);
        Assert.Equal((0xFF, 0x40), (s.Skin, s.Alpha));
    }

    [Fact]
    public void Every_Field_At_Once_In_Wire_Order()
    {
        var w = Frame();
        w.WriteShort(1000);
        Bits(w, DpProtocol.E5FullUpdate | DpProtocol.E5Flags | DpProtocol.E5Origin | DpProtocol.E5Angles | DpProtocol.E5Model
            | DpProtocol.E5Frame | DpProtocol.E5Skin | DpProtocol.E5Effects | DpProtocol.E5Alpha | DpProtocol.E5Scale
            | DpProtocol.E5Colormap | DpProtocol.E5Attachment | DpProtocol.E5Light | DpProtocol.E5Glow | DpProtocol.E5ColorMod
            | DpProtocol.E5GlowMod | DpProtocol.E5ComplexAnimation | DpProtocol.E5TrailEffectNum);
        w.WriteByte(1);                                             // flags
        w.WriteShort(8); w.WriteShort(16); w.WriteShort(24);        // origin 13i
        w.WriteByte(64); w.WriteByte(0); w.WriteByte(0);            // angles 8i
        w.WriteByte(2);                                             // model
        w.WriteByte(3);                                             // frame
        w.WriteByte(4);                                             // skin
        w.WriteByte(5);                                             // effects
        w.WriteByte(6);                                             // alpha
        w.WriteByte(7);                                             // scale
        w.WriteByte(8);                                             // colormap
        w.WriteShort(9); w.WriteByte(10);                           // attachment
        w.WriteShort(11); w.WriteShort(12); w.WriteShort(13); w.WriteShort(14); w.WriteByte(15); w.WriteByte(16); // light
        w.WriteByte(17); w.WriteByte(18);                           // glow
        w.WriteByte(19); w.WriteByte(20); w.WriteByte(21);          // colormod
        w.WriteByte(22); w.WriteByte(23); w.WriteByte(24);          // glowmod
        w.WriteByte(0); w.WriteShort(25); w.WriteShort(500);        // complex animation type 0
        w.WriteShort(26);                                           // traileffectnum
        w.WriteShort(1001 | 0x8000);                                // and a remove of a neighbour in the same frame
        var (table, frame, _) = Read(w);
        EntityState s = table.Current(1000);
        Assert.Equal(new[] { 1000, 1001 }, frame.Changed);
        Assert.Equal(new Vector3(1, 2, 3), s.Origin);
        Assert.Equal(new Vector3(90, 0, 0), s.Angles);
        Assert.Equal((1, 2, 3, 4, 5, 6, 7, 8), (s.Flags, s.ModelIndex, s.Frame, s.Skin, s.Effects, s.Alpha, s.Scale, s.Colormap));
        Assert.Equal((9, 10, 11, 14, 15, 16, 17, 18), (s.TagEntity, s.TagIndex, s.Light0, s.Light3, s.LightStyle, s.LightPFlags, s.GlowSize, s.GlowColor));
        Assert.Equal((19, 21, 22, 24, 26), (s.ColorMod0, s.ColorMod2, s.GlowMod0, s.GlowMod2, s.TrailEffectNum));
        Assert.Equal(25, s.Blend0.Frame);
        Assert.Equal(0.5f, s.Blend0.StartAge);
        Assert.Equal(1f, s.Blend0.Lerp);
        Assert.Equal(1002, table.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Complex_Animation_Blends(int type)
    {
        int count = type + 1;
        var w = Frame();
        w.WriteShort(1);
        Bits(w, DpProtocol.E5ComplexAnimation);
        w.WriteByte(type);
        for (int i = 0; i < count; i++) w.WriteShort(10 + i);          // frames
        for (int i = 0; i < count; i++) w.WriteShort(1000 * (i + 1));  // ages in ms
        for (int i = 0; i < count; i++) w.WriteByte(255 - 51 * i);     // weights
        EntityState s = Read(w).table.Current(1);
        DpFrameGroupBlend[] blends = { s.Blend0, s.Blend1, s.Blend2, s.Blend3 };
        for (int i = 0; i < 4; i++)
        {
            if (i < count)
            {
                Assert.Equal(10 + i, blends[i].Frame);
                Assert.Equal(i + 1f, blends[i].StartAge, 3);
                Assert.Equal((255 - 51 * i) / 255f, blends[i].Lerp, 5);
            }
            else
                Assert.Equal((0, 0f, 0f), (blends[i].Frame, blends[i].StartAge, blends[i].Lerp));
        }
    }

    [Fact]
    public void Complex_Animation_Skeleton_And_Unknown_Type()
    {
        var w = Frame();
        w.WriteShort(1);
        Bits(w, DpProtocol.E5ComplexAnimation);
        w.WriteByte(4);
        w.WriteShort(77);  // modelindex
        w.WriteByte(2);    // bones
        for (int i = 0; i < 14; i++) w.WriteShort(i - 5);
        EntityState s = Read(w).table.Current(1);
        Assert.Equal(77, s.SkeletonModelIndex);
        Assert.Equal(14, s.SkeletonPose7s!.Length);
        Assert.Equal(-5, s.SkeletonPose7s[0]);
        Assert.Equal(8, s.SkeletonPose7s[13]);

        w = Frame();
        w.WriteShort(1);
        Bits(w, DpProtocol.E5ComplexAnimation);
        w.WriteByte(9);
        w.WriteShort(0x8000);
        var r = new DpMessageReader(w.ToArray());
        Assert.False(new DpEntityTable().ReadFrame(r, 0, out _, out string? error));
        Assert.Contains("unknown type 9", error);
    }

    [Fact]
    public void Skeleton_Claiming_More_Bones_Than_The_Message_Holds_Is_A_Short_Read_Not_An_Allocation()
    {
        var w = Frame();
        w.WriteShort(1);
        Bits(w, DpProtocol.E5ComplexAnimation);
        w.WriteByte(4);
        w.WriteShort(1);
        w.WriteByte(255); // 255 bones = 3570 bytes that are not there
        w.WriteShort(0);
        var r = new DpMessageReader(w.ToArray());
        var table = new DpEntityTable();
        Assert.True(table.ReadFrame(r, 0, out _, out _));
        Assert.True(r.BadRead);
        Assert.Null(table.Current(1).SkeletonPose7s);
    }

    [Fact]
    public void Truncated_Frame_Sets_BadRead_And_Does_Not_Throw()
    {
        var full = Frame();
        full.WriteShort(1);
        Bits(full, DpProtocol.E5FullUpdate | DpProtocol.E5Origin | DpProtocol.E5Origin32 | DpProtocol.E5Light);
        full.WriteFloat(1); full.WriteFloat(2); full.WriteFloat(3);
        for (int i = 0; i < 10; i++) full.WriteByte(i);
        full.WriteShort(0x8000);
        byte[] bytes = full.ToArray();
        for (int cut = 0; cut < bytes.Length; cut++)
        {
            var r = new DpMessageReader(bytes, 0, cut);
            var table = new DpEntityTable();
            table.ReadFrame(r, 0, out _, out _);
            Assert.True(r.BadRead, $"cut at {cut}");
            Assert.True(r.Position <= cut);
        }
    }

    [Fact]
    public void Highest_Entity_Number_Grows_The_Table_To_MaxEdicts()
    {
        var w = Frame();
        w.WriteShort(0x7FFF);
        Bits(w, DpProtocol.E5FullUpdate | DpProtocol.E5Skin);
        w.WriteByte(9);
        var (table, _, _) = Read(w);
        Assert.Equal(9, table.Current(0x7FFF).Skin);
        Assert.Equal(DpProtocol.MaxEdicts, table.Count);
        Assert.Equal(255, table.Current(0x7FFE).Alpha); // new slots are default states, not zeroes
    }

    [Fact]
    public void Baseline_Becomes_Current_And_Clear_Wipes_Everything()
    {
        var table = new DpEntityTable();
        EntityState b = EntityState.Default;
        b.ModelIndex = 12;
        b.Active = DpProtocol.ActiveNetwork;
        table.SetBaseline(700, b);
        Assert.Equal(12, table.Baseline(700).ModelIndex);
        Assert.Equal(12, table.Current(700).ModelIndex);
        Assert.Equal(12, table.Previous(700).ModelIndex);
        table.SetBaseline(DpProtocol.MaxEdicts, b); // ignored
        table.SetBaseline(-1, b);
        table.Clear();
        Assert.Equal(0, table.Current(700).ModelIndex);
        Assert.Equal(255, table.Baseline(700).Alpha);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void Acks_Cover_Frames_Received_Since_The_Last_Few_Input_Packets()
    {
        var table = new DpEntityTable();
        void Receive(int frame, uint moveSequence)
        {
            var w = Frame(frame);
            w.WriteShort(0x8000);
            Assert.True(table.ReadFrame(new DpMessageReader(w.ToArray()), moveSequence, out _, out _));
        }
        Receive(500, 10);
        Receive(501, 11);
        Receive(502, 12);
        Receive(503, 12);

        var acks = new List<int>();
        table.CollectAcks(sequence: 12, repeat: 1, acks); // oldsequence = 11
        Assert.Equal(new[] { 501, 502, 503 }, acks);
        acks.Clear();
        table.CollectAcks(sequence: 13, repeat: 1, acks); // oldsequence = 12
        Assert.Equal(new[] { 502, 503 }, acks);
        acks.Clear();
        table.CollectAcks(sequence: 13, repeat: 3, acks); // oldsequence = 10
        Assert.Equal(new[] { 500, 501, 502, 503 }, acks);
        acks.Clear();
        table.CollectAcks(sequence: 20, repeat: 2, acks);
        Assert.Empty(acks);
        // sequence at or below the repeat clamps oldsequence to 1, never to 0: empty ring slots (send 0) are not acked
        table.CollectAcks(sequence: 1, repeat: 2, acks);
        Assert.Equal(new[] { 500, 501, 502, 503 }, acks);
    }

    [Fact]
    public void Ack_Ring_Holds_The_Last_32_Frames()
    {
        var table = new DpEntityTable();
        for (int i = 0; i < 40; i++)
        {
            var w = Frame(1000 + i);
            w.WriteShort(0x8000);
            table.ReadFrame(new DpMessageReader(w.ToArray()), 5, out _, out _);
        }
        var acks = new List<int>();
        table.CollectAcks(5, 1, acks);
        Assert.Equal(32, acks.Count);
        Assert.Equal(1008, acks[0]);  // oldest first
        Assert.Equal(1039, acks[^1]);
    }
}
