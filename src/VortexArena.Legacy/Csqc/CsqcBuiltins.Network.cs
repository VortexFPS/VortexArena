// Port of Base/darkplaces/clvm_cmds.c VM_CL_ReadByte, VM_CL_ReadChar, VM_CL_ReadShort, VM_CL_ReadLong,
// VM_CL_ReadCoord, VM_CL_ReadAngle, VM_CL_ReadString, VM_CL_ReadFloat, VM_CL_ReadPicture, VM_CL_getstatf,
// VM_CL_getstati, VM_CL_getstats, VM_CL_getplayerkey, VM_CL_isdemo, VM_CL_serverkey, VM_CL_GetEntity,
// prvm_cmds.c VM_isserver and VM_uri_get (the "no libcurl" outcome), common.c InfoString_GetValue.
using System.Globalization;
using System.Numerics;
using System.Text;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

public sealed partial class CsqcBuiltins
{
    private void RegisterNetwork()
    {
        Here(330, "getstatf", GetStatF);
        Here(331, "getstati", GetStatI);
        Here(332, "getstats", GetStatS);
        Here(348, "getplayerkeyvalue", GetPlayerKey);
        Here(349, "isdemo", IsDemo);
        Here(350, "isserver", IsServer);
        Here(354, "serverkey", ServerKey);
        Here(360, "ReadByte", ReadByte);
        Here(361, "ReadChar", ReadChar);
        Here(362, "ReadShort", ReadShort);
        Here(363, "ReadLong", ReadLong);
        Here(364, "ReadCoord", ReadCoord);
        Here(365, "ReadAngle", ReadAngle);
        Here(366, "ReadString", ReadString);
        Here(367, "ReadFloat", ReadFloat);
        Here(501, "ReadPicture", ReadPicture);
        Here(504, "getentity", GetEntity);
        Here(513, "uri_get", UriGet);
    }

    // ---- reads from the message being parsed -------------------------------------------------------
    //
    // DarkPlaces reads from cl_message, a global: outside a message that buffer is exhausted, so every
    // read fails the way a read past the end does (-1, or the empty string) and sets badread on a
    // buffer nobody is looking at. The same happens here, on an empty reader of the host's own.

    private DpMessageReader Reader(string builtin, int parms = 0)
    {
        Parms(parms, "VM_CL_" + builtin);
        if (!_host.InMessage) _host.ReadsOutsideMessage++;
        return _host.MessageReader;
    }

    // The diagnostic that makes a layout disagreement findable: which QuakeC statement asked for bytes
    // that were not there.
    private void Check(DpMessageReader reader, string builtin)
    {
        if (reader.BadRead) _host.NoteBadRead(builtin);
    }

    // #360 float() ReadByte
    private void ReadByte(QcVm vm)
    {
        DpMessageReader r = Reader("ReadByte");
        vm.ReturnFloat(r.ReadByte());
        Check(r, "ReadByte");
    }

    // #361 float() ReadChar
    private void ReadChar(QcVm vm)
    {
        DpMessageReader r = Reader("ReadChar");
        vm.ReturnFloat(r.ReadChar());
        Check(r, "ReadChar");
    }

    // #362 float() ReadShort
    private void ReadShort(QcVm vm)
    {
        DpMessageReader r = Reader("ReadShort");
        vm.ReturnFloat(r.ReadShort());
        Check(r, "ReadShort");
    }

    // #363 float() ReadLong. A 32-bit value does not fit a float exactly; the program gets the nearest.
    private void ReadLong(QcVm vm)
    {
        DpMessageReader r = Reader("ReadLong");
        vm.ReturnFloat(r.ReadLong());
        Check(r, "ReadLong");
    }

    // #364 float() ReadCoord: a 32-bit float in DP7.
    private void ReadCoord(QcVm vm)
    {
        DpMessageReader r = Reader("ReadCoord");
        vm.ReturnFloat(r.ReadCoord());
        Check(r, "ReadCoord");
    }

    // #365 float() ReadAngle: 16-bit in DP7.
    private void ReadAngle(QcVm vm)
    {
        DpMessageReader r = Reader("ReadAngle");
        vm.ReturnFloat(r.ReadAngle());
        Check(r, "ReadAngle");
    }

    // #366 string() ReadString
    private void ReadString(QcVm vm)
    {
        DpMessageReader r = Reader("ReadString");
        vm.ReturnString(r.ReadString());
        Check(r, "ReadString");
    }

    // #367 float() ReadFloat
    private void ReadFloat(QcVm vm)
    {
        DpMessageReader r = Reader("ReadFloat");
        vm.ReturnFloat(r.ReadFloat());
        Check(r, "ReadFloat");
    }

    // #501 string() ReadPicture: a picture name, a 16-bit length, and that many bytes of JPEG - a
    // low-quality stand-in the server attaches (map previews, in Xonotic) in case the client lacks the
    // real picture. The bytes are consumed whether or not they are used.
    private void ReadPicture(QcVm vm)
    {
        DpMessageReader r = Reader("ReadPicture");
        string name = r.ReadString();
        int size = r.ReadUShort();
        if (size != 0)
        {
            bool have = _presentation.Draw.PictureExists(name);
            ReadOnlySpan<byte> jpeg = r.ReadSpan(size);
            // "texture not found: use the attached jpeg as texture" (or cl_readpicture_force).
            if ((!have || _host.Services.CvarFloat("cl_readpicture_force") != 0) && !r.BadRead)
                _presentation.Draw.DefinePicture(name, jpeg);
        }
        vm.ReturnString(name);
        Check(r, "ReadPicture");
    }

    // ---- stats -------------------------------------------------------------------------------------

    // #330 float(float stnum) getstatf: the stat's 32 bits read as a float.
    private void GetStatF(QcVm vm)
    {
        Parms(1, "VM_CL_getstatf");
        int index = ArgInt(0);
        if ((uint)index >= DpProtocol.MaxClStats)
        {
            vm.ReturnFloat(0);
            Warning("VM_CL_getstatf: index>=MAX_CL_STATS or index<0\n");
            return;
        }
        vm.ReturnInt(_state.Stats[index]);
    }

    // #331 float(float stnum[, float firstbit[, float bitcount]]) getstati: the stat as an integer,
    // or a bit field of it.
    private void GetStatI(QcVm vm)
    {
        Parms(1, 3, "VM_CL_getstati");
        int index = ArgInt(0);
        if ((uint)index >= DpProtocol.MaxClStats)
        {
            vm.ReturnFloat(0);
            Warning($"VM_CL_getstati: index({index}) is >=MAX_CL_STATS({DpProtocol.MaxClStats}) or <0\n");
            return;
        }
        int firstBit = 0, bitCount = 32;
        if (vm.ArgCount > 1)
        {
            firstBit = ArgInt(1);
            bitCount = vm.ArgCount > 2 ? ArgInt(2) : 1;
        }
        int stat = _state.Stats[index];
        // "32 causes the mask to overflow, so there's nothing to subtract from." Shift counts outside
        // 0..31 are undefined in C; x86 masks them to five bits, which is also what C# does.
        vm.ReturnFloat(bitCount < 32 ? (stat >> firstBit) & ((1 << bitCount) - 1) : stat);
    }

    // #332 string(float firststnum) getstats: four consecutive stats read as up to 16 characters.
    private void GetStatS(QcVm vm)
    {
        Parms(1, "VM_CL_getstats");
        int index = ArgInt(0);
        if (index < 0 || index > DpProtocol.MaxClStats - 4)
        {
            vm.ReturnInt(0);
            Warning("VM_CL_getstats: index>MAX_CL_STATS-4 or index<0\n");
            return;
        }
        Span<byte> bytes = stackalloc byte[16];
        int length = 0;
        for (; length < 16; length++)
        {
            byte b = (byte)(_state.Stats[index + (length >> 2)] >> ((length & 3) * 8));
            if (b == 0) break;
            bytes[length] = b;
        }
        vm.ReturnString(Encoding.UTF8.GetString(bytes[..length]));
    }

    // ---- players and the server --------------------------------------------------------------------

    // #348 string(float playernum, string keyname) getplayerkeyvalue. A negative player number counts
    // from the top of the scoreboard: -1 is the leader.
    private void GetPlayerKey(QcVm vm)
    {
        Parms(2, "VM_CL_getplayerkey");
        int i = ArgInt(0);
        string key = vm.ArgString(1);
        vm.ReturnInt(0);
        if (i < 0) i = _state.SortedPlayerIndex(-1 - i);
        if (i < 0 || i >= _state.MaxClients) return;

        ref CsqcPlayerInfo player = ref _state.Scores[i];
        string? text = key.ToLowerInvariant() switch
        {
            "name" => Truncate(player.Name ?? "", 127),
            "frags" => player.Frags.ToString(CultureInfo.InvariantCulture),
            "ping" => player.Ping.ToString(CultureInfo.InvariantCulture),
            "pl" => player.PacketLoss.ToString(CultureInfo.InvariantCulture),
            "movementloss" => player.MovementLoss.ToString(CultureInfo.InvariantCulture),
            "entertime" => player.EnterTime.ToString("F6", CultureInfo.InvariantCulture),
            "colors" => player.Colors.ToString(CultureInfo.InvariantCulture),
            "topcolor" => (player.Colors & 0xf0).ToString(CultureInfo.InvariantCulture),
            "bottomcolor" => ((player.Colors & 15) << 4).ToString(CultureInfo.InvariantCulture),
            "viewentity" => (i + 1).ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
        // "if(!t[0]) return": an empty value is the null string, like an unknown key.
        if (!string.IsNullOrEmpty(text)) vm.ReturnString(text);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    // #349 float() isdemo
    private void IsDemo(QcVm vm)
    {
        Parms(0, "VM_CL_isdemo");
        vm.ReturnFloat(_state.IsDemo ? 1 : 0);
    }

    // #350 float() isserver
    private void IsServer(QcVm vm)
    {
        Parms(0, "VM_isserver");
        vm.ReturnFloat(_state.IsServer ? 1 : 0);
    }

    // #354 string(string key) serverkey: a value from the QuakeWorld-style server info string.
    private void ServerKey(QcVm vm)
    {
        Parms(1, "VM_CL_serverkey");
        vm.ReturnString(InfoValue(_state.ServerInfoString, vm.ArgString(0)));
    }

    /// <summary>InfoString_GetValue: the value of <paramref name="key"/> in "\key\value\key\value", or "".</summary>
    public static string InfoValue(string info, string key)
    {
        if (key.Length == 0) return "";
        int at = 0;
        while (at < info.Length && info[at] == '\\')
        {
            int keyEnd = info.IndexOf('\\', at + 1);
            if (keyEnd < 0) return "";
            int valueEnd = info.IndexOf('\\', keyEnd + 1);
            if (valueEnd < 0) valueEnd = info.Length;
            if (info.AsSpan(at + 1, keyEnd - at - 1).SequenceEqual(key.AsSpan())) return info[(keyEnd + 1)..valueEnd];
            at = valueEnd;
        }
        return "";
    }

    // #513 float(string uri, float id, ...) uri_get. Starting a request needs an HTTP client the host
    // supplies; without one the answer is 0, "could not be started", as DarkPlaces gives without libcurl.
    private void UriGet(QcVm vm)
    {
        Parms(2, 6, "VM_uri_get");
        vm.ReturnFloat(_host.UriGet(vm.ArgString(0), ArgInt(1)) ? 1 : 0);
    }

    // #504 getentity(float entitynum, float fldnum): a property of one of the engine's own network
    // entities (not one of the program's). Returns a float or a vector depending on the field.
    private void GetEntity(QcVm vm)
    {
        Parms(2, "VM_CL_GetEntity");
        int number = ArgInt(0);
        vm.ReturnVector(default);
        DpEntityTable? table = _state.NetworkEntities;
        if (table is null || number < 0 || number >= table.Count) return;

        ref readonly EntityState state = ref table.Current(number);
        QcVector origin = Q(state.Origin);
        switch (ArgInt(1))
        {
            case 0: vm.ReturnFloat(state.IsActive ? 1 : 0); break;                 // active state
            case 1: vm.ReturnVector(origin); break;                                 // origin
            case 2: Axes(state, out QcVector f, out _, out _); vm.ReturnVector(f); break;
            case 3: Axes(state, out _, out QcVector r, out _); vm.ReturnVector(r); break;
            case 4: Axes(state, out _, out _, out QcVector u); vm.ReturnVector(u); break;
            case 5: vm.ReturnFloat(state.Scale == 0 ? 1 : state.Scale * (1f / 16f)); break; // scale
            case 6: // origin + v_forward, v_right, v_up
            {
                Axes(state, out QcVector forward, out QcVector right, out QcVector up);
                _host.SetVector(_g.VForward, forward);
                _host.SetVector(_g.VRight, right);
                _host.SetVector(_g.VUp, up);
                vm.ReturnVector(origin);
                break;
            }
            case 7: vm.ReturnFloat(state.Alpha * (1f / 255f)); break;              // alpha
            case 8: vm.ReturnVector(new QcVector(state.ColorMod0 * (1f / 32f), state.ColorMod1 * (1f / 32f), state.ColorMod2 * (1f / 32f))); break;
            // 9, 10: pants and shirt colours. They come from the palette, which the presentation
            // owns; without it they are black.
            case 9 or 10: break;
            case 11: vm.ReturnFloat(state.Skin); break;
            case 12 or 13 or 14 or 15:
            {
                QcVector mins = default, maxs = default;
                if (_state.ModelNameForIndex(state.ModelIndex) is { } model) _host.ModelBounds(model, out mins, out maxs);
                int field = ArgInt(1);
                QcVector box = field is 12 or 14 ? mins : maxs;
                vm.ReturnVector(field >= 14 ? new QcVector(box.X + origin.X, box.Y + origin.Y, box.Z + origin.Z) : box);
                break;
            }
            // 16: the entity's model lighting, which only a renderer has.
            default: break;
        }

        static QcVector Q(Vector3 v) => new(v.X, v.Y, v.Z);

        // The entity's render matrix is built from its angles with the pitch negated for models
        // (CL_UpdateNetworkEntity); the model type is not known here, so it is not negated.
        static void Axes(in EntityState s, out QcVector forward, out QcVector right, out QcVector up) =>
            QcCoreBuiltins.AngleVectors(Q(s.Angles), out forward, out right, out up);
    }
}
