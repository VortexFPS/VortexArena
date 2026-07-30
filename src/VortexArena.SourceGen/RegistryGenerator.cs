// =====================================================================================================
// RegistryGenerator — Roslyn incremental source generator for the XonoticGodot (Godot/C#) port of Xonotic.
//
// Purpose (ADR-0003): replace QuakeC's REGISTER_WEAPON / [[accumulate]] compile-time registry
// metaprogramming with generated C#. Types tagged with the marker attributes
// ([Weapon]/[Item]/[Mutator]/[GameType]/[Monster] from XonoticGodot.Common.Framework,
// [Turret]/[Vehicle] from XonoticGodot.Common.Gameplay) are enrolled into their
// Registry<T> catalog at COMPILE TIME — no runtime reflection (cf. GameRegistries.Bootstrap).
//
// What it emits, into the *consuming* assembly (XonoticGodot.Common, where all registrable content lives):
//
//     namespace XonoticGodot.Common.Gameplay
//     {
//         public static partial class GeneratedRegistrations
//         {
//             public static void RegisterAll()
//             {
//                 XonoticGodot.Common.Framework.Registry<XonoticGodot.Common.Gameplay.Weapon>.Register(new global::SomeNs.Blaster());
//                 ...
//                 XonoticGodot.Common.Framework.Registry<XonoticGodot.Common.Gameplay.Weapon>.Sort();
//                 ...
//             }
//         }
//     }
//
// HOW A CONSUMER ENABLES THIS:
//   The generator ships in the netstandard2.0 analyzer project XonoticGodot.SourceGen, wired into
//   src/XonoticGodot.Common/XonoticGodot.Common.csproj (the assembly holding every attributed type) as:
//       <ProjectReference Include="..\XonoticGodot.SourceGen\XonoticGodot.SourceGen.csproj"
//                         OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
//   GameRegistries.Bootstrap() (Gameplay/Registries.cs) calls the generated
//   GeneratedRegistrations.RegisterAll() — zero reflection on the hot boot path; a reflection scan
//   remains only for explicitly-passed extra (mod) assemblies. Do NOT also wire the analyzer into a
//   project that REFERENCES Common (e.g. the Godot host): that compilation would gain its own — empty —
//   GeneratedRegistrations shadowing Common's real one (CS0436).
//   GeneratedRegistrations is declared `partial` so hand-written helpers can be added alongside it.
//
// CONSTRAINTS:
//   * This is a netstandard2.0 analyzer; it references only Microsoft.CodeAnalysis and CANNOT
//     project-reference XonoticGodot.Common. Marker attributes are matched BY NAME via the semantic model.
//   * Pipeline stages carry small equatable value types (RegisteredType), never ISymbol, to preserve
//     incrementality. ImplicitUsings is OFF — every using below is explicit.
// =====================================================================================================

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace XonoticGodot.SourceGen
{
    [Generator]
    public sealed class RegistryGenerator : IIncrementalGenerator
    {
        // All marker attribute metadata names we hook — one per Registry<T> catalog
        // (QC: REGISTER_WEAPON/ITEM/MUTATOR/GAMETYPE/MONSTER/TURRET/VEHICLE).
        private static readonly string[] s_markerAttributeNames =
        {
            GeneratorHelpers.WeaponAttributeName,
            GeneratorHelpers.ItemAttributeName,
            GeneratorHelpers.MutatorAttributeName,
            GeneratorHelpers.GameTypeAttributeName,
            GeneratorHelpers.MonsterAttributeName,
            GeneratorHelpers.TurretAttributeName,
            GeneratorHelpers.VehicleAttributeName,
        };

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // One syntax provider per known marker attribute name. ForAttributeWithMetadataName is the
            // fast path: it filters by attribute metadata name before invoking the transform, and works
            // without referencing the assembly that declares the attribute (matched by name). Each is
            // Collect()-ed to a (cached) array, then the arrays are Combine()-d so the single output
            // stage sees every discovered type. There is no built-in stream-merge, hence collect+combine.
            IncrementalValueProvider<ImmutableArray<RegisteredType>> collected =
                context.CompilationProvider.Select(static (_, _) => ImmutableArray<RegisteredType>.Empty);

            foreach (string attrName in s_markerAttributeNames)
            {
                IncrementalValueProvider<ImmutableArray<RegisteredType>> perAttribute = context.SyntaxProvider
                    .ForAttributeWithMetadataName(
                        attrName,
                        predicate: static (node, _) => IsCandidateNode(node),
                        transform: static (ctx, ct) => Transform(ctx, ct))
                    .Where(static rt => rt.HasValue)
                    .Select(static (rt, _) => rt!.Value)
                    .Collect();

                collected = collected
                    .Combine(perAttribute)
                    .Select(static (pair, _) => pair.Left.AddRange(pair.Right));
            }

            context.RegisterSourceOutput(collected, static (spc, types) => Emit(spc, types));
        }

        // Cheap syntactic gate: a class declaration that is not abstract/static and carries at least one
        // attribute. Keeps the (more expensive) semantic transform off obviously-irrelevant nodes.
        private static bool IsCandidateNode(SyntaxNode node)
        {
            if (node is not Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax cls)
                return false;
            if (cls.AttributeLists.Count == 0)
                return false;

            foreach (Microsoft.CodeAnalysis.CSharp.SyntaxKind kind in EnumerateModifierKinds(cls))
            {
                if (kind == Microsoft.CodeAnalysis.CSharp.SyntaxKind.AbstractKeyword
                    || kind == Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)
                {
                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<Microsoft.CodeAnalysis.CSharp.SyntaxKind> EnumerateModifierKinds(
            Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax cls)
        {
            foreach (SyntaxToken mod in cls.Modifiers)
                yield return (Microsoft.CodeAnalysis.CSharp.SyntaxKind)mod.RawKind;
        }

        // Semantic transform: resolve the tagged symbol, confirm it is registrable, infer the category
        // from the matched attribute, and project to the equatable RegisteredType. Returns null to drop.
        private static RegisteredType? Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (ctx.TargetSymbol is not INamedTypeSymbol type)
                return null;

            if (!GeneratorHelpers.IsRegistrable(type))
                return null;

            // Determine the category from the attribute(s) that matched on this declaration. A single
            // type could in principle carry more than one marker; take the first that maps to a catalog.
            RegistryCategory category = RegistryCategory.None;
            foreach (AttributeData attr in ctx.Attributes)
            {
                RegistryCategory candidate = GeneratorHelpers.InferCategory(attr.AttributeClass);
                if (candidate != RegistryCategory.None)
                {
                    category = candidate;
                    break;
                }
            }

            if (category == RegistryCategory.None)
                return null;

            return new RegisteredType(GeneratorHelpers.GetFullyQualifiedName(type), category);
        }

        private static void Emit(SourceProductionContext context, ImmutableArray<RegisteredType> types)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // Deduplicate (a type seen via multiple partial declarations or repeated markers) and order
            // deterministically by (category, fully-qualified name) so the generated file is stable.
            var seen = new HashSet<RegisteredType>();
            var unique = new List<RegisteredType>(types.Length);
            foreach (RegisteredType rt in types)
            {
                if (seen.Add(rt))
                    unique.Add(rt);
            }

            unique.Sort(static (a, b) =>
            {
                int byCat = ((int)a.Category).CompareTo((int)b.Category);
                if (byCat != 0)
                    return byCat;
                return string.CompareOrdinal(a.FullyQualifiedName, b.FullyQualifiedName);
            });

            context.AddSource(GeneratorHelpers.HintName, BuildSource(unique));
        }

        private static SourceText BuildSource(List<RegisteredType> types)
        {
            var sb = new StringBuilder(1024);

            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("// Generated by XonoticGodot.SourceGen.RegistryGenerator (ADR-0003). Do not edit.");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.Append("namespace ").Append(GeneratorHelpers.TargetNamespace).AppendLine();
            sb.AppendLine("{");
            sb.Append("    public static partial class ").Append(GeneratorHelpers.GeneratedClassName).AppendLine();
            sb.AppendLine("    {");
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Compile-time registry population (the source-generated successor to");
            sb.AppendLine("        /// GameRegistries.Bootstrap). Idempotent: Registry&lt;T&gt;.Register dedups by RegistryName.");
            sb.AppendLine("        /// Call once at startup; no runtime reflection is used.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public static void RegisterAll()");
            sb.AppendLine("        {");

            if (types.Count == 0)
            {
                sb.AppendLine("            // No [Weapon]/[Item]/[Mutator]/[GameType]/[Monster]/[Turret]/[Vehicle]-tagged types were found.");
            }
            else
            {
                AppendRegistrations(sb, types);
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return SourceText.From(sb.ToString(), Encoding.UTF8);
        }

        private static void AppendRegistrations(StringBuilder sb, List<RegisteredType> types)
        {
            RegistryCategory currentCategory = RegistryCategory.None;
            var categoriesInOrder = new List<RegistryCategory>();

            foreach (RegisteredType rt in types)
            {
                if (rt.Category != currentCategory)
                {
                    if (currentCategory != RegistryCategory.None)
                        sb.AppendLine();
                    currentCategory = rt.Category;
                    categoriesInOrder.Add(currentCategory);
                    sb.Append("            // ").Append(currentCategory).AppendLine();
                }

                sb.Append("            ")
                  .Append(GeneratorHelpers.ClosedRegistryType(rt.Category))
                  .Append(".Register(new global::")
                  .Append(rt.FullyQualifiedName)
                  .AppendLine("());");
            }

            // Deterministic CL/SV ordering: Sort() each touched catalog after all Register() calls.
            sb.AppendLine();
            sb.AppendLine("            // Deterministic ordering for client/server agreement (FNV content hash).");
            foreach (RegistryCategory category in categoriesInOrder)
            {
                sb.Append("            ")
                  .Append(GeneratorHelpers.ClosedRegistryType(category))
                  .AppendLine(".Sort();");
            }
        }
    }
}
