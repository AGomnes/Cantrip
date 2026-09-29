using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cantrip.ApiDoc;

/// <summary>
/// Writes <c>docs/api/</c> from the XML documentation comments in the sources.
/// </summary>
/// <remarks>
/// <para>
/// The public surface of this library is frozen for the whole 1.x line, and a frozen surface with
/// no reference is a surface nobody can read. <c>docs/csharp.md</c> is a guide: it teaches the
/// twenty calls a game needs in the order a game needs them, and it is deliberately not a list of
/// everything. This is the list of everything.
/// </para>
/// <para>
/// It is generated rather than written, and CI regenerates it and fails on a difference
/// (<c>--check</c>), so it cannot drift from the code the way a hand-written reference does. It
/// reads the sources rather than a built assembly, with Roslyn, for three reasons: the same code
/// path then covers the Godot node, whose assembly needs Godot to build; nothing has to be built
/// before the docs can be regenerated; and the documentation comments are in the syntax tree, so
/// no separate XML file has to be kept in step.
/// </para>
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        string? root = null;
        bool check = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--check": check = true; break;
                case "--root" when i + 1 < args.Length: root = args[++i]; break;
                case "--help" or "-h":
                    Console.WriteLine("cantrip-apidoc [--root <repository>] [--check]");
                    Console.WriteLine("  Writes docs/api/ from the XML documentation comments.");
                    Console.WriteLine("  --check regenerates and fails if what is on disk differs.");
                    return 0;
                default:
                    Console.Error.WriteLine("cantrip-apidoc: unknown argument `" + args[i] + "`. Try --help.");
                    return 2;
            }
        }

        root ??= FindRoot(Directory.GetCurrentDirectory());
        if (root == null)
        {
            Console.Error.WriteLine("cantrip-apidoc: run this from inside the repository, or pass --root.");
            return 2;
        }

        SourceSet[] sets =
        {
            new SourceSet(
                "Cantrip.Core",
                "The rules engine: everything a game written in C# touches, and everything a tool that reads content touches.",
                Sources(Path.Combine(root, "src", "Cantrip.Core")),
                Array.Empty<string>()),

            new SourceSet(
                "Cantrip.GodotAdapter",
                "The Godot addon's script surface: the `CantripRuntime` node a scene holds, and everything it publishes to GDScript.",
                // Both halves of the addon. `shared/` is where the words a game compares against
                // live -- SaveCheck.NameOf, ChoiceAnswer.NameOf, Words -- and docs/stability.md
                // freezes "the Godot node's methods, signals and dictionary keys", so a reference
                // that listed only `runtime/` left half of what freezes out of the list.
                Sources(Path.Combine(root, "godot", "Cantrip.Demo", "addons", "cantrip", "runtime"))
                    .Concat(Sources(Path.Combine(root, "godot", "Cantrip.Demo", "addons", "cantrip", "shared")))
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToList(),
                Array.Empty<string>()),
        };

        var pages = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var coverage = new Coverage();
        var index = new List<IndexEntry>();

        foreach (SourceSet set in sets)
        {
            if (set.Files.Count == 0)
            {
                Console.Error.WriteLine("cantrip-apidoc: no sources found for " + set.Name + ".");
                return 2;
            }

            Compilation compilation = Compile(set);
            foreach (ApiNamespace ns in Surface.Read(compilation, set))
            {
                pages["docs/api/" + ns.Name + ".md"] = Render.Namespace(ns);
                coverage.Add(ns.Coverage);
                index.Add(new IndexEntry(set, ns));
            }
        }

        pages["docs/api/README.md"] = Render.Index(index, coverage);

        Console.WriteLine(coverage.Report());

        return check ? Check(root, pages) : Write(root, pages);
    }

    private static int Write(string root, SortedDictionary<string, string> pages)
    {
        string directory = Path.Combine(root, "docs", "api");
        Directory.CreateDirectory(directory);

        foreach (string stale in Directory.GetFiles(directory, "*.md"))
        {
            if (!pages.ContainsKey(Relative(root, stale))) File.Delete(stale);
        }

        foreach (KeyValuePair<string, string> page in pages)
        {
            string path = Path.Combine(root, page.Key.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(path, page.Value, new UTF8Encoding(false));
            Console.WriteLine("wrote " + page.Key);
        }

        return 0;
    }

    private static int Check(string root, SortedDictionary<string, string> pages)
    {
        var wrong = new List<string>();

        foreach (KeyValuePair<string, string> page in pages)
        {
            string path = Path.Combine(root, page.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { wrong.Add(page.Key + " is missing"); continue; }
            if (Normalise(File.ReadAllText(path)) != Normalise(page.Value)) wrong.Add(page.Key + " is out of date");
        }

        string directory = Path.Combine(root, "docs", "api");
        if (!Directory.Exists(directory)) wrong.Add("docs/api/ does not exist");
        else
        {
            foreach (string extra in Directory.GetFiles(directory, "*.md"))
            {
                string relative = Relative(root, extra);
                if (!pages.ContainsKey(relative)) wrong.Add(relative + " is no longer generated");
            }
        }

        if (wrong.Count == 0)
        {
            Console.WriteLine("docs/api/ is up to date.");
            return 0;
        }

        Console.Error.WriteLine("docs/api/ does not match the sources:");
        foreach (string line in wrong) Console.Error.WriteLine("  " + line);
        Console.Error.WriteLine("Run tools/api-docs.sh to regenerate it, and commit the result.");
        return 1;
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n");

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string? FindRoot(string start)
    {
        for (DirectoryInfo? directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cantrip.sln"))) return directory.FullName;
        }

        return null;
    }

    private static List<string> Sources(string directory)
    {
        if (!Directory.Exists(directory)) return new List<string>();

        string bin = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;
        string obj = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;

        return Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(bin, StringComparison.Ordinal) && !f.Contains(obj, StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Parses one set of sources into a compilation. The references are whatever this tool is
    /// running against, which resolves every framework type the library uses; Godot's own types do
    /// not resolve and do not need to, because a base class that is an error type still leaves
    /// every member declared on the node itself readable.
    /// </summary>
    private static Compilation Compile(SourceSet set)
    {
        var parse = new CSharpParseOptions(
            LanguageVersion.Latest,
            DocumentationMode.Parse,
            preprocessorSymbols: set.Symbols);

        IEnumerable<SyntaxTree> trees = set.Files
            .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), parse, file));

        string platform = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        IEnumerable<MetadataReference> references = platform
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        return CSharpCompilation.Create(
            set.Name,
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }
}

/// <summary>One set of sources read as one assembly.</summary>
public sealed record SourceSet(string Name, string Blurb, List<string> Files, string[] Symbols);

/// <summary>A namespace's line in the index.</summary>
public sealed record IndexEntry(SourceSet Set, ApiNamespace Namespace);
