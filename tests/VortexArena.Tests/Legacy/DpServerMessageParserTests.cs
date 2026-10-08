using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using VortexArena.Legacy.Protocol;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>Writes each callback as one line of text, so a test can assert on exactly what was decoded.</summary>
internal sealed class RecordingHandler : IDpClientHandler
{
    public readonly List<string> Log = new();
    public DpServerInfo? ServerInfo;
    public EntityState LastState;
    public DpClientData LastClientData;
    public DpTempEntity LastTempEntity;
    public byte[] LastDownload = Array.Empty<byte>();

    /// <summary>How svc_temp_entity is answered: bytes to consume, or null for "not mine".</summary>
    public Func<DpMessageReader, DpPayloadResult>? TempEntity;
    /// <summary>How a csqc entity update is answered.</summary>
    public Func<int, DpMessageReader, DpPayloadResult>? CsqcUpdate;

    private static string F(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => $"({F(v.X)} {F(v.Y)} {F(v.Z)})";
    private void Add(string s) => Log.Add(s);

    public void OnNop() => Add("nop");
    public void OnDisconnect() => Add("disconnect");
    public void OnUpdateStat(int index, int value) => Add($"stat {index}={value}");
    public void OnVersion(int protocol) => Add($"version {protocol}");
    public void OnSetView(int entity) => Add($"setview {entity}");
    public void OnSound(in DpSound s) => Add($"sound ent={s.Entity} ch={s.Channel} snd={s.SoundIndex} vol={s.Volume} att={F(s.Attenuation)} speed={F(s.Speed)} at={V(s.Origin)}");
    public void OnTime(float time) => Add($"time {F(time)}");
    public void OnPrint(string text) => Add($"print {text}");
    public void OnStuffText(string text) => Add($"stufftext {text}");
    public void OnSetAngle(Vector3 angles) => Add($"setangle {V(angles)}");
    public void OnServerInfo(DpServerInfo info) { ServerInfo = info; Add($"serverinfo {info.Protocol} {info.MaxClients} {info.GameType} \"{info.WorldMessage}\" models={info.Models.Count - 1} sounds={info.Sounds.Count - 1}"); }
    public void OnLightStyle(int style, string map) => Add($"lightstyle {style} {map}");
    public void OnUpdateName(int client, string name) => Add($"name {client} {name}");
    public void OnUpdateFrags(int client, int frags) => Add($"frags {client} {frags}");
    public void OnClientData(in DpClientData d) { LastClientData = d; Add($"clientdata bits={d.Bits:X}"); }
    public void OnStopSound(int entity, int channel) => Add($"stopsound {entity} {channel}");
    public void OnUpdateColors(int client, int colors) => Add($"colors {client} {colors}");
    public void OnParticle(in DpParticle p) => Add($"particle at={V(p.Origin)} dir={V(p.Direction)} count={p.Count} color={p.Color}");
    public void OnDamage(int armor, int blood, Vector3 from) => Add($"damage {armor} {blood} {V(from)}");
    public void OnSpawnStatic(in EntityState s) { LastState = s; Add($"static model={s.ModelIndex} frame={s.Frame} colormap={s.Colormap} skin={s.Skin} at={V(s.Origin)} ang={V(s.Angles)}"); }
    public void OnSpawnBaseline(int entity, in EntityState s) { LastState = s; Add($"baseline {entity} model={s.ModelIndex} frame={s.Frame} colormap={s.Colormap} skin={s.Skin} at={V(s.Origin)} ang={V(s.Angles)}"); }
    public void OnSetPause(bool paused) => Add($"pause {paused}");
    public void OnSignonNum(int stage) => Add($"signon {stage}");
    public void OnCenterPrint(string text) => Add($"centerprint {text}");
    public void OnKilledMonster() => Add("killedmonster");
    public void OnFoundSecret() => Add("foundsecret");
    public void OnSpawnStaticSound(in DpStaticSound s) => Add($"staticsound snd={s.SoundIndex} vol={s.Volume} att={s.Attenuation} at={V(s.Origin)}");
    public void OnIntermission() => Add("intermission");
    public void OnFinale(string text) => Add($"finale {text}");
    public void OnCdTrack(int track, int loopTrack) => Add($"cdtrack {track} {loopTrack}");
    public void OnSellScreen() => Add("sellscreen");
    public void OnCutscene(string text) => Add($"cutscene {text}");
    public void OnShowLmp(string label, string picture, int x, int y) => Add($"showlmp {label} {picture} {x} {y}");
    public void OnHideLmp(string label) => Add($"hidelmp {label}");
    public void OnSkybox(string name) => Add($"skybox {name}");
    public void OnDownloadData(int start, ReadOnlySpan<byte> data) { LastDownload = data.ToArray(); Add($"download {start} {data.Length}"); }
    public void OnEffect(in DpEffect e) => Add($"effect model={e.ModelIndex} start={e.StartFrame} count={e.FrameCount} rate={e.FrameRate} at={V(e.Origin)}");
    public void OnPrecache(int index, bool isSound, string name) => Add($"precache {(isSound ? "sound" : "model")} {index} {name}");
    public void OnEntityFrame(in DpEntityFrame frame, DpEntityTable entities) => Add($"entities frame={frame.FrameNumber} move={frame.ServerMoveSequence} changed=[{string.Join(",", frame.Changed)}]");
    public void OnTrailParticles(in DpTrailParticles t) => Add($"trail ent={t.Entity} fx={t.EffectIndex} {V(t.Start)}->{V(t.End)}");
    public void OnPointParticles(in DpPointParticles p) => Add($"point fx={p.EffectIndex} at={V(p.Origin)} vel={V(p.Velocity)} count={p.Count}");
    public DpPayloadResult OnTempEntity(DpMessageReader reader) => TempEntity?.Invoke(reader) ?? DpPayloadResult.NotHandled;
    public void OnEngineTempEntity(in DpTempEntity te) { LastTempEntity = te; Add($"te {te.Type} at={V(te.Origin)}"); }
    public DpPayloadResult OnCsqcEntityUpdate(int entity, DpMessageReader reader) { Add($"csqc update {entity}"); return CsqcUpdate?.Invoke(entity, reader) ?? DpPayloadResult.Abort; }
    public void OnCsqcEntityRemove(int entity) => Add($"csqc remove {entity}");
}

/// <summary>Every DP7 server message (cl_parse.c CL_ParseServerMessage), from hand-built bytes.</summary>
public class DpServerMessageParserTests
{
    private static (RecordingHandler h, DpParseResult result, DpServerMessageParser parser) Parse(Action<DpMessageWriter> build, RecordingHandler? handler = null, DpServerMessageParser? parser = null)
    {
        var w = new DpMessageWriter();
        build(w);
        handler ??= new RecordingHandler();
        parser ??= new DpServerMessageParser(handler);
        return (handler, parser.Parse(w.ToArray()), parser);
    }

    private static string One(Action<DpMessageWriter> build)
    {
        var (h, result, _) = Parse(build);
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(1, result.Commands);
        return Assert.Single(h.Log);
    }

    internal static void WriteServerInfo(DpMessageWriter w, int protocol = 3504, int maxClients = 16, string[]? models = null, string[]? sounds = null)
    {
        w.WriteByte((int)Svc.ServerInfo);
        w.WriteLong(protocol);
        w.WriteByte(maxClients);
        w.WriteByte(1);
        w.WriteString("Test Map");
        foreach (string m in models ?? new[] { "maps/test.bsp", "*1", "models/player/erebus.iqm" })
            w.WriteString(m);
        w.WriteByte(0);
        foreach (string s in sounds ?? new[] { "misc/null.wav", "weapons/fire.wav" })
            w.WriteString(s);
        w.WriteByte(0);
    }

    // ---------------------------------------------------------------- simple messages

    [Fact]
    public void Empty_Message_Is_Complete()
    {
        var (h, result, _) = Parse(_ => { });
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(0, result.Commands);
        Assert.Empty(h.Log);
    }

    [Fact] public void Nop() => Assert.Equal("nop", One(w => w.WriteByte(1)));
    [Fact] public void Version() => Assert.Equal("version 3504", One(w => { w.WriteByte(4); w.WriteLong(3504); }));
    [Fact] public void SetView() => Assert.Equal("setview 513", One(w => { w.WriteByte(5); w.WriteShort(513); }));
    [Fact] public void Time() => Assert.Equal("time 12.5", One(w => { w.WriteByte(7); w.WriteFloat(12.5f); }));
    [Fact] public void Print() => Assert.Equal("print hello\n", One(w => { w.WriteByte(8); w.WriteString("hello\n"); }));
    [Fact] public void StuffText() => Assert.Equal("stufftext cmd x\n", One(w => { w.WriteByte(9); w.WriteString("cmd x\n"); }));
    [Fact] public void LightStyle() => Assert.Equal("lightstyle 63 mmamam", One(w => { w.WriteByte(12); w.WriteByte(63); w.WriteString("mmamam"); }));
    [Fact] public void StopSound() => Assert.Equal("stopsound 1000 5", One(w => { w.WriteByte(16); w.WriteShort((1000 << 3) | 5); }));
    [Fact] public void SetPause() => Assert.Equal("pause True", One(w => { w.WriteByte(24); w.WriteByte(1); }));
    [Fact] public void SignonNum() => Assert.Equal("signon 2", One(w => { w.WriteByte(25); w.WriteByte(2); }));
    [Fact] public void CenterPrint() => Assert.Equal("centerprint hi", One(w => { w.WriteByte(26); w.WriteString("hi"); }));
    [Fact] public void KilledMonster() => Assert.Equal("killedmonster", One(w => w.WriteByte(27)));
    [Fact] public void FoundSecret() => Assert.Equal("foundsecret", One(w => w.WriteByte(28)));
    [Fact] public void Intermission() => Assert.Equal("intermission", One(w => w.WriteByte(30)));
    [Fact] public void Finale() => Assert.Equal("finale the end", One(w => { w.WriteByte(31); w.WriteString("the end"); }));
    [Fact] public void CdTrack() => Assert.Equal("cdtrack 4 5", One(w => { w.WriteByte(32); w.WriteByte(4); w.WriteByte(5); }));
    [Fact] public void SellScreen() => Assert.Equal("sellscreen", One(w => w.WriteByte(33)));
    [Fact] public void Cutscene() => Assert.Equal("cutscene scene", One(w => { w.WriteByte(34); w.WriteString("scene"); }));
    [Fact] public void ShowLmp() => Assert.Equal("showlmp slot gfx/pic 100 -20", One(w => { w.WriteByte(35); w.WriteString("slot"); w.WriteString("gfx/pic"); w.WriteShort(100); w.WriteShort(-20); }));
    [Fact] public void HideLmp() => Assert.Equal("hidelmp slot", One(w => { w.WriteByte(36); w.WriteString("slot"); }));
    [Fact] public void Skybox() => Assert.Equal("skybox env/sky", One(w => { w.WriteByte(37); w.WriteString("env/sky"); }));

    [Fact]
    public void UpdateStat_Long_And_UByte_Forms()
    {
        Assert.Equal("stat 255=-123456", One(w => { w.WriteByte(3); w.WriteByte(255); w.WriteLong(-123456); }));
        Assert.Equal("stat 7=200", One(w => { w.WriteByte(51); w.WriteByte(7); w.WriteByte(200); }));
    }

    [Fact]
    public void SetAngle_Is_Three_16_Bit_Angles()
    {
        Assert.Equal("setangle (90 -45 0)", One(w => { w.WriteByte(10); w.WriteAngle16i(90); w.WriteAngle16i(-45); w.WriteAngle16i(0); }));
    }

    [Fact]
    public void Disconnect_Stops_The_Message()
    {
        var (h, result, _) = Parse(w => { w.WriteByte(1); w.WriteByte(2); w.WriteByte(1); w.WriteByte(200); });
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(new[] { "nop", "disconnect" }, h.Log);
    }

    [Fact]
    public void Scoreboard_Updates_Are_Bounded_By_MaxClients()
    {
        var handler = new RecordingHandler();
        var parser = new DpServerMessageParser(handler);
        var (_, result, _) = Parse(w =>
        {
            WriteServerInfo(w, maxClients: 8);
            w.WriteByte(13); w.WriteByte(7); w.WriteString("^1Player");
            w.WriteByte(14); w.WriteByte(0); w.WriteShort(-3);
            w.WriteByte(17); w.WriteByte(2); w.WriteByte(0x4D);
        }, handler, parser);
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(new[] { "name 7 ^1Player", "frags 0 -3", "colors 2 77" }, handler.Log.Skip(1));
        Assert.Equal(8, parser.MaxClients);

        foreach (int svc in new[] { 13, 14, 17 })
        {
            var (h2, r2, _) = Parse(w =>
            {
                w.WriteByte(svc); w.WriteByte(8);
                if (svc == 13) w.WriteString("x"); else if (svc == 14) w.WriteShort(1); else w.WriteByte(1);
            }, null, parser);
            Assert.Equal(DpParseStatus.Error, r2.Status);
            Assert.Equal(svc, r2.Svc);
            Assert.Contains("maxclients", r2.Message);
        }
    }

    // ---------------------------------------------------------------- serverinfo

    [Fact]
    public void ServerInfo_Decodes_Lists_With_Index_Zero_Reserved()
    {
        var (h, result, parser) = Parse(w => WriteServerInfo(w));
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal("serverinfo 3504 16 1 \"Test Map\" models=3 sounds=2", Assert.Single(h.Log));
        DpServerInfo info = h.ServerInfo!;
        Assert.Equal(new[] { "", "maps/test.bsp", "*1", "models/player/erebus.iqm" }, info.Models);
        Assert.Equal(new[] { "", "misc/null.wav", "weapons/fire.wav" }, info.Sounds);
        Assert.Equal("maps/test.bsp", info.WorldModel);
        Assert.Equal(3504, parser.Protocol);
        Assert.Equal(16, parser.MaxClients);
    }

    [Fact]
    public void ServerInfo_With_Empty_Lists()
    {
        var (h, result, _) = Parse(w => WriteServerInfo(w, models: Array.Empty<string>(), sounds: Array.Empty<string>()));
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal("", h.ServerInfo!.WorldModel);
        Assert.Single(h.ServerInfo.Models);
    }

    [Theory]
    [InlineData(15)]      // PROTOCOL_QUAKE
    [InlineData(3503)]    // DP6
    [InlineData(3505)]    // DP8
    [InlineData(-1)]
    public void ServerInfo_For_Another_Protocol_Is_An_Error(int protocol)
    {
        var (h, result, _) = Parse(w => WriteServerInfo(w, protocol: protocol));
        Assert.Equal(DpParseStatus.Error, result.Status);
        Assert.Equal((int)Svc.ServerInfo, result.Svc);
        Assert.Equal(0, result.Offset);
        Assert.Contains("not DP7", result.Message);
        Assert.Empty(h.Log);
    }

    [Theory]
    [InlineData(0)]
    public void ServerInfo_Bad_MaxClients_Is_An_Error(int maxClients)
    {
        var (_, result, _) = Parse(w => WriteServerInfo(w, maxClients: maxClients));
        Assert.Equal(DpParseStatus.Error, result.Status);
        Assert.Contains("maxclients", result.Message);
    }

    [Fact]
    public void ServerInfo_Precache_Name_Of_MaxQPath_Characters_Is_An_Error()
    {
        var ok = Parse(w => WriteServerInfo(w, models: new[] { new string('m', 127) }));
        Assert.Equal(DpParseStatus.Complete, ok.result.Status);
        var bad = Parse(w => WriteServerInfo(w, models: new[] { new string('m', 128) }));
        Assert.Equal(DpParseStatus.Error, bad.result.Status);
        Assert.Contains("128 characters", bad.result.Message);
        var badSound = Parse(w => WriteServerInfo(w, sounds: new[] { new string('s', 500) }));
        Assert.Equal(DpParseStatus.Error, badSound.result.Status);
    }

    [Fact]
    public void ServerInfo_With_More_Than_Max_Models_Or_Sounds_Is_An_Error()
    {
        string[] names(int n) => Enumerable.Range(0, n).Select(i => "m" + i).ToArray();
        // index 0 is reserved, so MAX_MODELS - 1 names fill the table exactly
        Assert.Equal(DpParseStatus.Complete, Parse(w => WriteServerInfo(w, models: names(DpProtocol.MaxModels - 1))).result.Status);
        var tooMany = Parse(w => WriteServerInfo(w, models: names(DpProtocol.MaxModels)));
        Assert.Equal(DpParseStatus.Error, tooMany.result.Status);
        Assert.Contains("too many model", tooMany.result.Message);
        var tooManySounds = Parse(w => WriteServerInfo(w, sounds: names(DpProtocol.MaxSounds)));
        Assert.Equal(DpParseStatus.Error, tooManySounds.result.Status);
        Assert.Contains("too many sound", tooManySounds.result.Message);
    }

    [Fact]
    public void ServerInfo_Clears_The_Entity_Table()
    {
        var handler = new RecordingHandler();
        var parser = new DpServerMessageParser(handler);
        Parse(w =>
        {
            w.WriteByte(22); w.WriteShort(5);
            w.WriteByte(9); w.WriteByte(0); w.WriteByte(0); w.WriteByte(0);
            for (int i = 0; i < 3; i++) { w.WriteFloat(0); w.WriteShort(0); }
        }, handler, parser);
        Assert.Equal(9, parser.Entities.Current(5).ModelIndex);
        Parse(w => WriteServerInfo(w), handler, parser);
        Assert.Equal(0, parser.Entities.Current(5).ModelIndex);
    }

    // ---------------------------------------------------------------- baselines, statics

    private static void WriteBaselineBody(DpMessageWriter w, bool large, int model, int frame)
    {
        if (large) { w.WriteShort(model); w.WriteShort(frame); }
        else { w.WriteByte(model); w.WriteByte(frame); }
        w.WriteByte(3);  // colormap
        w.WriteByte(4);  // skin
        w.WriteFloat(10); w.WriteAngle16i(90);   // origin and angle are interleaved per axis
        w.WriteFloat(20); w.WriteAngle16i(-90);
        w.WriteFloat(30); w.WriteAngle16i(45);
    }

    [Fact]
    public void SpawnBaseline_Small_And_Large()
    {
        var a = Parse(w => { w.WriteByte(22); w.WriteShort(300); WriteBaselineBody(w, false, 200, 7); });
        Assert.Equal("baseline 300 model=200 frame=7 colormap=3 skin=4 at=(10 20 30) ang=(90 -90 45)", Assert.Single(a.h.Log));
        Assert.True(a.h.LastState.IsActive);
        Assert.Equal(255, a.h.LastState.Alpha);
        Assert.Equal(300, a.h.LastState.Number);
        Assert.Equal(200, a.parser.Entities.Baseline(300).ModelIndex);

        var b = Parse(w => { w.WriteByte(55); w.WriteShort(32767); WriteBaselineBody(w, true, 5000, 60000); });
        Assert.Equal("baseline 32767 model=5000 frame=60000 colormap=3 skin=4 at=(10 20 30) ang=(90 -90 45)", Assert.Single(b.h.Log));
    }

    [Fact]
    public void SpawnBaseline_Entity_At_MaxEdicts_Is_An_Error()
    {
        var (h, result, _) = Parse(w => { w.WriteByte(22); w.WriteShort(32768); WriteBaselineBody(w, false, 1, 1); });
        Assert.Equal(DpParseStatus.Error, result.Status);
        Assert.Contains("invalid entity number 32768", result.Message);
        Assert.Empty(h.Log);
    }

    [Fact]
    public void SpawnStatic_Small_Large_And_Modelless()
    {
        Assert.Equal("static model=9 frame=1 colormap=3 skin=4 at=(10 20 30) ang=(90 -90 45)", One(w => { w.WriteByte(20); WriteBaselineBody(w, false, 9, 1); }));
        Assert.Equal("static model=900 frame=300 colormap=3 skin=4 at=(10 20 30) ang=(90 -90 45)", One(w => { w.WriteByte(56); WriteBaselineBody(w, true, 900, 300); }));
        // a static entity without a model is dropped, but its bytes are consumed
        var (h, result, _) = Parse(w => { w.WriteByte(20); WriteBaselineBody(w, false, 0, 1); w.WriteByte(1); });
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(new[] { "nop" }, h.Log);
        // a model index beyond the table is an error (CL_ValidateState)
        var bad = Parse(w => { w.WriteByte(56); WriteBaselineBody(w, true, 8192, 0); });
        Assert.Equal(DpParseStatus.Error, bad.result.Status);
    }

    [Fact]
    public void StaticSound_Small_Large_And_Out_Of_Range()
    {
        Assert.Equal("staticsound snd=200 vol=255 att=64 at=(1 2 3)", One(w => { w.WriteByte(29); w.WriteVector(new Vector3(1, 2, 3)); w.WriteByte(200); w.WriteByte(255); w.WriteByte(64); }));
        Assert.Equal("staticsound snd=4095 vol=128 att=32 at=(1 2 3)", One(w => { w.WriteByte(59); w.WriteVector(new Vector3(1, 2, 3)); w.WriteShort(4095); w.WriteByte(128); w.WriteByte(32); }));
        var bad = Parse(w => { w.WriteByte(59); w.WriteVector(default); w.WriteShort(4096); w.WriteByte(1); w.WriteByte(1); });
        Assert.Equal(DpParseStatus.Error, bad.result.Status);
        Assert.Equal(59, bad.result.Svc);
    }

    // ---------------------------------------------------------------- clientdata, sound

    [Fact]
    public void ClientData_Minimal_Is_Just_The_Bit_Word()
    {
        var (h, result, _) = Parse(w => { w.WriteByte(15); w.WriteShort(DpProtocol.SuOnGround | DpProtocol.SuInWater); });
        Assert.Equal(DpParseStatus.Complete, result.Status);
        DpClientData d = h.LastClientData;
        Assert.True(d.OnGround);
        Assert.True(d.InWater);
        Assert.False(d.HasItems);
        Assert.False(d.HasViewHeight);
        Assert.Equal(255, d.ViewZoom);
        Assert.Equal(Vector3.Zero, d.Velocity);
    }

    [Fact]
    public void ClientData_Every_Field_In_Wire_Order()
    {
        int bits = DpProtocol.SuViewHeight | DpProtocol.SuIdealPitch | (DpProtocol.SuPunch1 * 7) | (DpProtocol.SuVelocity1 * 7)
            | DpProtocol.SuItems | DpProtocol.SuExtend1 | (DpProtocol.SuPunchVec1 * 7) | DpProtocol.SuViewZoom;
        var (h, result, _) = Parse(w =>
        {
            w.WriteByte(15);
            w.WriteShort(bits & 0xFFFF);
            w.WriteByte((bits >> 16) & 0xFF);
            w.WriteChar(-22);                  // viewheight
            w.WriteChar(5);                    // idealpitch
            for (int i = 0; i < 3; i++)
            {
                w.WriteAngle16i(10 * (i + 1)); // punch angle: 16-bit in DP7
                w.WriteFloat(100 + i);         // punch vector: float
                w.WriteFloat(-300 - i);        // velocity: float
            }
            w.WriteLong(0x12345678);           // items
            w.WriteShort(128);                 // viewzoom
            w.WriteByte(1);                    // a following nop proves nothing extra was consumed
        });
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(2, result.Commands);
        DpClientData d = h.LastClientData;
        Assert.Equal(-22, d.ViewHeight);
        Assert.Equal(5, d.IdealPitch);
        Assert.Equal(10f, d.PunchAngle.X, 2);
        Assert.Equal(30f, d.PunchAngle.Z, 2);
        Assert.Equal(new Vector3(100, 101, 102), d.PunchVector);
        Assert.Equal(new Vector3(-300, -301, -302), d.Velocity);
        Assert.True(d.HasItems);
        Assert.Equal(0x12345678, d.Items);
        Assert.True(d.HasViewZoom);
        Assert.Equal(128, d.ViewZoom);
        Assert.False(d.OnGround);
    }

    [Fact]
    public void ClientData_Second_Extend_Byte_Is_Consumed()
    {
        int bits = DpProtocol.SuExtend1 | DpProtocol.SuExtend2;
        var (h, result, _) = Parse(w =>
        {
            w.WriteByte(15);
            w.WriteShort(bits & 0xFFFF);
            w.WriteByte((bits >> 16) & 0xFF);
            w.WriteByte(0x01); // bits 24-31
            w.WriteByte(1);
        });
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(new[] { $"clientdata bits={bits | 0x01000000:X}", "nop" }, h.Log);
    }

    [Fact]
    public void Sound_Default_Form()
    {
        Assert.Equal("sound ent=100 ch=3 snd=17 vol=255 att=1 speed=1 at=(1 2 3)",
            One(w => { w.WriteByte(6); w.WriteByte(0); w.WriteShort((100 << 3) | 3); w.WriteByte(17); w.WriteVector(new Vector3(1, 2, 3)); }));
    }

    [Fact]
    public void Sound_With_Every_Optional_Field()
    {
        int mask = DpProtocol.SndVolume | DpProtocol.SndAttenuation | DpProtocol.SndSpeedUShort4000 | DpProtocol.SndLargeEntity | DpProtocol.SndLargeSound;
        Assert.Equal("sound ent=20000 ch=-1 snd=3000 vol=128 att=0.5 speed=1.5 at=(1 2 3)",
            One(w =>
            {
                w.WriteByte(6); w.WriteByte(mask);
                w.WriteByte(128);       // volume
                w.WriteByte(32);        // attenuation * 64
                w.WriteShort(6000);     // speed * 4000
                w.WriteShort(20000);    // entity
                w.WriteChar(-1);        // channel as a signed byte
                w.WriteShort(3000);     // sound
                w.WriteVector(new Vector3(1, 2, 3));
            }));
    }

    [Fact]
    public void Sound_With_Out_Of_Range_Index_Is_Dropped_But_Parsing_Continues()
    {
        var (h, result, _) = Parse(w =>
        {
            w.WriteByte(6); w.WriteByte(DpProtocol.SndLargeSound); w.WriteShort(8); w.WriteShort(4096); w.WriteVector(default);
            w.WriteByte(6); w.WriteByte(DpProtocol.SndLargeEntity); w.WriteShort(40000); w.WriteChar(0); w.WriteByte(1); w.WriteVector(default);
            w.WriteByte(1);
        });
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(new[] { "nop" }, h.Log);
    }

    // ---------------------------------------------------------------- effects

    [Fact]
    public void Particle_Scales_Direction_And_Expands_255()
    {
        Assert.Equal("particle at=(1 2 3) dir=(1 -1 0.5) count=20 color=73",
            One(w => { w.WriteByte(18); w.WriteVector(new Vector3(1, 2, 3)); w.WriteChar(16); w.WriteChar(-16); w.WriteChar(8); w.WriteByte(20); w.WriteByte(73); }));
        Assert.Equal("particle at=(0 0 0) dir=(0 0 0) count=1024 color=0",
            One(w => { w.WriteByte(18); w.WriteVector(default); w.WriteChar(0); w.WriteChar(0); w.WriteChar(0); w.WriteByte(255); w.WriteByte(0); }));
    }

    [Fact]
    public void Damage() => Assert.Equal("damage 10 20 (4 5 6)", One(w => { w.WriteByte(19); w.WriteByte(10); w.WriteByte(20); w.WriteVector(new Vector3(4, 5, 6)); }));

    [Fact]
    public void Effect_And_Effect2()
    {
        Assert.Equal("effect model=200 start=3 count=8 rate=10 at=(1 2 3)",
            One(w => { w.WriteByte(52); w.WriteVector(new Vector3(1, 2, 3)); w.WriteByte(200); w.WriteByte(3); w.WriteByte(8); w.WriteByte(10); }));
        Assert.Equal("effect model=2000 start=300 count=8 rate=10 at=(1 2 3)",
            One(w => { w.WriteByte(53); w.WriteVector(new Vector3(1, 2, 3)); w.WriteShort(2000); w.WriteShort(300); w.WriteByte(8); w.WriteByte(10); }));
    }

    [Fact]
    public void Trail_And_Point_Particles()
    {
        Assert.Equal("trail ent=77 fx=12 (1 2 3)->(4 5 6)",
            One(w => { w.WriteByte(60); w.WriteShort(77); w.WriteShort(12); w.WriteVector(new Vector3(1, 2, 3)); w.WriteVector(new Vector3(4, 5, 6)); }));
        // an entity number past MAX_EDICTS becomes 0, as in CL_ParseTrailParticles
        Assert.Equal("trail ent=0 fx=12 (0 0 0)->(0 0 0)",
            One(w => { w.WriteByte(60); w.WriteShort(40000); w.WriteShort(12); w.WriteVector(default); w.WriteVector(default); }));
        Assert.Equal("point fx=300 at=(1 2 3) vel=(4 5 6) count=50000",
            One(w => { w.WriteByte(61); w.WriteShort(300); w.WriteVector(new Vector3(1, 2, 3)); w.WriteVector(new Vector3(4, 5, 6)); w.WriteShort(50000); }));
        Assert.Equal("point fx=300 at=(1 2 3) vel=(0 0 0) count=1",
            One(w => { w.WriteByte(62); w.WriteShort(300); w.WriteVector(new Vector3(1, 2, 3)); }));
    }

    [Fact]
    public void Precache_Model_Sound_And_Out_Of_Range()
    {
        Assert.Equal("precache model 300 models/x.md3", One(w => { w.WriteByte(54); w.WriteShort(300); w.WriteString("models/x.md3"); }));
        Assert.Equal("precache sound 5 misc/x.wav", One(w => { w.WriteByte(54); w.WriteShort(32768 + 5); w.WriteString("misc/x.wav"); }));
        foreach (int index in new[] { 0, DpProtocol.MaxModels, 32768, 32768 + DpProtocol.MaxSounds })
        {
            var (h, result, _) = Parse(w => { w.WriteByte(54); w.WriteShort(index); w.WriteString("n"); w.WriteByte(1); });
            Assert.Equal(DpParseStatus.Complete, result.Status);
            Assert.Equal(new[] { "nop" }, h.Log); // logged and ignored by DarkPlaces too
        }
    }

    [Fact]
    public void DownloadData_Including_The_Empty_End_Block()
    {
        var a = Parse(w => { w.WriteByte(50); w.WriteLong(2800); w.WriteShort(3); w.WriteBytes(new byte[] { 7, 8, 9 }); w.WriteByte(1); });
        Assert.Equal(new[] { "download 2800 3", "nop" }, a.h.Log);
        Assert.Equal(new byte[] { 7, 8, 9 }, a.h.LastDownload);
        Assert.Equal("download 4125849 0", One(w => { w.WriteByte(50); w.WriteLong(4125849); w.WriteShort(0); }));
        // a block that claims more bytes than the message has
        var bad = Parse(w => { w.WriteByte(50); w.WriteLong(0); w.WriteShort(60000); w.WriteBytes(new byte[10]); });
        Assert.Equal(DpParseStatus.Error, bad.result.Status);
        Assert.Empty(bad.h.Log);
    }

    // ---------------------------------------------------------------- entities

    [Fact]
    public void Entities_Updates_The_Table_And_Reports_The_Frame()
    {
        var (h, result, parser) = Parse(w =>
        {
            w.WriteByte(57);
            w.WriteLong(900); w.WriteLong(41);
            w.WriteShort(12); w.WriteByte((int)(DpProtocol.E5FullUpdate | DpProtocol.E5Model)); w.WriteByte(33);
            w.WriteShort(13 | 0x8000);
            w.WriteShort(0x8000);
            w.WriteByte(1);
        });
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(new[] { "entities frame=900 move=41 changed=[12,13]", "nop" }, h.Log);
        Assert.Equal(33, parser.Entities.Current(12).ModelIndex);
    }

    [Fact]
    public void Entities_With_Bad_Complex_Animation_Is_An_Error()
    {
        var (_, result, _) = Parse(w =>
        {
            w.WriteByte(57); w.WriteLong(1); w.WriteLong(1);
            w.WriteShort(1);
            w.WriteByte(0x80); w.WriteByte(0x80); w.WriteByte(0x80); w.WriteByte(0x02); // only E5_COMPLEXANIMATION
            w.WriteByte(200);
            w.WriteShort(0x8000);
        });
        Assert.Equal(DpParseStatus.Error, result.Status);
        Assert.Equal(57, result.Svc);
        Assert.Contains("unknown type 200", result.Message);
    }

    // ---------------------------------------------------------------- QuakeC-defined payloads

    [Fact]
    public void CsqcEntities_Default_Handler_Aborts_At_The_First_Update()
    {
        var (h, result, _) = Parse(w =>
        {
            w.WriteByte(1);
            w.WriteByte(58);
            w.WriteShort(40 | 0x8000);   // remove: no payload, always decodable
            w.WriteShort(41);            // update: QuakeC-defined from here on
            w.WriteBytes(new byte[] { 1, 2, 3, 4, 5 });
            w.WriteShort(0);
        });
        Assert.Equal(DpParseStatus.Aborted, result.Status);
        Assert.Equal(58, result.Svc);
        Assert.Equal(1, result.Offset);
        Assert.Equal(1, result.Commands);
        Assert.Equal(new[] { "nop", "csqc remove 40", "csqc update 41" }, h.Log);
        Assert.False(result.IsError);
    }

    [Fact]
    public void CsqcEntities_Handler_That_Consumes_Its_Payload_Lets_Parsing_Continue()
    {
        var handler = new RecordingHandler
        {
            // "QuakeC" for this test: each update is one byte of length followed by that many bytes
            CsqcUpdate = (_, r) => { int n = r.ReadByte(); r.ReadSpan(n); return DpPayloadResult.Consumed; },
        };
        var (h, result, _) = Parse(w =>
        {
            w.WriteByte(58);
            w.WriteShort(41); w.WriteByte(2); w.WriteByte(0xAA); w.WriteByte(0xBB);
            w.WriteShort(0x7FFF); w.WriteByte(0);
            w.WriteShort(42 | 0x8000);
            w.WriteShort(0);
            w.WriteByte(7); w.WriteFloat(3);
        }, handler);
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(new[] { "csqc update 41", "csqc update 32767", "csqc remove 42", "time 3" }, h.Log);
    }

    [Fact]
    public void CsqcEntities_Without_Terminator_Is_A_Short_Message()
    {
        var handler = new RecordingHandler { CsqcUpdate = (_, r) => { r.ReadByte(); return DpPayloadResult.Consumed; } };
        var (_, result, _) = Parse(w => { w.WriteByte(58); w.WriteShort(5); w.WriteByte(1); }, handler);
        Assert.Equal(DpParseStatus.Error, result.Status);
        Assert.Equal(58, result.Svc);
    }

    [Fact]
    public void TempEntity_Falls_Back_To_The_Engine_Decoder_After_Rewinding()
    {
        // The handler peeks at the id, as CSQC_Parse_TempEntity does, and declines.
        var handler = new RecordingHandler { TempEntity = r => { r.ReadByte(); r.ReadByte(); return DpPayloadResult.NotHandled; } };
        var (h, result, _) = Parse(w =>
        {
            w.WriteByte(23); w.WriteByte((int)TempEntityType.Explosion); w.WriteVector(new Vector3(1, 2, 3));
            w.WriteByte(1);
        }, handler);
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(new[] { "te Explosion at=(1 2 3)", "nop" }, h.Log);
    }

    [Fact]
    public void TempEntity_Consumed_By_The_Handler_Skips_The_Engine_Decoder()
    {
        var handler = new RecordingHandler { TempEntity = r => { r.ReadByte(); r.ReadShort(); return DpPayloadResult.Consumed; } };
        var (h, result, _) = Parse(w => { w.WriteByte(23); w.WriteByte(200); w.WriteShort(5); w.WriteByte(1); }, handler);
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(new[] { "nop" }, h.Log);
    }

    [Fact]
    public void TempEntity_Abort_And_Unknown_Engine_Type()
    {
        var aborting = new RecordingHandler { TempEntity = _ => DpPayloadResult.Abort };
        var a = Parse(w => { w.WriteByte(1); w.WriteByte(23); w.WriteByte(90); w.WriteByte(1); }, aborting);
        Assert.Equal(DpParseStatus.Aborted, a.result.Status);
        Assert.Equal((23, 1), (a.result.Svc, a.result.Offset));

        var b = Parse(w => { w.WriteByte(23); w.WriteByte(90); w.WriteByte(1); });
        Assert.Equal(DpParseStatus.Error, b.result.Status);
        Assert.Contains("bad type 90", b.result.Message);

        var empty = Parse(w => w.WriteByte(23));
        Assert.Equal(DpParseStatus.Error, empty.result.Status);
    }

    public static IEnumerable<object[]> EngineTempEntities()
    {
        static object[] Case(TempEntityType type, int payloadBytes) => new object[] { type, payloadBytes };
        foreach (var t in new[] { TempEntityType.Spike, TempEntityType.SuperSpike, TempEntityType.Gunshot, TempEntityType.Explosion,
                     TempEntityType.TarExplosion, TempEntityType.WizSpike, TempEntityType.KnightSpike, TempEntityType.LavaSplash,
                     TempEntityType.Teleport, TempEntityType.GunshotQuad, TempEntityType.SpikeQuad, TempEntityType.SuperSpikeQuad,
                     TempEntityType.ExplosionQuad, TempEntityType.SmallFlash, TempEntityType.PlasmaBurn, TempEntityType.TeiBigExplosion })
            yield return Case(t, 12);
        foreach (var t in new[] { TempEntityType.Lightning1, TempEntityType.Lightning2, TempEntityType.Lightning3, TempEntityType.Beam })
            yield return Case(t, 2 + 24);
        yield return Case(TempEntityType.Explosion2, 12 + 2);
        yield return Case(TempEntityType.Explosion3, 12 + 12);
        yield return Case(TempEntityType.Blood, 12 + 4);
        yield return Case(TempEntityType.Spark, 12 + 4);
        yield return Case(TempEntityType.BloodShower, 24 + 4 + 2);
        yield return Case(TempEntityType.ExplosionRgb, 12 + 3);
        yield return Case(TempEntityType.ParticleCube, 36 + 2 + 1 + 1 + 4);
        yield return Case(TempEntityType.ParticleRain, 36 + 2 + 1);
        yield return Case(TempEntityType.ParticleSnow, 36 + 2 + 1);
        yield return Case(TempEntityType.CustomFlash, 12 + 5);
        yield return Case(TempEntityType.FlameJet, 24 + 1);
        yield return Case(TempEntityType.TeiG3, 36);
        yield return Case(TempEntityType.TeiSmoke, 24 + 1);
        yield return Case(TempEntityType.TeiPlasmaHit, 24 + 1);
    }

    [Theory]
    [MemberData(nameof(EngineTempEntities))]
    public void Engine_TempEntity_Consumes_Exactly_Its_Payload(TempEntityType type, int payloadBytes)
    {
        var (h, result, _) = Parse(w =>
        {
            w.WriteByte(23);
            w.WriteByte((int)type);
            for (int i = 0; i < payloadBytes; i++) w.WriteByte(i + 1);
            w.WriteByte(7); w.WriteFloat(1.5f); // must land exactly on this
        });
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(2, h.Log.Count);
        Assert.StartsWith($"te {type} ", h.Log[0]);
        Assert.Equal("time 1.5", h.Log[1]);

        // and one byte short is an error, not a read past the end
        var shortResult = Parse(w =>
        {
            w.WriteByte(23);
            w.WriteByte((int)type);
            for (int i = 0; i < payloadBytes - 1; i++) w.WriteByte(1);
        });
        Assert.Equal(DpParseStatus.Error, shortResult.result.Status);
    }

    [Fact]
    public void Engine_TempEntity_Field_Values()
    {
        var a = Parse(w =>
        {
            w.WriteByte(23); w.WriteByte((int)TempEntityType.Lightning2);
            w.WriteShort(321); w.WriteVector(new Vector3(1, 2, 3)); w.WriteVector(new Vector3(4, 5, 6));
        });
        Assert.Equal(321, a.h.LastTempEntity.Entity);
        Assert.Equal(new Vector3(4, 5, 6), a.h.LastTempEntity.Origin2);

        var b = Parse(w =>
        {
            w.WriteByte(23); w.WriteByte((int)TempEntityType.Blood);
            w.WriteVector(new Vector3(1, 2, 3)); w.WriteChar(-10); w.WriteChar(20); w.WriteChar(127); w.WriteByte(200);
        });
        Assert.Equal(new Vector3(-10, 20, 127), b.h.LastTempEntity.Direction);
        Assert.Equal(200, b.h.LastTempEntity.Count);

        var c = Parse(w =>
        {
            w.WriteByte(23); w.WriteByte((int)TempEntityType.CustomFlash);
            w.WriteVector(default); w.WriteByte(24); w.WriteByte(127); w.WriteByte(255); w.WriteByte(0); w.WriteByte(255);
        });
        Assert.Equal(200f, c.h.LastTempEntity.Radius);
        Assert.Equal(0.5f, c.h.LastTempEntity.Speed);
        Assert.Equal(2f, c.h.LastTempEntity.Color.X, 4);
        Assert.Equal(0f, c.h.LastTempEntity.Color.Y, 4);
        Assert.Equal(2f, c.h.LastTempEntity.Color.Z, 4);

        var d = Parse(w =>
        {
            w.WriteByte(23); w.WriteByte((int)TempEntityType.Lightning4Neh);
            w.WriteString("progs/bolt.mdl"); w.WriteShort(50000); w.WriteVector(default); w.WriteVector(default);
        });
        Assert.Equal("progs/bolt.mdl", d.h.LastTempEntity.Model);
        Assert.Equal(0, d.h.LastTempEntity.Entity); // out of range becomes "no owner"

        var e = Parse(w =>
        {
            w.WriteByte(23); w.WriteByte((int)TempEntityType.ParticleCube);
            w.WriteVector(new Vector3(1, 1, 1)); w.WriteVector(new Vector3(2, 2, 2)); w.WriteVector(new Vector3(0, 0, -1));
            w.WriteShort(60000); w.WriteByte(12); w.WriteByte(1); w.WriteFloat(3.5f);
        });
        Assert.Equal((60000, 12, 1, 3.5f), (e.h.LastTempEntity.Count, e.h.LastTempEntity.ColorStart, e.h.LastTempEntity.ColorLength, e.h.LastTempEntity.Speed));
    }

    // ---------------------------------------------------------------- errors

    [Theory]
    [InlineData(0)]    // svc_bad
    [InlineData(21)]   // svc_spawnbinary, never implemented
    [InlineData(38)]
    [InlineData(49)]
    [InlineData(63)]
    [InlineData(100)]
    [InlineData(127)]
    public void Unknown_Id_Is_An_Error_Carrying_The_Id_And_Offset(int id)
    {
        var (h, result, _) = Parse(w => { w.WriteByte(1); w.WriteByte(7); w.WriteFloat(1); w.WriteByte(id); w.WriteByte(1); });
        Assert.Equal(DpParseStatus.Error, result.Status);
        Assert.Equal(id, result.Svc);
        Assert.Equal(6, result.Offset);
        Assert.Equal(2, result.Commands);
        Assert.Equal(new[] { "nop", "time 1" }, h.Log);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(255)]
    public void NetQuake_Fast_Update_Is_An_Error_In_DP7(int id)
    {
        var (_, result, _) = Parse(w => { w.WriteByte(id); w.WriteByte(0); });
        Assert.Equal(DpParseStatus.Error, result.Status);
        Assert.Equal(id, result.Svc);
        Assert.Equal(0, result.Offset);
    }

    [Fact]
    public void Version_Other_Than_DP7_And_SetView_Out_Of_Range_Are_Errors()
    {
        Assert.Equal(DpParseStatus.Error, Parse(w => { w.WriteByte(4); w.WriteLong(15); }).result.Status);
        Assert.Equal(DpParseStatus.Error, Parse(w => { w.WriteByte(5); w.WriteShort(32768); }).result.Status);
        Assert.Equal(DpParseStatus.Complete, Parse(w => { w.WriteByte(5); w.WriteShort(32767); }).result.Status);
    }

    /// <summary>Each message cut at every length: always an error at the right command, never a callback with garbage.</summary>
    [Fact]
    public void Every_Message_Truncated_At_Every_Byte_Is_An_Error_Not_A_Callback()
    {
        var builders = new List<Action<DpMessageWriter>>
        {
            w => { w.WriteByte(3); w.WriteByte(1); w.WriteLong(5); },
            w => { w.WriteByte(4); w.WriteLong(3504); },
            w => { w.WriteByte(5); w.WriteShort(1); },
            w => { w.WriteByte(6); w.WriteByte(63); w.WriteByte(1); w.WriteByte(1); w.WriteShort(1); w.WriteShort(1); w.WriteChar(1); w.WriteShort(1); w.WriteVector(default); },
            w => { w.WriteByte(7); w.WriteFloat(1); },
            w => { w.WriteByte(8); w.WriteString("abc"); },
            w => { w.WriteByte(9); w.WriteString("abc"); },
            w => { w.WriteByte(10); w.WriteShort(1); w.WriteShort(1); w.WriteShort(1); },
            w => WriteServerInfo(w),
            w => { w.WriteByte(12); w.WriteByte(1); w.WriteString("m"); },
            w => { w.WriteByte(13); w.WriteByte(1); w.WriteString("n"); },
            w => { w.WriteByte(14); w.WriteByte(1); w.WriteShort(1); },
            w => { w.WriteByte(15); w.WriteShort(0x8200 | 1); w.WriteByte(0x08); w.WriteChar(1); w.WriteLong(1); w.WriteShort(1); },
            w => { w.WriteByte(16); w.WriteShort(1); },
            w => { w.WriteByte(17); w.WriteByte(1); w.WriteByte(1); },
            w => { w.WriteByte(18); w.WriteVector(default); w.WriteByte(1); w.WriteByte(1); w.WriteByte(1); w.WriteByte(1); w.WriteByte(1); },
            w => { w.WriteByte(19); w.WriteByte(1); w.WriteByte(1); w.WriteVector(default); },
            w => { w.WriteByte(20); WriteBaselineBody(w, false, 1, 1); },
            w => { w.WriteByte(22); w.WriteShort(1); WriteBaselineBody(w, false, 1, 1); },
            w => { w.WriteByte(23); w.WriteByte(52); w.WriteVector(default); w.WriteVector(default); w.WriteFloat(1); w.WriteShort(1); },
            w => { w.WriteByte(24); w.WriteByte(1); },
            w => { w.WriteByte(25); w.WriteByte(1); },
            w => { w.WriteByte(26); w.WriteString("c"); },
            w => { w.WriteByte(29); w.WriteVector(default); w.WriteByte(1); w.WriteByte(1); w.WriteByte(1); },
            w => { w.WriteByte(31); w.WriteString("f"); },
            w => { w.WriteByte(32); w.WriteByte(1); w.WriteByte(1); },
            w => { w.WriteByte(34); w.WriteString("c"); },
            w => { w.WriteByte(35); w.WriteString("a"); w.WriteString("b"); w.WriteShort(1); w.WriteShort(1); },
            w => { w.WriteByte(36); w.WriteString("a"); },
            w => { w.WriteByte(37); w.WriteString("a"); },
            w => { w.WriteByte(50); w.WriteLong(0); w.WriteShort(4); w.WriteBytes(new byte[4]); },
            w => { w.WriteByte(51); w.WriteByte(1); w.WriteByte(1); },
            w => { w.WriteByte(52); w.WriteVector(default); w.WriteByte(1); w.WriteByte(1); w.WriteByte(1); w.WriteByte(1); },
            w => { w.WriteByte(53); w.WriteVector(default); w.WriteShort(1); w.WriteShort(1); w.WriteByte(1); w.WriteByte(1); },
            w => { w.WriteByte(54); w.WriteShort(1); w.WriteString("a"); },
            w => { w.WriteByte(55); w.WriteShort(1); WriteBaselineBody(w, true, 1, 1); },
            w => { w.WriteByte(56); WriteBaselineBody(w, true, 1, 1); },
            w => { w.WriteByte(57); w.WriteLong(1); w.WriteLong(1); w.WriteShort(1); w.WriteByte(0x21); w.WriteByte(2); w.WriteShort(0x8000); },
            w => { w.WriteByte(58); w.WriteShort(0x8001); w.WriteShort(0); },
            w => { w.WriteByte(59); w.WriteVector(default); w.WriteShort(1); w.WriteByte(1); w.WriteByte(1); },
            w => { w.WriteByte(60); w.WriteShort(1); w.WriteShort(1); w.WriteVector(default); w.WriteVector(default); },
            w => { w.WriteByte(61); w.WriteShort(1); w.WriteVector(default); w.WriteVector(default); w.WriteShort(1); },
            w => { w.WriteByte(62); w.WriteShort(1); w.WriteVector(default); },
        };
        foreach (var build in builders)
        {
            var w = new DpMessageWriter();
            build(w);
            byte[] full = w.ToArray();
            int svc = full[0];

            var whole = new RecordingHandler();
            DpParseResult ok = new DpServerMessageParser(whole).Parse(full);
            Assert.True(ok.Status == DpParseStatus.Complete, $"svc {svc}: {ok}");
            int callbacks = whole.Log.Count;

            for (int cut = 1; cut < full.Length; cut++)
            {
                var h = new RecordingHandler();
                var reader = new DpMessageReader(full, 0, cut);
                DpParseResult result = new DpServerMessageParser(h).Parse(reader);
                Assert.True(result.Status == DpParseStatus.Error, $"svc {svc} cut at {cut}/{full.Length}: {result}");
                Assert.Equal(svc, result.Svc);
                Assert.Equal(0, result.Offset);
                Assert.True(reader.Position <= cut);
                // csqc removes are complete sub-records, so they may legitimately be reported before the cut
                if (svc != 58)
                    Assert.True(h.Log.Count < Math.Max(callbacks, 1), $"svc {svc} cut at {cut}: called back with a truncated message: {string.Join("|", h.Log)}");
            }
        }
    }

    [Fact]
    public void Handler_Exception_Becomes_A_Parse_Error()
    {
        var handler = new RecordingHandler { TempEntity = _ => throw new InvalidOperationException("boom") };
        var (_, result, _) = Parse(w => { w.WriteByte(23); w.WriteByte(1); }, handler);
        Assert.Equal(DpParseStatus.Error, result.Status);
        Assert.Contains("boom", result.Message);
    }

    [Fact]
    public void Command_Trace_Sees_Each_Id_And_Offset()
    {
        var seen = new List<(int, int)>();
        var parser = new DpServerMessageParser(new RecordingHandler()) { CommandTrace = (svc, off) => seen.Add((svc, off)) };
        var w = new DpMessageWriter();
        w.WriteByte(1); w.WriteByte(7); w.WriteFloat(1); w.WriteByte(8); w.WriteString("x"); w.WriteByte(99);
        parser.Parse(w.ToArray());
        Assert.Equal(new[] { (1, 0), (7, 1), (8, 6), (99, 9) }, seen);
    }

    // ---------------------------------------------------------------- fuzz

    private sealed class GreedyHandler : IDpClientHandler
    {
        private readonly Random _rng;
        public GreedyHandler(Random rng) => _rng = rng;

        // A stand-in for QuakeC that reads an arbitrary number of bytes, sometimes more than exist.
        private DpPayloadResult Chew(DpMessageReader r, bool allowNotHandled)
        {
            int n = _rng.Next(0, 40);
            for (int i = 0; i < n; i++)
            {
                switch (_rng.Next(6))
                {
                    case 0: r.ReadByte(); break;
                    case 1: r.ReadShort(); break;
                    case 2: r.ReadLong(); break;
                    case 3: r.ReadFloat(); break;
                    case 4: r.ReadString(); break;
                    default: r.ReadVector(); break;
                }
            }
            int choice = _rng.Next(allowNotHandled ? 3 : 2);
            return choice == 0 ? DpPayloadResult.Consumed : choice == 1 ? DpPayloadResult.Abort : DpPayloadResult.NotHandled;
        }

        public DpPayloadResult OnTempEntity(DpMessageReader reader) => Chew(reader, true);
        public DpPayloadResult OnCsqcEntityUpdate(int entity, DpMessageReader reader) => Chew(reader, false);
    }

    /// <summary>
    /// Random bytes, and valid messages with bits flipped, bytes dropped and tails cut. The parser
    /// must return a result every time: no exception, no read outside the window it was given.
    /// The buffer is padded with a guard pattern on both sides so an out-of-window read that happens
    /// to stay inside the array would still be visible as a position past the window.
    /// </summary>
    [Fact]
    public void Fuzz_Parser_Never_Throws_And_Never_Reads_Out_Of_Bounds()
    {
        var rng = new Random(20261007);
        var corpus = new List<byte[]>();
        void AddToCorpus(Action<DpMessageWriter> build)
        {
            var w = new DpMessageWriter();
            build(w);
            corpus.Add(w.ToArray());
        }
        AddToCorpus(w => WriteServerInfo(w));
        AddToCorpus(w =>
        {
            w.WriteByte(8); w.WriteString("Server: test\n");
            w.WriteByte(9); w.WriteString("csqc_progname csprogs.dat\n");
            w.WriteByte(9); w.WriteString("csqc_progsize 100\n");
            WriteServerInfo(w);
            w.WriteByte(32); w.WriteByte(1); w.WriteByte(1);
            w.WriteByte(5); w.WriteShort(1);
            w.WriteByte(25); w.WriteByte(1);
        });
        AddToCorpus(w =>
        {
            w.WriteByte(7); w.WriteFloat(10);
            w.WriteByte(15); w.WriteShort(0x8FFF); w.WriteByte(0x0F);
            for (int i = 0; i < 60; i++) w.WriteByte(i);
            w.WriteByte(57); w.WriteLong(5); w.WriteLong(6);
            for (int e = 1; e < 20; e++)
            {
                w.WriteShort(e);
                w.WriteByte(0xFF); w.WriteByte(0xFF); w.WriteByte(0xFF); w.WriteByte(0x07);
                for (int i = 0; i < 70; i++) w.WriteByte(i & 3);
            }
            w.WriteShort(0x8000);
        });
        AddToCorpus(w =>
        {
            for (int t = 0; t < 80; t++) { w.WriteByte(23); w.WriteByte(t); for (int i = 0; i < 43; i++) w.WriteByte(1); }
        });
        AddToCorpus(w =>
        {
            w.WriteByte(58);
            for (int e = 1; e < 30; e++) { w.WriteShort(e | ((e & 1) << 15)); w.WriteByte(e); }
            w.WriteShort(0);
            w.WriteByte(50); w.WriteLong(100); w.WriteShort(200); w.WriteBytes(new byte[200]);
            w.WriteByte(6); w.WriteByte(63); for (int i = 0; i < 22; i++) w.WriteByte(i);
            w.WriteByte(22); w.WriteShort(9); WriteBaselineBody(w, false, 1, 1);
            w.WriteByte(55); w.WriteShort(9); WriteBaselineBody(w, true, 1, 1);
            w.WriteByte(54); w.WriteShort(40000); w.WriteString("sound.wav");
        });

        const int guard = 64;
        var parser = new DpServerMessageParser(new GreedyHandler(rng));
        int iterations = 0, errors = 0, complete = 0, aborted = 0;
        for (int i = 0; i < 60000; i++)
        {
            byte[] body;
            int mode = rng.Next(4);
            if (mode == 0)
            {
                body = new byte[rng.Next(0, 300)];
                rng.NextBytes(body);
            }
            else
            {
                body = (byte[])corpus[rng.Next(corpus.Count)].Clone();
                int flips = rng.Next(1, mode == 1 ? 3 : 12);
                for (int f = 0; f < flips && body.Length > 0; f++)
                {
                    int at = rng.Next(body.Length);
                    if (mode == 3) body[at] = (byte)rng.Next(256);
                    else body[at] ^= (byte)(1 << rng.Next(8));
                }
                if (rng.Next(3) == 0)
                    body = body[..rng.Next(body.Length + 1)];
            }

            var padded = new byte[body.Length + 2 * guard];
            Array.Fill(padded, (byte)0xA5);
            body.CopyTo(padded, guard);
            var reader = new DpMessageReader(padded, guard, body.Length);
            // A fresh parser now and then, a reused one in between: state left by one bad message must
            // not make the next one unsafe either.
            if (i % 64 == 0)
                parser = new DpServerMessageParser(new GreedyHandler(rng));

            DpParseResult result = parser.Parse(reader); // must not throw
            iterations++;
            Assert.InRange(reader.Position, 0, body.Length);
            Assert.InRange(reader.Remaining, 0, body.Length);
            if (result.Status != DpParseStatus.Complete)
            {
                Assert.InRange(result.Offset, 0, Math.Max(0, body.Length - 1));
                Assert.InRange(result.Svc, 0, 255);
            }
            Assert.DoesNotContain("handler threw", result.Message ?? "");
            Assert.InRange(parser.Entities.Count, 1, DpProtocol.MaxEdicts);
            switch (result.Status)
            {
                case DpParseStatus.Complete: complete++; break;
                case DpParseStatus.Aborted: aborted++; break;
                default: errors++; break;
            }
        }
        Assert.Equal(60000, iterations);
        // the fuzzer has to reach all three outcomes to mean anything
        Assert.True(errors > 1000 && complete > 100 && aborted > 100, $"errors {errors}, complete {complete}, aborted {aborted}");
    }

    /// <summary>The same for the pieces below the parser: the netchan, the handshake and the demo reader.</summary>
    [Fact]
    public void Fuzz_Channel_Handshake_And_Demo_Reader_Never_Throw()
    {
        var rng = new Random(424242);
        var channel = new DpNetChannel(0);
        var handshake = new DpConnectionHandshake();
        handshake.Start(0);
        var outgoing = new List<byte[]>();
        for (int i = 0; i < 40000; i++)
        {
            var d = new byte[rng.Next(0, 80)];
            rng.NextBytes(d);
            if (d.Length >= 8 && rng.Next(2) == 0)
            {
                // a plausible header: valid length, random flag and a small sequence
                uint[] flags = { DpProtocol.NetFlagData, DpProtocol.NetFlagData | DpProtocol.NetFlagEom, DpProtocol.NetFlagAck, DpProtocol.NetFlagUnreliable, DpProtocol.NetFlagNak, DpProtocol.NetFlagCtl };
                uint first = flags[rng.Next(flags.Length)] | (uint)d.Length;
                d[0] = (byte)(first >> 24); d[1] = (byte)(first >> 16); d[2] = (byte)(first >> 8); d[3] = (byte)first;
                d[4] = d[5] = d[6] = 0; d[7] = (byte)rng.Next(4);
            }
            else if (d.Length >= 4 && rng.Next(3) == 0)
                d[0] = d[1] = d[2] = d[3] = 0xFF;
            outgoing.Clear();
            channel.Receive(d, i * 0.01, outgoing, out _);
            handshake.Receive(d, i * 0.01, outgoing);
            handshake.Update(i * 0.01, outgoing);
            channel.Transmit(d.AsSpan(0, Math.Min(d.Length, 3)), i * 0.01, outgoing);
            Assert.True(outgoing.Count < 16);

            var demo = new DpDemoReader(new System.IO.MemoryStream(d));
            int guard = 0;
            while (demo.TryReadMessage(out _) && guard++ < 100) { }
        }
    }
}
