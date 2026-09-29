using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Cantrip.Syntax;

namespace Cantrip.Content
{
    /// <summary>A <c>test</c> block together with the file it came from.</summary>
    public sealed class TestDefinition
    {
        internal TestDefinition(TestDeclNode syntax, string file)
        {
            Syntax = syntax;
            File = file;
        }

        /// <summary>The parsed <c>test</c> block: its name, its body and its span.</summary>
        public TestDeclNode Syntax { get; }
        /// <summary>
        /// The file this was loaded from, as it was named to the loader — a real path for
        /// <see cref="ContentLibrary.LoadFile"/>, and whatever a host passed to
        /// <see cref="ContentLibrary.LoadText"/> otherwise.
        /// </summary>
        public string File { get; }
        /// <summary>
        /// The name in the <c>test</c> line. Nothing enforces that it is unique across a library, so a
        /// report that groups by it may merge two different tests.
        /// </summary>
        public string Name => Syntax.Name;
    }

    /// <summary>A <c>scenario</c> block together with the file it came from.</summary>
    public sealed class ScenarioDefinition
    {
        internal ScenarioDefinition(ScenarioDeclNode syntax, string file)
        {
            Syntax = syntax;
            File = file;
        }

        /// <summary>The parsed <c>scenario</c> block: its name, its body and its span.</summary>
        public ScenarioDeclNode Syntax { get; }
        /// <summary>The file this was loaded from, named as <see cref="TestDefinition.File"/> is.</summary>
        public string File { get; }
        /// <summary>The name in the <c>scenario</c> line, which the simulator reports its numbers under.</summary>
        public string Name => Syntax.Name;
    }

    /// <summary>
    /// Every definition loaded from DSL files. Parse errors are collected rather than thrown, so a
    /// designer sees all the problems in a folder at once. Reloading a file replaces exactly the
    /// definitions that file contributed, which is what makes hot reload cheap.
    /// </summary>
    public sealed class ContentLibrary
    {
        /// <summary>File extension used by <see cref="LoadFolder"/> when none is given.</summary>
        public const string DefaultExtension = ".cantrip";

        private readonly Dictionary<(string Kind, string Name), EntityDefinition> _definitions =
            new Dictionary<(string, string), EntityDefinition>();

        private readonly Dictionary<string, List<EntityDefinition>> _byName =
            new Dictionary<string, List<EntityDefinition>>(StringComparer.OrdinalIgnoreCase);

        // Pools sorted for the RNG, rebuilt whenever Generation moves. See Pool().
        private readonly Dictionary<string, IReadOnlyList<EntityDefinition>> _pools =
            new Dictionary<string, IReadOnlyList<EntityDefinition>>(StringComparer.OrdinalIgnoreCase);

        private int _poolGeneration = -1;

        private readonly Dictionary<string, VerbDefinition> _verbs =
            new Dictionary<string, VerbDefinition>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, ResourceRule> _resources =
            new Dictionary<string, ResourceRule>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Boards by name, and in declaration order, so "the one declared first" is stable.</summary>
        private readonly Dictionary<string, BoardShape> _boards = new Dictionary<string, BoardShape>(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> _boardOrder = new List<string>();

        private readonly List<TestDefinition> _tests = new List<TestDefinition>();
        private readonly List<ScenarioDefinition> _scenarios = new List<ScenarioDefinition>();
        private readonly Dictionary<string, SourceFileNode> _files = new Dictionary<string, SourceFileNode>(FileNames);
        private readonly Dictionary<string, DiagnosticBag> _fileDiagnostics = new Dictionary<string, DiagnosticBag>(FileNames);

        /// <summary>Ruleset blocks in load order. A list, not a dictionary, so "last loaded" survives unloads.</summary>
        private readonly List<(string File, RulesetDeclNode Syntax)> _rulesets = new List<(string, RulesetDeclNode)>();

        /// <summary>
        /// An empty library with the built-in resources already in it — <c>hp</c>, <c>block</c>,
        /// <c>energy</c>, <c>stacks</c> and <c>gold</c> — which content may override with a
        /// <c>resource</c> block of its own. Load files into it with <see cref="LoadText"/>,
        /// <see cref="LoadFile"/> or <see cref="LoadFolder"/>.
        /// </summary>
        public ContentLibrary()
        {
            AddBuiltInResources();
        }

        /// <summary>Incremented whenever content changes, so a runtime knows to rebind live entities.</summary>
        public int Generation { get; private set; }

        /// <summary>
        /// A stable hash of what is loaded: every definition's kind and name, plus verb and resource
        /// names. A save file records it and refuses to restore against content that has changed
        /// underneath it, which turns "a definition this snapshot needs is missing" into a message
        /// the game can show before anything goes wrong.
        /// </summary>
        public string Fingerprint
        {
            get
            {
                var names = new List<string>();
                foreach (EntityDefinition definition in _definitions.Values) names.Add(definition.KindName + ":" + definition.Name);
                foreach (string verb in _verbs.Keys) names.Add("verb:" + verb);
                foreach (string resource in _resources.Keys) names.Add("resource:" + resource);
                // The name only, as for everything else here: a board that has been reshaped is
                // still the board the save names, and a restore judges the new shape itself.
                foreach (BoardShape board in _boards.Values) names.Add("board:" + board.Name);
                names.Sort(StringComparer.OrdinalIgnoreCase);

                ulong hash = 14695981039346656037UL;
                foreach (string name in names)
                {
                    foreach (char c in name)
                    {
                        unchecked
                        {
                            hash ^= char.ToLowerInvariant(c);
                            hash *= 1099511628211UL;
                        }
                    }
                    unchecked
                    {
                        hash ^= '\n';
                        hash *= 1099511628211UL;
                    }
                }
                return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Every diagnostic from every loaded file, in load order.</summary>
        public DiagnosticBag Diagnostics
        {
            get
            {
                var all = new DiagnosticBag();
                foreach (DiagnosticBag bag in _fileDiagnostics.Values) all.AddRange(bag);
                return all;
            }
        }

        /// <summary>
        /// Every definition loaded, of every kind, <c>resource</c> and <c>board</c> included — filter on
        /// <see cref="EntityDefinition.IsThing"/> for the ones a game can make one of.
        /// </summary>
        /// <remarks>
        /// The order is a dictionary's, and a reload removes and re-adds entries, so it is not stable
        /// and must never reach the RNG. Anything that picks a definition at random reads
        /// <see cref="Pool"/> instead, or the same seed would stop replaying.
        /// </remarks>
        public IEnumerable<EntityDefinition> Definitions => _definitions.Values;

        /// <summary>The verbs content declares. Verbs the game registers from C# are not here; they belong to the runtime.</summary>
        public IEnumerable<VerbDefinition> Verbs => _verbs.Values;

        /// <summary>Every <c>test</c> block, in load order — which is the order <c>DslTestRunner.RunAll(string)</c> runs them in.</summary>
        public IReadOnlyList<TestDefinition> Tests => _tests;

        /// <summary>Every <c>scenario</c> block, in load order. A real-time game should declare none: a bot plays a scenario by taking turns.</summary>
        public IReadOnlyList<ScenarioDefinition> Scenarios => _scenarios;

        /// <summary>
        /// Every resource and its bounds, keyed by stat name, case-insensitively. It always has the
        /// built-in five in it, so it is never empty and its count is not a measure of what content
        /// declared.
        /// </summary>
        public IReadOnlyDictionary<string, ResourceRule> Resources => _resources;

        /// <summary>
        /// The parsed syntax tree of every file loaded, for a tool that wants the text rather than the
        /// definitions. A game never needs it.
        /// </summary>
        public IEnumerable<SourceFileNode> Files => _files.Values;

        /// <summary>The boards content declares, in declaration order. Empty when it declares none.</summary>
        public IReadOnlyList<BoardShape> Boards =>
            _boardOrder.Where(_boards.ContainsKey).Select(name => _boards[name]).ToList();

        /// <summary>The board of that name, or null. Names compare case-insensitively, as everything does.</summary>
        public BoardShape? Board(string name) =>
            name != null && _boards.TryGetValue(name, out BoardShape? board) ? board : null;

        /// <summary>
        /// The board a battle is fought on when nobody names one: the first board content declares,
        /// or <see cref="BoardShape.Default"/> when it declares none — today's board, spelled out,
        /// which is what keeps every game written before boards existed working unchanged.
        /// </summary>
        public BoardShape DefaultBoard
        {
            get
            {
                foreach (string name in _boardOrder)
                {
                    if (_boards.TryGetValue(name, out BoardShape? board)) return board;
                }
                return BoardShape.Default;
            }
        }

        /// <summary>The ruleset declared in content, merged with defaults. Null when content declares none.</summary>
        public RulesetDeclNode? RulesetSyntax => _rulesets.Count == 0 ? null : _rulesets[_rulesets.Count - 1].Syntax;

        /// <summary>
        /// A library holding one piece of text, for a test or a snippet. It does <b>not</b> throw on
        /// errors — check <see cref="Diagnostics"/>, or use <c>CardRuntime.FromText(string, RuntimeOptions)</c>,
        /// which does.
        /// </summary>
        /// <param name="text">The content, as it would be written in a <c>.cantrip</c> file.</param>
        /// <param name="file">The name diagnostics will point at. It does not have to exist.</param>
        public static ContentLibrary FromText(string text, string file = "<inline>")
        {
            var library = new ContentLibrary();
            library.LoadText(text, file);
            return library;
        }

        /// <summary>Builds the effective ruleset: defaults overridden by any <c>ruleset</c> block.</summary>
        public Ruleset BuildRuleset(DiagnosticBag? diagnostics = null)
        {
            RulesetDeclNode? syntax = RulesetSyntax;
            return syntax == null ? Ruleset.CreateDefault() : Ruleset.FromSyntax(syntax, diagnostics ?? new DiagnosticBag());
        }

        // Loading ------------------------------------------------------------------------------

        /// <summary>Parses and registers a source file. Returns that file's diagnostics.</summary>
        public DiagnosticBag LoadText(string text, string file)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            file ??= "<inline>";

            Unload(file);

            var diagnostics = new DiagnosticBag();
            SourceFileNode syntax = Parser.Parse(text, file, diagnostics);
            _files[file] = syntax;
            _fileDiagnostics[file] = diagnostics;

            foreach (DeclarationNode declaration in syntax.Declarations)
                Register(declaration, file, diagnostics);

            Generation++;
            return diagnostics;
        }

        /// <summary>
        /// Reads a file from the file system. Tools and desktop games use this; a game engine that
        /// packs content into an archive (Godot's <c>res://</c>, for one) cannot, because this goes
        /// through <see cref="File"/>. Such a host reads the text its own way and calls
        /// <see cref="LoadText"/>, which is the only seam that needs no file system at all.
        /// </summary>
        public DiagnosticBag LoadFile(string path) => LoadText(File.ReadAllText(path), path);

        /// <summary>
        /// Loads every matching file under a folder, in ordinal path order for determinism. Like
        /// <see cref="LoadFile"/>, this reads the real file system, so a packed game must do its own
        /// discovery and call <see cref="LoadText"/> per file, keeping the same ordinal order.
        /// </summary>
        public DiagnosticBag LoadFolder(string folder, string extension = DefaultExtension, bool recursive = true)
        {
            var all = new DiagnosticBag();
            SearchOption option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (string path in Directory.GetFiles(folder, "*" + extension, option).OrderBy(p => p, StringComparer.Ordinal))
                all.AddRange(LoadFile(path));
            return all;
        }

        /// <summary>Removes everything a file contributed.</summary>
        public void Unload(string file)
        {
            if (!_files.Remove(file)) return;
            _fileDiagnostics.Remove(file);
            _rulesets.RemoveAll(r => SameFile(r.File, file));

            foreach (var key in _definitions.Where(kv => SameFile(kv.Value.Syntax.Span.File, file)).Select(kv => kv.Key).ToList())
            {
                EntityDefinition definition = _definitions[key];
                _definitions.Remove(key);
                if (_byName.TryGetValue(definition.Name, out List<EntityDefinition>? list)) list.Remove(definition);
                if (definition.KindName == "resource") _resources.Remove(definition.Name);
                if (definition.KindName == "board")
                {
                    _boards.Remove(definition.Name);
                    _boardOrder.RemoveAll(n => string.Equals(n, definition.Name, StringComparison.OrdinalIgnoreCase));
                }
            }

            foreach (string verb in _verbs.Where(kv => SameFile(kv.Value.Syntax.Span.File, file)).Select(kv => kv.Key).ToList())
                _verbs.Remove(verb);

            _tests.RemoveAll(t => SameFile(t.File, file));
            _scenarios.RemoveAll(s => SameFile(s.File, file));
            AddBuiltInResources();
            Generation++;
        }

        /// <summary>
        /// File names compare case-insensitively and with either slash, matching the file table. File
        /// watchers on Windows report the same file with whatever casing they like, and
        /// <see cref="LoadFolder"/> spells a Windows path with backslashes where a game may write
        /// <c>content/cards.cantrip</c>; a reload must replace the file either way.
        /// </summary>
        private static bool SameFile(string a, string b) => FileNames.Equals(a, b);

        private static readonly FileNameComparer FileNames = new FileNameComparer();

        private sealed class FileNameComparer : IEqualityComparer<string>
        {
            public bool Equals(string? a, string? b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(string name) => StringComparer.OrdinalIgnoreCase.GetHashCode(Normalize(name)!);

            private static string? Normalize(string? name) => name?.Replace('\\', '/');
        }

        private void Register(DeclarationNode declaration, string file, DiagnosticBag diagnostics)
        {
            switch (declaration)
            {
                case EntityDeclNode entity:
                {
                    // Reserved words with no meaning yet. Loading them as inert definitions would let
                    // content look finished while nothing ever ran it.
                    if (entity.Kind == "event" || entity.Kind == "encounter")
                    {
                        diagnostics.Error("CT0113", $"`{entity.Kind}` declarations are reserved but not supported yet, so {entity.Kind} \"{entity.Name}\" would never run.", entity.Span);
                        return;
                    }

                    var key = (entity.Kind, entity.Name.ToLowerInvariant());
                    if (_definitions.TryGetValue(key, out EntityDefinition? existing))
                    {
                        diagnostics.Error("CT0110", $"{entity.Kind} \"{entity.Name}\" is already defined at {existing.Syntax.Span}.", entity.Span);
                        return;
                    }

                    var definition = new EntityDefinition(entity, diagnostics);
                    _definitions[key] = definition;

                    // A resource names a stat, not a thing: `mana` in an expression must read the
                    // stat, so resources are kept out of name lookup.
                    if (entity.Kind == "resource")
                    {
                        _resources[definition.Name] = ResourceRule.FromDefinition(definition);
                        break;
                    }

                    // A board names a shape, not a thing anything is ever made from, so it is kept
                    // out of name lookup for the same reason a resource is.
                    if (entity.Kind == "board")
                    {
                        _boards[definition.Name] = BoardShape.FromDefinition(definition, diagnostics);
                        _boardOrder.Add(definition.Name);
                        break;
                    }

                    if (!_byName.TryGetValue(definition.Name, out List<EntityDefinition>? list))
                    {
                        list = new List<EntityDefinition>();
                        _byName[definition.Name] = list;
                    }
                    list.Add(definition);
                    break;
                }

                case VerbDeclNode verb:
                    if (_verbs.TryGetValue(verb.Name, out VerbDefinition? existingVerb))
                    {
                        diagnostics.Error("CT0111", $"verb `{verb.Name}` is already defined at {existingVerb.Syntax.Span}.", verb.Span);
                        return;
                    }
                    _verbs[verb.Name] = new VerbDefinition(verb);
                    break;

                case RulesetDeclNode ruleset:
                    if (_rulesets.Count > 0)
                        diagnostics.Warn("CT0112", "More than one ruleset is loaded; the last one loaded wins.", ruleset.Span);
                    _rulesets.Add((file, ruleset));
                    Ruleset.FromSyntax(ruleset, diagnostics); // validate eagerly so errors show at load time
                    break;

                case TestDeclNode test:
                    _tests.Add(new TestDefinition(test, file));
                    break;

                case ScenarioDeclNode scenario:
                    _scenarios.Add(new ScenarioDefinition(scenario, file));
                    break;
            }
        }

        // Lookup -------------------------------------------------------------------------------

        /// <summary>
        /// The definition of that name, or null. Names compare case-insensitively, as everywhere else.
        /// </summary>
        /// <param name="name">A definition's name, as content wrote it.</param>
        /// <param name="kind">
        /// The declaring keyword — <c>card</c>, <c>status</c>, <c>enemy</c> — which is what makes the
        /// answer unambiguous. Null takes the first definition of that name whatever its kind, and
        /// "first" is not defined when two kinds share a name, so pass a kind whenever the caller knows
        /// one. <see cref="FindAny"/> is the way to ask for several in a stated order.
        /// </param>
        public EntityDefinition? Find(string name, string? kind = null)
        {
            if (string.IsNullOrEmpty(name)) return null;

            if (kind != null)
                return _definitions.TryGetValue((kind, name.ToLowerInvariant()), out EntityDefinition? exact) ? exact : null;

            if (!_byName.TryGetValue(name, out List<EntityDefinition>? list) || list.Count == 0) return null;
            return list[0];
        }

        /// <summary>
        /// Every definition declared with one keyword, in a stated order: sorted by
        /// <c>kind:name</c> with <see cref="StringComparer.OrdinalIgnoreCase"/>, the same key and
        /// comparer <see cref="Fingerprint"/> sorts by. Cached until content changes.
        /// </summary>
        /// <remarks>
        /// <see cref="Definitions"/> enumerates a dictionary, and a reload removes then re-adds its
        /// entries, so its order is not a contract and must never reach the RNG. Anything that picks
        /// content at random reads it from here instead, or the same seed would stop replaying.
        /// </remarks>
        public IReadOnlyList<EntityDefinition> Pool(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return Array.Empty<EntityDefinition>();

            if (_poolGeneration != Generation)
            {
                _pools.Clear();
                _poolGeneration = Generation;
            }

            if (_pools.TryGetValue(kind, out IReadOnlyList<EntityDefinition>? cached)) return cached;

            var members = new List<EntityDefinition>();
            foreach (EntityDefinition definition in _definitions.Values)
            {
                if (string.Equals(definition.KindName, kind, StringComparison.OrdinalIgnoreCase)) members.Add(definition);
            }
            members.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.KindName + ":" + a.Name, b.KindName + ":" + b.Name));

            _pools[kind] = members;
            return members;
        }

        /// <summary>
        /// The first match among several kinds, in the order given — how a call that accepts either a
        /// <c>relic</c> or an <c>item</c> asks for both without guessing which came first.
        /// </summary>
        public EntityDefinition? FindAny(string name, params string[] kinds)
        {
            foreach (string kind in kinds)
            {
                EntityDefinition? found = Find(name, kind);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// The verb content declares under that name, or null. A verb the game registered from C# is not
        /// here; ask <c>Interpreter.IsVerb(string)</c> to cover both.
        /// </summary>
        public VerbDefinition? FindVerb(string name) => _verbs.TryGetValue(name, out VerbDefinition? verb) ? verb : null;

        /// <summary>
        /// The bounds and reset behaviour declared for a stat, or null when the stat is just a number
        /// with no rules attached. The built-in five always answer.
        /// </summary>
        public ResourceRule? Resource(string stat) => _resources.TryGetValue(stat, out ResourceRule? rule) ? rule : null;

        /// <summary>
        /// Every name that has at least one definition, once each however many kinds share it. It is
        /// what a "did you mean?" or an editor's completion list is built from.
        /// </summary>
        public IEnumerable<string> AllNames => _byName.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key);

        /// <summary>
        /// hp, block, energy and stacks behave the way players expect without any declarations.
        /// Content can override any of them with its own <c>resource</c> block.
        /// </summary>
        private void AddBuiltInResources()
        {
            SourceSpan span = SourceSpan.None;
            NumberExpr Zero() => new NumberExpr(Num.Zero, null, span);

            AddIfMissing(new ResourceRule("hp") { Min = Zero(), Max = new NameExpr("max_hp", span) });
            AddIfMissing(new ResourceRule("block") { Min = Zero(), ResetTo = Zero(), ResetOn = "turn_start" });
            AddIfMissing(new ResourceRule("energy") { Min = Zero(), ResetTo = new NameExpr("max_energy", span), ResetOn = "turn_start" });
            AddIfMissing(new ResourceRule("stacks") { Min = Zero() });
            AddIfMissing(new ResourceRule("gold") { Min = Zero() });

            void AddIfMissing(ResourceRule rule)
            {
                if (!_resources.ContainsKey(rule.Stat)) _resources[rule.Stat] = rule;
            }
        }
    }
}
