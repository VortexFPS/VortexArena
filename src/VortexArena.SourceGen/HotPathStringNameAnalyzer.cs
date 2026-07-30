using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace XonoticGodot.SourceGen
{
    /// <summary>
    /// Roslyn analyzer enforcing the godot#105750 per-frame-allocation convention, Tier 2 (planning/PERFORMANCE_REPORT.md
    /// C3). It flags an implicit <c>string</c> → <c>StringName</c>/<c>NodePath</c> conversion (the per-call
    /// marshaling allocation — a string literal OR a <c>const string</c> passed to a Godot API typed
    /// <c>StringName</c>/<c>NodePath</c>) that occurs lexically inside a <c>_Process</c> / <c>_PhysicsProcess</c> /
    /// <c>_Draw</c> override — i.e. on a PER-FRAME path. That syntactic scope encodes "per-frame" precisely and has
    /// near-zero false positives: the 40+ legitimate build-time <c>SetShaderParameter("literal", …)</c> sites live
    /// in material-setup methods, never in those three. The fix is to cache the name as a
    /// <c>static readonly StringName</c> (C2) and reference it instead — then the call is alloc-free.
    ///
    /// <para>Reported as a WARNING (XG0002), not a build error: unlike the Tier-1 hard ban, the per-frame scope is
    /// heuristic (a lambda lexically inside <c>_Process</c> but invoked later is a possible false positive), so it
    /// is an IDE squiggle + build warning rather than a build break, suppressible with
    /// <c>#pragma warning disable XG0002</c>. The honest limitation both tiers share: a bad call buried in a helper
    /// method *invoked from* <c>_Process</c> isn't caught (no interprocedural reachability) — the runtime
    /// <c>dotnet-counters</c> alloc-rate backstop (§7) covers that.</para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class HotPathStringNameAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "XG0002";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            title: "string -> StringName/NodePath conversion on a per-frame path",
            messageFormat: "Implicit string -> {0} conversion inside {1} allocates per frame (godot#105750). " +
                           "Cache the name as a 'static readonly StringName' and reference that instead.",
            category: "Performance",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description:
                "A string literal or const string passed to a Godot API typed StringName/NodePath mints a " +
                "StringName/NodePath allocation per call — a GC treadmill on a per-frame (_Process/_PhysicsProcess/" +
                "_Draw) path. Cache it as a static readonly StringName (planning/PERFORMANCE_REPORT.md C2/C3). Suppress with " +
                "#pragma warning disable XG0002 on a genuine non-per-frame lambda.",
            helpLinkUri: "https://github.com/godotengine/godot/issues/105750");

        private static readonly ImmutableHashSet<string> HotMethods =
            ImmutableHashSet.Create("_Process", "_PhysicsProcess", "_Draw");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterOperationAction(AnalyzeConversion, OperationKind.Conversion);
        }

        private static void AnalyzeConversion(OperationAnalysisContext context)
        {
            var conv = (IConversionOperation)context.Operation;

            // string -> StringName / NodePath (the per-call marshaling alloc). The operand is string-typed (a
            // literal, a const string, or any string expression); the target is the Godot value type.
            if (conv.Operand.Type?.SpecialType != SpecialType.System_String)
                return;
            if (conv.Type is not INamedTypeSymbol target)
                return;
            string targetName = target.Name;
            if ((targetName != "StringName" && targetName != "NodePath") || !IsGodotType(target))
                return;

            // Only on a per-frame path: the nearest enclosing METHOD declaration is a Godot _Process/_PhysicsProcess/
            // _Draw override. (A lambda/local function inside it resolves to the same enclosing method.)
            string? hotMethod = EnclosingHotMethodName(conv.Syntax);
            if (hotMethod is null)
                return;

            context.ReportDiagnostic(Diagnostic.Create(Rule, conv.Syntax.GetLocation(), targetName, hotMethod));
        }

        /// <summary>The name of the nearest enclosing <c>override</c> method declaration if it is one of the
        /// per-frame Godot callbacks, else null. Walks syntax ancestors so a lambda/local-function body inside the
        /// callback still resolves to it.</summary>
        private static string? EnclosingHotMethodName(SyntaxNode node)
        {
            for (SyntaxNode? n = node; n != null; n = n.Parent)
            {
                if (n is MethodDeclarationSyntax method)
                {
                    string name = method.Identifier.ValueText;
                    if (HotMethods.Contains(name) && method.Modifiers.Any(SyntaxKind.OverrideKeyword))
                        return name;
                    return null; // an enclosing non-hot method: not a per-frame path
                }
            }
            return null;
        }

        private static bool IsGodotType(INamedTypeSymbol? type)
        {
            for (INamespaceSymbol? ns = type?.ContainingNamespace; ns != null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
                if (ns.Name == "Godot" && (ns.ContainingNamespace?.IsGlobalNamespace ?? false))
                    return true;
            return false;
        }
    }
}
