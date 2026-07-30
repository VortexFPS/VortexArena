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
/// In-memory tests for the C3 Tier-1 <see cref="StringApiAnalyzer"/> (XG0001): it must flag the banned
/// string-keyed Godot APIs (godot#105750) and ONLY those — never a user-defined same-named method, never the
/// typed (non-string) overloads. Mirrors <c>SourceGenTests</c>' compile harness; a self-contained <c>Godot</c>
/// namespace stub gives the analyzer the engine types to recognise.
/// </summary>
public class StringApiAnalyzerTests
{
    private const string GodotStubs = @"
namespace Godot {
    public struct NodePath { public NodePath(string s){} public static implicit operator NodePath(string s) => new NodePath(s); }
    public static class Input {
        public static bool IsActionPressed(string a) => false;
        public static bool IsKeyPressed(int key) => false; // enum/int-keyed — NOT banned
    }
    public class Node {
        public Node GetNode(string path) => null;
        public Node GetNode(NodePath path) => null;
        public Node GetNodeOrNull(string path) => null;
        public object GetNodesInGroup(string group) => null;
        public void AddToGroup(string group) {}
    }
}
";

    private static CSharpCompilation Compile(string source)
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
            "AnalyzerStubAssembly",
            new[] { CSharpSyntaxTree.ParseText(GodotStubs + source) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "stub compilation has errors: " + string.Join("; ", errors));
        return compilation;
    }

    private static int CountXg0001(string body)
    {
        string source = "namespace Game { class C : Godot.Node { void M() {\n" + body + "\n} } }";
        CSharpCompilation compilation = Compile(source);
        ImmutableArray<Diagnostic> diags = compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new StringApiAnalyzer()))
            .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
        return diags.Count(d => d.Id == StringApiAnalyzer.DiagnosticId);
    }

    [Theory]
    [InlineData("Godot.Input.IsActionPressed(\"jump\");")]
    [InlineData("GetNodesInGroup(\"players\");")]
    [InlineData("AddToGroup(\"players\");")]
    [InlineData("GetNode(\"Child\");")]
    [InlineData("GetNodeOrNull(\"Child\");")]
    public void Flags_bannedStringKeyedApis(string body)
    {
        Assert.True(CountXg0001(body) >= 1, $"expected XG0001 for: {body}");
    }

    [Fact]
    public void DoesNotFlag_typedNodePathOverload()
    {
        // The NodePath overload (not the string one) is allocation-free of the per-call string→NodePath kind.
        Assert.Equal(0, CountXg0001("GetNode((Godot.NodePath)\"Child\");"));
    }

    [Fact]
    public void DoesNotFlag_enumKeyedInput()
    {
        Assert.Equal(0, CountXg0001("Godot.Input.IsKeyPressed(4);"));
    }

    [Fact]
    public void DoesNotFlag_userDefinedSameNameMethod()
    {
        // A same-named method that is NOT a Godot type must not be flagged (the namespace guard).
        const string body = "var h = new Game.Helper(); h.GetNodesInGroup(\"x\");";
        string source = "namespace Game { class Helper { public object GetNodesInGroup(string g) => null; }"
                      + " class C : Godot.Node { void M() {\n" + body + "\n} } }";
        CSharpCompilation compilation = Compile(source);
        int n = compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new StringApiAnalyzer()))
            .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult()
            .Count(d => d.Id == StringApiAnalyzer.DiagnosticId);
        Assert.Equal(0, n);
    }

    [Fact]
    public void DoesNotFlag_cleanCode()
    {
        // A non-banned invocation (Int32.ToString, CoreLib) plus arithmetic — nothing the analyzer should touch.
        Assert.Equal(0, CountXg0001("int x = 1 + 2; string s = x.ToString();"));
    }
}
