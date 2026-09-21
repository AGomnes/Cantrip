using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Diagnostics;
using Cantrip.Syntax;

namespace Cantrip.Content
{
    /// <summary>Status flags: what a status is (buff, debuff) and how it behaves (persistent, unique).</summary>
    [Flags]
    public enum StatusFlags
    {
        None = 0,
        Buff = 1 << 0,
        Debuff = 1 << 1,
        Dispellable = 1 << 2,
        Persistent = 1 << 3,
        Hidden = 1 << 4,
        UniquePerSource = 1 << 5,
    }

    public enum EnemyPatternKind
    {
        /// <summary>Moves in order, then loop.</summary>
        Cycle,

        /// <summary>Uniform random move each turn.</summary>
        Random,

        /// <summary>Uniform random, never the same move twice in a row.</summary>
        RandomNoRepeat,
    }

    /// <summary>
    /// A behaviour phase: <c>phase Broken when hp &lt;= max_hp / 2</c>. Moves tagged with the phase
    /// are available only while its condition holds.
    /// </summary>
    public sealed class PhaseDefinition
    {
        public PhaseDefinition(string name, ExprNode condition, SourceSpan span, bool retelegraph = false)
        {
            Name = name;
            Condition = condition;
            Span = span;
            Retelegraph = retelegraph;
        }

        public string Name { get; }

        /// <summary>Evaluated against the enemy each time its intent is rolled.</summary>
        public ExprNode Condition { get; }

        public SourceSpan Span { get; }

        /// <summary>
        /// Whether entering this phase re-rolls the enemy's intent there and then, so a boss can
        /// telegraph the move the phase has just unlocked.
        /// </summary>
        /// <remarks>
        /// Opt-in, because it trades away a guarantee worth keeping: ordinarily the intent shown
        /// during the player's turn is exactly the move that follows, and re-telegraphing means a
        /// boss can change its mind after the player has committed. Only a phase that asks for it
        /// does that.
        /// </remarks>
        public bool Retelegraph { get; }

        public override string ToString() => Name;
    }

    /// <summary>A named enemy move: <c>move "Chomp": deal 11 to player</c>.</summary>
    public sealed class MoveDefinition
    {
        public MoveDefinition(string name, BlockNode body, Num weight, string? phase = null)
        {
            Name = name;
            Body = body;
            Weight = weight;
            Phase = phase;
        }

        public string Name { get; }
        public BlockNode Body { get; }
        public Num Weight { get; }

        /// <summary>The phase this move belongs to, or null when it is available in every phase.</summary>
        public string? Phase { get; }
    }

    /// <summary>
    /// Loaded, validated content: one card, status, relic, enemy, keyword or resource. Built once
    /// from the syntax tree and shared by every entity instantiated from it.
    /// </summary>
    public sealed class EntityDefinition
    {
        private readonly Dictionary<string, PropertyNode> _properties;
        private readonly Dictionary<string, Num> _stats;

        internal EntityDefinition(EntityDeclNode syntax, DiagnosticBag diagnostics)
        {
            Syntax = syntax;
            Name = syntax.Name;
            KindName = syntax.Kind;
            Kind = ParseKind(syntax.Kind);

            _properties = new Dictionary<string, PropertyNode>(StringComparer.OrdinalIgnoreCase);
            _stats = new Dictionary<string, Num>(StringComparer.OrdinalIgnoreCase);
            var tags = new List<string>();
            var listeners = new List<ListenerNode>();
            var modifiers = new List<ModifyNode>();
            var blocks = new Dictionary<string, BlockMemberNode>(StringComparer.OrdinalIgnoreCase);
            var moves = new List<MoveDefinition>();
            var phases = new List<PhaseDefinition>();

            foreach (MemberNode member in syntax.Members)
            {
                switch (member)
                {
                    case ListenerNode listener:
                        listeners.Add(listener);
                        break;

                    case ModifyNode modify:
                        modifiers.Add(modify);
                        break;

                    case BlockMemberNode block when block.Name == "move":
                        moves.Add(ReadMove(block, diagnostics));
                        break;

                    case BlockMemberNode block:
                        if (blocks.ContainsKey(block.Name))
                            diagnostics.Warn("CT0101", $"`{Name}` defines `{block.Name}:` more than once; the last one wins.", block.Span);
                        blocks[block.Name] = block;
                        break;

                    // Collected here rather than left to `_properties`, which is keyed by name: an
                    // enemy may declare several phases and a dictionary would keep only the last.
                    case PropertyNode property when property.Name == "phase":
                    {
                        PhaseDefinition? phase = ReadPhase(property, diagnostics);
                        if (phase != null) phases.Add(phase);
                        break;
                    }

                    case PropertyNode property when property.Name == "tags" || property.Name == "tag":
                        foreach (ExprNode value in property.Values) tags.AddRange(ReadWords(value));
                        break;

                    case PropertyNode property:
                        _properties[property.Name] = property;
                        if (property.Values.Count == 1 && property.Values[0] is NumberExpr number && !IsConfigProperty(property.Name))
                            _stats[property.Name] = number.Value;
                        else if (property.Values.Count == 1 && property.Values[0] is UnaryExpr { Operator: UnaryOperator.Negate, Operand: NumberExpr negative } && !IsConfigProperty(property.Name))
                            _stats[property.Name] = -negative.Value;
                        break;
                }
            }

            Tags = tags.Select(t => t.ToLowerInvariant()).Distinct().ToArray();
            Listeners = listeners;
            Modifiers = modifiers;
            Blocks = blocks;
            Moves = moves;
            Phases = phases;

            // An enemy's hp doubles as its max_hp unless both are given.
            if (_stats.ContainsKey("hp") && !_stats.ContainsKey("max_hp")) _stats["max_hp"] = _stats["hp"];

            ReadCost(diagnostics);
            ReadStatusConfig(diagnostics);
            ReadPattern(diagnostics);
            ValidateMovePhases(diagnostics);
        }

        public EntityDeclNode Syntax { get; }
        public string Name { get; }

        /// <summary>The keyword it was declared with: <c>card</c>, <c>status</c>, <c>relic</c>...</summary>
        public string KindName { get; }

        public EntityKind Kind { get; }
        public IReadOnlyList<string> Tags { get; }
        public IReadOnlyList<ListenerNode> Listeners { get; }
        public IReadOnlyList<ModifyNode> Modifiers { get; }
        public IReadOnlyDictionary<string, BlockMemberNode> Blocks { get; }
        public IReadOnlyList<MoveDefinition> Moves { get; }
        public IReadOnlyDictionary<string, PropertyNode> Properties => _properties;

        /// <summary>Numeric properties that become base stats on instantiation (<c>cost</c>, <c>hp</c>...).</summary>
        public IReadOnlyDictionary<string, Num> Stats => _stats;

        public BlockNode? Effect => Blocks.TryGetValue("effect", out BlockMemberNode? block) ? block.Body : null;

        // Status configuration ------------------------------------------------------------

        public StackingMode Stacking { get; private set; } = StackingMode.Intensity;
        public int? MaxStacks { get; private set; }
        public StatusFlags Flags { get; private set; }

        /// <summary>How many stacks are lost when <see cref="DecayOn"/> fires. Zero means no decay.</summary>
        public Num DecayAmount { get; private set; }

        /// <summary>The event that triggers decay, typically <c>turn_end</c>.</summary>
        public string? DecayOn { get; private set; }

        /// <summary>
        /// The resource a card's cost is paid in. <c>energy</c> unless the cost names another, as in
        /// <c>cost 2 bones</c>.
        /// </summary>
        public string CostResource { get; private set; } = "energy";

        // Enemy configuration -------------------------------------------------------------

        public EnemyPatternKind Pattern { get; private set; } = EnemyPatternKind.Cycle;

        /// <summary>Move names in pattern order. Empty means "all moves, in declaration order".</summary>
        public IReadOnlyList<string> PatternMoves { get; private set; } = new string[0];

        /// <summary>
        /// Behaviour phases, in declaration order. Empty means the enemy has one behaviour and every
        /// move is always available.
        /// </summary>
        public IReadOnlyList<PhaseDefinition> Phases { get; private set; } = new PhaseDefinition[0];

        // Presentation --------------------------------------------------------------------

        /// <summary>Custom rules text with live <c>{placeholders}</c> (description level 2).</summary>
        public string? Text => ReadString("text");

        /// <summary>Plain text with no live values (description level 3).</summary>
        public string? TextOverride => ReadString("text_override");

        public string? Flavour => ReadString("flavour") ?? ReadString("flavor");

        public bool HasTag(string tag) => Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);

        public PropertyNode? Property(string name) => _properties.TryGetValue(name, out PropertyNode? node) ? node : null;

        /// <summary>First word of a property, for enum-like settings such as <c>target enemy</c>.</summary>
        public string? Word(string property)
        {
            PropertyNode? node = Property(property);
            if (node == null || node.Values.Count == 0) return null;
            return ReadWords(node.Values[0]).FirstOrDefault()?.ToLowerInvariant();
        }

        public string? ReadString(string property)
        {
            PropertyNode? node = Property(property);
            if (node == null) return null;
            return node.Values.Count > 0 && node.Values[0] is StringExpr text ? text.Value : null;
        }

        public override string ToString() => $"{KindName} \"{Name}\"";

        private static EntityKind ParseKind(string kind) => kind switch
        {
            "card" => EntityKind.Card,
            "status" => EntityKind.Status,
            "relic" => EntityKind.Relic,
            "ability" => EntityKind.Ability,
            "keyword" => EntityKind.Keyword,
            "item" => EntityKind.Item,
            "enemy" => EntityKind.Actor,
            "actor" => EntityKind.Actor,
            _ => EntityKind.Global,
        };

        /// <summary>Properties that configure behaviour and must not be mistaken for stats.</summary>
        private static bool IsConfigProperty(string name) => name switch
        {
            "max_stacks" => true,
            "decay" => true,
            "priority" => true,
            "weight" => true,
            _ => false,
        };

        /// <summary>Reads bare identifiers out of an expression, flattening <c>a, b</c> juxtapositions.</summary>
        internal static IEnumerable<string> ReadWords(ExprNode value)
        {
            switch (value)
            {
                case NameExpr name:
                    yield return name.Name;
                    break;
                case StringExpr text:
                    yield return text.Value;
                    break;
                case QualifiedExpr qualified:
                    yield return qualified.Name;
                    break;
            }
        }

        private MoveDefinition ReadMove(BlockMemberNode block, DiagnosticBag diagnostics)
        {
            string name = "<unnamed move>";
            Num weight = Num.One;
            string? phase = null;

            if (block.Arguments.Count > 0)
            {
                string? word = ReadWords(block.Arguments[0]).FirstOrDefault();
                if (word != null) name = word;
            }
            else
            {
                diagnostics.Error("CT0102", $"`move` in `{Name}` needs a name, as in `move \"Chomp\":`.", block.Span);
            }

            // Optional header pairs: `move "Chomp" weight 2:` for random patterns, and
            // `move "Split" phase Broken:` to limit a move to one phase.
            for (int i = 1; i + 1 < block.Arguments.Count; i++)
            {
                if (block.Arguments[i] is NameExpr { Name: "weight" } && block.Arguments[i + 1] is NumberExpr w) weight = w.Value;
                else if (block.Arguments[i] is NameExpr { Name: "phase" }) phase = ReadWords(block.Arguments[i + 1]).FirstOrDefault();
            }

            return new MoveDefinition(name, block.Body, weight, phase);
        }

        private void ReadStatusConfig(DiagnosticBag diagnostics)
        {
            string? stacking = Word("stacking");
            if (stacking != null)
            {
                switch (stacking)
                {
                    case "intensity": Stacking = StackingMode.Intensity; break;
                    case "duration": Stacking = StackingMode.Duration; break;
                    case "both": Stacking = StackingMode.Both; break;
                    case "none": Stacking = StackingMode.None; break;
                    case "refresh": Stacking = StackingMode.Refresh; break;
                    case "separate": Stacking = StackingMode.Separate; break;
                    default:
                        diagnostics.Error(
                            "CT0103",
                            $"Unknown stacking mode `{stacking}`.",
                            Property("stacking")!.Span,
                            Suggest.Closest(stacking, new[] { "intensity", "duration", "both", "none", "refresh", "separate" }));
                        break;
                }
            }

            PropertyNode? max = Property("max_stacks");
            if (max?.First is NumberExpr maxValue) MaxStacks = maxValue.Value.ToInt();

            // `decay 1 on turn_end` parses as `1 on turn_end`; `decay 1` alone decays at turn end.
            PropertyNode? decay = Property("decay");
            if (decay?.First != null)
            {
                switch (decay.First)
                {
                    case NumberExpr amount:
                        DecayAmount = amount.Value;
                        DecayOn = "turn_end";
                        break;
                    case BinaryExpr { Operator: BinaryOperator.On, Left: NumberExpr amount, Right: NameExpr trigger }:
                        DecayAmount = amount.Value;
                        DecayOn = trigger.Name.ToLowerInvariant();
                        break;
                    default:
                        diagnostics.Error("CT0104", "`decay` expects a number, optionally followed by `on <event>`.", decay.Span);
                        break;
                }
            }
            else if (Stacking == StackingMode.Duration || Stacking == StackingMode.Refresh)
            {
                // Duration-stacked statuses tick down by default; that is what the mode means.
                DecayAmount = Num.One;
                DecayOn = "turn_end";
            }

            StatusFlags flags = StatusFlags.None;
            PropertyNode? flagList = Property("flags");
            if (flagList != null)
            {
                foreach (ExprNode value in flagList.Values)
                {
                    foreach (string word in ReadWords(value))
                    {
                        switch (word.ToLowerInvariant())
                        {
                            case "buff": flags |= StatusFlags.Buff; break;
                            case "debuff": flags |= StatusFlags.Debuff; break;
                            case "dispellable": flags |= StatusFlags.Dispellable; break;
                            case "persistent": flags |= StatusFlags.Persistent; break;
                            case "hidden": flags |= StatusFlags.Hidden; break;
                            case "unique": flags |= StatusFlags.UniquePerSource; break;
                            default:
                                diagnostics.Warn("CT0105", $"Unknown status flag `{word}`.", flagList.Span,
                                    Suggest.Closest(word, new[] { "buff", "debuff", "dispellable", "persistent", "hidden", "unique" }));
                                break;
                        }
                    }
                }
            }

            // Tags double as flags so `tags debuff, dot` works without a separate `flags` line.
            if (HasTag("buff")) flags |= StatusFlags.Buff;
            if (HasTag("debuff")) flags |= StatusFlags.Debuff;
            Flags = flags;
        }

        /// <summary>
        /// <c>phase Broken when hp &lt;= max_hp / 2</c>. The name, the word `when`, and a condition
        /// arrive as three values, because a property's values are whitespace-separated expressions.
        /// </summary>
        private PhaseDefinition? ReadPhase(PropertyNode property, DiagnosticBag diagnostics)
        {
            string? name = property.Values.Count > 0 ? ReadWords(property.Values[0]).FirstOrDefault() : null;
            bool when = property.Values.Count > 1 && property.Values[1] is NameExpr { Name: "when" };

            if (name == null || !when || property.Values.Count < 3)
            {
                diagnostics.Error("CT0107",
                    $"`phase` in `{Name}` needs a name and a condition, as in `phase Broken when hp <= max_hp / 2`.",
                    property.Span);
                return null;
            }

            // `phase Broken when hp <= max_hp / 2, retelegraph` — a trailing word, so the condition
            // is still whatever sits in the third value.
            bool retelegraph = false;
            for (int i = 3; i < property.Values.Count; i++)
            {
                if (property.Values[i] is NameExpr { Name: "retelegraph" }) retelegraph = true;
            }

            return new PhaseDefinition(name, property.Values[2], property.Span, retelegraph);
        }

        /// <summary>
        /// A move naming a phase the enemy never declares would simply never be chosen, which is the
        /// kind of silence worth a diagnostic.
        /// </summary>
        private void ValidateMovePhases(DiagnosticBag diagnostics)
        {
            foreach (MoveDefinition move in Moves)
            {
                if (move.Phase == null) continue;
                if (Phases.Any(p => string.Equals(p.Name, move.Phase, StringComparison.OrdinalIgnoreCase))) continue;

                diagnostics.Error("CT0108", $"Move `{move.Name}` of `{Name}` names unknown phase `{move.Phase}`.",
                    Syntax.Span, Suggest.Closest(move.Phase, Phases.Select(p => p.Name)));
            }
        }

        /// <summary>
        /// <c>cost 2 bones</c>: an amount, and the resource it is paid in.
        /// </summary>
        /// <remarks>
        /// A property with more than one value never becomes a stat on its own, so the amount has to
        /// be registered here by hand. Without that, the cost would silently read as zero.
        /// </remarks>
        private void ReadCost(DiagnosticBag diagnostics)
        {
            PropertyNode? cost = Property("cost");
            if (cost == null || cost.Values.Count < 2) return;

            string? resource = cost.Values.Count == 2 ? ReadWords(cost.Values[1]).FirstOrDefault() : null;
            if (resource == null)
            {
                diagnostics.Error("CT0109",
                    $"`cost` in `{Name}` takes an amount and, at most, the resource to pay it in, as in `cost 2 bones`.",
                    cost.Span);
                return;
            }

            CostResource = resource.ToLowerInvariant();

            // `cost x bones` leaves the amount unregistered on purpose: it is an X cost, and
            // CostOf reads the whole of the resource instead.
            if (cost.Values[0] is NumberExpr amount) _stats["cost"] = amount.Value;
        }

        private void ReadPattern(DiagnosticBag diagnostics)
        {
            PropertyNode? pattern = Property("pattern");
            if (pattern == null || pattern.Values.Count == 0) return;

            var words = pattern.Values.SelectMany(ReadWords).ToList();
            if (words.Count == 0) return;

            switch (words[0].ToLowerInvariant())
            {
                case "cycle": Pattern = EnemyPatternKind.Cycle; words.RemoveAt(0); break;
                case "random": Pattern = EnemyPatternKind.Random; words.RemoveAt(0); break;
                case "random_no_repeat": Pattern = EnemyPatternKind.RandomNoRepeat; words.RemoveAt(0); break;
            }

            foreach (string move in words)
            {
                if (!Moves.Any(m => string.Equals(m.Name, move, StringComparison.OrdinalIgnoreCase)))
                {
                    diagnostics.Error("CT0106", $"Pattern of `{Name}` names unknown move `{move}`.", pattern.Span,
                        Suggest.Closest(move, Moves.Select(m => m.Name)));
                }
            }

            PatternMoves = words;
        }
    }

    /// <summary>A content-defined verb: <c>verb shatter(t): ...</c>.</summary>
    public sealed class VerbDefinition
    {
        internal VerbDefinition(VerbDeclNode syntax)
        {
            Syntax = syntax;
        }

        public VerbDeclNode Syntax { get; }
        public string Name => Syntax.Name;
        public IReadOnlyList<string> Parameters => Syntax.Parameters;
        public BlockNode Body => Syntax.Body;
    }

    /// <summary>
    /// Bounds and reset behaviour for a stat treated as a resource. Resources are data, declared
    /// with <c>resource "energy"</c>; common ones are built in.
    /// </summary>
    public sealed class ResourceRule
    {
        public ResourceRule(string stat) => Stat = stat;

        public string Stat { get; }

        /// <summary>Lower bound, or null for none.</summary>
        public ExprNode? Min { get; set; }

        /// <summary>Upper bound, or null for none. May name another stat, as in <c>max max_hp</c>.</summary>
        public ExprNode? Max { get; set; }

        /// <summary>Value restored when <see cref="ResetOn"/> fires.</summary>
        public ExprNode? ResetTo { get; set; }

        /// <summary>Event that resets the resource for its owner, such as <c>turn_start</c>.</summary>
        public string? ResetOn { get; set; }

        internal static ResourceRule FromDefinition(EntityDefinition definition)
        {
            var rule = new ResourceRule(definition.Name.ToLowerInvariant())
            {
                Min = definition.Property("min")?.First,
                Max = definition.Property("max")?.First,
                ResetTo = definition.Property("reset_to")?.First,
                ResetOn = definition.Word("reset_on"),
            };
            return rule;
        }
    }
}
