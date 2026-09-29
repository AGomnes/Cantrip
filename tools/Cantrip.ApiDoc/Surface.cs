using Microsoft.CodeAnalysis;

namespace Cantrip.ApiDoc;

/// <summary>What the reference is about: every public type and member, in a fixed order.</summary>
public static class Surface
{
    public static IEnumerable<ApiNamespace> Read(Compilation compilation, SourceSet set)
    {
        var byNamespace = new SortedDictionary<string, List<ApiType>>(StringComparer.Ordinal);

        foreach (INamedTypeSymbol type in Types(compilation.Assembly.GlobalNamespace))
        {
            if (!IsVisible(type)) continue;

            string ns = type.ContainingNamespace.IsGlobalNamespace ? "(global)" : type.ContainingNamespace.ToDisplayString();
            if (!byNamespace.TryGetValue(ns, out List<ApiType>? types)) byNamespace[ns] = types = new List<ApiType>();
            types.Add(Describe(type));
        }

        foreach (KeyValuePair<string, List<ApiType>> entry in byNamespace)
        {
            entry.Value.Sort((a, b) => string.CompareOrdinal(a.SortKey, b.SortKey));
            yield return new ApiNamespace(entry.Key, set, entry.Value);
        }
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol container)
    {
        foreach (ISymbol member in container.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol ns:
                    foreach (INamedTypeSymbol found in Types(ns)) yield return found;
                    break;

                case INamedTypeSymbol type:
                    yield return type;
                    foreach (INamedTypeSymbol nested in Types(type)) yield return nested;
                    break;
            }
        }
    }

    /// <summary>
    /// Whether a game can see it: public all the way out. <c>protected</c> counts on a type that
    /// can be derived from, because a game that derives from it is using that member.
    /// </summary>
    private static bool IsVisible(ISymbol symbol)
    {
        bool open = symbol.DeclaredAccessibility == Accessibility.Public
            || (IsProtected(symbol) && CanBeDerivedFrom(symbol.ContainingType));

        if (!open) return false;

        for (INamedTypeSymbol? container = symbol.ContainingType; container != null; container = container.ContainingType)
        {
            if (container.DeclaredAccessibility != Accessibility.Public && !IsProtected(container)) return false;
        }

        return true;
    }

    private static bool IsProtected(ISymbol symbol) =>
        symbol.DeclaredAccessibility is Accessibility.Protected or Accessibility.ProtectedOrInternal;

    private static bool CanBeDerivedFrom(INamedTypeSymbol? type) =>
        type is { TypeKind: TypeKind.Class, IsSealed: false, IsStatic: false };

    private static ApiType Describe(INamedTypeSymbol type)
    {
        var members = new List<ApiMember>();

        foreach (ISymbol member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared) continue;
            if (!IsVisible(member)) continue;

            if (member is IMethodSymbol method)
            {
                // Accessors are shown on their property or event, and a record's compiler-shaped
                // members are not something a game writes.
                if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet
                    or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise) continue;
                if (method.MethodKind is MethodKind.Destructor) continue;
                if (method.Name is "<Clone>$" or "PrintMembers") continue;
            }

            if (member is IFieldSymbol { AssociatedSymbol: not null }) continue;

            members.Add(new ApiMember(member, Signature.Of(member), Docs.Read(member), members.Count));
        }

        members.Sort(ApiMember.Compare);

        return new ApiType(type, Signature.OfType(type), Docs.Read(type), members);
    }
}

/// <summary>A namespace's page.</summary>
public sealed class ApiNamespace
{
    public ApiNamespace(string name, SourceSet set, List<ApiType> types)
    {
        Name = name;
        Set = set;
        Types = types;
    }

    public string Name { get; }
    public SourceSet Set { get; }
    public List<ApiType> Types { get; }

    public Coverage Coverage
    {
        get
        {
            var coverage = new Coverage();
            foreach (ApiType type in Types)
            {
                coverage.Count(type.Docs.HasSummary, isType: true);
                foreach (ApiMember member in type.Members) coverage.Count(member.Docs.HasSummary, isType: false);
            }

            return coverage;
        }
    }
}

/// <summary>One public type.</summary>
public sealed class ApiType
{
    public ApiType(INamedTypeSymbol symbol, string declaration, DocComment docs, List<ApiMember> members)
    {
        Symbol = symbol;
        Declaration = declaration;
        Docs = docs;
        Members = members;
    }

    public INamedTypeSymbol Symbol { get; }
    public string Declaration { get; }
    public DocComment Docs { get; }
    public List<ApiMember> Members { get; }

    /// <summary>The name a reader looks for, containing types first: <c>CardRuntime.ReloadReport</c>.</summary>
    public string Title => Symbol.ToDisplayString(Signature.TitleFormat);

    public string SortKey => Title;

    public string Anchor => Render.Anchor(Title);
}

/// <summary>One public member of a public type.</summary>
public sealed class ApiMember
{
    public ApiMember(ISymbol symbol, string declaration, DocComment docs, int index)
    {
        Symbol = symbol;
        Declaration = declaration;
        Docs = docs;
        Index = index;
    }

    public ISymbol Symbol { get; }
    public string Declaration { get; }
    public DocComment Docs { get; }

    /// <summary>Where it stands in the source, which is the order an enum's members must keep.</summary>
    public int Index { get; }

    /// <summary>Constructors, then fields and constants, then properties, then methods, then events.</summary>
    public int Group => Symbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor } => 0,
        IFieldSymbol => 1,
        IPropertySymbol => 2,
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => 4,
        IMethodSymbol => 3,
        IEventSymbol => 5,
        _ => 6,
    };

    public static int Compare(ApiMember a, ApiMember b)
    {
        // Enum members keep their declared order, because that order is the enum.
        if (a.Symbol.ContainingType.TypeKind == TypeKind.Enum) return a.Index.CompareTo(b.Index);

        int group = a.Group.CompareTo(b.Group);
        if (group != 0) return group;

        int name = string.CompareOrdinal(a.Symbol.Name, b.Symbol.Name);
        if (name != 0) return name;

        int declaration = string.CompareOrdinal(a.Declaration, b.Declaration);
        return declaration != 0 ? declaration : a.Index.CompareTo(b.Index);
    }
}

/// <summary>How much of the surface says what it is for.</summary>
public sealed class Coverage
{
    public int Types { get; private set; }
    public int DocumentedTypes { get; private set; }
    public int Members { get; private set; }
    public int DocumentedMembers { get; private set; }

    public int Total => Types + Members;
    public int Documented => DocumentedTypes + DocumentedMembers;

    public void Count(bool documented, bool isType)
    {
        if (isType)
        {
            Types++;
            if (documented) DocumentedTypes++;
        }
        else
        {
            Members++;
            if (documented) DocumentedMembers++;
        }
    }

    public void Add(Coverage other)
    {
        Types += other.Types;
        DocumentedTypes += other.DocumentedTypes;
        Members += other.Members;
        DocumentedMembers += other.DocumentedMembers;
    }

    public static string Percent(int part, int whole) =>
        whole == 0 ? "-" : ((part * 1000L / whole) / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    public string Report() =>
        "types " + DocumentedTypes + "/" + Types + " (" + Percent(DocumentedTypes, Types) + "), " +
        "members " + DocumentedMembers + "/" + Members + " (" + Percent(DocumentedMembers, Members) + "), " +
        "all " + Documented + "/" + Total + " (" + Percent(Documented, Total) + ")";
}
