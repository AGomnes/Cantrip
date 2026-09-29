using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cantrip.Diagnostics;
using Cantrip.Syntax;

namespace Cantrip.Content
{
    /// <summary>Status flags: what a status is (buff, debuff) and how it behaves (persistent, unique).</summary>
    [Flags]
    public enum StatusFlags
    {
        /// <summary>No flags. What a status has unless its declaration says otherwise.</summary>
        None = 0,

        /// <summary>Good for whoever has it. Read by <c>dispel</c>-style effects and by a UI choosing a colour.</summary>
        Buff = 1 << 0,

        /// <summary>Bad for whoever has it. A status may be neither, and nothing sets one from the other automatically.</summary>
        Debuff = 1 << 1,

        /// <summary>May be removed by an effect that strips statuses. Without it, a status stays through a cleanse.</summary>
        Dispellable = 1 << 2,

        /// <summary>Survives the end of a battle instead of being cleared with everything else.</summary>
        Persistent = 1 << 3,

        /// <summary>Not to be shown to the player. The rules treat it like any other status; this is a note for the UI.</summary>
        Hidden = 1 << 4,

        /// <summary>
        /// One instance per applying source rather than one per holder, so two enemies each keep their
        /// own copy on the same target.
        /// </summary>
        UniquePerSource = 1 << 5,
    }

    /// <summary>
    /// How an enemy picks its next move. It is rolled once per turn, when the intent is telegraphed,
    /// not when the move runs.
    /// </summary>
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
        /// <summary>
        /// A phase built in C#, for a test or a tool. Content declares them with
        /// <c>phase Broken when ...</c>.
        /// </summary>
        /// <param name="name">What moves tag themselves with to belong to this phase.</param>
        /// <param name="condition">Evaluated against the enemy each time its intent is rolled.</param>
        /// <param name="span">Where it was written, for diagnostics.</param>
        /// <param name="retelegraph">Whether entering the phase re-rolls the intent there and then.</param>
        public PhaseDefinition(string name, ExprNode condition, SourceSpan span, bool retelegraph = false)
        {
            Name = name;
            Condition = condition;
            Span = span;
            Retelegraph = retelegraph;
        }

        /// <summary>The phase's name, which is what a <c>move</c> tags itself with to belong to it.</summary>
        public string Name { get; }

        /// <summary>Evaluated against the enemy each time its intent is rolled.</summary>
        public ExprNode Condition { get; }

        /// <summary>Where the <c>phase</c> line was written, so a diagnostic about it can point somewhere.</summary>
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

        /// <summary>The phase's name.</summary>
        public override string ToString() => Name;
    }

    /// <summary>
    /// How far an action reaches, in slots on the board: <c>range 1</c> is everything up to one
    /// step away — melee — and <c>range 2..3</c> is a bow that cannot shoot point blank.
    /// </summary>
    /// <remarks>
    /// A reach is measured with <see cref="Runtime.GameState.Distance"/> between whoever is using
    /// the action and the candidate, and it is the only thing the <c>range</c> modifier channel
    /// changes: the channel computes <see cref="Max"/> and <see cref="Min"/> follows it down, so
    /// <c>modify range: set 1</c> makes a longbow melee rather than leaving it unable to reach
    /// anything at all.
    /// </remarks>
    public readonly struct Reach : IEquatable<Reach>
    {
        /// <summary>
        /// A reach from two step counts. A negative <paramref name="min"/> is clamped to 0 rather than
        /// refused, so a <c>range</c> modifier that drives the near end below zero still names a reach.
        /// </summary>
        /// <param name="min">The nearest slot reached. 0 means "from where I stand outwards".</param>
        /// <param name="max">The furthest slot reached.</param>
        public Reach(int min, int max)
        {
            Min = min < 0 ? 0 : min;
            Max = max;
        }

        /// <summary>The nearest slot this reaches. Zero unless content wrote <c>range 2..3</c>.</summary>
        public int Min { get; }

        /// <summary>The furthest slot this reaches.</summary>
        public int Max { get; }

        /// <summary>Whether something that many steps away is within this reach.</summary>
        public bool Reaches(int distance) => distance >= Min && distance <= Max;

        /// <summary>Both ends, exactly.</summary>
        public bool Equals(Reach other) => Min == other.Min && Max == other.Max;

        /// <summary>The boxing form of <see cref="Equals(Reach)"/>.</summary>
        public override bool Equals(object? obj) => obj is Reach other && Equals(other);

        /// <summary>Hashes both ends.</summary>
        public override int GetHashCode() => (Min * 397) ^ Max;

        /// <summary>The form content writes: <c>1</c> for a reach that starts where the user stands, <c>2..3</c> otherwise.</summary>
        public override string ToString() => Min == 0 ? Max.ToString(CultureInfo.InvariantCulture) : $"{Min}..{Max}";
    }

    /// <summary>A named enemy move: <c>move "Chomp": deal 11 to player</c>.</summary>
    public sealed class MoveDefinition
    {
        /// <summary>
        /// A move built in C#, for a test or a tool. Content declares them with
        /// <c>move "Chomp": ...</c>.
        /// </summary>
        /// <param name="name">What the intent panel shows and what <c>use</c> names.</param>
        /// <param name="body">The statements the move runs.</param>
        /// <param name="weight">Its share of a <c>random</c> pattern. Ignored by a <c>cycle</c> pattern.</param>
        /// <param name="phase">The phase it belongs to, or null for a move available in every phase.</param>
        public MoveDefinition(string name, BlockNode body, Num weight, string? phase = null)
        {
            Name = name;
            Body = body;
            Weight = weight;
            Phase = phase;
        }

        /// <summary>The move's name, as the intent panel shows it and as <c>use "Chomp"</c> names it.</summary>
        public string Name { get; }

        /// <summary>The statements the move runs. They are run in the enemy's turn, not when the intent is rolled.</summary>
        public BlockNode Body { get; }

        /// <summary>
        /// This move's share of a <c>random</c> pattern, relative to the other moves'. A <c>cycle</c>
        /// pattern takes every move in order and ignores it.
        /// </summary>
        public Num Weight { get; }

        /// <summary>The phase this move belongs to, or null when it is available in every phase.</summary>
        public string? Phase { get; }

        /// <summary>
        /// How far this move reaches, from <c>move "Swing" range 1:</c>, or null when it says
        /// nothing and reaches as far as the board is wide.
        /// </summary>
        public Reach? Range { get; internal set; }

        /// <summary>
        /// Who the move telegraphs against, from <c>move "Cutthroat" at lowest hp enemies:</c>.
        /// Null when the move says nothing, and the target is then a living party member drawn
        /// uniformly — which for a party of one is that one member, with nothing rolled.
        /// </summary>
        public ExprNode? TargetSelector { get; internal set; }
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
            Abilities = ReadWordList("abilities");

            // An enemy's hp doubles as its max_hp unless both are given.
            if (_stats.ContainsKey("hp") && !_stats.ContainsKey("max_hp")) _stats["max_hp"] = _stats["hp"];

            if (Property("range") is PropertyNode reach)
            {
                if (reach.Values.Count == 1) Range = ReadReach(reach.Values[0], ToString(), reach.Span, diagnostics);
                else BadReach(ToString(), reach.Span, diagnostics);
            }

            ReadCost(diagnostics);
            ReadStatusConfig(diagnostics);
            ReadPattern(diagnostics);
            ValidateMovePhases(diagnostics);
        }

        /// <summary>
        /// The declaration this was built from, for a tool that needs the text, the spans or a member
        /// this class does not surface.
        /// </summary>
        public EntityDeclNode Syntax { get; }

        /// <summary>The name in the declaration, as content wrote it. Two definitions of different kinds may share one.</summary>
        public string Name { get; }

        /// <summary>The keyword it was declared with: <c>card</c>, <c>status</c>, <c>relic</c>...</summary>
        public string KindName { get; }

        /// <summary>
        /// True for a <c>hero</c>: an actor on the player's side that the game asks for input.
        /// Every other actor, summoned or declared, is not one.
        /// </summary>
        public bool IsHero => string.Equals(KindName, "hero", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The abilities a <c>hero</c> is granted when it is created, from its <c>abilities</c>
        /// line. Empty for everything else.
        /// </summary>
        public IReadOnlyList<string> Abilities { get; } = new string[0];

        /// <summary>
        /// Whether this declares something the game can make one of, rather than a rule about the
        /// game. <c>resource</c> names a stat and <c>board</c> names a shape: nothing is instantiated
        /// from either, neither has rules text, and no card can name one. So both are left out
        /// wherever definitions are listed, suggested or described.
        /// </summary>
        public bool IsThing => KindName != "resource" && KindName != "board";

        /// <summary>
        /// What the engine treats this as. Several keywords land on one kind — <c>enemy</c>,
        /// <c>actor</c> and <c>hero</c> are all <see cref="EntityKind.Actor"/> — so
        /// <see cref="KindName"/> is what tells them apart.
        /// </summary>
        public EntityKind Kind { get; }

        /// <summary>
        /// The words on the <c>tags</c> line, lower-cased and deduplicated. A tag written on a line of
        /// its own is not here: it is a property nothing reads, which is CT316.
        /// </summary>
        public IReadOnlyList<string> Tags { get; }

        /// <summary>
        /// Every <c>on ...:</c> block, in declaration order. A line that only looks like a listener —
        /// <c>when card_played:</c> — is in <see cref="Blocks"/> instead and never runs, which is CT313.
        /// </summary>
        public IReadOnlyList<ListenerNode> Listeners { get; }

        /// <summary>Every <c>modify</c> line, in declaration order. They are live while the entity is, and the pipeline decides their order, not this list.</summary>
        public IReadOnlyList<ModifyNode> Modifiers { get; }

        /// <summary>
        /// Labelled blocks by name, case-insensitively. The engine runs only <c>effect</c> and the
        /// <c>move</c>s; anything else here is a block the game runs itself, or a typo that never runs.
        /// A game that does run one names it in <c>LintOptions.HostBlocks</c> so the linter stops
        /// reporting it.
        /// </summary>
        public IReadOnlyDictionary<string, BlockMemberNode> Blocks { get; }

        /// <summary>Every <c>move</c>, in declaration order — which is also the order a <c>cycle</c> pattern takes them in when no <c>pattern</c> line names them.</summary>
        public IReadOnlyList<MoveDefinition> Moves { get; }

        /// <summary>
        /// Every property line as it was parsed, by name. It holds the configuration lines as well as the
        /// numbers, so <see cref="Stats"/> is the narrower question and usually the one meant.
        /// </summary>
        public IReadOnlyDictionary<string, PropertyNode> Properties => _properties;

        /// <summary>Numeric properties that become base stats on instantiation (<c>cost</c>, <c>hp</c>...).</summary>
        public IReadOnlyDictionary<string, Num> Stats => _stats;

        /// <summary>
        /// The <c>effect:</c> block, or null when the definition has none. A card with no effect is
        /// legal: its whole behaviour may be its listeners, its modifiers or a tag.
        /// </summary>
        public BlockNode? Effect => Blocks.TryGetValue("effect", out BlockMemberNode? block) ? block.Body : null;

        /// <summary>
        /// How far this reaches, from its <c>range</c> line, or null when it says nothing and
        /// reaches as far as the board is wide. Read by targeting through the <c>range</c> modifier
        /// channel; a card's own <c>target … where</c> filter is a separate rule and is not a
        /// channel, because a card's printed reach is its own and not a stranger's to rewrite.
        /// </summary>
        public Reach? Range { get; private set; }

        // Status configuration ------------------------------------------------------------

        /// <summary>
        /// How a second application combines with the first. The default is
        /// <see cref="StackingMode.Intensity"/>, which has no timer at all, so a status meant to run out
        /// has to say so.
        /// </summary>
        public StackingMode Stacking { get; private set; } = StackingMode.Intensity;

        /// <summary>
        /// The cap on <c>stacks</c>, or null for none. It is the <c>max_stacks</c> line: misspell it and
        /// the status silently has no cap, since any other property is just a stat.
        /// </summary>
        public int? MaxStacks { get; private set; }

        /// <summary>What kind of status this is and how it behaves, from the words on its declaration. <see cref="StatusFlags.None"/> for everything that is not a status.</summary>
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

        /// <summary>
        /// How this enemy picks its next move. The default takes the moves in order, which is what makes
        /// an enemy readable; only a <c>pattern</c> line changes it.
        /// </summary>
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

        /// <summary>
        /// The flavour line, or null. Both spellings are read, <c>flavour</c> first, so content written
        /// either way works and a definition that has both shows the British one.
        /// </summary>
        public string? Flavour => ReadString("flavour") ?? ReadString("flavor");

        /// <summary>
        /// Whether the <c>tags</c> line carries this word, ignoring case. It asks about the definition,
        /// so a tag an effect added to one live entity is <c>Entity.HasTag(string)</c> instead.
        /// </summary>
        public bool HasTag(string tag) => Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// One property line as it was parsed, or null when it was not written. The parsed node is what a
        /// tool wants; <see cref="Stats"/>, <see cref="ReadString"/> and <see cref="Word"/> are the
        /// shortcuts for reading a value out of one.
        /// </summary>
        public PropertyNode? Property(string name) => _properties.TryGetValue(name, out PropertyNode? node) ? node : null;

        /// <summary>
        /// Every word a property lists: <c>abilities Smite, Bulwark</c> gives both. Empty when the
        /// property is not written at all, so a caller never has to check first.
        /// </summary>
        public IReadOnlyList<string> ReadWordList(string property)
        {
            PropertyNode? node = Property(property);
            if (node == null) return new string[0];
            return node.Values.SelectMany(ReadWords).ToArray();
        }

        /// <summary>First word of a property, for enum-like settings such as <c>target enemy</c>.</summary>
        public string? Word(string property)
        {
            PropertyNode? node = Property(property);
            if (node == null || node.Values.Count == 0) return null;
            return ReadWords(node.Values[0]).FirstOrDefault()?.ToLowerInvariant();
        }

        /// <summary>
        /// A property's value when it was written as a quoted string, or null — which also covers a
        /// property that was written without quotes. Use <see cref="Word"/> for a bare word.
        /// </summary>
        public string? ReadString(string property)
        {
            PropertyNode? node = Property(property);
            if (node == null) return null;
            return node.Values.Count > 0 && node.Values[0] is StringExpr text ? text.Value : null;
        }

        /// <summary>The declaration's first line, as content wrote it: <c>card "Strike"</c>. This is what diagnostics name it by.</summary>
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
            "hero" => EntityKind.Actor,
            _ => EntityKind.Global,
        };

        /// <summary>Properties that configure behaviour and must not be mistaken for stats.</summary>
        private static bool IsConfigProperty(string name) => name switch
        {
            "max_stacks" => true,
            "decay" => true,
            "priority" => true,
            "weight" => true,

            // Reach is a rule about an action, not a number on the thing using it. Left out of the
            // stat table so that `range` is read in exactly one place — Interpreter.InReach — and
            // cannot also arrive through the stat pipeline under the same name.
            "range" => true,
            _ => false,
        };

        /// <summary>
        /// Reads a <c>range</c> line: <c>range 1</c> is everything up to one step away and
        /// <c>range 2..3</c> is a span. Anything else is refused by name rather than ignored.
        /// </summary>
        internal static Reach? ReadReach(ExprNode value, string what, SourceSpan span, DiagnosticBag diagnostics)
        {
            switch (value)
            {
                case NumberExpr number when number.Unit == null && number.Value == number.Value.Floor() && !number.Value.IsNegative:
                    return new Reach(0, number.Value.ToInt());

                case RangeExpr range
                    when range.Low is NumberExpr low && low.Unit == null && low.Value == low.Value.Floor() && !low.Value.IsNegative
                      && range.High is NumberExpr high && high.Unit == null && high.Value == high.Value.Floor() && !high.Value.IsNegative:
                    return new Reach(low.Value.ToInt(), high.Value.ToInt());

                default:
                    BadReach(what, span, diagnostics);
                    return null;
            }
        }

        private static void BadReach(string what, SourceSpan span, DiagnosticBag diagnostics) =>
            diagnostics.Error(
                "CT0115",
                $"`range` on {what} is a whole number of slots, as in `range 1`, or a span, as in `range 2..3`. " +
                "A length with a unit, such as `5m`, is a question about the world and belongs to the game rather than to the board.",
                span);

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

            // Optional header pairs: `move "Chomp" weight 2:` for random patterns,
            // `move "Split" phase Broken:` to limit a move to one phase, and `move "Swing" range 1:`
            // for a move that only reaches what is one step away.
            Reach? range = null;
            ExprNode? at = null;
            for (int i = 1; i + 1 < block.Arguments.Count; i++)
            {
                if (block.Arguments[i] is NameExpr { Name: "weight" } && block.Arguments[i + 1] is NumberExpr w) weight = w.Value;
                else if (block.Arguments[i] is NameExpr { Name: "phase" }) phase = ReadWords(block.Arguments[i + 1]).FirstOrDefault();
                else if (block.Arguments[i] is NameExpr { Name: "range" }) range = ReadReach(block.Arguments[i + 1], $"move \"{name}\" in `{Name}`", block.Span, diagnostics);
                else if (block.Arguments[i] is NameExpr { Name: "at" }) at = block.Arguments[i + 1];
            }

            return new MoveDefinition(name, block.Body, weight, phase) { Range = range, TargetSelector = at };
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

        /// <summary>The parsed <c>verb</c> declaration, for a tool that needs the spans.</summary>
        public VerbDeclNode Syntax { get; }

        /// <summary>What content calls this verb. It shadows nothing: a name that is already a built-in is CT301 at every use.</summary>
        public string Name => Syntax.Name;

        /// <summary>The parameter names, in order, as the body reads them. A call with the wrong count is a runtime error, not a load error.</summary>
        public IReadOnlyList<string> Parameters => Syntax.Parameters;

        /// <summary>The statements the verb runs, with its parameters bound as locals.</summary>
        public BlockNode Body => Syntax.Body;
    }

    /// <summary>
    /// Bounds and reset behaviour for a stat treated as a resource. Resources are data, declared
    /// with <c>resource "energy"</c>; common ones are built in.
    /// </summary>
    public sealed class ResourceRule
    {
        /// <summary>
        /// An unbounded rule for a stat: no floor, no ceiling and no reset until one is set. Content
        /// declares these with <c>resource "energy"</c>; a game builds one only to add a resource the
        /// content did not.
        /// </summary>
        public ResourceRule(string stat) => Stat = stat;

        /// <summary>The stat this governs, lower-cased. Every actor's stat of that name is bound by it; a resource is not owned by anybody.</summary>
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
