using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Descriptions;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Cantrip.Syntax;
using Cantrip.Testing;

namespace Cantrip.Linting
{
    /// <summary>Tunes a lint run for a particular game.</summary>
    public sealed class LintOptions
    {
        /// <summary>Diagnostic codes to leave out, such as <c>CT306</c>.</summary>
        public ISet<string> Suppressed { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Verbs the game registers from C# with <c>RegisterVerb</c>.</summary>
        public ISet<string> HostVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Events the game raises from C#.</summary>
        public ISet<string> HostEvents { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Names the game's <see cref="IEffectHost"/> resolves, and stats it only sets from C#.</summary>
        public ISet<string> HostNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Blocks the game runs itself, reading them from C# through <see cref="EntityDefinition.Blocks"/>,
        /// such as a custom <c>on_reveal:</c>. The engine runs only <c>effect:</c> and <c>move ...:</c>.
        /// </summary>
        public ISet<string> HostBlocks { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Static checks over loaded content: mistakes that would otherwise only
    /// show up when the effect runs, if ever. The linter never executes content, so it is safe to
    /// run in editors and CI on anything, including content that does not load cleanly.
    /// </summary>
    public sealed class Linter
    {
        public const string UnknownVerb = "CT301";
        public const string UnknownName = "CT302";
        public const string UnknownTag = "CT303";
        public const string UnknownEvent = "CT304";
        public const string UnheardEvent = "CT305";
        public const string EventCycle = "CT306";
        public const string EventOutsideListener = "CT307";
        public const string CancelAfterEvent = "CT308";
        public const string UnusedTarget = "CT309";
        public const string UnusedVerb = "CT310";
        public const string StacksOutsideStatus = "CT311";
        public const string UnknownMove = "CT312";
        public const string UnknownBlock = "CT313";
        public const string ForOnDurationStatus = "CT314";
        public const string IgnoredDuration = "CT315";
        public const string TagWrittenAsProperty = "CT316";

        /// <summary>Tags the engine itself gives meaning to, so using one never needs a declaration.</summary>
        private static readonly string[] EngineTags =
        {
            "attack", "skill", "power", "curse", "exhaust", "retain", "ethereal", "unplayable", "buff", "debuff",
        };

        /// <summary>The tags whose behaviour a card gets only from its <c>tags</c> line (see CT316).</summary>
        private static readonly string[] CardBehaviourTags = { "exhaust", "retain", "ethereal", "unplayable", "power", "attack" };

        /// <summary>The tags that set a status's flags (see CT316).</summary>
        private static readonly string[] StatusBehaviourTags = { "buff", "debuff" };

        /// <summary>Event data the runtime also exposes as bare names inside listeners.</summary>
        private static readonly string[] EventDataNames =
        {
            "stat", "old", "new", "base", "total", "blocked", "overkill", "status", "status_name", "from", "to", "won", "move", "ability",
        };

        /// <summary>Names that verbs bind for the statements after them, and the X of X-cost cards.</summary>
        private static readonly string[] BoundNames = { "chosen", "created", "index", "x" };

        /// <summary>Stats the runtime writes itself.</summary>
        private static readonly string[] RuntimeStats = { "expires_at", "ready_at" };

        /// <summary>Verbs whose <c>X on Y</c> argument means "X aimed at Y".</summary>
        private static readonly HashSet<string> AimingVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "play", "replay", "cast", "change",
        };

        /// <summary>The blocks in a declaration that the engine runs. Any other is only a label.</summary>
        private static readonly string[] RunBlocks = { "effect", "move" };

        /// <summary>Words that start a listener in other languages, and so in a mistyped one.</summary>
        private static readonly HashSet<string> ListenerWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "when", "whenever", "upon", "at", "after", "before", "instead", "once", "on",
        };

        /// <summary>Listener words that are also the timing of an event, as in <c>before_damaged</c>.</summary>
        private static readonly HashSet<string> TimingWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "before", "instead",
        };

        /// <summary>Words an event's name can do without: <c>start_of_turn</c> is <c>turn_start</c>.</summary>
        private static readonly HashSet<string> FillerWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "of", "on", "at", "is", "my", "your",
        };

        /// <summary>Other words for the words built-in events use.</summary>
        private static readonly Dictionary<string, string> EventSynonyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["begin"] = "start", ["begins"] = "start", ["began"] = "start", ["beginning"] = "start",
            ["finish"] = "end", ["finishes"] = "end", ["finished"] = "end",
            ["death"] = "die", ["dead"] = "die", ["dies"] = "die", ["died"] = "die",
            ["drew"] = "draw",
        };

        /// <summary>Endings taken off a word to compare it with another form of itself.</summary>
        private static readonly string[] WordEndings = { "ing", "ed", "es", "s", "d", "n" };

        /// <summary>Names that only ever mean actors, never a card.</summary>
        private static readonly HashSet<string> ActorWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "player", "controller", "enemy", "enemies", "allies", "everyone", "actors",
        };

        /// <summary>Verbs that act on the effect's target when they have no <c>to</c> clause.</summary>
        private static readonly HashSet<string> TargetingVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "deal", "damage", "apply", "add", "remove", "kill", "emit", "replay",
        };

        private enum BodyKind
        {
            Effect,
            Listener,
            Modifier,
            Verb,
            Test,
        }

        /// <summary>One piece of content to check: a block, a listener, a modifier, a verb or a test.</summary>
        private sealed class Body
        {
            public Body(BodyKind kind, Node anchor, EntityDefinition? owner)
            {
                Kind = kind;
                Anchor = anchor;
                Owner = owner;
            }

            public BodyKind Kind { get; }
            public Node Anchor { get; }
            public EntityDefinition? Owner { get; }
            public ListenerNode? Listener { get; set; }
            public VerbDefinition? Verb { get; set; }
            public BlockNode? Block { get; set; }
            public List<ExprNode> Expressions { get; } = new List<ExprNode>();
            public Facts Facts { get; } = new Facts();
        }

        /// <summary>Everything a walk over one body turns up.</summary>
        private sealed class Facts : AstWalker
        {
            public List<CommandNode> Commands { get; } = new List<CommandNode>();
            public List<NameExpr> Names { get; } = new List<NameExpr>();
            public List<MemberExpr> Members { get; } = new List<MemberExpr>();
            public List<QualifiedExpr> Qualified { get; } = new List<QualifiedExpr>();
            public List<AssignNode> Assigns { get; } = new List<AssignNode>();
            public List<CallExpr> Calls { get; } = new List<CallExpr>();
            public List<BinaryExpr> OnExpressions { get; } = new List<BinaryExpr>();
            public HashSet<string> Locals { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public override void VisitStatement(StatementNode statement)
            {
                switch (statement)
                {
                    case CommandNode command:
                        Commands.Add(command);
                        if (command.Clause("as") is NameExpr alias
                            && (string.Equals(command.Verb, "choose", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(command.Verb, "discover", StringComparison.OrdinalIgnoreCase)))
                            Locals.Add(alias.Name);

                        // `deal 4 to all enemies into dealt` binds what landed, the way
                        // `choose ... as` binds what was chosen. Only the verbs that honour the
                        // clause bind anything, so a name written on one that ignores it still warns.
                        if (command.Clause("into") is NameExpr bound
                            && (string.Equals(command.Verb, "deal", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(command.Verb, "damage", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(command.Verb, "attack", StringComparison.OrdinalIgnoreCase)))
                            Locals.Add(bound.Name);
                        break;
                    case ForEachNode loop:
                        Locals.Add(loop.Variable);
                        break;
                    case LetNode let:
                        Locals.Add(let.Name);
                        break;
                    case AssignNode assign:
                        Assigns.Add(assign);
                        break;
                }
                base.VisitStatement(statement);
            }

            public override void VisitExpression(ExprNode expression)
            {
                switch (expression)
                {
                    case NameExpr name:
                        Names.Add(name);
                        break;
                    case MemberExpr member:
                        Members.Add(member);
                        break;
                    case QualifiedExpr qualified:
                        Qualified.Add(qualified);
                        break;
                    case CallExpr call:
                        Calls.Add(call);
                        break;
                    case BinaryExpr { Operator: BinaryOperator.On } on:
                        OnExpressions.Add(on);
                        break;
                }
                base.VisitExpression(expression);
            }
        }

        private readonly ContentLibrary _content;
        private readonly LintOptions _options;
        private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();
        private readonly HashSet<Node> _reported = new HashSet<Node>();
        private readonly List<Body> _bodies = new List<Body>();
        private readonly HashSet<string> _verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _stats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CommandNode> _emitted = new Dictionary<string, CommandNode>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _listened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _calledVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Linter(ContentLibrary content, LintOptions? options)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _options = options ?? new LintOptions();
        }

        /// <summary>Runs every check and returns the diagnostics in source order.</summary>
        public static IReadOnlyList<Diagnostic> Lint(ContentLibrary content, LintOptions? options = null) =>
            new Linter(content, options).Run();

        private IReadOnlyList<Diagnostic> Run()
        {
            CollectBodies();
            CollectGlobalFacts();

            foreach (Body body in _bodies)
            {
                CheckVerbs(body);
                CheckNames(body);
                CheckTags(body);
                CheckEventUse(body);
                CheckStacks(body);
                CheckMoves(body);
                CheckStatusLengths(body);
            }

            CheckBlocks();
            CheckIgnoredDurations();
            CheckTagProperties();
            CheckListenedEvents();
            CheckEmittedEvents();
            CheckEventCycles();
            CheckCardTargets();
            CheckUnusedVerbs();
            CheckDescriptions();

            return _diagnostics
                .Where(d => !_options.Suppressed.Contains(d.Code))
                .OrderBy(d => d.Span.File, StringComparer.Ordinal)
                .ThenBy(d => d.Span.Line)
                .ThenBy(d => d.Span.Column)
                .ThenBy(d => d.Code, StringComparer.Ordinal)
                .ToList();
        }

        // Gathering ----------------------------------------------------------------------------

        private void CollectBodies()
        {
            IEnumerable<EntityDefinition> definitions = _content.Definitions
                .Where(d => d.KindName != "resource")
                .OrderBy(d => d.Syntax.Span.File, StringComparer.Ordinal)
                .ThenBy(d => d.Syntax.Span.Line);

            foreach (EntityDefinition definition in definitions)
            {
                foreach (MemberNode member in definition.Syntax.Members)
                {
                    switch (member)
                    {
                        case ListenerNode listener:
                        {
                            var body = new Body(BodyKind.Listener, listener, definition) { Listener = listener, Block = listener.Body };
                            if (listener.Filter != null) body.Expressions.Add(listener.Filter);
                            _bodies.Add(body);
                            break;
                        }

                        case ModifyNode modify:
                        {
                            var body = new Body(BodyKind.Modifier, modify, definition);
                            if (modify.Scope != null) body.Expressions.Add(modify.Scope);
                            if (modify.Filter != null) body.Expressions.Add(modify.Filter);
                            body.Expressions.Add(modify.Amount);
                            _bodies.Add(body);
                            break;
                        }

                        case BlockMemberNode block:
                            _bodies.Add(new Body(BodyKind.Effect, block, definition) { Block = block.Body });
                            break;
                    }
                }
            }

            foreach (VerbDefinition verb in _content.Verbs.OrderBy(v => v.Syntax.Span.File, StringComparer.Ordinal).ThenBy(v => v.Syntax.Span.Line))
            {
                var body = new Body(BodyKind.Verb, verb.Syntax, null) { Verb = verb, Block = verb.Body };
                foreach (string parameter in verb.Parameters) body.Facts.Locals.Add(parameter);
                _bodies.Add(body);
            }

            foreach (TestDefinition test in _content.Tests)
                _bodies.Add(new Body(BodyKind.Test, test.Syntax, null) { Block = test.Syntax.Body });

            foreach (Body body in _bodies)
            {
                if (body.Block != null) body.Facts.VisitBlock(body.Block);
                foreach (ExprNode expression in body.Expressions) body.Facts.VisitExpression(expression);
            }
        }

        private void CollectGlobalFacts()
        {
            // Verbs: everything a runtime would register, plus content, host and (in tests) test verbs.
            var probe = new CardRuntime(_content);
            _verbs.UnionWith(probe.Interpreter.VerbNames);
            _verbs.UnionWith(_options.HostVerbs);

            _stats.UnionWith(Interpreter.CommonStats);
            _stats.UnionWith(RuntimeStats);
            _stats.UnionWith(_content.Resources.Keys);
            foreach (EntityDefinition definition in _content.Definitions) _stats.UnionWith(definition.Stats.Keys);

            foreach (EntityDefinition definition in _content.Definitions)
            {
                _tags.UnionWith(definition.Tags);
                _tags.Add(definition.Name); // a status's name counts as a tag on its host
            }
            _tags.UnionWith(EngineTags);

            foreach (Body body in _bodies)
            {
                if (body.Listener != null) _listened.Add(EventPart(body.Listener.EventName));

                foreach (AssignNode assign in body.Facts.Assigns)
                {
                    switch (assign.Target)
                    {
                        case NameExpr name when !IsReserved(name.Name): _stats.Add(name.Name); break;
                        case MemberExpr member when !StartsUpper(member.Member): _stats.Add(member.Member); break;
                    }
                }

                foreach (CommandNode command in body.Facts.Commands)
                {
                    _calledVerbs.Add(command.Verb);
                    string verb = command.Verb.ToLowerInvariant();

                    switch (verb)
                    {
                        case "emit":
                            if (WordAt(command, 0) is string emitted && !_emitted.ContainsKey(emitted)) _emitted[emitted] = command;
                            break;

                        case "gain":
                        case "lose":
                            if (WordAt(command, 1) is string gained && !StartsUpper(gained)) _stats.Add(gained);
                            break;

                        case "change":
                            if (command.Arguments.Count > 0 && StatOfChange(command.Arguments[0]) is string changed) _stats.Add(changed);
                            break;

                        case "add":
                            if (command.Arguments.Count > 0 && command.Arguments[0] is QualifiedExpr { Qualifier: "tag" } added) _tags.Add(added.Name);
                            break;

                        case "deal":
                        case "damage":
                            if (command.Clause("as") is ExprNode damageType && FirstWord(damageType) is string typeTag) _tags.Add(typeTag);
                            break;

                        case "player":
                        case "enemy":
                            // Test setup writes stats by name: `player rage 5`. The name in
                            // `enemy Ghoul hp 12` is not one, defined or not.
                            if (body.Kind == BodyKind.Test)
                            {
                                NameExpr? enemyName = verb == "enemy" ? TestEnemyName(command) : null;
                                foreach (ExprNode argument in command.Arguments)
                                {
                                    if (argument is NameExpr stat && stat != enemyName && _content.Find(stat.Name) == null) _stats.Add(stat.Name);
                                }
                            }
                            break;
                    }
                }
            }
        }

        // Per-body checks ------------------------------------------------------------------------

        private void CheckVerbs(Body body)
        {
            foreach (CommandNode command in body.Facts.Commands)
            {
                if (command.Verb.Length == 0) continue; // a statement with no verb: the parser has reported it (CT0010)
                if (_verbs.Contains(command.Verb)) continue;
                if (body.Kind == BodyKind.Test && DslTestRunner.TestVerbs.Contains(command.Verb, StringComparer.OrdinalIgnoreCase)) continue;

                IEnumerable<string> candidates = body.Kind == BodyKind.Test ? _verbs.Concat(DslTestRunner.TestVerbs) : _verbs;
                string extra = body.Kind != BodyKind.Test && DslTestRunner.TestVerbs.Contains(command.Verb, StringComparer.OrdinalIgnoreCase)
                    ? " It only exists inside `test` blocks."
                    : string.Empty;

                Error(UnknownVerb, $"Unknown verb `{command.Verb}`.{extra}", command.Span, Suggest.Closest(command.Verb, candidates));
            }
        }

        private void CheckNames(Body body)
        {
            // `emit sparked` and `use Chomp` name an event and a move, not values. CT304/CT305 and
            // CT312 check those words instead.
            foreach (CommandNode command in body.Facts.Commands)
            {
                bool namesSomething = string.Equals(command.Verb, "emit", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(command.Verb, "use", StringComparison.OrdinalIgnoreCase);
                if (namesSomething && command.Arguments.FirstOrDefault() is NameExpr word) _reported.Add(word);
            }

            // Positions where only a definition makes sense are errors: the runtime will fail there.
            foreach (CommandNode command in body.Facts.Commands)
            {
                string verb = command.Verb.ToLowerInvariant();
                switch (verb)
                {
                    case "apply":
                        RequireDefinition(body, command.Arguments.FirstOrDefault(), "status", "keyword");
                        break;
                    case "create":
                        RequireDefinition(body, command.Arguments.FirstOrDefault(), "card", "enemy", "actor", "relic", "item", "status", "keyword", "ability");
                        break;
                    case "shuffle":
                        if (command.Arguments.FirstOrDefault() is NameExpr shuffled && !IsReserved(shuffled.Name) && !IsLocal(body, shuffled.Name))
                            RequireDefinition(body, shuffled, "card");
                        break;
                    case "add":
                        if (command.Arguments.FirstOrDefault() is NameExpr)
                            RequireDefinition(body, command.Arguments[0], "status", "keyword");
                        break;
                    case "gain":
                    case "lose":
                        if (command.Arguments.Count > 1 && command.Arguments[1] is NameExpr named && StartsUpper(named.Name))
                            RequireDefinition(body, named, "status", "keyword");
                        break;
                }
            }

            if (body.Kind == BodyKind.Test) CheckTestNames(body);

            foreach (CallExpr call in body.Facts.Calls)
            {
                if (!string.Equals(call.Name, "has", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (ExprNode argument in call.Arguments)
                {
                    if (argument is NameExpr name && StartsUpper(name.Name)) RequireDefinition(body, name, "status", "keyword");
                }
            }

            // In `play Strike on enemy`, `replay card on target` and `change hp -5 on target`, `on`
            // names who an action is aimed at rather than the stacks of a status.
            var aimed = new HashSet<ExprNode>();
            foreach (CommandNode command in body.Facts.Commands)
            {
                if (!AimingVerbs.Contains(command.Verb)) continue;
                foreach (ExprNode argument in command.Arguments)
                {
                    if (argument is BinaryExpr { Operator: BinaryOperator.On }) aimed.Add(argument);
                }
            }

            foreach (BinaryExpr on in body.Facts.OnExpressions)
            {
                if (aimed.Contains(on)) continue;
                if (on.Left is NameExpr name && !IsLocal(body, name.Name)) RequireDefinition(body, name, "status", "keyword");
            }

            foreach (MemberExpr member in body.Facts.Members)
            {
                if (!StartsUpper(member.Member) || member.Target is NameExpr { Name: "event" }) continue;
                if (_content.FindAny(member.Member, "status", "keyword") != null) continue;

                Error(UnknownName, $"No status called `{member.Member}` is defined.", member.Span,
                    Suggest.Closest(member.Member, StatusNames()));
            }

            foreach (QualifiedExpr qualified in body.Facts.Qualified)
            {
                string? kind = qualified.Qualifier switch
                {
                    "status" => "status",
                    "card" => "card",
                    _ => null,
                };
                if (kind == null || _content.Find(qualified.Name, kind) != null) continue;

                Error(UnknownName, $"No {kind} called `{qualified.Name}` is defined.", qualified.Span,
                    Suggest.Closest(qualified.Name, _content.Definitions.Where(d => d.KindName == kind).Select(d => d.Name)));
            }

            // Everything else is a warning: a host could be supplying the name at runtime.
            foreach (NameExpr name in body.Facts.Names)
            {
                if (_reported.Contains(name) || IsKnownName(body, name.Name)) continue;

                string message = StartsUpper(name.Name)
                    ? $"`{name.Name}` is not defined anywhere in the loaded content."
                    : $"`{name.Name}` is not a stat, name or local the rules know; at runtime this is an error.";

                // "Host" is what the docs call the entity a status is on; content calls it `owner`.
                string? suggestion = string.Equals(name.Name, "host", StringComparison.OrdinalIgnoreCase)
                    ? "owner"
                    : Suggest.Closest(name.Name, NameCandidates(body));
                Warn(UnknownName, message, name.Span, suggestion);
            }
        }

        /// <summary>
        /// The test lines that name a definition, which the runner looks up and fails the test
        /// without: <c>hand</c>, <c>deck</c> and <c>discard_pile</c> name cards, <c>relic</c> a relic
        /// or item, <c>grant</c> and <c>cast</c> an ability, <c>play</c> a card, and <c>enemy</c>
        /// an enemy when its first word stands alone as a name (<c>enemy Ghoul hp 12</c>).
        /// </summary>
        private void CheckTestNames(Body body)
        {
            foreach (CommandNode command in body.Facts.Commands)
            {
                switch (command.Verb.ToLowerInvariant())
                {
                    case "hand":
                    case "deck":
                    case "discard_pile":
                        RequireDefinitions(body, TestNames(command), "card");
                        break;
                    case "relic":
                        RequireDefinitions(body, TestNames(command), "relic", "item");
                        break;
                    case "grant":
                        RequireDefinitions(body, TestNames(command), "ability");
                        break;
                    case "play":
                        RequireDefinition(body, Aimed(command.Arguments.FirstOrDefault()), "card");
                        break;
                    case "cast":
                        RequireDefinition(body, Aimed(command.Arguments.FirstOrDefault()), "ability");
                        break;
                    case "enemy":
                        RequireDefinition(body, TestEnemyName(command), "enemy");
                        break;
                }
            }
        }

        /// <summary>Checks each name a line lists once, so <c>deck Strik, Strik</c> is one error.</summary>
        private void RequireDefinitions(Body body, IEnumerable<ExprNode> nodes, params string[] kinds)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ExprNode node in nodes)
            {
                string name = node is StringExpr text ? text.Value : ((NameExpr)node).Name;
                if (seen.Add(name)) RequireDefinition(body, node, kinds);
                else _reported.Add(node);
            }
        }

        /// <summary><c>Strike</c> in <c>play Strike on enemy</c>.</summary>
        private static ExprNode? Aimed(ExprNode? argument) =>
            argument is BinaryExpr { Operator: BinaryOperator.On } on ? on.Left : argument;

        /// <summary>
        /// The names a test verb lists, the way the runner reads them: <c>deck Strike Defend</c>,
        /// <c>deck Strike, Defend</c>, <c>deck "Twin Strike", Defend</c>. A name after a comma is a
        /// flag to the parser, which keeps it in lower case.
        /// </summary>
        private static IEnumerable<ExprNode> TestNames(CommandNode command)
        {
            foreach (ExprNode argument in command.Arguments)
            {
                if (argument is NameExpr || argument is StringExpr) yield return argument;
            }
            foreach (ClauseNode clause in command.Clauses)
            {
                if (clause.Value == null) yield return new NameExpr(clause.Keyword, clause.Span);
                else if (clause.Value is NameExpr || clause.Value is StringExpr) yield return clause.Value;
            }
        }

        /// <summary>
        /// The first word of a test's <c>enemy</c> line when the runner takes it for an enemy's name:
        /// a bare word, not a status, with no value of its own after it, as in <c>enemy Ghoul</c> and
        /// <c>enemy Ghoul hp 12</c>, but not <c>enemy hp 12</c>. A quoted first word that names no
        /// enemy is a label for a plain one, so it is never checked.
        /// </summary>
        private NameExpr? TestEnemyName(CommandNode command)
        {
            IReadOnlyList<ExprNode> nodes = command.Arguments;
            if (nodes.Count % 2 == 0 || !(nodes[0] is NameExpr name)) return null;
            if (nodes.Count > 1 && !(nodes[1] is NameExpr)) return null;
            if (_content.FindAny(name.Name, "status", "keyword") != null) return null;
            return name;
        }

        private void RequireDefinition(Body body, ExprNode? node, params string[] kinds)
        {
            string? written = node switch
            {
                NameExpr name => name.Name,
                StringExpr text => text.Value,
                _ => null,
            };
            if (written == null || node == null) return;
            if (node is NameExpr local && IsLocal(body, local.Name)) return;

            _reported.Add(node);
            if (_content.FindAny(written, kinds) != null) return;

            EntityDefinition? other = _content.Find(written);
            if (other != null)
            {
                Error(UnknownName, $"`{written}` is {A(other.KindName)}, but {A(string.Join(" or ", kinds.Take(2)))} is needed here.", node.Span);
                return;
            }

            Error(UnknownName, $"Nothing called `{written}` is defined.", node.Span,
                Suggest.Closest(written, _content.Definitions.Where(d => kinds.Contains(d.KindName)).Select(d => d.Name)));
        }

        private void CheckTags(Body body)
        {
            foreach (QualifiedExpr qualified in body.Facts.Qualified)
            {
                if (qualified.Qualifier != "tag" && qualified.Qualifier != "keyword") continue;
                if (_tags.Contains(qualified.Name)) continue;

                Warn(UnknownTag, $"No definition has the tag `{qualified.Name}`, so this never matches.", qualified.Span,
                    Suggest.Closest(qualified.Name, _tags));
            }
        }

        private void CheckEventUse(Body body)
        {
            bool eventAvailable = body.Kind == BodyKind.Listener || body.Kind == BodyKind.Verb;
            if (!eventAvailable)
            {
                foreach (NameExpr name in body.Facts.Names)
                {
                    if (!string.Equals(name.Name, "event", StringComparison.OrdinalIgnoreCase) || IsLocal(body, name.Name)) continue;
                    _reported.Add(name);
                    Error(EventOutsideListener, "`event` is only available inside an `on ...:` listener.", name.Span);
                }
            }

            if (body.Listener != null && body.Listener.Phase == EventPhase.After)
            {
                foreach (CommandNode command in body.Facts.Commands)
                {
                    if (!string.Equals(command.Verb, "cancel", StringComparison.OrdinalIgnoreCase)) continue;
                    Error(CancelAfterEvent, $"`cancel` cannot undo `{body.Listener.EventName}` after it has happened; listen to `before_{EventPart(body.Listener.EventName)}` instead.", command.Span);
                }
            }
        }

        private void CheckStacks(Body body)
        {
            if (body.Kind == BodyKind.Verb || body.Kind == BodyKind.Test || body.Owner == null) return;
            if (body.Owner.Kind == EntityKind.Status || body.Owner.Kind == EntityKind.Keyword) return;

            foreach (NameExpr name in body.Facts.Names)
            {
                if (!string.Equals(name.Name, "stacks", StringComparison.OrdinalIgnoreCase) || IsLocal(body, name.Name)) continue;
                _reported.Add(name);
                Warn(StacksOutsideStatus, $"`stacks` only means something inside a status; in {body.Owner} it reads a stat that is probably never set.", name.Span);
            }
        }

        private void CheckMoves(Body body)
        {
            foreach (CommandNode command in body.Facts.Commands)
            {
                if (!string.Equals(command.Verb, "use", StringComparison.OrdinalIgnoreCase)) continue;
                string? move = WordAt(command, 0);
                if (move == null) continue;

                if (body.Owner == null || body.Owner.Moves.Count == 0)
                {
                    if (body.Kind != BodyKind.Verb)
                        Error(UnknownMove, "Only enemies with moves can `use` one.", command.Span);
                    continue;
                }

                if (body.Owner.Moves.Any(m => string.Equals(m.Name, move, StringComparison.OrdinalIgnoreCase))) continue;

                Error(UnknownMove, $"`{body.Owner.Name}` has no move called `{move}`.", command.Span,
                    Suggest.Closest(move, body.Owner.Moves.Select(m => m.Name)));
            }
        }

        /// <summary>
        /// CT314: <c>for N turns</c> on a <c>duration</c> or <c>refresh</c> status, longer than the
        /// amount applied. Such a status loses its duration on its host's turns and lasts as many of
        /// them as the amount, while <c>for</c> only adds a deadline, so <c>apply Weak for 2 turns</c>
        /// is Weak for one turn. A <c>for</c> no longer than the amount can cut the status short, and
        /// one on a status that does not tick down on turns, or that may land on a card, which has no
        /// turns, is what removes it, so those are left alone.
        /// </summary>
        private void CheckStatusLengths(Body body)
        {
            foreach (CommandNode command in body.Facts.Commands)
            {
                bool apply = string.Equals(command.Verb, "apply", StringComparison.OrdinalIgnoreCase);
                bool add = string.Equals(command.Verb, "add", StringComparison.OrdinalIgnoreCase);
                if (!apply && !add) continue;

                // Seconds and ticks belong to a real-time clock, where a duration status may never
                // see a turn end and `for` is what removes it.
                if (!(command.Clause("for") is NumberExpr { Unit: "turn" or "turns" or "t" } length) || !IsWholeCount(length.Value)) continue;

                // `add "Weak"` adds a tag, so only `apply` names a status with a string.
                string? name = command.Arguments.FirstOrDefault() switch
                {
                    NameExpr named when !IsLocal(body, named.Name) => named.Name,
                    StringExpr quoted when apply => quoted.Value,
                    _ => null,
                };
                EntityDefinition? status = name == null ? null : _content.FindAny(name, "status", "keyword");
                if (status == null || (status.Stacking != StackingMode.Duration && status.Stacking != StackingMode.Refresh)) continue;
                if (status.DecayAmount < Num.One || !(status.DecayOn is "turn_end" or "turn_start")) continue;
                if (!LandsOnActors(body, command.Clause("to"))) continue;

                Num amount = Num.One;
                if (command.Arguments.Count > 1)
                {
                    if (!(command.Arguments[1] is NumberExpr { Unit: null } written) || !IsWholeCount(written.Value)) continue;
                    amount = written.Value;
                }
                if (amount >= length.Value) continue;

                string turns = length.Value + (length.Value == Num.One ? " turn" : " turns");
                ExprNode? to = command.Clause("to");
                Warn(ForOnDurationStatus,
                    $"`for {turns}` does not make {status.Name} last {turns}. A `stacking {status.Stacking.ToString().ToLowerInvariant()}` status lasts as many of its host's turns as the amount applied, here {amount}; `for` only sets a deadline, which can end it sooner but never later.",
                    command.Span,
                    $"{command.Verb.ToLowerInvariant()} {AstPrinter.Name(status.Name)} {length.Value}" + (to == null ? string.Empty : " to " + AstPrinter.Print(to)));
            }
        }

        /// <summary>
        /// Whether a status applied to <paramref name="to"/> lands on actors, whose turns tick it
        /// down: a word that only ever means actors, or, in a card's or ability's effect or an enemy's
        /// move, the effect's own target. Anything else may be a card.
        /// </summary>
        private static bool LandsOnActors(Body body, ExprNode? to) => to switch
        {
            null => body.Kind == BodyKind.Effect,
            NameExpr { Name: var name } when IsLocal(body, name) => false,
            NameExpr { Name: var name } when string.Equals(name, "target", StringComparison.OrdinalIgnoreCase) => body.Kind == BodyKind.Effect,
            NameExpr { Name: var name } => ActorWords.Contains(name) || (body.Kind == BodyKind.Test && IsTestBinding(name)),
            SelectorExpr selector => LandsOnActors(body, selector.Source),
            WhereExpr where => LandsOnActors(body, where.Source),
            _ => false,
        };

        // Whole-content checks -----------------------------------------------------------------

        /// <summary>
        /// CT313: a line in a declaration that ends in a colon but is neither a listener nor a block
        /// the engine runs. The parser has to accept it, because a game may run blocks of its own from
        /// C# (<see cref="LintOptions.HostBlocks"/>), so a listener written another way, such as
        /// <c>when card_played:</c>, would otherwise load cleanly and never fire.
        /// </summary>
        private void CheckBlocks()
        {
            IEnumerable<EntityDefinition> definitions = _content.Definitions
                .OrderBy(d => d.Syntax.Span.File, StringComparer.Ordinal)
                .ThenBy(d => d.Syntax.Span.Line);

            foreach (EntityDefinition definition in definitions)
            {
                foreach (MemberNode member in definition.Syntax.Members)
                {
                    if (!(member is BlockMemberNode block)) continue;
                    if (RunBlocks.Contains(block.Name) || _options.HostBlocks.Contains(block.Name)) continue;

                    string header = string.Join(" ", new[] { block.Name }.Concat(block.Arguments.Select(AstPrinter.Print)));
                    Warn(UnknownBlock,
                        $"`{header}:` in {definition} never runs. A line ending in `:` is only a label unless it is `effect:`, `move ...:` or a listener, which starts with `on`.",
                        block.Span,
                        SuggestBlock(block));
                }
            }
        }

        /// <summary>
        /// What a CT313 block was probably meant to be: a listener when an event can be found in its
        /// first line, else the closest of <c>effect:</c> and <c>move</c>, else a listener on the
        /// closest event.
        /// </summary>
        private string? SuggestBlock(BlockMemberNode block)
        {
            var words = new List<(string Word, IReadOnlyList<ExprNode>? Filter)> { (block.Name, null) };
            foreach (ExprNode argument in block.Arguments) words.AddRange(HeaderWords(argument));

            // The event: a word that names one, or names one once `on_` or `when_` is taken off it, or
            // two or three words that do once joined (`at turn start`), or else what follows `on`, or
            // `when` and the like, as long as it can stand for an event (see TakeUnknownEvent). A
            // timing written into the word, as in `before_damaged`, stays with it.
            int at = words.FindIndex(w => IsKnownListenerEvent(w.Word));
            if (at < 0) at = words.FindIndex(w => IsKnownListenerEvent(StripListenerPrefix(w.Word)));
            if (at < 0) JoinSplitEvent(words, ref at);
            if (at < 0)
            {
                int on = words.FindIndex(w => string.Equals(w.Word, "on", StringComparison.OrdinalIgnoreCase));
                if (on >= 0 && on + 1 < words.Count) at = on + 1;
                else if (ListenerWords.Contains(block.Name) && words.Count > 1 && !ListenerWords.Contains(words[1].Word)) at = 1;

                if (at >= 0 && !TakeUnknownEvent(words, at)) return null;
            }

            if (at >= 0)
            {
                string eventName = StripListenerPrefix(words[at].Word);

                // `every 2 turns:` keeps its interval, which is a number rather than a word.
                if (string.Equals(eventName, "every", StringComparison.OrdinalIgnoreCase))
                {
                    NumberExpr? interval = block.Arguments.OfType<NumberExpr>().FirstOrDefault();
                    return interval == null ? null : $"on every {AstPrinter.Print(interval)}:";
                }

                if (Parser.NormalizeEventName(eventName).Phase == EventPhase.After && !eventName.StartsWith("after_", StringComparison.OrdinalIgnoreCase))
                    eventName = TimingWrittenBefore(words, at, block.Name) + eventName;

                string filter = words[at].Filter is IReadOnlyList<ExprNode> clauses ? "(" + string.Join(", ", clauses.Select(AstPrinter.Print)) + ")" : string.Empty;

                string limit = string.Empty;
                for (int i = 0; i + 2 < words.Count; i++)
                {
                    if (string.Equals(words[i].Word, "once", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(words[i + 1].Word, "per", StringComparison.OrdinalIgnoreCase)
                        && words[i + 2].Word.ToLowerInvariant() is "turn" or "battle" or "run" or "chain")
                        limit = " once per " + words[i + 2].Word.ToLowerInvariant();
                }

                return $"on {eventName}{filter}{limit}:";
            }

            string? known = Suggest.Closest(block.Name, RunBlocks);
            if (known == "effect") return "effect:";
            if (known == "move") return string.Join(" ", new[] { "move" }.Concat(block.Arguments.Select(AstPrinter.Print))) + ":";

            // Not `every`, which needs an interval that a label with none cannot supply.
            IEnumerable<string> events = ListenableEvents().Where(e => !string.Equals(e, BuiltinEvents.Every, StringComparison.OrdinalIgnoreCase));
            (string named, EventPhase timing) = Parser.NormalizeEventName(StripListenerPrefix(block.Name));
            string? closeEvent = SuggestEvent(named, events, partial: false);
            return closeEvent == null ? null : $"on {TimingPrefix(timing)}{closeEvent}:";
        }

        /// <summary>
        /// Whether the words from <paramref name="at"/> to the colon, or to a <c>once per</c> or
        /// <c>priority</c>, can stand for the event when the linter knows none of them. An event
        /// close to them joined takes their place, keeping any timing and scope written with them:
        /// <c>turn starts</c> and <c>start of turn</c> are <c>turn_start</c>, and
        /// <c>before_damagd</c> is <c>before_damaged</c>. Failing that, one word is kept, as an
        /// event the game may raise from C#, while several are prose that suggests no listener, as
        /// in <c>whenever player takes damage:</c>.
        /// </summary>
        private bool TakeUnknownEvent(List<(string Word, IReadOnlyList<ExprNode>? Filter)> words, int at)
        {
            var rest = words.Skip(at)
                .TakeWhile(w => !string.Equals(w.Word, "once", StringComparison.OrdinalIgnoreCase) && !string.Equals(w.Word, "priority", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (rest.Count == 0) return false;

            (string named, EventPhase timing) = Parser.NormalizeEventName(StripListenerPrefix(string.Join("_", rest.Select(w => w.Word))));
            int dot = named.LastIndexOf('.');
            string? close = SuggestEvent(named.Substring(dot + 1), ListenableEvents(), partial: false);
            if (close != null)
            {
                words[at] = (TimingPrefix(timing) + named.Substring(0, dot + 1) + close, rest[rest.Count - 1].Filter);
                return true;
            }
            return rest.Count == 1;
        }

        /// <summary>
        /// A timing written as a word of its own just before the event, as in <c>before damaged</c>,
        /// <c>once per battle instead of died</c> and <c>when before owner.damaged</c>, or else as the
        /// block's first word, as its prefix: <c>before_</c>, <c>instead_of_</c> or none.
        /// </summary>
        private static string TimingWrittenBefore(List<(string Word, IReadOnlyList<ExprNode>? Filter)> words, int at, string first)
        {
            string previous = at > 0 ? words[at - 1].Word : string.Empty;
            if (string.Equals(previous, "of", StringComparison.OrdinalIgnoreCase) && at > 1) previous = words[at - 2].Word + "_of";

            foreach (string word in new[] { previous, first })
            {
                switch (word.ToLowerInvariant())
                {
                    case "before":
                        return "before_";
                    case "instead":
                    case "instead_of":
                        return "instead_of_";
                }
            }
            return string.Empty;
        }

        /// <summary>The prefix that writes a timing into an event's name: <c>before_</c>, <c>instead_of_</c> or none.</summary>
        private static string TimingPrefix(EventPhase timing) => timing switch
        {
            EventPhase.Before => "before_",
            EventPhase.Instead => "instead_of_",
            _ => string.Empty,
        };

        /// <summary>The events a listener can hear: the built-in ones, those content emits and the game's own.</summary>
        private IEnumerable<string> ListenableEvents() => BuiltinEvents.Names.Concat(_emitted.Keys).Concat(_options.HostEvents);

        /// <summary>
        /// The words of a block's first line in order, each with the <c>(...)</c> after it, if any.
        /// <c>once per battle on card_played</c> arrives as the expressions <c>per</c> and
        /// <c>battle on card_played</c>, and comes back out as its five words.
        /// </summary>
        private static IEnumerable<(string Word, IReadOnlyList<ExprNode>? Filter)> HeaderWords(ExprNode node)
        {
            switch (node)
            {
                case NameExpr name:
                    yield return (name.Name, null);
                    break;
                case MemberExpr member:
                    yield return (AstPrinter.Print(member), null);
                    break;
                case CallExpr call:
                    yield return ((call.Receiver == null ? string.Empty : AstPrinter.Print(call.Receiver) + ".") + call.Name, call.Arguments);
                    break;
                case BinaryExpr binary:
                    foreach (var word in HeaderWords(binary.Left)) yield return word;
                    yield return (AstPrinter.Symbol(binary.Operator), null);
                    foreach (var word in HeaderWords(binary.Right)) yield return word;
                    break;
            }
        }

        /// <summary>
        /// <c>at turn start:</c> writes <c>turn_start</c> as two words. Finds the first run of two or
        /// three words that names an event once joined with <c>_</c>, and puts the joined event in
        /// place of its first word.
        /// </summary>
        private void JoinSplitEvent(List<(string Word, IReadOnlyList<ExprNode>? Filter)> words, ref int at)
        {
            for (int start = 0; start < words.Count; start++)
            {
                for (int length = 2; length <= 3 && start + length <= words.Count; length++)
                {
                    string joined = string.Join("_", words.Skip(start).Take(length).Select(w => w.Word));
                    if (!IsKnownListenerEvent(joined)) continue;

                    words[start] = (joined, words[start + length - 1].Filter);
                    at = start;
                    return;
                }
            }
        }

        /// <summary>True for an event a listener could hear, written with or without a scope and timing.</summary>
        private bool IsKnownListenerEvent(string word)
        {
            if (word.Length == 0 || ListenerWords.Contains(word)) return false;
            (string name, _) = Parser.NormalizeEventName(word);
            return IsKnownEvent(EventPart(name));
        }

        /// <summary>
        /// <c>on_turn_start</c> and <c>when_damaged</c>: the event with the listener word taken off.
        /// <c>before_</c> and <c>instead_of_</c> stay, because they are the timing of the event the
        /// listener hears, and <c>on damaged</c> in place of <c>on before_damaged</c> is a different
        /// listener, one that can no longer <c>cancel</c>.
        /// </summary>
        private static string StripListenerPrefix(string word)
        {
            foreach (string prefix in ListenerWords)
            {
                if (TimingWords.Contains(prefix)) continue;
                if (word.Length > prefix.Length + 1 && word.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
                    return word.Substring(prefix.Length + 1);
            }
            return word;
        }

        /// <summary>
        /// The event among <paramref name="candidates"/> that <paramref name="written"/> most
        /// likely meant. First the same words in another order or form, as <c>start_of_turn</c> is
        /// <c>turn_start</c> and <c>play_card</c> is <c>card_played</c>; then a misspelling, as
        /// <c>turn_strat</c> is <c>turn_start</c>; then, when <paramref name="partial"/>, the event
        /// with the most words that <paramref name="written"/> has among others, as
        /// <c>card_drawn</c> has <c>drawn</c>.
        /// </summary>
        private static string? SuggestEvent(string written, IEnumerable<string> candidates, bool partial)
        {
            List<string> events = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> words = EventWords(written);

            string? same = events.FirstOrDefault(e => SameWords(EventWords(e), words));
            if (same != null) return same;

            string? close = Suggest.Closest(written, events);
            if (close != null || !partial) return close;

            string? best = null;
            int bestCount = 0;
            foreach (string candidate in events)
            {
                List<string> needed = EventWords(candidate);
                if (needed.Count <= bestCount || needed.Count >= words.Count) continue;
                if (needed.All(n => words.Any(w => SameWord(n, w))))
                {
                    best = candidate;
                    bestCount = needed.Count;
                }
            }
            return best;
        }

        /// <summary>The words of an event's name that carry meaning: <c>start_of_turn</c> is start, turn.</summary>
        private static List<string> EventWords(string name) =>
            name.Split(new[] { '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !FillerWords.Contains(w))
                .ToList();

        /// <summary>The same words in any order, each in any of its forms.</summary>
        private static bool SameWords(List<string> a, List<string> b)
        {
            if (a.Count == 0 || a.Count != b.Count) return false;
            var unmatched = new List<string>(b);
            foreach (string word in a)
            {
                int at = unmatched.FindIndex(w => SameWord(word, w));
                if (at < 0) return false;
                unmatched.RemoveAt(at);
            }
            return true;
        }

        /// <summary><c>played</c> and <c>play</c>, <c>drawn</c> and <c>draw</c>, <c>begin</c> and <c>start</c>.</summary>
        private static bool SameWord(string a, string b) => WordForms(a).Intersect(WordForms(b)).Any();

        private static IEnumerable<string> WordForms(string word)
        {
            string lower = word.ToLowerInvariant();
            if (EventSynonyms.TryGetValue(lower, out string? same))
            {
                yield return same;
                yield break;
            }

            yield return lower;
            if (lower.Length > 4 && lower.EndsWith("ied", StringComparison.Ordinal)) yield return lower.Substring(0, lower.Length - 3) + "y";
            foreach (string ending in WordEndings)
            {
                if (lower.Length - ending.Length >= 3 && lower.EndsWith(ending, StringComparison.Ordinal))
                    yield return lower.Substring(0, lower.Length - ending.Length);
            }
        }

        /// <summary>
        /// CT315: a <c>duration</c> line on a <c>duration</c>, <c>refresh</c> or <c>both</c> status.
        /// Creating the status sets its duration from whoever applies it, as in <c>apply Weak 2</c>,
        /// so the line's number never reaches a status in play. On any other status it is a stat
        /// content may read, so it is left alone there.
        /// </summary>
        private void CheckIgnoredDurations()
        {
            // The number is still readable where the definition stands in for the status: in a
            // `discover` filter, or as `event.status.duration` before the status exists. Content that
            // reads a `duration` member anywhere, or a game that reads the stat from C#, keeps it.
            if (_options.HostNames.Contains("duration")) return;
            bool read = _bodies.Any(b =>
                b.Facts.Members.Any(m => string.Equals(m.Member, "duration", StringComparison.OrdinalIgnoreCase))
                || (b.Facts.Commands.Any(c => string.Equals(c.Verb, "discover", StringComparison.OrdinalIgnoreCase))
                    && b.Facts.Names.Any(n => string.Equals(n.Name, "duration", StringComparison.OrdinalIgnoreCase))));
            if (read) return;

            foreach (EntityDefinition definition in _content.Definitions.OrderBy(d => d.Syntax.Span.File, StringComparer.Ordinal).ThenBy(d => d.Syntax.Span.Line))
            {
                if (definition.Kind != EntityKind.Status && definition.Kind != EntityKind.Keyword) continue;
                if (definition.Stacking != StackingMode.Duration && definition.Stacking != StackingMode.Refresh && definition.Stacking != StackingMode.Both) continue;

                PropertyNode? duration = definition.Property("duration");
                if (duration == null) continue;

                // `text: "Lasts {duration} turns."` prints the number, which is a use of a kind.
                if (definition.Text?.IndexOf("{duration}", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                Warn(IgnoredDuration,
                    $"`duration` on {definition} does nothing: a `stacking {definition.Stacking.ToString().ToLowerInvariant()}` status takes its duration from whoever applies it, as in `apply {AstPrinter.Name(definition.Name)} 2`, never from its declaration.",
                    duration.Span);
            }
        }

        /// <summary>
        /// CT316: a line of its own on a card, such as <c>exhaust</c>, that names a tag with built-in
        /// behaviour. It loads as a property with no value, which is not even a stat, so the card is
        /// discarded as usual. The same goes for <c>buff</c> and <c>debuff</c> on a status. Only a
        /// bare line, or one set to <c>true</c> or <c>yes</c>, is reported: <c>power 3</c> and
        /// <c>attack 2</c> are stats content may read, the second by the <c>attack</c> verb.
        /// </summary>
        private void CheckTagProperties()
        {
            foreach (EntityDefinition definition in _content.Definitions.OrderBy(d => d.Syntax.Span.File, StringComparer.Ordinal).ThenBy(d => d.Syntax.Span.Line))
            {
                string[] tags = definition.Kind switch
                {
                    EntityKind.Card => CardBehaviourTags,
                    EntityKind.Status or EntityKind.Keyword => StatusBehaviourTags,
                    _ => Array.Empty<string>(),
                };
                if (tags.Length == 0) continue;

                foreach (MemberNode member in definition.Syntax.Members)
                {
                    if (!(member is PropertyNode property) || !tags.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (definition.HasTag(property.Name)) continue;

                    bool flagLike = property.Values.Count == 0
                        || (property.Values.Count == 1 && property.Values[0] is NameExpr { Name: var value }
                            && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)));
                    if (!flagLike) continue;

                    string tag = property.Name.ToLowerInvariant();
                    Warn(TagWrittenAsProperty,
                        $"`{property.Name}` on a line of its own does nothing to {definition}: `{tag}` works only as a tag.",
                        property.Span,
                        "tags " + string.Join(", ", definition.Tags.Concat(new[] { tag })));
                }
            }
        }

        private void CheckListenedEvents()
        {
            foreach (Body body in _bodies)
            {
                if (body.Listener == null) continue;
                string name = EventPart(body.Listener.EventName);
                if (IsKnownEvent(name)) continue;

                if (BuiltinEvents.TryParseStatChanged(name, out string stat))
                {
                    Warn(UnknownEvent, $"`{name}` is raised when `{stat}` changes, but nothing in the content has a stat called `{stat}`.", body.Listener.Span,
                        Suggest.Closest(stat, _stats) is string closeStat ? BuiltinEvents.StatChanged(closeStat) : null);
                    continue;
                }

                // The nearest event, whether misspelt (`turn_strat`), worded another way
                // (`start_of_turn`, `play_card`) or with words to spare (`card_drawn`).
                IEnumerable<string> events = ListenableEvents().Concat(_stats.OrderBy(s => s, StringComparer.Ordinal).Select(BuiltinEvents.StatChanged));
                Warn(UnknownEvent, $"Nothing raises an event called `{name}`: it is not built in and no content emits it.", body.Listener.Span,
                    SuggestEvent(name, events, partial: true));
            }
        }

        private void CheckEmittedEvents()
        {
            foreach (var emitted in _emitted)
            {
                if (_listened.Contains(emitted.Key) || _options.HostEvents.Contains(emitted.Key)) continue;
                Info(UnheardEvent, $"`{emitted.Key}` is emitted but no content listens for it.", emitted.Value.Span);
            }
        }

        /// <summary>
        /// Finds events whose listeners can raise the same event again, directly or through other
        /// events and content verbs. The runtime stops each listener after one pass, so this is
        /// information for the author rather than an error.
        /// </summary>
        private void CheckEventCycles()
        {
            var verbBodies = _bodies.Where(b => b.Kind == BodyKind.Verb).ToDictionary(b => b.Verb!.Name, StringComparer.OrdinalIgnoreCase);
            var edges = new SortedDictionary<string, List<(string To, ListenerNode Via)>>(StringComparer.Ordinal);

            foreach (Body body in _bodies)
            {
                if (body.Listener == null) continue;
                string from = EventPart(body.Listener.EventName);

                foreach (string raised in RaisedEvents(body.Facts, verbBodies, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).OrderBy(e => e, StringComparer.Ordinal))
                {
                    if (!_listened.Contains(raised)) continue;
                    if (!edges.TryGetValue(from, out var list)) edges[from] = list = new List<(string, ListenerNode)>();
                    if (!list.Any(e => e.To == raised)) list.Add((raised, body.Listener));
                }
            }

            var reportedCycles = new HashSet<string>(StringComparer.Ordinal);
            foreach (string start in edges.Keys)
            {
                var path = new List<(string Event, ListenerNode Via)>();
                FindCycles(start, start, edges, path, new HashSet<string>(StringComparer.Ordinal), reportedCycles);
            }
        }

        private void FindCycles(
            string start,
            string current,
            SortedDictionary<string, List<(string To, ListenerNode Via)>> edges,
            List<(string Event, ListenerNode Via)> path,
            HashSet<string> visiting,
            HashSet<string> reportedCycles)
        {
            if (!edges.TryGetValue(current, out var next)) return;
            visiting.Add(current);

            foreach (var (to, via) in next)
            {
                path.Add((current, via));
                if (to == start)
                {
                    string key = string.Join(",", path.Select(p => p.Event).OrderBy(e => e, StringComparer.Ordinal));
                    if (reportedCycles.Add(key))
                    {
                        string chain = string.Join(" → ", path.Select(p => $"`{p.Event}`")) + $" → `{start}`";
                        Info(EventCycle, $"These listeners can re-trigger each other: {chain}. Loop protection stops each after one pass; check that is intended.", path[0].Via.Span);
                    }
                }
                else if (!visiting.Contains(to) && string.CompareOrdinal(to, start) > 0)
                {
                    // Only walk to events that sort after the start, so every cycle is found once,
                    // from its smallest event.
                    FindCycles(start, to, edges, path, visiting, reportedCycles);
                }
                path.RemoveAt(path.Count - 1);
            }

            visiting.Remove(current);
        }

        private IEnumerable<string> RaisedEvents(Facts facts, Dictionary<string, Body> verbBodies, HashSet<string> visitingVerbs)
        {
            foreach (CommandNode command in facts.Commands)
            {
                foreach (string raised in BuiltinEvents.RaisedBy(command.Verb)) yield return raised;

                string verb = command.Verb.ToLowerInvariant();
                if (verb == "emit" && WordAt(command, 0) is string emitted) yield return emitted;
                if ((verb == "gain" || verb == "lose") && WordAt(command, 1) is string gained && !StartsUpper(gained)) yield return BuiltinEvents.StatChanged(gained);
                if (verb == "change" && command.Arguments.Count > 0 && StatOfChange(command.Arguments[0]) is string changed) yield return BuiltinEvents.StatChanged(changed);

                if (verbBodies.TryGetValue(command.Verb, out Body? verbBody) && visitingVerbs.Add(command.Verb))
                {
                    foreach (string raised in RaisedEvents(verbBody.Facts, verbBodies, visitingVerbs)) yield return raised;
                }
            }

            foreach (AssignNode assign in facts.Assigns)
            {
                switch (assign.Target)
                {
                    case NameExpr name: yield return BuiltinEvents.StatChanged(name.Name); break;
                    case MemberExpr member when !StartsUpper(member.Member): yield return BuiltinEvents.StatChanged(member.Member); break;
                }
            }
        }

        private void CheckCardTargets()
        {
            foreach (Body body in _bodies)
            {
                if (body.Kind != BodyKind.Effect || body.Owner?.Kind != EntityKind.Card) continue;
                if (!(body.Anchor is BlockMemberNode { Name: "effect" })) continue;

                string? target = body.Owner.Word("target");
                if (target != "enemy" && target != "ally" && target != "any") continue;

                bool usesTarget = body.Facts.Names.Any(n => string.Equals(n.Name, "target", StringComparison.OrdinalIgnoreCase))
                    || body.Facts.Commands.Any(c => TargetingVerbs.Contains(c.Verb) && c.Clause("to") == null)
                    || body.Facts.Commands.Any(c => _content.FindVerb(c.Verb) != null);
                if (usesTarget) continue;

                Warn(UnusedTarget, $"{body.Owner} asks the player to choose a target (`target {target}`), but its effect never uses it.", body.Owner.Syntax.Span);
            }
        }

        /// <summary>Description drift: CT401 to CT403, from <see cref="DescriptionBuilder.Validate"/>.</summary>
        private void CheckDescriptions()
        {
            var descriptions = new DescriptionBuilder(_content);
            IEnumerable<EntityDefinition> definitions = _content.Definitions
                .Where(d => d.KindName != "resource")
                .OrderBy(d => d.Syntax.Span.File, StringComparer.Ordinal)
                .ThenBy(d => d.Syntax.Span.Line);

            foreach (EntityDefinition definition in definitions) _diagnostics.AddRange(descriptions.Validate(definition));
        }

        private void CheckUnusedVerbs()
        {
            foreach (VerbDefinition verb in _content.Verbs.OrderBy(v => v.Name, StringComparer.Ordinal))
            {
                if (_calledVerbs.Contains(verb.Name) || _options.HostVerbs.Contains(verb.Name)) continue;
                Info(UnusedVerb, $"verb `{verb.Name}` is never used.", verb.Syntax.Span);
            }
        }

        // Helpers ------------------------------------------------------------------------------

        private bool IsKnownEvent(string name) =>
            BuiltinEvents.IsBuiltin(name)
            || _emitted.ContainsKey(name)
            || _options.HostEvents.Contains(name)
            || (BuiltinEvents.TryParseStatChanged(name, out string stat) && _stats.Contains(stat));

        private bool IsKnownName(Body body, string name) =>
            IsReserved(name)
            || IsLocal(body, name)
            || _stats.Contains(name)
            || _content.Find(name) != null
            || _options.HostNames.Contains(name)
            || name.EndsWith("_this_turn", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_this_battle", StringComparison.OrdinalIgnoreCase)
            || ((body.Kind == BodyKind.Listener || body.Kind == BodyKind.Verb) && EventDataNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            || (body.Kind == BodyKind.Test && IsTestBinding(name));

        private static bool IsReserved(string name) => Interpreter.ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase);

        private static bool IsLocal(Body body, string name) =>
            body.Facts.Locals.Contains(name) || BoundNames.Contains(name, StringComparer.OrdinalIgnoreCase);

        /// <summary>The test runner binds each spawned enemy as <c>enemy1</c>, <c>enemy2</c>...</summary>
        private static bool IsTestBinding(string name) =>
            name.Length > 5 && name.StartsWith("enemy", StringComparison.OrdinalIgnoreCase) && name.Substring(5).All(char.IsDigit);

        private IEnumerable<string> NameCandidates(Body body) =>
            Interpreter.ReservedNames
                .Concat(body.Facts.Locals)
                .Concat(_stats)
                .Concat(_content.AllNames);

        private IEnumerable<string> StatusNames() =>
            _content.Definitions.Where(d => d.Kind == EntityKind.Status || d.Kind == EntityKind.Keyword).Select(d => d.Name);

        private static string EventPart(string eventName)
        {
            int dot = eventName.LastIndexOf('.');
            return (dot >= 0 ? eventName.Substring(dot + 1) : eventName).ToLowerInvariant();
        }

        private static bool StartsUpper(string name) => name.Length > 0 && char.IsUpper(name[0]);

        /// <summary>"a card", "an ability".</summary>
        private static string A(string noun) => ("aeiou".IndexOf(char.ToLowerInvariant(noun[0])) >= 0 ? "an " : "a ") + noun;

        /// <summary>A whole number of one or more, as a count of turns is.</summary>
        private static bool IsWholeCount(Num value) => value >= Num.One && value == value.Floor();

        private static string? WordAt(CommandNode command, int index) =>
            index < command.Arguments.Count ? FirstWord(command.Arguments[index]) : null;

        private static string? FirstWord(ExprNode node) => node switch
        {
            NameExpr name => name.Name,
            StringExpr text => text.Value,
            QualifiedExpr qualified => qualified.Name,
            _ => null,
        };

        /// <summary>The stat in <c>change hp by 5</c> or the compact <c>change hp -5 on target</c>.</summary>
        private static string? StatOfChange(ExprNode node) => node switch
        {
            NameExpr name => name.Name,
            BinaryExpr { Operator: BinaryOperator.On } on => StatOfChange(on.Left),
            BinaryExpr { Operator: BinaryOperator.Add or BinaryOperator.Subtract, Left: NameExpr name } => name.Name,
            _ => null,
        };

        private void Error(string code, string message, SourceSpan span, string? suggestion = null) =>
            _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, code, message, span, suggestion));

        private void Warn(string code, string message, SourceSpan span, string? suggestion = null) =>
            _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, code, message, span, suggestion));

        private void Info(string code, string message, SourceSpan span) =>
            _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, code, message, span));
    }
}
