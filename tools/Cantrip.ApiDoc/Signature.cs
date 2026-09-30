using System.Text;
using Microsoft.CodeAnalysis;

namespace Cantrip.ApiDoc;

/// <summary>Declarations as a reader would write them, with the modifiers that matter.</summary>
public static class Signature
{
    /// <summary>A nested type reads as <c>CardRuntime.ReloadReport</c>, which is what a game types.</summary>
    public static readonly SymbolDisplayFormat TitleFormat = new SymbolDisplayFormat(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    private static readonly SymbolDisplayFormat TypeReferenceFormat = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly SymbolDisplayFormat MemberFormat = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
            | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeRef
            | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeExtensionThis,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public static string OfType(INamedTypeSymbol type)
    {
        var text = new StringBuilder();
        text.Append(Access(type)).Append(' ');

        if (type.IsStatic) text.Append("static ");
        else if (type.TypeKind == TypeKind.Class)
        {
            if (type.IsAbstract) text.Append("abstract ");
            else if (type.IsSealed) text.Append("sealed ");
        }

        if (type.IsRecord) text.Append("record ");

        text.Append(type.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Struct => type.IsRecord ? "struct" : "struct",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => type.IsRecord ? string.Empty : "class",
        });

        if (text[text.Length - 1] != ' ') text.Append(' ');
        text.Append(type.ToDisplayString(TitleFormat));

        if (type.TypeKind == TypeKind.Enum)
        {
            // An enum's base type is `System.Enum`, which is true and says nothing. Its underlying
            // type is what a reader might need, and only when it is not the one everybody assumes.
            if (type.EnumUnderlyingType?.SpecialType != SpecialType.System_Int32)
                text.Append(" : ").Append(type.EnumUnderlyingType?.ToDisplayString(TypeReferenceFormat));
        }
        else
        {
            // An unresolved base is kept by name. Godot's `Node` and Cantrip's own `IEffectHost`
            // are error types in this tool's compilation, and "the node a scene holds derives from
            // Node" is exactly what a reader of this page needs to know.
            List<string> bases = new List<string>();
            if (type.BaseType is { } baseType
                && baseType.SpecialType != SpecialType.System_Object
                && baseType.SpecialType != SpecialType.System_ValueType)
            {
                bases.Add(baseType.ToDisplayString(TypeReferenceFormat));
            }

            foreach (INamedTypeSymbol contract in type.Interfaces.OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                // An interface a reader cannot see is not part of the surface this page describes,
                // and naming one would send them looking for a type that is not in it:
                // `Interpreter : IModifierEvaluator` for an interface that is internal. An error
                // type is kept, because that is the unresolved-base case the comment above covers.
                if (contract.TypeKind != TypeKind.Error && contract.DeclaredAccessibility != Accessibility.Public) continue;

                bases.Add(contract.ToDisplayString(TypeReferenceFormat));
            }

            if (bases.Count > 0) text.Append(" : ").Append(string.Join(", ", bases));
        }

        return text.ToString();
    }

    public static string Of(ISymbol member)
    {
        if (member.ContainingType.TypeKind == TypeKind.Enum && member is IFieldSymbol enumMember)
        {
            return enumMember.Name + (enumMember.HasConstantValue ? " = " + Literal(enumMember.ConstantValue) : string.Empty);
        }

        var text = new StringBuilder();
        if (member.ContainingType.TypeKind != TypeKind.Interface) text.Append(Access(member)).Append(' ');

        if (member is IFieldSymbol { IsConst: true }) text.Append("const ");
        else
        {
            if (member.IsStatic && member.ContainingType.TypeKind != TypeKind.Enum) text.Append("static ");
            if (member is IFieldSymbol { IsReadOnly: true }) text.Append("readonly ");
            if (member.IsAbstract && member.ContainingType.TypeKind != TypeKind.Interface) text.Append("abstract ");
            else if (member.IsOverride) text.Append("override ");
            else if (member.IsVirtual) text.Append("virtual ");
        }

        if (member is IEventSymbol) text.Append("event ");

        text.Append(member is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor
            ? constructor.ContainingType.Name + Parameters(constructor)
            : member.ToDisplayString(MemberFormat));

        if (member is IFieldSymbol { IsConst: true, HasConstantValue: true } constant)
        {
            text.Append(" = ").Append(Literal(constant.ConstantValue));
        }

        return text.ToString();
    }

    private static string Parameters(IMethodSymbol method) =>
        "(" + string.Join(", ", method.Parameters.Select(p => p.ToDisplayString(MemberFormat))) + ")";

    private static string Access(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        _ => "public",
    };

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        bool flag => flag ? "true" : "false",
        char c => "'" + c + "'",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
