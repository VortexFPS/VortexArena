using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace XonoticGodot.SourceGen
{
    /// <summary>
    /// Roslyn analyzer enforcing the godot#105750 per-frame-allocation convention (planning/PERFORMANCE_REPORT.md C3,
    /// Tier 1). Godot's C# marshaling mints a managed allocation for many string-keyed engine APIs (a
    /// <c>StringName</c>/<c>NodePath</c>/<c>Godot.Collections.Array</c> per call) — invisible to a
    /// <c>new List&lt;&gt;</c>/<c>$"..."</c> grep and a GC treadmill at high framerate. This codebase is clean of
    /// these APIs <em>by construction</em> (gameplay input reads the port's own <c>BindTable</c>, not Godot's
    /// string <c>InputMap</c>; entities live in typed dictionaries, not the scene-group system), so a hard ban on
    /// the never-used offenders has ZERO false positives and locks in a property that is currently true only by
    /// habit. Suppressible per-line (<c>#pragma warning disable XG0001</c>) on the rare genuine need.
    ///
    /// <para>The analyzer is referenced as an Analyzer by both <c>XonoticGodot.Common</c> and the Godot host
    /// (<c>XonoticGodot.csproj</c>) so it runs over <c>game/</c> — where 100% of the Godot interop lives. The host
    /// csproj elevates <c>XG0001</c> to an error via <c>WarningsAsErrors</c> so a regression fails the build.</para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class StringApiAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "XG0001";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            title: "Avoid string-keyed Godot API (per-frame marshaling alloc)",
            messageFormat: "{0}",
            category: "Performance",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description:
                "Godot's C# marshaling allocates a StringName/NodePath/Collections.Array per call for these " +
                "string-keyed APIs (godot#105750) — a GC treadmill at high framerate. The port uses its own " +
                "BindTable for input and typed dictionaries for entities, so these are never legitimately needed. " +
                "Suppress with #pragma warning disable XG0001 on a genuine exception.",
            helpLinkUri: "https://github.com/godotengine/godot/issues/105750");

        // String input APIs on Godot.Input — the port reads its own BindTable instead.
        private static readonly ImmutableHashSet<string> InputApis = ImmutableHashSet.Create(
            "IsActionPressed", "IsActionJustPressed", "IsActionJustReleased",
            "GetActionStrength", "GetActionRawStrength");

        // Scene-group APIs (return/take a heap Godot.Collections.Array / string group name) — entities are held
        // in typed dictionaries here, not the group system.
        private static readonly ImmutableHashSet<string> GroupApis = ImmutableHashSet.Create(
            "GetNodesInGroup", "CallGroup", "CallGroupFlags", "AddToGroup", "RemoveFromGroup");

        // String-path node lookups — typed dictionaries are used instead of the scene tree path system.
        private static readonly ImmutableHashSet<string> NodePathApis = ImmutableHashSet.Create(
            "GetNode", "GetNodeOrNull");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var op = (IInvocationOperation)context.Operation;
            IMethodSymbol method = op.TargetMethod;
            string name = method.Name;

            // Only methods declared on a Godot engine type are offenders (a user-defined same-named method is fine).
            if (!IsGodotType(method.ContainingType))
                return;

            string? message = null;
            if (InputApis.Contains(name))
                message = $"'Input.{name}' mints a StringName per call (godot#105750). Read the port's BindTable instead of Godot's string InputMap.";
            else if (GroupApis.Contains(name))
                message = $"'{name}' uses the string scene-group system (heap Godot.Collections.Array / group-name alloc). Hold entities in a typed dictionary instead.";
            else if (NodePathApis.Contains(name) && HasStringArgument(op))
                message = $"'{name}(string)' allocates a NodePath per call (godot#105750). Resolve nodes through a typed field/dictionary instead of a string path.";

            if (message != null)
                context.ReportDiagnostic(Diagnostic.Create(Rule, op.Syntax.GetLocation(), message));
        }

        /// <summary>True when the first argument is a <see cref="string"/> (the banned string→NodePath overload of
        /// GetNode/GetNodeOrNull; the typed <c>NodePath</c> overload is left alone).</summary>
        private static bool HasStringArgument(IInvocationOperation op)
        {
            foreach (IArgumentOperation arg in op.Arguments)
                if (arg.Value?.Type?.SpecialType == SpecialType.System_String)
                    return true;
            return false;
        }

        /// <summary>True when <paramref name="type"/> is declared in the <c>Godot</c> namespace (or a sub-namespace).</summary>
        private static bool IsGodotType(INamedTypeSymbol? type)
        {
            for (INamespaceSymbol? ns = type?.ContainingNamespace; ns != null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
                if (ns.Name == "Godot" && (ns.ContainingNamespace?.IsGlobalNamespace ?? false))
                    return true;
            return false;
        }
    }
}
