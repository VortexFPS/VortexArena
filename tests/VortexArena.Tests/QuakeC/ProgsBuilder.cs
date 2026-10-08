using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VortexArena.QuakeC;

namespace VortexArena.Tests.QuakeC;

/// <summary>
/// A minimal assembler that emits a version-6 progs file, so the VM tests need no QuakeC compiler and
/// can construct exactly the statement under test - including malformed ones no compiler would emit.
/// </summary>
internal sealed class ProgsBuilder
{
    private readonly List<(int Op, int A, int B, int C)> _statements = new();
    private readonly List<(int Type, int Offset, int Name)> _globalDefs = new() { (0, 0, 0) };
    private readonly List<(int Type, int Offset, int Name)> _fieldDefs = new() { (0, 0, 0) };
    private readonly List<(int First, int ParmStart, int Locals, int Name, int NumParms, byte[] Sizes)> _functions = new() { (0, 0, 0, 0, 0, new byte[8]) };
    private readonly MemoryStream _strings = new();
    private readonly List<int> _globals = new();
    private int _entityFields;

    public ProgsBuilder()
    {
        _strings.WriteByte(0); // offset 0 is the empty string
        for (int i = 0; i < ProgsFile.ReservedOfs; i++) _globals.Add(0);
        Emit(QcOp.Done); // statement 0 is never a function body
    }

    public int String(string text)
    {
        int offset = (int)_strings.Length;
        _strings.Write(Encoding.UTF8.GetBytes(text));
        _strings.WriteByte(0);
        return offset;
    }

    public int Float(float value, string? name = null) => Cell(BitConverter.SingleToInt32Bits(value), QcType.Float, name);
    public int Int(int value, string? name = null, QcType type = QcType.Float) => Cell(value, type, name);

    public int Vector(float x, float y, float z, string? name = null)
    {
        int offset = Float(x);
        Float(y); Float(z);
        if (name is not null) _globalDefs.Add(((int)QcType.Vector, offset, String(name)));
        return offset;
    }

    private int Cell(int raw, QcType type, string? name)
    {
        int offset = _globals.Count;
        _globals.Add(raw);
        if (name is not null) _globalDefs.Add(((int)type, offset, String(name)));
        return offset;
    }

    /// <summary>Declares an entity field and returns a global holding its offset (how QuakeC refers to fields).</summary>
    public int Field(string name, QcType type = QcType.Float)
    {
        int offset = _entityFields;
        _entityFields += type == QcType.Vector ? 3 : 1;
        _fieldDefs.Add(((int)type, offset, String(name)));
        return Int(offset, null, QcType.Field);
    }

    public int Emit(QcOp op, int a = 0, int b = 0, int c = 0) => EmitRaw((int)op, a, b, c);

    public int EmitRaw(int op, int a = 0, int b = 0, int c = 0)
    {
        _statements.Add((op, a, b, c));
        return _statements.Count - 1;
    }

    public int NextStatement => _statements.Count;

    /// <summary>Declares a function whose body starts at the next statement. Returns a global holding its index.</summary>
    public int Function(string name, int parmStart = 0, int locals = 0, params byte[] parmSizes)
    {
        byte[] sizes = new byte[8];
        parmSizes.CopyTo(sizes, 0);
        _functions.Add((_statements.Count, parmStart, locals, String(name), parmSizes.Length, sizes));
        return Int(_functions.Count - 1, null, QcType.Function);
    }

    public int Builtin(string name, int number)
    {
        _functions.Add((-number, 0, 0, String(name), 0, new byte[8]));
        return Int(_functions.Count - 1, null, QcType.Function);
    }

    /// <summary>Patches a jump's relative offset so it lands on <paramref name="target"/>.</summary>
    public void PatchJump(int statement, int target)
    {
        (int op, int a, int b, int c) = _statements[statement];
        _statements[statement] = op == (int)QcOp.Goto ? (op, target - statement, b, c) : (op, a, target - statement, c);
    }

    public byte[] Build(int version = 6)
    {
        using MemoryStream file = new();
        using BinaryWriter w = new(file);
        byte[] strings = _strings.ToArray();
        int offset = ProgsFile.HeaderSize;
        int ofsStatements = offset; offset += _statements.Count * 8;
        int ofsGlobalDefs = offset; offset += _globalDefs.Count * 8;
        int ofsFieldDefs = offset; offset += _fieldDefs.Count * 8;
        int ofsFunctions = offset; offset += _functions.Count * 36;
        int ofsStrings = offset; offset += strings.Length;
        int ofsGlobals = offset;

        w.Write(version); w.Write(0);
        w.Write(ofsStatements); w.Write(_statements.Count);
        w.Write(ofsGlobalDefs); w.Write(_globalDefs.Count);
        w.Write(ofsFieldDefs); w.Write(_fieldDefs.Count);
        w.Write(ofsFunctions); w.Write(_functions.Count);
        w.Write(ofsStrings); w.Write(strings.Length);
        w.Write(ofsGlobals); w.Write(_globals.Count);
        w.Write(_entityFields);

        foreach ((int op, int a, int b, int c) in _statements) { w.Write((ushort)op); w.Write((ushort)a); w.Write((ushort)b); w.Write((ushort)c); }
        foreach ((int type, int ofs, int name) in _globalDefs) { w.Write((ushort)type); w.Write((ushort)ofs); w.Write(name); }
        foreach ((int type, int ofs, int name) in _fieldDefs) { w.Write((ushort)type); w.Write((ushort)ofs); w.Write(name); }
        foreach ((int first, int parmStart, int locals, int name, int numParms, byte[] sizes) in _functions)
        {
            w.Write(first); w.Write(parmStart); w.Write(locals); w.Write(0); w.Write(name); w.Write(0); w.Write(numParms); w.Write(sizes);
        }
        w.Write(strings);
        foreach (int cell in _globals) w.Write(cell);
        w.Flush();
        return file.ToArray();
    }

    public QcVm BuildVm() => new(ProgsFile.Load(Build()), "test");
}
