// Shared logic for the XonoticGodot registry source generator (ADR-0003).
//
// This file is part of the netstandard2.0 analyzer assembly `XonoticGodot.SourceGen`. It deliberately
// references ONLY Microsoft.CodeAnalysis types and cannot project-reference XonoticGodot.Common
// (net8.0 lib vs netstandard2.0 analyzer). Marker attributes are therefore matched BY NAME via the
// semantic model rather than by symbol identity. ImplicitUsings is OFF, so every using is explicit.

using System;
using Microsoft.CodeAnalysis;

namespace XonoticGodot.SourceGen
{
    /// <summary>
    /// The registry categories the generator understands. Mirrors the attribute set in
    /// <c>XonoticGodot.Common.Framework.Registry</c> that has a corresponding <c>Registry&lt;T&gt;</c> catalog.
    /// </summary>
    internal enum RegistryCategory
    {
        /// <summary>Not a recognised registry attribute.</summary>
        None = 0,
        Weapon,
        Item,
        Mutator,
        GameType,
        Monster,
        Turret,
        Vehicle,
    }

    /// <summary>
    /// A single discovered, registrable type, projected to a small equatable value so the incremental
    /// pipeline caches correctly (never hold <see cref="ISymbol"/> across pipeline stages).
    /// </summary>
    internal readonly struct RegisteredType : IEquatable<RegisteredType>
    {
        /// <summary>Fully-qualified type name without the <c>global::</c> prefix, e.g. <c>XonoticGodot.Common.Gameplay.Blaster</c>.</summary>
        public readonly string FullyQualifiedName;

        /// <summary>Which registry catalog this type enrolls into.</summary>
        public readonly RegistryCategory Category;

        public RegisteredType(string fullyQualifiedName, RegistryCategory category)
        {
            FullyQualifiedName = fullyQualifiedName;
            Category = category;
        }

        public bool Equals(RegisteredType other)
            => Category == other.Category
               && string.Equals(FullyQualifiedName, other.FullyQualifiedName, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is RegisteredType other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = (h * 31) + (FullyQualifiedName?.GetHashCode() ?? 0);
                h = (h * 31) + (int)Category;
                return h;
            }
        }
    }

    internal static class GeneratorHelpers
    {
        /// <summary>Target namespace and partial class the generated registration lives in.</summary>
        public const string TargetNamespace = "XonoticGodot.Common.Gameplay";

        public const string GeneratedClassName = "GeneratedRegistrations";

        public const string HintName = "GeneratedRegistrations.g.cs";

        /// <summary>Open generic registry type in Common; consumers reference Common by full name.</summary>
        private const string RegistryOpenType = "global::XonoticGodot.Common.Framework.Registry";

        // Fully-qualified metadata names of the marker attributes (used by ForAttributeWithMetadataName).
        // Weapon/Item/Mutator/GameType/Monster live in Framework (Framework/Registry.cs); Turret and Vehicle
        // were declared later, next to their descriptor bases, in the Gameplay namespace
        // (Gameplay/Turrets/TurretAI.cs, Gameplay/Vehicles/VehicleCommon.cs).
        public const string WeaponAttributeName = "XonoticGodot.Common.Framework.WeaponAttribute";
        public const string ItemAttributeName = "XonoticGodot.Common.Framework.ItemAttribute";
        public const string MutatorAttributeName = "XonoticGodot.Common.Framework.MutatorAttribute";
        public const string GameTypeAttributeName = "XonoticGodot.Common.Framework.GameTypeAttribute";
        public const string MonsterAttributeName = "XonoticGodot.Common.Framework.MonsterAttribute";
        public const string TurretAttributeName = "XonoticGodot.Common.Gameplay.TurretAttribute";
        public const string VehicleAttributeName = "XonoticGodot.Common.Gameplay.VehicleAttribute";

        public const string GameRegistryAttributeSimpleName = "GameRegistryAttribute";

        /// <summary>
        /// Format used to render the fully-qualified type name. We strip the leading <c>global::</c>
        /// because the generated <c>new ...()</c> already qualifies, and keep things deterministic.
        /// </summary>
        private static readonly SymbolDisplayFormat s_fqnFormat = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.None,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.None);

        /// <summary>
        /// Infers the registry category from a marker attribute's type symbol, matching BY NAME so no
        /// reference to XonoticGodot.Common is required. Recognises the concrete attribute names and, as a
        /// fallback, anything whose base type chain includes <c>GameRegistryAttribute</c>.
        /// Returns <see cref="RegistryCategory.None"/> for unrecognised attributes.
        /// </summary>
        public static RegistryCategory InferCategory(INamedTypeSymbol? attributeType)
        {
            if (attributeType is null)
                return RegistryCategory.None;

            RegistryCategory direct = CategoryFromSimpleName(attributeType.Name);
            if (direct != RegistryCategory.None)
                return direct;

            // Be robust to user-defined attributes that merely derive from GameRegistryAttribute:
            // walk the base chain and match the first recognised name. If only the abstract base is
            // found we cannot pick a catalog, so report None (the type is skipped).
            for (INamedTypeSymbol? b = attributeType.BaseType; b is not null; b = b.BaseType)
            {
                RegistryCategory viaBase = CategoryFromSimpleName(b.Name);
                if (viaBase != RegistryCategory.None)
                    return viaBase;
            }

            return RegistryCategory.None;
        }

        private static RegistryCategory CategoryFromSimpleName(string? simpleName)
        {
            if (simpleName is null || simpleName.Length == 0)
                return RegistryCategory.None;

            // Match on the trailing "*Attribute" name; tolerate custom subclass names that END WITH
            // one of the known markers (e.g. "SuperWeaponAttribute"). No category suffix may be a
            // suffix of another's (checked by SourceGenTests) or this greedy match would misroute.
            if (EndsWithOrdinal(simpleName, "WeaponAttribute")) return RegistryCategory.Weapon;
            if (EndsWithOrdinal(simpleName, "ItemAttribute")) return RegistryCategory.Item;
            if (EndsWithOrdinal(simpleName, "MutatorAttribute")) return RegistryCategory.Mutator;
            if (EndsWithOrdinal(simpleName, "GameTypeAttribute")) return RegistryCategory.GameType;
            if (EndsWithOrdinal(simpleName, "MonsterAttribute")) return RegistryCategory.Monster;
            if (EndsWithOrdinal(simpleName, "TurretAttribute")) return RegistryCategory.Turret;
            if (EndsWithOrdinal(simpleName, "VehicleAttribute")) return RegistryCategory.Vehicle;
            // The bare GameRegistryAttribute base intentionally has no catalog.
            return RegistryCategory.None;
        }

        private static bool EndsWithOrdinal(string value, string suffix)
            => value.EndsWith(suffix, StringComparison.Ordinal);

        /// <summary>Fully-qualified name of the concrete type, sans <c>global::</c>.</summary>
        public static string GetFullyQualifiedName(INamedTypeSymbol type)
            => type.ToDisplayString(s_fqnFormat);

        /// <summary>The base catalog type argument for a category (Weapon/Pickup/MutatorBase/GameType/Monster/Turret/Vehicle).</summary>
        public static string CatalogElementType(RegistryCategory category) => category switch
        {
            RegistryCategory.Weapon => "global::XonoticGodot.Common.Gameplay.Weapon",
            RegistryCategory.Item => "global::XonoticGodot.Common.Gameplay.Pickup",
            RegistryCategory.Mutator => "global::XonoticGodot.Common.Gameplay.MutatorBase",
            RegistryCategory.GameType => "global::XonoticGodot.Common.Gameplay.GameType",
            RegistryCategory.Monster => "global::XonoticGodot.Common.Gameplay.Monster",
            RegistryCategory.Turret => "global::XonoticGodot.Common.Gameplay.Turret",
            RegistryCategory.Vehicle => "global::XonoticGodot.Common.Gameplay.Vehicle",
            _ => string.Empty,
        };

        /// <summary>Closed registry type for a category, e.g. <c>Registry&lt;Weapon&gt;</c> fully qualified.</summary>
        public static string ClosedRegistryType(RegistryCategory category)
            => RegistryOpenType + "<" + CatalogElementType(category) + ">";

        /// <summary>
        /// True for types we can safely <c>new()</c> and register: a concrete (non-abstract,
        /// non-static), non-generic, accessible class with a usable parameterless constructor.
        /// </summary>
        public static bool IsRegistrable(INamedTypeSymbol type)
        {
            if (type.TypeKind != TypeKind.Class)
                return false;
            if (type.IsAbstract || type.IsStatic)
                return false;
            // Open generics / unbound type parameters cannot be instantiated by the generated code.
            if (type.IsGenericType || type.Arity > 0)
                return false;
            if (type.IsUnboundGenericType)
                return false;
            // Must be reachable from the generated code in XonoticGodot.Common.Gameplay.
            if (type.DeclaredAccessibility != Accessibility.Public
                && type.DeclaredAccessibility != Accessibility.Internal)
                return false;

            return HasUsableParameterlessConstructor(type);
        }

        private static bool HasUsableParameterlessConstructor(INamedTypeSymbol type)
        {
            bool sawExplicitCtor = false;
            foreach (IMethodSymbol ctor in type.InstanceConstructors)
            {
                sawExplicitCtor = true;
                if (ctor.Parameters.Length == 0
                    && ctor.DeclaredAccessibility != Accessibility.Private
                    && ctor.DeclaredAccessibility != Accessibility.Protected
                    && ctor.DeclaredAccessibility != Accessibility.ProtectedAndInternal)
                {
                    return true;
                }
            }

            // No instance constructors listed at all => implicit public default ctor.
            return !sawExplicitCtor;
        }
    }
}
