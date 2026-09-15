using System;
using System.Collections.Generic;
using System.Linq;
using GameplayEffects.Content;
using GameplayEffects.Descriptions;
using GameplayEffects.Diagnostics;
using GameplayEffects.Runtime;
using GameplayEffects.Syntax;
using GameplayEffects.Testing;

namespace GameplayEffects.Linting
{
    /// <summary>Tunes a lint run for a particular game.</summary>
    public sealed class LintOptions
    {
        /// <summary>Diagnostic codes to leave out, such as <c>GE306</c>.</summary>
        public ISet<string> Suppressed { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Verbs the game registers from C# with <c>RegisterVerb</c>.</summary>
        public ISet<string> HostVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Events the game raises from C#.</summary>
        public ISet<string> HostEvents { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Names the game's <see cref="IEffectHost"/> resolves, and stats it only sets from C#.</summary>
        public ISet<string> HostNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Static checks over loaded content (section 5, item 4): mistakes that would otherwise only
    /// show up when the effect runs, if ever. The linter never executes content, so it is safe to
    /// run in editors and CI on anything, including content that does not load cleanly.
    /// </summary>
    public sealed class Linter
    {
        public const string UnknownVerb = "GE301";
        public const string UnknownName = "GE302";
        public const string UnknownTag = "GE303";
        public const string UnknownEvent = "GE304";
        public const string UnheardEvent = "GE305";
        public const string EventCycle = "GE306";
        public const string EventOutsideListener = "GE307";
        public const string CancelAfterEvent = "GE308";
        public const string UnusedTarget = "GE309";
        public const string UnusedVerb = "GE310";
        public const string StacksOutsideStatus = "GE311";
        public const string UnknownMove = "GE312";

        /// <summary>Tags the engine itself gives meaning to, so using one never needs a declaration.</summary>
        private static readonly string[] EngineTags =
        {
            "attack", "skill", "power", "curse", "exhaust", "retain", "ethereal", "unplayable", "buff", "debuff",
        };

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
                        if (command.Clause("as") is NameExpr alias && string.Equals(command.Verb, "choose", StringComparison.OrdinalIgnoreCase))
                            Locals.Add(alias.Name);
                        break;
                    case ForEachNode loop:
                        Locals.Add(loop.Variable);
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
            }

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
                            // Test setup writes stats by name: `player rage 5`.
                            if (body.Kind == BodyKind.Test)
                            {
                                foreach (ExprNode argument in command.Arguments)
                                {
                                    if (argument is NameExpr stat && _content.Find(stat.Name) == null) _stats.Add(stat.Name);
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
                Warn(UnknownName, message, name.Span, Suggest.Closest(name.Name, NameCandidates(body)));
            }
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
                Error(UnknownName, $"`{written}` is a {other.KindName}, but a {string.Join(" or ", kinds.Take(2))} is needed here.", node.Span);
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

        // Whole-content checks -----------------------------------------------------------------

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

                Warn(UnknownEvent, $"Nothing raises an event called `{name}`: it is not built in and no content emits it.", body.Listener.Span,
                    Suggest.Closest(name, BuiltinEvents.Names.Concat(_emitted.Keys)));
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

        /// <summary>Description drift: GE401 to GE403, from <see cref="DescriptionBuilder.Validate"/>.</summary>
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
