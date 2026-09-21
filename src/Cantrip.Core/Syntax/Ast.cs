using System.Collections.Generic;
using Cantrip.Diagnostics;

namespace Cantrip.Syntax
{
    /// <summary>Base for every parsed node. The span is what makes causality traces clickable.</summary>
    public abstract class Node
    {
        protected Node(SourceSpan span) => Span = span;

        public SourceSpan Span { get; }
    }

    // ---------------------------------------------------------------------------------------
    // Expressions
    // ---------------------------------------------------------------------------------------

    public abstract class ExprNode : Node
    {
        protected ExprNode(SourceSpan span) : base(span) { }
    }

    /// <summary>A literal number, with the unit suffix it was written with (<c>%</c>, <c>s</c>, ...).</summary>
    public sealed class NumberExpr : ExprNode
    {
        public NumberExpr(Num value, string? unit, SourceSpan span) : base(span)
        {
            Value = value;
            Unit = unit;
        }

        public Num Value { get; }
        public string? Unit { get; }
    }

    public sealed class StringExpr : ExprNode
    {
        public StringExpr(string value, SourceSpan span) : base(span) => Value = value;

        public string Value { get; }
    }

    /// <summary>A bare name: <c>target</c>, <c>stacks</c>, <c>Poison</c>, <c>enemies</c>.</summary>
    public sealed class NameExpr : ExprNode
    {
        public NameExpr(string name, SourceSpan span) : base(span) => Name = name;

        public string Name { get; }
    }

    /// <summary>A <c>tag:fire</c> style reference.</summary>
    public sealed class QualifiedExpr : ExprNode
    {
        public QualifiedExpr(string qualifier, string name, SourceSpan span) : base(span)
        {
            Qualifier = qualifier;
            Name = name;
        }

        public string Qualifier { get; }
        public string Name { get; }
    }

    /// <summary><c>target.hp</c>, <c>event.amount</c>.</summary>
    public sealed class MemberExpr : ExprNode
    {
        public MemberExpr(ExprNode target, string member, SourceSpan span) : base(span)
        {
            Target = target;
            Member = member;
        }

        public ExprNode Target { get; }
        public string Member { get; }
    }

    /// <summary><c>adjacent(target)</c>, <c>min(a, b)</c>, <c>t.has(tag:ice)</c>.</summary>
    public sealed class CallExpr : ExprNode
    {
        public CallExpr(string name, IReadOnlyList<ExprNode> arguments, SourceSpan span, ExprNode? receiver = null)
            : base(span)
        {
            Name = name;
            Arguments = arguments;
            Receiver = receiver;
        }

        public string Name { get; }
        public IReadOnlyList<ExprNode> Arguments { get; }

        /// <summary>Set for method-style calls such as <c>t.has(...)</c>.</summary>
        public ExprNode? Receiver { get; }
    }

    public enum UnaryOperator
    {
        Negate,
        Not,
    }

    public sealed class UnaryExpr : ExprNode
    {
        public UnaryExpr(UnaryOperator op, ExprNode operand, SourceSpan span) : base(span)
        {
            Operator = op;
            Operand = operand;
        }

        public UnaryOperator Operator { get; }
        public ExprNode Operand { get; }
    }

    public enum BinaryOperator
    {
        Add,
        Subtract,
        Multiply,
        Divide,
        Modulo,
        Equal,
        NotEqual,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
        And,
        Or,

        /// <summary><c>2 per Poison on target</c> - multiplies the left side by a count.</summary>
        Per,

        /// <summary><c>Poison on target</c> - reads a stack count or membership.</summary>
        On,

        /// <summary><c>cards in hand</c> - restricts a selector to a zone.</summary>
        In,

        /// <summary><c>x has tag:fire</c>.</summary>
        Has,
    }

    public sealed class BinaryExpr : ExprNode
    {
        public BinaryExpr(BinaryOperator op, ExprNode left, ExprNode right, SourceSpan span) : base(span)
        {
            Operator = op;
            Left = left;
            Right = right;
        }

        public BinaryOperator Operator { get; }
        public ExprNode Left { get; }
        public ExprNode Right { get; }
    }

    /// <summary><c>3..6</c>: rolled when evaluated as a number, kept intact when inspected.</summary>
    public sealed class RangeExpr : ExprNode
    {
        public RangeExpr(ExprNode low, ExprNode high, SourceSpan span) : base(span)
        {
            Low = low;
            High = high;
        }

        public ExprNode Low { get; }
        public ExprNode High { get; }
    }

    /// <summary><c>cards in hand where tag:fire and cost &gt; 1</c>.</summary>
    public sealed class WhereExpr : ExprNode
    {
        public WhereExpr(ExprNode source, ExprNode predicate, SourceSpan span) : base(span)
        {
            Source = source;
            Predicate = predicate;
        }

        public ExprNode Source { get; }
        public ExprNode Predicate { get; }
    }

    /// <summary>Selector prefixes that take a group: <c>all</c>, <c>random N</c>, <c>lowest hp</c>.</summary>
    public enum SelectorModifier
    {
        All,
        Random,
        Lowest,
        Highest,
        Other,
    }

    public sealed class SelectorExpr : ExprNode
    {
        public SelectorExpr(SelectorModifier modifier, ExprNode source, ExprNode? count, string? key, SourceSpan span)
            : base(span)
        {
            Modifier = modifier;
            Source = source;
            Count = count;
            Key = key;
        }

        public SelectorModifier Modifier { get; }
        public ExprNode Source { get; }

        /// <summary>How many to take, for <see cref="SelectorModifier.Random"/> and the sorted picks.</summary>
        public ExprNode? Count { get; }

        /// <summary>Stat name to sort by, for <see cref="SelectorModifier.Lowest"/>/<see cref="SelectorModifier.Highest"/>.</summary>
        public string? Key { get; }
    }

    // ---------------------------------------------------------------------------------------
    // Statements
    // ---------------------------------------------------------------------------------------

    public abstract class StatementNode : Node
    {
        protected StatementNode(SourceSpan span) : base(span) { }
    }

    public sealed class BlockNode : Node
    {
        public BlockNode(IReadOnlyList<StatementNode> statements, SourceSpan span) : base(span) =>
            Statements = statements;

        public IReadOnlyList<StatementNode> Statements { get; }

        public static BlockNode Empty(SourceSpan span) => new BlockNode(new StatementNode[0], span);
    }

    /// <summary>A named clause attached to a command, such as the <c>to</c> in <c>deal 6 to target</c>.</summary>
    public sealed class ClauseNode : Node
    {
        public ClauseNode(string keyword, ExprNode? value, SourceSpan span) : base(span)
        {
            Keyword = keyword;
            Value = value;
        }

        public string Keyword { get; }

        /// <summary>Null for bare flags such as <c>ignore block</c>.</summary>
        public ExprNode? Value { get; }
    }

    /// <summary>
    /// A verb invocation. The parser deliberately does not know what any verb means: it captures
    /// positional arguments and named clauses, and the verb implementation interprets them. That
    /// is what lets content declare new verbs without touching the grammar.
    /// </summary>
    public sealed class CommandNode : StatementNode
    {
        public CommandNode(
            string verb,
            IReadOnlyList<ExprNode> arguments,
            IReadOnlyList<ClauseNode> clauses,
            SourceSpan span)
            : base(span)
        {
            Verb = verb;
            Arguments = arguments;
            Clauses = clauses;
        }

        public string Verb { get; }
        public IReadOnlyList<ExprNode> Arguments { get; }
        public IReadOnlyList<ClauseNode> Clauses { get; }

        public ExprNode? Clause(string keyword)
        {
            for (int i = 0; i < Clauses.Count; i++)
            {
                if (string.Equals(Clauses[i].Keyword, keyword, System.StringComparison.OrdinalIgnoreCase))
                    return Clauses[i].Value;
            }
            return null;
        }

        public bool HasFlag(string keyword)
        {
            for (int i = 0; i < Clauses.Count; i++)
            {
                if (string.Equals(Clauses[i].Keyword, keyword, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    public sealed class IfNode : StatementNode
    {
        public IfNode(ExprNode condition, BlockNode thenBlock, BlockNode? elseBlock, SourceSpan span) : base(span)
        {
            Condition = condition;
            Then = thenBlock;
            Else = elseBlock;
        }

        public ExprNode Condition { get; }
        public BlockNode Then { get; }
        public BlockNode? Else { get; }
    }

    public sealed class LetNode : StatementNode
    {
        public LetNode(string name, ExprNode value, SourceSpan span) : base(span)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }
        public ExprNode Value { get; }
    }

    public sealed class RepeatNode : StatementNode
    {
        public RepeatNode(ExprNode count, BlockNode body, SourceSpan span) : base(span)
        {
            Count = count;
            Body = body;
        }

        public ExprNode Count { get; }
        public BlockNode Body { get; }
    }

    public sealed class ForEachNode : StatementNode
    {
        public ForEachNode(string variable, ExprNode source, BlockNode body, SourceSpan span) : base(span)
        {
            Variable = variable;
            Source = source;
            Body = body;
        }

        public string Variable { get; }
        public ExprNode Source { get; }
        public BlockNode Body { get; }
    }

    public sealed class ChanceNode : StatementNode
    {
        public ChanceNode(ExprNode probability, BlockNode body, BlockNode? elseBody, SourceSpan span) : base(span)
        {
            Probability = probability;
            Body = body;
            Else = elseBody;
        }

        public ExprNode Probability { get; }
        public BlockNode Body { get; }
        public BlockNode? Else { get; }
    }

    public enum ScheduleKind
    {
        /// <summary><c>next turn:</c></summary>
        NextTurn,

        /// <summary><c>in 2 turns:</c> / <c>in 3s:</c></summary>
        After,

        /// <summary><c>until turn_end:</c> - runs now, undone automatically at the deadline.</summary>
        Until,
    }

    public sealed class ScheduleNode : StatementNode
    {
        public ScheduleNode(ScheduleKind kind, ExprNode? delay, string? deadline, BlockNode body, SourceSpan span)
            : base(span)
        {
            Kind = kind;
            Delay = delay;
            Deadline = deadline;
            Body = body;
        }

        public ScheduleKind Kind { get; }
        public ExprNode? Delay { get; }

        /// <summary>Event name that ends an <see cref="ScheduleKind.Until"/> window.</summary>
        public string? Deadline { get; }

        public BlockNode Body { get; }
    }

    /// <summary>A labelled group of statements such as <c>setup:</c>. Runs its body; the label is for tools.</summary>
    public sealed class LabeledBlockNode : StatementNode
    {
        public LabeledBlockNode(string label, BlockNode body, SourceSpan span) : base(span)
        {
            Label = label;
            Body = body;
        }

        public string Label { get; }
        public BlockNode Body { get; }
    }

    public enum AssignOperator
    {
        Set,
        Add,
        Subtract,
        Multiply,
    }

    /// <summary><c>stacks -1</c>, <c>hp = 10</c>, <c>energy += 1</c>.</summary>
    public sealed class AssignNode : StatementNode
    {
        public AssignNode(ExprNode target, AssignOperator op, ExprNode value, SourceSpan span) : base(span)
        {
            Target = target;
            Operator = op;
            Value = value;
        }

        public ExprNode Target { get; }
        public AssignOperator Operator { get; }
        public ExprNode Value { get; }
    }

    // ---------------------------------------------------------------------------------------
    // Declarations
    // ---------------------------------------------------------------------------------------

    public abstract class DeclarationNode : Node
    {
        protected DeclarationNode(string name, SourceSpan span) : base(span) => Name = name;

        public string Name { get; }
    }

    public abstract class MemberNode : Node
    {
        protected MemberNode(SourceSpan span) : base(span) { }
    }

    /// <summary>A simple <c>key value[, value]</c> line such as <c>cost 2</c> or <c>tags dot, poison</c>.</summary>
    public sealed class PropertyNode : MemberNode
    {
        public PropertyNode(string name, IReadOnlyList<ExprNode> values, SourceSpan span) : base(span)
        {
            Name = name;
            Values = values;
        }

        public string Name { get; }
        public IReadOnlyList<ExprNode> Values { get; }

        public ExprNode? First => Values.Count > 0 ? Values[0] : null;
    }

    /// <summary>
    /// A named block member such as <c>effect:</c>, optionally with header arguments as in
    /// <c>move "Chomp":</c>.
    /// </summary>
    public sealed class BlockMemberNode : MemberNode
    {
        public BlockMemberNode(string name, IReadOnlyList<ExprNode> arguments, BlockNode body, SourceSpan span) : base(span)
        {
            Name = name;
            Arguments = arguments;
            Body = body;
        }

        public string Name { get; }

        /// <summary>Values written between the member name and its colon. Usually empty.</summary>
        public IReadOnlyList<ExprNode> Arguments { get; }

        public BlockNode Body { get; }
    }

    public enum LimitScope
    {
        None,
        Turn,
        Battle,
        Run,
        Chain,
    }

    /// <summary><c>on &lt;event&gt;(&lt;filter&gt;):</c></summary>
    public sealed class ListenerNode : MemberNode
    {
        public ListenerNode(
            string eventName,
            EventPhase phase,
            ExprNode? filter,
            BlockNode body,
            LimitScope limit,
            int priority,
            SourceSpan span,
            Num interval = default,
            string? intervalUnit = null)
            : base(span)
        {
            EventName = eventName;
            Phase = phase;
            Filter = filter;
            Body = body;
            Limit = limit;
            Priority = priority;
            Interval = interval;
            IntervalUnit = intervalUnit;
        }

        /// <summary>Normalised event name with any <c>before_</c>/<c>after_</c> prefix stripped.</summary>
        public string EventName { get; }

        public EventPhase Phase { get; }
        public ExprNode? Filter { get; }
        public BlockNode Body { get; }
        public LimitScope Limit { get; }
        public int Priority { get; }

        /// <summary>How often <c>on every ...:</c> fires. Zero for an ordinary event listener.</summary>
        public Num Interval { get; }

        /// <summary>The unit written against the interval: <c>s</c>, <c>ms</c>, <c>turns</c>.</summary>
        public string? IntervalUnit { get; }
    }

    /// <summary><c>modify damage where tag:fire, source:self: x1.5</c>.</summary>
    public sealed class ModifyNode : MemberNode
    {
        public ModifyNode(
            string channel,
            ExprNode? scope,
            ExprNode? filter,
            ModifierLayer layer,
            ExprNode amount,
            SourceSpan span)
            : base(span)
        {
            Channel = channel;
            Scope = scope;
            Filter = filter;
            Layer = layer;
            Amount = amount;
        }

        /// <summary>What is being modified: a stat name, or a pseudo-channel like <c>damage</c> or <c>cost</c>.</summary>
        public string Channel { get; }

        /// <summary>
        /// Explicit subjects from <c>of &lt;selector&gt;</c>. When absent, the modifier applies to
        /// whatever it is attached to (or its holder), which is almost always what an author means.
        /// </summary>
        public ExprNode? Scope { get; }

        /// <summary>Extra conditions from <c>where ...</c>, always combined with the scope.</summary>
        public ExprNode? Filter { get; }
        public ModifierLayer Layer { get; }
        public ExprNode Amount { get; }
    }

    /// <summary>Any of <c>card</c>, <c>status</c>, <c>relic</c>, <c>ability</c>, <c>enemy</c>, <c>keyword</c>, <c>item</c>.</summary>
    public sealed class EntityDeclNode : DeclarationNode
    {
        public EntityDeclNode(string kind, string name, IReadOnlyList<MemberNode> members, SourceSpan span)
            : base(name, span)
        {
            Kind = kind;
            Members = members;
        }

        public string Kind { get; }
        public IReadOnlyList<MemberNode> Members { get; }
    }

    /// <summary><c>verb shatter(t): ...</c></summary>
    public sealed class VerbDeclNode : DeclarationNode
    {
        public VerbDeclNode(string name, IReadOnlyList<string> parameters, BlockNode body, SourceSpan span)
            : base(name, span)
        {
            Parameters = parameters;
            Body = body;
        }

        public IReadOnlyList<string> Parameters { get; }
        public BlockNode Body { get; }
    }

    /// <summary>The <c>ruleset</c> block: rules that content is written against.</summary>
    public sealed class RulesetDeclNode : DeclarationNode
    {
        public RulesetDeclNode(IReadOnlyList<PropertyNode> settings, SourceSpan span)
            : base("ruleset", span) => Settings = settings;

        public IReadOnlyList<PropertyNode> Settings { get; }
    }

    /// <summary>A <c>test</c> block, executed by the DSL test runner.</summary>
    public sealed class TestDeclNode : DeclarationNode
    {
        public TestDeclNode(string name, BlockNode body, SourceSpan span) : base(name, span) => Body = body;

        public BlockNode Body { get; }
    }

    /// <summary>Everything parsed out of a single source file.</summary>
    public sealed class SourceFileNode : Node
    {
        public SourceFileNode(string path, IReadOnlyList<DeclarationNode> declarations, SourceSpan span) : base(span)
        {
            Path = path;
            Declarations = declarations;
        }

        public string Path { get; }
        public IReadOnlyList<DeclarationNode> Declarations { get; }
    }
}
