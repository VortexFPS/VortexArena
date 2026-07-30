using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using XonoticGodot.SourceGen;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// In-memory tests for the C3 Tier-2 <see cref="HotPathStringNameAnalyzer"/> (XG0002): it must flag a
/// <c>string</c> → <c>StringName</c>/<c>NodePath</c> conversion ONLY when it occurs inside a per-frame Godot
/// override (<c>_Process</c>/<c>_PhysicsProcess</c>/<c>_Draw</c>), and never when a cached StringName is used or
/// the call sits in a build-time method.
/// </summary>
public class HotPathStringNameAnalyzerTests
{
    private const string GodotStubs = @"
namespace Godot {
    public struct StringName { public static implicit operator StringName(string s) => default; }
    public struct NodePath { public static implicit operator NodePath(string s) => default; }
    public class Node {
        public virtual void _Process(double delta) {}
        public virtual void _PhysicsProcess(double delta) {}
        public virtual void _Draw() {}
        public void SetMeta(StringName name, int value) {}
        public Node GetNode(NodePath path) => null;
    }
}
";

    private static int CountXg0002(string classes)
    {
        var refs = new List<MetadataReference>();
        string tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (string path in tpa.Split(Path.PathSeparator))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name is "System.Private.CoreLib" or "System.Runtime" or "netstandard")
                refs.Add(MetadataReference.CreateFromFile(path));
        }
        var compilation = CSharpCompilation.Create(
            "HotPathStub",
            new[] { CSharpSyntaxTree.ParseText(GodotStubs + "namespace Game {\n" + classes + "\n}") },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "stub compilation has errors: " + string.Join("; ", errors));

        return compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new HotPathStringNameAnalyzer()))
            .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult()
            .Count(d => d.Id == HotPathStringNameAnalyzer.DiagnosticId);
    }

    [Fact]
    public void Flags_stringLiteralToStringName_inProcess()
    {
        Assert.Equal(1, CountXg0002(
            "class C : Godot.Node { public override void _Process(double d) { SetMeta(\"cmd\", 1); } }"));
    }

    [Fact]
    public void Flags_stringToNodePath_inPhysicsProcess()
    {
        Assert.Equal(1, CountXg0002(
            "class C : Godot.Node { public override void _PhysicsProcess(double d) { GetNode(\"Child\"); } }"));
    }

    [Fact]
    public void Flags_constStringConversion_inDraw()
    {
        // Even a const string converts to StringName per call — flagged on a per-frame path (the C2 point).
        Assert.Equal(1, CountXg0002(
            "class C : Godot.Node { const string K = \"cmd\"; public override void _Draw() { SetMeta(K, 1); } }"));
    }

    [Fact]
    public void DoesNotFlag_cachedStringName_inProcess()
    {
        // The conversion happens at the field initializer (static init), not inside _Process.
        Assert.Equal(0, CountXg0002(
            "class C : Godot.Node { static readonly Godot.StringName N = \"cmd\"; "
            + "public override void _Process(double d) { SetMeta(N, 1); } }"));
    }

    [Fact]
    public void DoesNotFlag_stringLiteral_inNonHotMethod()
    {
        // The same conversion in a build-time method is fine — only per-frame paths are flagged.
        Assert.Equal(0, CountXg0002(
            "class C : Godot.Node { void Build() { SetMeta(\"cmd\", 1); } }"));
    }

    [Fact]
    public void DoesNotFlag_nonOverrideMethodNamed_Process()
    {
        // A coincidentally-named non-override _Process is not the Godot per-frame callback.
        Assert.Equal(0, CountXg0002(
            "class C : Godot.Node { void _Process(int x) { SetMeta(\"cmd\", 1); } }"));
    }
}
