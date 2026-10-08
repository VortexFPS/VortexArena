using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Menu;
using VortexArena.QuakeC;
using VortexArena.Tests.QuakeC;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// A few lines of assembler over <see cref="ProgsBuilder"/> for menu-program tests: a program with the
/// five functions MP_CheckRequiredFuncs demands, to which a test adds bodies that call menu builtins by
/// their vm_m_builtins[] numbers and store the results in named globals.
/// </summary>
internal sealed class MenuAsm
{
    public readonly ProgsBuilder B = new();
    private readonly Dictionary<int, int> _builtins = new();
    private readonly HashSet<string> _defined = new();

    public MenuAsm()
    {
        B.Int(0, "self", QcType.Entity);
        B.Float(0, "drawfont");
        B.Vector(0, 0, 0, "drawfontscale");
        B.Field("classname", QcType.String);
    }

    public int F(string name, float value = 0) => B.Float(value, name);
    public int V(string name, float x = 0, float y = 0, float z = 0) => B.Vector(x, y, z, name);
    public int S(string name) => B.Int(0, name, QcType.String);
    public int Const(float value) => B.Float(value);
    public int Text(string text) => B.Int(B.String(text), null, QcType.String);

    public void Begin(string function)
    {
        _defined.Add(function);
        B.Function(function);
    }

    public void End() => B.Emit(QcOp.Done);

    /// <summary>Copies the n-th parameter cell (as the caller left it) into a global.</summary>
    public void Parm(int index, int destination) => B.Emit(QcOp.StoreF, ProgsFile.OfsParm0 + index * 3, destination);

    /// <summary>Calls menu builtin #<paramref name="builtin"/>; a negative argument offset means "a vector at -offset".</summary>
    public void Call(int builtin, params int[] args)
    {
        if (!_builtins.TryGetValue(builtin, out int function)) _builtins[builtin] = function = B.Builtin("builtin" + builtin, builtin);
        for (int i = 0; i < args.Length; i++)
            B.Emit(args[i] < 0 ? QcOp.StoreV : QcOp.StoreF, Math.Abs(args[i]), ProgsFile.OfsParm0 + i * 3);
        B.EmitRaw((int)QcOp.Call0 + args.Length, function);
    }

    public void Get(int builtin, int result, params int[] args)
    {
        Call(builtin, args);
        B.Emit(QcOp.StoreF, ProgsFile.OfsReturn, result);
    }

    public void GetV(int builtin, int result, params int[] args)
    {
        Call(builtin, args);
        B.Emit(QcOp.StoreV, ProgsFile.OfsReturn, result);
    }

    /// <summary>The program, with an empty body for every required function the test did not write.</summary>
    public byte[] Build()
    {
        foreach (string required in new[] { "m_init", "m_keydown", "m_draw", "m_toggle", "m_shutdown" })
        {
            if (_defined.Contains(required)) continue;
            Begin(required);
            End();
        }
        return B.Build();
    }
}

/// <summary>A console over a temporary game directory, and a menu host on it.</summary>
internal sealed class MenuRig : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "va-menu-" + Guid.NewGuid().ToString("N"));
    public string GameDir => Path.Combine(Root, "game");
    public string UserData => Path.Combine(Root, "user", "data");
    public VirtualFileSystem Vfs { get; } = new();
    public LegacyConsole Console { get; }
    public HeadlessMenuDraw Draw { get; }
    public StringBuilder Printed { get; } = new();
    public List<string> Warnings { get; } = new();
    public MenuHost? Host { get; private set; }

    /// <param name="files">Files of the game data, by relative path. Without a default.cfg an empty one is written.</param>
    public MenuRig(params (string Path, string Text)[] files)
    {
        Directory.CreateDirectory(GameDir);
        bool hasDefault = false;
        foreach ((string path, string text) in files)
        {
            string full = Path.Combine(GameDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            hasDefault |= path == "default.cfg";
        }
        if (!hasDefault) File.WriteAllText(Path.Combine(GameDir, "default.cfg"), "// empty\n");
        Assert.True(Vfs.Mount(GameDir));
        Console = new LegacyConsole(Vfs, UserData, text => Printed.Append(text));
        Draw = new HeadlessMenuDraw(Vfs);
    }

    public MenuHost Load(MenuAsm asm, MenuHostOptions? options = null, bool init = true)
    {
        Host = new MenuHost(asm.Build(), Console, Draw, options, text => Printed.Append(text), Warnings.Add);
        if (init) Assert.True(Host.Init(), Host.Faults.Count > 0 ? Host.Faults[0].Message : "m_init did not run");
        return Host;
    }

    public float Float(string global) => Host!.Vm.GlobalFloat(Host.Vm.FindGlobal(global)!.Offset);
    public QcVector Vector(string global) => Host!.Vm.GlobalVector(Host.Vm.FindGlobal(global)!.Offset);
    public string String(string global) => Host!.Vm.GetString(Host.Vm.GlobalInt(Host.Vm.FindGlobal(global)!.Offset));
    public int Handle(string global) => Host!.Vm.GlobalInt(Host.Vm.FindGlobal(global)!.Offset);

    public void Dispose()
    {
        Host?.Dispose();
        Vfs.Dispose();
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
    }
}
