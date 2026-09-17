using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GameplayEffects.Content;
using GameplayEffects.Diagnostics;
using GameplayEffects.Runtime;
using GameplayEffects.Syntax;

namespace GameplayEffects.Descriptions
{
    /// <summary>
    /// Builds descriptions for every kind of entity (section 3.12). One walk over a definition's
    /// effects both generates the automatic text and names every value in it (<c>damage</c>,
    /// <c>damage2</c>, <c>block</c>, <c>Poison</c>...), which is what links a writer's
    /// <c>{placeholders}</c> to the real effect.
    /// </summary>
    public sealed class DescriptionBuilder
    {
        public const string UnknownPlaceholder = "GE401";
        public const string StaleText = "GE402";
        public const string IgnoredText = "GE403";

        /// <summary>Card tags that read as keywords at the end of the rules text, in display order.</summary>
        private static readonly string[] CardKeywords = { "retain", "ethereal", "exhaust", "unplayable" };

        private static readonly List<DescriptionSegment> NoSegments = new List<DescriptionSegment>();

        private readonly ContentLibrary _content;
        private readonly IDescriptionLocalizer _localizer;

        public DescriptionBuilder(ContentLibrary content, IDescriptionLocalizer? localizer = null)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _localizer = localizer ?? EnglishDescriptions.Instance;
        }

        /// <summary>Describes a definition with its printed values, as in a card library or wiki.</summary>
        public Description Describe(EntityDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            return Build(definition, null, withTooltips: true);
        }

        /// <summary>
        /// Describes a live entity. Values go through the modifier pipeline exactly as the rules
        /// would use them now: damage through <c>damage</c> (and <c>damage_taken</c> when a target is
        /// given), costs as <see cref="CardRuntime.CostOf"/> computes them, stacks as they stand.
        /// </summary>
        public Description Describe(Entity entity, CardRuntime runtime, Entity? target = null)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));

            if (entity.Definition == null)
                return new Description(entity.Name, null, DescriptionLevel.Auto, NoSegments, null, new KeywordTooltip[0], null);

            return Build(entity.Definition, new Live(runtime, entity, target), withTooltips: true);
        }

        /// <summary>
        /// Describes a single enemy move, which is what an intent panel shows: "Deal 11 damage to
        /// you", not the enemy's whole repertoire. Pass the runtime and the enemy as well to get the
        /// numbers it would actually deal now, modifiers and all.
        /// </summary>
        public Description DescribeMove(EntityDefinition definition, string moveName, CardRuntime? runtime = null, Entity? enemy = null)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (moveName == null) throw new ArgumentNullException(nameof(moveName));

            MoveDefinition? move = definition.Moves.FirstOrDefault(m => string.Equals(m.Name, moveName, StringComparison.OrdinalIgnoreCase));
            if (move == null)
            {
                string? suggestion = Suggest.Closest(moveName, definition.Moves.Select(m => m.Name));
                throw new ArgumentException(
                    $"{definition} has no move \"{moveName}\"." + (suggestion == null ? string.Empty : $" Did you mean \"{suggestion}\"?"),
                    nameof(moveName));
            }

            Live? live = runtime != null && enemy != null ? new Live(runtime, enemy, runtime.Player) : null;
            var session = new Session(this, definition, live);
            List<DescriptionSegment> segments = session.RenderMove(move);

            return new Description(move.Name, definition, DescriptionLevel.Auto, Merge(segments), null, Tooltips(definition, session.References), null);
        }

        /// <summary>
        /// Describes what an enemy intends to do on its next turn. Empty while intents have not been
        /// rolled, which is also what a UI should show then.
        /// </summary>
        public Description DescribeIntent(Entity enemy, CardRuntime runtime)
        {
            if (enemy == null) throw new ArgumentNullException(nameof(enemy));
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));

            if (enemy.Definition == null || enemy.Intent == null)
                return new Description(enemy.Name, enemy.Definition, DescriptionLevel.Auto, new List<DescriptionSegment>(), null, new KeywordTooltip[0], null);

            return DescribeMove(enemy.Definition, enemy.Intent, runtime, enemy);
        }

        /// <summary>
        /// Drift protection: GE401 for placeholders that point at nothing, GE402 when the effect has
        /// changed since the writer recorded <c>text_checked</c>, GE403 when <c>text</c> is shadowed.
        /// </summary>
        public IReadOnlyList<Diagnostic> Validate(EntityDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            var diagnostics = new List<Diagnostic>();

            PropertyNode? text = definition.Property("text");
            if (text != null && definition.Property("text_override") != null)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, IgnoredText,
                    $"`text` on {definition} is ignored because `text_override` is also set.", text.Span));
            }

            if (text != null && definition.Text != null)
            {
                var session = new Session(this, definition, null);
                session.RenderAuto();

                foreach (string placeholder in Placeholders(definition.Text))
                {
                    if (session.Resolve(placeholder) != null) continue;
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, UnknownPlaceholder,
                        $"`{{{placeholder}}}` in the text of {definition} does not match any value in its effect.",
                        text.Span,
                        Suggest.Closest(placeholder, session.Values.Keys)));
                }
            }

            PropertyNode? recorded = definition.Property("text_checked");
            if (recorded != null)
            {
                string previous = recorded.First is StringExpr quoted ? quoted.Value : string.Empty;
                string current = EffectHash(definition);
                if (!string.Equals(previous, current, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, StaleText,
                        $"The rules of {definition} changed after its text was last checked. Review the text, then set `text_checked \"{current}\"`.",
                        recorded.Span));
                }
            }

            return diagnostics;
        }

        /// <summary>
        /// A stable fingerprint of everything that affects the rules of a definition, and nothing
        /// that only affects presentation. Record it as <c>text_checked "&lt;hash&gt;"</c>.
        /// </summary>
        public static string EffectHash(EntityDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));

            var canonical = new StringBuilder();
            foreach (MemberNode member in definition.Syntax.Members) Canonical.Member(member, canonical);

            ulong hash = 14695981039346656037UL;
            foreach (char c in canonical.ToString())
            {
                unchecked
                {
                    hash ^= c;
                    hash *= 1099511628211UL;
                }
            }
            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        // Building -----------------------------------------------------------------------------

        private Description Build(EntityDefinition definition, Live? live, bool withTooltips)
        {
            var session = new Session(this, definition, live);
            List<DescriptionSegment> auto = session.RenderAuto();

            DescriptionLevel level;
            List<DescriptionSegment> segments;
            string? localized = _localizer.Text(definition);

            if (definition.TextOverride != null)
            {
                level = DescriptionLevel.Override;
                segments = new List<DescriptionSegment> { DescriptionSegment.Plain(localized ?? definition.TextOverride) };
            }
            else if ((localized ?? definition.Text) is string template)
            {
                level = DescriptionLevel.Custom;
                segments = session.RenderCustom(template);
            }
            else
            {
                level = DescriptionLevel.Auto;
                segments = auto;
            }

            IReadOnlyList<KeywordTooltip> tooltips = withTooltips ? Tooltips(definition, session.References) : new KeywordTooltip[0];
            string name = _localizer.Name(definition) ?? definition.Name;
            string? flavour = _localizer.Flavour(definition) ?? definition.Flavour;
            return new Description(name, definition, level, Merge(segments), flavour, tooltips, session.Cost());
        }

        /// <summary>Keywords referenced directly or through other keywords, each once, breadth first.</summary>
        private IReadOnlyList<KeywordTooltip> Tooltips(EntityDefinition root, IEnumerable<EntityDefinition> direct)
        {
            var result = new List<KeywordTooltip>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Key(root) };
            var queue = new Queue<EntityDefinition>(direct);

            while (queue.Count > 0)
            {
                EntityDefinition next = queue.Dequeue();
                if (!seen.Add(Key(next))) continue;

                var session = new Session(this, next, null);
                session.RenderAuto();
                foreach (EntityDefinition nested in session.References) queue.Enqueue(nested);

                // A marker status with no rules of its own has nothing to explain.
                Description description = Build(next, null, withTooltips: false);
                if (!description.IsEmpty) result.Add(new KeywordTooltip(next, description));
            }

            return result;
        }

        private static string Key(EntityDefinition definition) => definition.KindName + ":" + definition.Name;

        internal string? Template(string key) => _localizer.Phrase(key) ?? EnglishDescriptions.Default(key);

        private static List<DescriptionSegment> Merge(List<DescriptionSegment> segments)
        {
            var merged = new List<DescriptionSegment>();
            var pending = new StringBuilder();

            foreach (DescriptionSegment segment in segments)
            {
                if (segment.Kind == SegmentKind.Text)
                {
                    pending.Append(segment.Text);
                    continue;
                }

                if (pending.Length > 0)
                {
                    merged.Add(DescriptionSegment.Plain(pending.ToString()));
                    pending.Clear();
                }
                merged.Add(segment);
            }

            if (pending.Length > 0) merged.Add(DescriptionSegment.Plain(pending.ToString()));
            return merged;
        }

        private static IEnumerable<string> Placeholders(string template)
        {
            int index = 0;
            while (index < template.Length)
            {
                int open = template.IndexOf('{', index);
                if (open < 0) yield break;
                int close = template.IndexOf('}', open + 1);
                if (close < 0) yield break;
                yield return template.Substring(open + 1, close - open - 1).Trim();
                index = close + 1;
            }
        }

        /// <summary>
        /// Fills <c>{slots}</c> in a template. Slots the caller does not supply render as nothing
        /// in generated phrases, but stay visible in a writer's text so a typo is easy to spot.
        /// </summary>
        private static List<DescriptionSegment> Fill(string template, Func<string, List<DescriptionSegment>?> lookup, bool keepUnknown)
        {
            var result = new List<DescriptionSegment>();
            int index = 0;

            while (index < template.Length)
            {
                int open = template.IndexOf('{', index);
                int close = open < 0 ? -1 : template.IndexOf('}', open + 1);
                if (open < 0 || close < 0)
                {
                    result.Add(DescriptionSegment.Plain(template.Substring(index)));
                    break;
                }

                if (open > index) result.Add(DescriptionSegment.Plain(template.Substring(index, open - index)));

                string name = template.Substring(open + 1, close - open - 1).Trim();
                List<DescriptionSegment>? value = lookup(name);
                if (value != null) result.AddRange(value);
                else if (keepUnknown) result.Add(DescriptionSegment.Plain(template.Substring(open, close - open + 1)));

                index = close + 1;
            }

            return result;
        }

        // Live state ---------------------------------------------------------------------------

        private sealed class Live
        {
            public Live(CardRuntime runtime, Entity entity, Entity? target)
            {
                Runtime = runtime;
                Entity = entity;
                Target = target;
            }

            public CardRuntime Runtime { get; }
            public Entity Entity { get; }
            public Entity? Target { get; }
        }

        // One description in progress ----------------------------------------------------------

        private sealed class Session
        {
            private static readonly NumberExpr One = new NumberExpr(Num.One, null, SourceSpan.None);
            private static readonly NumberExpr Zero = new NumberExpr(Num.Zero, null, SourceSpan.None);

            private readonly DescriptionBuilder _builder;
            private readonly EntityDefinition _definition;
            private readonly Live? _live;
            private readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly List<EntityDefinition> _references = new List<EntityDefinition>();

            public Session(DescriptionBuilder builder, EntityDefinition definition, Live? live)
            {
                _builder = builder;
                _definition = definition;
                _live = live;
            }

            /// <summary>Every named value, first occurrence wins, for resolving <c>{placeholders}</c>.</summary>
            public Dictionary<string, DescriptionSegment> Values { get; } = new Dictionary<string, DescriptionSegment>(StringComparer.OrdinalIgnoreCase);

            public IReadOnlyList<EntityDefinition> References => _references;

            private ContentLibrary Content => _builder._content;
            private bool IsStatus => _definition.Kind == EntityKind.Status || _definition.Kind == EntityKind.Keyword;
            private bool IsCard => _definition.Kind == EntityKind.Card;
            private bool IsEnemy => _definition.Kind == EntityKind.Actor;

            public DescriptionSegment? Resolve(string placeholder) =>
                Values.TryGetValue(placeholder, out DescriptionSegment? segment) ? segment : null;

            public List<DescriptionSegment> RenderCustom(string template) =>
                Fill(template, name => Resolve(name) is DescriptionSegment segment ? new List<DescriptionSegment> { segment } : null, keepUnknown: true);

            /// <summary>
            /// One move's body on its own, for an intent panel. Values are named exactly as they
            /// would be in the enemy's full description, so the same placeholders resolve.
            /// </summary>
            public List<DescriptionSegment> RenderMove(MoveDefinition move)
            {
                Values.Clear();
                _counts.Clear();
                _references.Clear();

                foreach (KeyValuePair<string, Num> stat in _definition.Stats)
                    Values[stat.Key] = DescriptionSegment.Number(stat.Value, stat.Value, stat.Key);

                return Block(move.Body);
            }

            public List<DescriptionSegment> RenderAuto()
            {
                Values.Clear();
                _counts.Clear();
                _references.Clear();

                foreach (KeyValuePair<string, Num> stat in _definition.Stats)
                    Values[stat.Key] = DescriptionSegment.Number(stat.Value, stat.Value, stat.Key);
                if (IsStatus) Values["stacks"] = Stacks();
                if (Cost() is DescriptionSegment cost) Values["cost"] = cost;

                var sentences = new List<List<DescriptionSegment>>();

                foreach (MemberNode member in _definition.Syntax.Members)
                {
                    switch (member)
                    {
                        case BlockMemberNode block when block.Name == "move":
                        {
                            string move = block.Arguments.Count > 0 ? EntityDefinition.ReadWords(block.Arguments[0]).FirstOrDefault() ?? "?" : "?";
                            sentences.Add(Phrase("move.enemy", ("move", Text(move)), ("body", Block(block.Body))));
                            break;
                        }

                        case BlockMemberNode block:
                            sentences.Add(Block(block.Body));
                            break;

                        case ListenerNode listener:
                            sentences.Add(Listener(listener));
                            break;

                        case ModifyNode modify:
                            sentences.Add(Modify(modify));
                            break;
                    }
                }

                if (IsStatus && _definition.DecayAmount > Num.Zero && (_definition.DecayOn is "turn_end" or "turn_start"))
                {
                    string key = _definition.DecayOn == "turn_start" ? "decay.turn_start" : "decay";
                    sentences.Add(Phrase(key, ("amount", Number(_definition.DecayAmount, null))));
                }

                // Printed with its unit (8s): unlike damage, a cooldown means nothing without one.
                if (_definition.Property("cooldown")?.First is NumberExpr cooldown)
                    sentences.Add(Phrase("cooldown", ("amount", Text(AstPrinter.Print(cooldown)))));

                if (IsCard)
                {
                    foreach (string keyword in CardKeywords)
                    {
                        if (!_definition.HasTag(keyword)) continue;
                        sentences.Add(Phrase("keyword." + keyword));
                        if (Content.FindAny(keyword, "keyword", "status") is EntityDefinition tooltip) Remember(tooltip);
                    }
                }

                return Join(sentences);
            }

            // Statements ---------------------------------------------------------------------

            private List<DescriptionSegment> Block(BlockNode block)
            {
                var sentences = new List<List<DescriptionSegment>>();
                foreach (StatementNode statement in block.Statements) sentences.Add(Statement(statement));
                return Join(sentences);
            }

            private List<DescriptionSegment> Statement(StatementNode statement)
            {
                switch (statement)
                {
                    case CommandNode command:
                        return Command(command);

                    case IfNode branch:
                    {
                        List<DescriptionSegment> sentence = Phrase("if", ("condition", Text(Condition(branch.Condition))), ("body", Lower(Block(branch.Then))));
                        if (branch.Else != null)
                        {
                            sentence.Add(DescriptionSegment.Plain(" "));
                            sentence.AddRange(Phrase("else", ("body", Lower(Block(branch.Else)))));
                        }
                        return sentence;
                    }

                    case RepeatNode repeat:
                        return Phrase("repeat", ("amount", Amount(repeat.Count, "repeat", "none", null)), ("body", Block(repeat.Body)));

                    case ForEachNode loop:
                        return Phrase("foreach", ("group", Text(Who(loop.Source))), ("body", Block(loop.Body)));

                    case ChanceNode chance:
                    {
                        List<DescriptionSegment> sentence = Phrase("chance", ("amount", Amount(chance.Probability, "chance", "none", null)), ("body", Block(chance.Body)));
                        if (chance.Else != null)
                        {
                            sentence.Add(DescriptionSegment.Plain(" "));
                            sentence.AddRange(Phrase("else", ("body", Lower(Block(chance.Else)))));
                        }
                        return sentence;
                    }

                    case ScheduleNode schedule:
                        switch (schedule.Kind)
                        {
                            case ScheduleKind.NextTurn:
                                return Phrase("next_turn", ("body", Block(schedule.Body)));
                            case ScheduleKind.After:
                                return Phrase("in_turns", ("amount", Amount(schedule.Delay ?? One, "delay", "none", null)), ("body", Block(schedule.Body)));
                            default:
                                return schedule.Deadline == "turn_end"
                                    ? Phrase("until.turn_end", ("body", Block(schedule.Body)))
                                    : Phrase("until", ("deadline", Text((schedule.Deadline ?? "?").Replace('_', ' '))), ("body", Block(schedule.Body)));
                        }

                    case AssignNode assign:
                        return Assign(assign);

                    case LabeledBlockNode labeled:
                        return Block(labeled.Body);

                    default:
                        return new List<DescriptionSegment>();
                }
            }

            private List<DescriptionSegment> Command(CommandNode command)
            {
                string verb = command.Verb.ToLowerInvariant();
                ExprNode? first = command.Arguments.Count > 0 ? command.Arguments[0] : null;
                ExprNode? second = command.Arguments.Count > 1 ? command.Arguments[1] : null;

                switch (verb)
                {
                    case "deal":
                    case "damage":
                    {
                        string? type = command.Clause("as") is ExprNode typeNode ? FirstWord(typeNode) : null;
                        bool ignore = command.HasFlag("ignore_block") || command.HasFlag("pierce") || command.HasFlag("unblockable") || command.HasFlag("true_damage");
                        return Phrase("deal",
                            ("amount", Amount(first ?? Zero, "damage", "damage", command)),
                            ("type", type == null ? Empty() : Text(type + " ")),
                            ("target", To(command.Clause("to"))),
                            ("ignore", ignore ? Phrase("ignore_block") : Empty()));
                    }

                    case "block":
                    case "gain_block":
                    case "heal":
                    {
                        string key = verb == "heal" ? "heal" : "block";
                        ExprNode? to = command.Clause("to");
                        List<DescriptionSegment> amount = Amount(first ?? Zero, key, key, command);
                        return to == null || IsSelf(to)
                            ? Phrase(key, ("amount", amount))
                            : Phrase(key + ".target", ("amount", amount), ("target", Text(Who(to))));
                    }

                    case "draw":
                    {
                        ExprNode count = first ?? One;
                        return Phrase(IsOne(count) ? "draw.one" : "draw.many", ("amount", Amount(count, "draw", "draw", command)));
                    }

                    case "apply":
                    case "add" when first is NameExpr || first is StringExpr:
                    {
                        string status = Reference(first, "status", "keyword");
                        List<DescriptionSegment> duration = command.Clause("for") is ExprNode length
                            ? Phrase("for_duration", ("amount", Text(AstPrinter.Print(length))))
                            : Empty();
                        return Phrase("apply",
                            ("amount", Amount(second ?? One, status, "none", command)),
                            ("status", Text(status)),
                            ("target", To(command.Clause("to"))),
                            ("duration", duration));
                    }

                    case "gain":
                    case "lose":
                    {
                        string thing = second == null ? "?" : Reference(second, "status", "keyword");
                        return Phrase(verb,
                            ("amount", Amount(first ?? One, thing, "none", command)),
                            ("thing", Text(thing)),
                            ("target", To(command.Clause("to"))));
                    }

                    case "remove":
                    {
                        List<DescriptionSegment> thing = first switch
                        {
                            QualifiedExpr { Qualifier: "tag" } tag => Phrase("remove.tag", ("tag", Text(tag.Name))),
                            NameExpr or StringExpr => Text(Reference(first, "status", "keyword")),
                            null => Text(Word("who.it") ?? "it"),
                            _ => Text(Who(first)),
                        };
                        return Phrase("remove", ("thing", thing), ("from", From(command.Clause("from"))));
                    }

                    case "create":
                    {
                        string card = first == null ? "?" : FirstWord(first) ?? AstPrinter.Print(first);
                        ExprNode count = second ?? One;
                        string zone = (command.Clause("into") ?? command.Clause("to") ?? command.Clause("onto")) is ExprNode zoneNode
                            ? FirstWord(zoneNode) ?? "hand"
                            : "hand";
                        return Phrase(IsOne(count) ? "create.one" : "create.many",
                            ("amount", Amount(count, "create", "none", command)),
                            ("card", Text(card)),
                            ("zone", Text(Word("zone." + zone) ?? zone)));
                    }

                    case "shuffle":
                        if (first == null) return Phrase("shuffle");
                        return Phrase("shuffle.cards",
                            ("amount", Amount(second ?? One, "shuffle", "none", command)),
                            ("card", Text(FirstWord(first) ?? AstPrinter.Print(first))));

                    case "discard":
                    case "exhaust":
                    {
                        if (first == null || first is NameExpr { Name: "self" })
                            return verb == "exhaust" ? Phrase("exhaust.self") : Phrase("discard.thing", ("thing", Text(Word("who.this_card") ?? "this card")));
                        if (Literal(first, out _) != null)
                            return Phrase(verb + (IsOne(first) ? ".one" : ".many"), ("amount", Amount(first, verb, "none", command)));
                        return Phrase(verb + ".thing", ("thing", Text(Who(first))));
                    }

                    case "kill":
                        return Phrase("kill", ("target", Text(Who(first ?? command.Clause("to") ?? new NameExpr("target", SourceSpan.None)))));

                    case "cancel":
                        return Phrase("cancel");

                    case "choose":
                        return Phrase("choose",
                            ("amount", Amount(first ?? One, "choose", "none", command)),
                            ("from", Text(command.Clause("from") is ExprNode group ? Who(group) : "?")));

                    case "emit":
                        return Phrase("emit", ("event", Text((first == null ? null : FirstWord(first))?.Replace('_', ' ') ?? "?")));

                    case "log":
                        return new List<DescriptionSegment>();

                    case "replay":
                    {
                        ExprNode? card = first is BinaryExpr { Operator: BinaryOperator.On } on ? on.Left : first;
                        return Phrase("replay", ("card", Text(card == null ? Word("who.it") ?? "it" : Who(card))));
                    }

                    case "use":
                        return Phrase("use", ("move", Text(first == null ? "?" : FirstWord(first) ?? AstPrinter.Print(first))));

                    default:
                    {
                        string arguments = command.Arguments.Count == 0 ? string.Empty : " " + string.Join(" ", command.Arguments.Select(a => Who(a)));
                        return Phrase("verb", ("verb", Text(Capitalize(command.Verb.Replace('_', ' ')))), ("args", Text(arguments)));
                    }
                }
            }

            private List<DescriptionSegment> Assign(AssignNode assign)
            {
                string thing = assign.Target switch
                {
                    NameExpr name => name.Name,
                    MemberExpr member => member.Member,
                    _ => AstPrinter.Print(assign.Target),
                };

                bool ownStacks = IsStatus && assign.Target is NameExpr { Name: "stacks" };

                switch (assign.Operator)
                {
                    case AssignOperator.Subtract when ownStacks:
                        return Phrase(IsOne(assign.Value) ? "stacks.lose.one" : "stacks.lose.many", ("amount", Amount(assign.Value, "stacks_lost", "none", null)));
                    case AssignOperator.Add:
                        return Phrase("gain", ("amount", Amount(assign.Value, thing, "none", null)), ("thing", Text(thing)));
                    case AssignOperator.Subtract:
                        return Phrase("lose", ("amount", Amount(assign.Value, thing, "none", null)), ("thing", Text(thing)));
                    case AssignOperator.Multiply:
                        return Phrase("stat.multiply", ("thing", Text(thing)), ("amount", Amount(assign.Value, thing, "none", null)));
                    default:
                        return Phrase("stat.set", ("thing", Text(thing)), ("amount", Amount(assign.Value, thing, "none", null)));
                }
            }

            // Listeners and modifiers -------------------------------------------------------

            private List<DescriptionSegment> Listener(ListenerNode listener)
            {
                string raw = listener.EventName;
                int dot = raw.LastIndexOf('.');
                string? scope = dot > 0 ? raw.Substring(0, dot) : null;
                string name = (dot > 0 ? raw.Substring(dot + 1) : raw).ToLowerInvariant();

                List<DescriptionSegment> limit = listener.Limit switch
                {
                    LimitScope.Turn => Phrase("limit.turn"),
                    LimitScope.Battle => Phrase("limit.battle"),
                    LimitScope.Run => Phrase("limit.run"),
                    _ => Empty(),
                };
                List<DescriptionSegment> body = Block(listener.Body);

                if (listener.Phase == EventPhase.After && scope == null && listener.Filter == null
                    && name is "turn_start" or "turn_end" or "battle_start" or "battle_end")
                {
                    string key = "on." + name + (IsStatus && name.StartsWith("turn_", StringComparison.Ordinal) ? ".status" : string.Empty);
                    return Phrase(key, ("limit", limit), ("body", body));
                }

                string phase = listener.Phase switch
                {
                    EventPhase.Before => "on.before",
                    EventPhase.Instead => "on.instead",
                    _ => "on.after",
                };
                return Phrase(phase, ("event", Text(EventPhrase(name, scope, listener.Filter))), ("limit", limit), ("body", body));
            }

            private string EventPhrase(string name, string? scope, ExprNode? filter)
            {
                var tags = new List<string>();
                var extra = new List<string>();
                string? who = scope == null ? null : Who(new NameExpr(scope, SourceSpan.None));

                foreach (ExprNode clause in Clauses(filter))
                {
                    switch (clause)
                    {
                        case QualifiedExpr { Qualifier: "tag" or "keyword" } tag:
                            tags.Add(tag.Name);
                            break;
                        case QualifiedExpr { Qualifier: "target" } role:
                            who ??= Who(new NameExpr(role.Name, SourceSpan.None));
                            break;
                        case NameExpr named when Content.FindAny(named.Name, "status", "keyword") is EntityDefinition status:
                            Remember(status);
                            tags.Add(status.Name);
                            break;
                        default:
                            extra.Add(AstPrinter.Print(clause));
                            break;
                    }
                }

                string tagText = tags.Count == 0 ? string.Empty : string.Join(" ", tags) + " ";
                string article = Article(tags.Count > 0 ? tags[0] : "x");
                bool you = who != null && who == Word("who.you");

                string? template = (you ? _builder.Template("event." + name + ".you") : null) ?? _builder.Template("event." + name);
                string phrase = template == null
                    ? name.Replace('_', ' ')
                    : Words(template, ("who", who ?? Word("who.anyone") ?? "anyone"), ("tags", tagText), ("a", article));

                return extra.Count == 0 ? phrase : phrase + " (" + string.Join(", ", extra) + ")";
            }

            private List<DescriptionSegment> Modify(ModifyNode modify)
            {
                string channel = Word("channel." + modify.Channel) ?? Capitalize(modify.Channel.Replace('_', ' '));
                var amount = new List<DescriptionSegment>();

                switch (modify.Layer)
                {
                    case ModifierLayer.Add:
                        if (!(Literal(modify.Amount, out _) is Num signed && signed.IsNegative)) amount.Add(DescriptionSegment.Plain("+"));
                        amount.AddRange(Amount(modify.Amount, "bonus", "none", null));
                        break;
                    case ModifierLayer.Multiply:
                        amount.Add(DescriptionSegment.Plain("×"));
                        amount.AddRange(Amount(modify.Amount, "multiplier", "none", null));
                        break;
                    case ModifierLayer.Clamp:
                        amount.AddRange(Phrase("modify.clamp", ("amount", Text(AstPrinter.Print(modify.Amount)))));
                        break;
                    default:
                        amount.AddRange(Phrase("modify.override", ("amount", Amount(modify.Amount, "value", "none", null))));
                        break;
                }

                var scope = new List<DescriptionSegment>();
                ExprNode? group = modify.Scope is WhereExpr where ? where.Source : modify.Scope;
                if (group != null) scope.AddRange(Phrase("modify.for", ("who", Text(Who(group)))));

                var tags = Clauses(modify.Filter)
                    .Concat(modify.Scope is WhereExpr scoped ? Clauses(scoped.Predicate) : Enumerable.Empty<ExprNode>())
                    .OfType<QualifiedExpr>()
                    .Where(q => q.Qualifier == "tag" || q.Qualifier == "keyword")
                    .Select(q => q.Name)
                    .ToList();
                if (tags.Count > 0) scope.Add(DescriptionSegment.Plain(" (" + string.Join(", ", tags) + ")"));

                return Phrase("modify", ("channel", Text(channel)), ("amount", amount), ("scope", scope));
            }

            // Values -------------------------------------------------------------------------

            public DescriptionSegment? Cost()
            {
                if (!IsCard) return null;
                ExprNode? cost = _definition.Property("cost")?.First;
                if (cost == null) return null;

                if (cost is NameExpr { Name: var name } && string.Equals(name, "x", StringComparison.OrdinalIgnoreCase))
                    return DescriptionSegment.Symbol("X", "cost");

                if (!(Literal(cost, out _) is Num printed)) return DescriptionSegment.Symbol(AstPrinter.Print(cost), "cost");

                if (_live == null || _live.Entity.Kind != EntityKind.Card)
                    return DescriptionSegment.Number(printed, printed, "cost", null, lowerIsBetter: true);

                Num current = Num.FromInt(_live.Runtime.CostOf(_live.Entity));
                return DescriptionSegment.Number(_live.Entity.GetBase("cost"), current, "cost", null, lowerIsBetter: true);
            }

            private DescriptionSegment Stacks()
            {
                if (_live != null && (_live.Entity.Kind == EntityKind.Status || _live.Entity.Kind == EntityKind.Keyword))
                {
                    Num stacks = _live.Entity.Get("stacks");
                    return DescriptionSegment.Number(stacks, stacks, "stacks");
                }
                return DescriptionSegment.Symbol("X", "stacks");
            }

            /// <summary>Names a value, evaluates it (live when possible) and records it for placeholders.</summary>
            private List<DescriptionSegment> Amount(ExprNode expression, string placeholder, string channel, CommandNode? command)
            {
                string name = NextName(placeholder);
                DescriptionSegment segment = Evaluate(expression, name, channel, command);
                if (!Values.ContainsKey(name)) Values[name] = segment;
                return new List<DescriptionSegment> { segment };
            }

            private DescriptionSegment Evaluate(ExprNode expression, string name, string channel, CommandNode? command)
            {
                Num? printed = Literal(expression, out string? unit);

                if (_live == null)
                {
                    return printed is Num value
                        ? DescriptionSegment.Number(value, value, name, unit)
                        : DescriptionSegment.Symbol(SymbolFor(expression), name);
                }

                Num? known = printed ?? TryEvaluateLive(expression, out unit);
                if (!(known is Num baseValue)) return DescriptionSegment.Symbol(SymbolFor(expression), name);

                return DescriptionSegment.Number(baseValue, ApplyChannel(channel, baseValue, command), name, unit);
            }

            /// <summary>
            /// Evaluates an amount against the live game when it cannot change anything: no ranges
            /// and no <c>random</c>, which would consume numbers from the game's RNG.
            /// </summary>
            private Num? TryEvaluateLive(ExprNode expression, out string? unit)
            {
                unit = null;
                if (_live == null) return null;

                var probe = new PurityProbe();
                probe.VisitExpression(expression);
                if (!probe.Pure) return null;

                Entity entity = _live.Entity;
                var context = new EvalContext(entity)
                {
                    Source = entity.Kind == EntityKind.Card ? entity.Controller : entity,
                    Target = _live.Target ?? (IsStatus ? entity.Owner : null),
                    Card = entity.Kind == EntityKind.Card ? entity : null,
                };

                try
                {
                    Value value = _live.Runtime.Interpreter.Evaluate(expression, context);
                    if (value.Kind != ValueKind.Number && value.Kind != ValueKind.Bool) return null;
                    unit = value.Unit;
                    return value.Number;
                }
                catch (RuntimeError)
                {
                    return null;
                }
            }

            /// <summary>Runs a value through the same modifier channels its verb would use.</summary>
            private Num ApplyChannel(string channel, Num amount, CommandNode? command)
            {
                if (_live == null) return amount;

                GameState state = _live.Runtime.State;
                Entity entity = _live.Entity;
                Entity controller = entity.Controller;
                Entity? card = entity.Kind == EntityKind.Card ? entity : null;
                Entity source = entity.Kind == EntityKind.Card ? controller : entity;

                switch (channel)
                {
                    case "damage":
                    {
                        var tags = entity.Kind == EntityKind.Actor ? new List<string>() : entity.Tags.ToList();
                        if (command?.Clause("as") is ExprNode type && FirstWord(type) is string typeTag) tags.Add(typeTag);
                        string[] tagArray = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

                        Num value = state.Modifiers.Compute(new ModifierQuery("damage") { Source = source, Subject = _live.Target, Card = card, Tags = tagArray }, amount);
                        if (_live.Target != null)
                            value = state.Modifiers.Compute(new ModifierQuery("damage_taken") { Source = source, Subject = _live.Target, Card = card, Tags = tagArray }, value);
                        return Num.Max(Num.Zero, value.Floor());
                    }

                    case "block":
                    case "heal":
                    {
                        Num value = state.Modifiers.Compute(new ModifierQuery(channel) { Source = source, Subject = controller, Card = card }, amount);
                        value = state.Modifiers.Compute(new ModifierQuery(channel + "_taken") { Source = source, Subject = controller, Card = card }, value);
                        return Num.Max(Num.Zero, value.Floor());
                    }

                    case "draw":
                        return Num.Max(Num.Zero, state.Modifiers.Compute(new ModifierQuery("draw") { Source = controller, Subject = controller, Card = card }, amount).Floor());

                    default:
                        return amount;
                }
            }

            private string NextName(string name)
            {
                _counts.TryGetValue(name, out int count);
                _counts[name] = ++count;
                return count == 1 ? name : name + count.ToString(CultureInfo.InvariantCulture);
            }

            private static Num? Literal(ExprNode expression, out string? unit)
            {
                unit = null;
                switch (expression)
                {
                    case NumberExpr number:
                        unit = number.Unit;
                        return number.Value;
                    case UnaryExpr { Operator: UnaryOperator.Negate, Operand: NumberExpr negative }:
                        unit = negative.Unit;
                        return -negative.Value;
                    default:
                        return null;
                }
            }

            private static string SymbolFor(ExprNode expression) => expression switch
            {
                NameExpr { Name: var name } when string.Equals(name, "x", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(name, "stacks", StringComparison.OrdinalIgnoreCase) => "X",
                RangeExpr range => AstPrinter.Print(range.Low) + "–" + AstPrinter.Print(range.High),
                _ => AstPrinter.Print(expression),
            };

            private static bool IsOne(ExprNode expression) => Literal(expression, out _) is Num value && value == Num.One;

            // Words ---------------------------------------------------------------------------

            private string Who(ExprNode expression, bool implicitTarget = false)
            {
                switch (expression)
                {
                    case NameExpr name:
                        switch (name.Name.ToLowerInvariant())
                        {
                            case "target": return implicitTarget && IsCard ? string.Empty : Word("who.target") ?? "the target";
                            case "owner": return Word(IsStatus ? "who.holder" : "who.you") ?? name.Name;
                            case "self": return Word(IsEnemy ? "who.itself" : IsCard ? "who.this_card" : "who.you") ?? name.Name;
                            case "player": return Word(IsEnemy || IsStatus ? "who.player" : "who.you") ?? name.Name;
                            case "controller": return Word("who.you") ?? name.Name;
                            case "source": return Word("who.attacker") ?? name.Name;
                            case "enemies": return Word(IsEnemy ? "who.its_enemies" : "who.all_enemies") ?? name.Name;
                            case "allies": return Word("who.all_allies") ?? name.Name;
                            case "everyone": return Word("who.everyone") ?? name.Name;
                            case "enemy": return Word("who.an_enemy") ?? name.Name;
                            case "any": return Word("who.anyone") ?? name.Name;
                            default: return name.Name;
                        }

                    case SelectorExpr { Modifier: SelectorModifier.All } all:
                        return Who(all.Source);

                    case SelectorExpr { Modifier: SelectorModifier.Random } random:
                        return Words(Word("who.random") ?? "{amount} random {group}",
                            ("amount", random.Count == null ? "1" : AstPrinter.Print(random.Count)),
                            ("group", Who(random.Source)));

                    case SelectorExpr { Modifier: SelectorModifier.Lowest or SelectorModifier.Highest } sorted:
                        return Words(Word(sorted.Modifier == SelectorModifier.Lowest ? "who.lowest" : "who.highest") ?? "{group}",
                            ("group", Who(sorted.Source)),
                            ("stat", sorted.Key ?? "hp"));

                    case CallExpr { Name: "adjacent" }:
                        return Word("who.adjacent") ?? "adjacent enemies";

                    case MemberExpr { Target: NameExpr { Name: "event" } } member:
                        return member.Member.ToLowerInvariant() switch
                        {
                            "target" => Word("who.it") ?? "it",
                            "source" => Word("who.attacker") ?? "the attacker",
                            "card" => Word("who.that_card") ?? "that card",
                            _ => AstPrinter.Print(expression),
                        };

                    default:
                        return AstPrinter.Print(expression);
                }
            }

            private List<DescriptionSegment> To(ExprNode? expression)
            {
                if (expression == null) return Empty();
                string who = Who(expression, implicitTarget: true);
                return who.Length == 0 ? Empty() : Phrase("to", ("who", Text(who)));
            }

            private List<DescriptionSegment> From(ExprNode? expression) =>
                expression == null ? Empty() : Phrase("from", ("who", Text(Who(expression))));

            private bool IsSelf(ExprNode expression) => expression is NameExpr { Name: var name }
                && (name == "self" || name == "controller" || (name == "owner" && !IsStatus));

            private string Condition(ExprNode expression)
            {
                switch (expression)
                {
                    case MemberExpr { Member: var member } dead when string.Equals(member, "dead", StringComparison.OrdinalIgnoreCase):
                        return Words(Word("cond.dies") ?? "{who} dies", ("who", Who(dead.Target)));
                    case MemberExpr { Member: var member } alive when string.Equals(member, "alive", StringComparison.OrdinalIgnoreCase):
                        return Words(Word("cond.alive") ?? "{who} is alive", ("who", Who(alive.Target)));
                    case UnaryExpr { Operator: UnaryOperator.Not } not:
                        return Words(Word("cond.not") ?? "not {condition}", ("condition", Condition(not.Operand)));
                    case CallExpr { Name: "has", Receiver: ExprNode receiver } has when has.Arguments.Count == 1:
                        return Words(Word("cond.has") ?? "{who} has {thing}", ("who", Who(receiver)), ("thing", Reference(has.Arguments[0], "status", "keyword")));
                    default:
                        return AstPrinter.Print(expression);
                }
            }

            /// <summary>The written name of a definition, remembered for tooltips when it is a status or keyword.</summary>
            private string Reference(ExprNode? node, params string[] kinds)
            {
                if (node == null) return "?";
                string? written = FirstWord(node);
                if (written == null) return AstPrinter.Print(node);

                EntityDefinition? definition = Content.FindAny(written, kinds);
                if (definition == null) return written;

                Remember(definition);
                return _builder._localizer.Name(definition) ?? definition.Name;
            }

            private void Remember(EntityDefinition definition)
            {
                if (SameDefinition(definition, _definition)) return;
                if (_references.Any(r => SameDefinition(r, definition))) return;
                _references.Add(definition);
            }

            private static bool SameDefinition(EntityDefinition a, EntityDefinition b) =>
                string.Equals(a.KindName, b.KindName, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

            private static IEnumerable<ExprNode> Clauses(ExprNode? filter)
            {
                if (filter == null) yield break;
                if (filter is BinaryExpr { Operator: BinaryOperator.And } and)
                {
                    foreach (ExprNode left in Clauses(and.Left)) yield return left;
                    foreach (ExprNode right in Clauses(and.Right)) yield return right;
                    yield break;
                }
                yield return filter;
            }

            // Templates ----------------------------------------------------------------------

            private List<DescriptionSegment> Phrase(string key, params (string Slot, List<DescriptionSegment> Value)[] slots)
            {
                string template = _builder.Template(key) ?? "{body}";
                return Fill(template, name =>
                {
                    foreach (var (slot, value) in slots)
                    {
                        if (string.Equals(slot, name, StringComparison.OrdinalIgnoreCase)) return value;
                    }
                    return Empty();
                }, keepUnknown: false);
            }

            private static string Words(string template, params (string Slot, string Value)[] slots)
            {
                var text = new StringBuilder();
                List<DescriptionSegment> filled = Fill(template, name =>
                {
                    foreach (var (slot, value) in slots)
                    {
                        if (string.Equals(slot, name, StringComparison.OrdinalIgnoreCase)) return Text(value);
                    }
                    return Empty();
                }, keepUnknown: false);
                foreach (DescriptionSegment segment in filled) text.Append(segment.Text);
                return text.ToString();
            }

            private string? Word(string key) => _builder.Template(key);

            private static List<DescriptionSegment> Join(List<List<DescriptionSegment>> sentences)
            {
                var result = new List<DescriptionSegment>();
                foreach (List<DescriptionSegment> sentence in sentences)
                {
                    if (sentence.All(s => s.Text.Length == 0)) continue;
                    if (result.Count > 0) result.Add(DescriptionSegment.Plain(" "));
                    result.AddRange(sentence);
                }
                return result;
            }

            /// <summary>Lower-cases the first letter, for a sentence that continues "If ..., ".</summary>
            private static List<DescriptionSegment> Lower(List<DescriptionSegment> segments)
            {
                var result = new List<DescriptionSegment>(segments);
                for (int i = 0; i < result.Count; i++)
                {
                    if (result[i].Text.Length == 0) continue;
                    if (result[i].Kind == SegmentKind.Text)
                    {
                        string text = result[i].Text;
                        result[i] = DescriptionSegment.Plain(char.ToLowerInvariant(text[0]) + text.Substring(1));
                    }
                    break;
                }
                return result;
            }

            private static List<DescriptionSegment> Text(string text) => new List<DescriptionSegment> { DescriptionSegment.Plain(text) };

            private static List<DescriptionSegment> Empty() => new List<DescriptionSegment>();

            private static List<DescriptionSegment> Number(Num value, string? placeholder, string? unit = null) =>
                new List<DescriptionSegment> { DescriptionSegment.Number(value, value, placeholder, unit) };

            private static string? FirstWord(ExprNode node) => node switch
            {
                NameExpr name => name.Name,
                StringExpr text => text.Value,
                QualifiedExpr qualified => qualified.Name,
                _ => null,
            };

            private static string Capitalize(string text) =>
                text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

            private static string Article(string word) =>
                word.Length > 0 && "aeiou".IndexOf(char.ToLowerInvariant(word[0])) >= 0 ? "an" : "a";
        }

        /// <summary>Finds expressions that are safe to evaluate for display: nothing that rolls dice.</summary>
        private sealed class PurityProbe : AstWalker
        {
            public bool Pure { get; private set; } = true;

            public override void VisitExpression(ExprNode expression)
            {
                switch (expression)
                {
                    case RangeExpr _:
                    case SelectorExpr { Modifier: SelectorModifier.Random }:
                    case CallExpr { Name: "random" }:
                        Pure = false;
                        return;
                }
                base.VisitExpression(expression);
            }
        }

        /// <summary>The rules-relevant text of a definition, fed to <see cref="EffectHash"/>.</summary>
        private static class Canonical
        {
            private static readonly HashSet<string> Presentation = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "text", "text_override", "flavour", "flavor", "text_checked",
            };

            public static void Member(MemberNode member, StringBuilder text)
            {
                switch (member)
                {
                    case PropertyNode property when Presentation.Contains(property.Name):
                        return;

                    case PropertyNode property:
                        text.Append("property ").Append(property.Name);
                        foreach (ExprNode value in property.Values) text.Append(' ').Append(AstPrinter.Print(value));
                        text.Append('\n');
                        return;

                    case BlockMemberNode block:
                        text.Append("block ").Append(block.Name);
                        foreach (ExprNode argument in block.Arguments) text.Append(' ').Append(AstPrinter.Print(argument));
                        text.Append('\n');
                        Block(block.Body, text, 1);
                        return;

                    case ListenerNode listener:
                        text.Append("on ").Append(listener.Phase).Append(' ').Append(listener.EventName)
                            .Append(' ').Append(listener.Filter == null ? string.Empty : AstPrinter.Print(listener.Filter))
                            .Append(' ').Append(listener.Limit)
                            .Append(' ').Append(listener.Priority.ToString(CultureInfo.InvariantCulture))
                            .Append('\n');
                        Block(listener.Body, text, 1);
                        return;

                    case ModifyNode modify:
                        text.Append("modify ").Append(modify.Channel)
                            .Append(' ').Append(modify.Scope == null ? string.Empty : AstPrinter.Print(modify.Scope))
                            .Append(' ').Append(modify.Filter == null ? string.Empty : AstPrinter.Print(modify.Filter))
                            .Append(' ').Append(modify.Layer)
                            .Append(' ').Append(AstPrinter.Print(modify.Amount))
                            .Append('\n');
                        return;
                }
            }

            private static void Block(BlockNode block, StringBuilder text, int depth)
            {
                foreach (StatementNode statement in block.Statements) Statement(statement, text, depth);
            }

            private static void Statement(StatementNode statement, StringBuilder text, int depth)
            {
                text.Append(' ', depth * 2);
                switch (statement)
                {
                    case CommandNode command:
                        text.Append(command.Verb.ToLowerInvariant());
                        foreach (ExprNode argument in command.Arguments) text.Append(' ').Append(AstPrinter.Print(argument));
                        foreach (ClauseNode clause in command.Clauses)
                        {
                            text.Append(' ').Append(clause.Keyword);
                            if (clause.Value != null) text.Append(' ').Append(AstPrinter.Print(clause.Value));
                        }
                        text.Append('\n');
                        break;

                    case IfNode branch:
                        text.Append("if ").Append(AstPrinter.Print(branch.Condition)).Append('\n');
                        Block(branch.Then, text, depth + 1);
                        if (branch.Else != null)
                        {
                            text.Append(' ', depth * 2).Append("else\n");
                            Block(branch.Else, text, depth + 1);
                        }
                        break;

                    case RepeatNode repeat:
                        text.Append("repeat ").Append(AstPrinter.Print(repeat.Count)).Append('\n');
                        Block(repeat.Body, text, depth + 1);
                        break;

                    case ForEachNode loop:
                        text.Append("for ").Append(loop.Variable).Append(" in ").Append(AstPrinter.Print(loop.Source)).Append('\n');
                        Block(loop.Body, text, depth + 1);
                        break;

                    case ChanceNode chance:
                        text.Append("chance ").Append(AstPrinter.Print(chance.Probability)).Append('\n');
                        Block(chance.Body, text, depth + 1);
                        if (chance.Else != null)
                        {
                            text.Append(' ', depth * 2).Append("else\n");
                            Block(chance.Else, text, depth + 1);
                        }
                        break;

                    case ScheduleNode schedule:
                        text.Append("schedule ").Append(schedule.Kind)
                            .Append(' ').Append(schedule.Delay == null ? string.Empty : AstPrinter.Print(schedule.Delay))
                            .Append(' ').Append(schedule.Deadline ?? string.Empty)
                            .Append('\n');
                        Block(schedule.Body, text, depth + 1);
                        break;

                    case AssignNode assign:
                        text.Append(AstPrinter.Print(assign.Target)).Append(' ').Append(assign.Operator).Append(' ').Append(AstPrinter.Print(assign.Value)).Append('\n');
                        break;

                    case LabeledBlockNode labeled:
                        text.Append(labeled.Label).Append(":\n");
                        Block(labeled.Body, text, depth + 1);
                        break;
                }
            }
        }
    }
}
