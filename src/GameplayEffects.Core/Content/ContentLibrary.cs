using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameplayEffects.Diagnostics;
using GameplayEffects.Runtime;
using GameplayEffects.Syntax;

namespace GameplayEffects.Content
{
    /// <summary>A <c>test</c> block together with the file it came from.</summary>
    public sealed class TestDefinition
    {
        internal TestDefinition(TestDeclNode syntax, string file)
        {
            Syntax = syntax;
            File = file;
        }

        public TestDeclNode Syntax { get; }
        public string File { get; }
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
        public const string DefaultExtension = ".ge";

        private readonly Dictionary<(string Kind, string Name), EntityDefinition> _definitions =
            new Dictionary<(string, string), EntityDefinition>();

        private readonly Dictionary<string, List<EntityDefinition>> _byName =
            new Dictionary<string, List<EntityDefinition>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, VerbDefinition> _verbs =
            new Dictionary<string, VerbDefinition>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, ResourceRule> _resources =
            new Dictionary<string, ResourceRule>(StringComparer.OrdinalIgnoreCase);

        private readonly List<TestDefinition> _tests = new List<TestDefinition>();
        private readonly Dictionary<string, SourceFileNode> _files = new Dictionary<string, SourceFileNode>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DiagnosticBag> _fileDiagnostics = new Dictionary<string, DiagnosticBag>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RulesetDeclNode> _rulesets = new Dictionary<string, RulesetDeclNode>(StringComparer.OrdinalIgnoreCase);

        public ContentLibrary()
        {
            AddBuiltInResources();
        }

        /// <summary>Incremented whenever content changes, so a runtime knows to rebind live entities.</summary>
        public int Generation { get; private set; }

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

        public IEnumerable<EntityDefinition> Definitions => _definitions.Values;
        public IEnumerable<VerbDefinition> Verbs => _verbs.Values;
        public IReadOnlyList<TestDefinition> Tests => _tests;
        public IReadOnlyDictionary<string, ResourceRule> Resources => _resources;
        public IEnumerable<SourceFileNode> Files => _files.Values;

        /// <summary>The ruleset declared in content, merged with defaults. Null when content declares none.</summary>
        public RulesetDeclNode? RulesetSyntax => _rulesets.Values.LastOrDefault();

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
            return syntax == null ? Ruleset.Default : Ruleset.FromSyntax(syntax, diagnostics ?? new DiagnosticBag());
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

        public DiagnosticBag LoadFile(string path) => LoadText(File.ReadAllText(path), path);

        /// <summary>Loads every matching file under a folder, in ordinal path order for determinism.</summary>
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
            _rulesets.Remove(file);

            foreach (var key in _definitions.Where(kv => kv.Value.Syntax.Span.File == file).Select(kv => kv.Key).ToList())
            {
                EntityDefinition definition = _definitions[key];
                _definitions.Remove(key);
                if (_byName.TryGetValue(definition.Name, out List<EntityDefinition>? list)) list.Remove(definition);
                if (definition.KindName == "resource") _resources.Remove(definition.Name);
            }

            foreach (string verb in _verbs.Where(kv => kv.Value.Syntax.Span.File == file).Select(kv => kv.Key).ToList())
                _verbs.Remove(verb);

            _tests.RemoveAll(t => t.File == file);
            AddBuiltInResources();
            Generation++;
        }

        private void Register(DeclarationNode declaration, string file, DiagnosticBag diagnostics)
        {
            switch (declaration)
            {
                case EntityDeclNode entity:
                {
                    var key = (entity.Kind, entity.Name.ToLowerInvariant());
                    if (_definitions.TryGetValue(key, out EntityDefinition? existing))
                    {
                        diagnostics.Error("GE0110", $"{entity.Kind} \"{entity.Name}\" is already defined at {existing.Syntax.Span}.", entity.Span);
                        return;
                    }

                    var definition = new EntityDefinition(entity, diagnostics);
                    _definitions[key] = definition;
                    if (!_byName.TryGetValue(definition.Name, out List<EntityDefinition>? list))
                    {
                        list = new List<EntityDefinition>();
                        _byName[definition.Name] = list;
                    }
                    list.Add(definition);

                    if (entity.Kind == "resource") _resources[definition.Name] = ResourceRule.FromDefinition(definition);
                    break;
                }

                case VerbDeclNode verb:
                    if (_verbs.TryGetValue(verb.Name, out VerbDefinition? existingVerb))
                    {
                        diagnostics.Error("GE0111", $"verb `{verb.Name}` is already defined at {existingVerb.Syntax.Span}.", verb.Span);
                        return;
                    }
                    _verbs[verb.Name] = new VerbDefinition(verb);
                    break;

                case RulesetDeclNode ruleset:
                    if (_rulesets.Count > 0 && !_rulesets.ContainsKey(file))
                        diagnostics.Warn("GE0112", "More than one ruleset is loaded; the last one loaded wins.", ruleset.Span);
                    _rulesets[file] = ruleset;
                    Ruleset.FromSyntax(ruleset, diagnostics); // validate eagerly so errors show at load time
                    break;

                case TestDeclNode test:
                    _tests.Add(new TestDefinition(test, file));
                    break;
            }
        }

        // Lookup -------------------------------------------------------------------------------

        public EntityDefinition? Find(string name, string? kind = null)
        {
            if (string.IsNullOrEmpty(name)) return null;

            if (kind != null)
                return _definitions.TryGetValue((kind, name.ToLowerInvariant()), out EntityDefinition? exact) ? exact : null;

            if (!_byName.TryGetValue(name, out List<EntityDefinition>? list) || list.Count == 0) return null;
            return list[0];
        }

        /// <summary>First match among several kinds, in the order given.</summary>
        public EntityDefinition? FindAny(string name, params string[] kinds)
        {
            foreach (string kind in kinds)
            {
                EntityDefinition? found = Find(name, kind);
                if (found != null) return found;
            }
            return null;
        }

        public VerbDefinition? FindVerb(string name) => _verbs.TryGetValue(name, out VerbDefinition? verb) ? verb : null;

        public ResourceRule? Resource(string stat) => _resources.TryGetValue(stat, out ResourceRule? rule) ? rule : null;

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
