using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Cantrip.Syntax;

namespace Cantrip.Content
{
    /// <summary>
    /// Stable names for every block of loaded content, such as <c>card:Prepare/effect/0.body</c>.
    /// Snapshots store scheduled work by address instead of by object reference, so a save file
    /// stays valid across process restarts. An address is a position within its definition, so a
    /// save also records a <see cref="BlockHash"/> of the block's statements, and a restore that
    /// finds other statements there looks for the same ones elsewhere in that definition.
    /// </summary>
    internal sealed class BlockAddressBook
    {
        /// <summary>The root of the addresses within statements that <c>CardRuntime.Execute</c> ran.</summary>
        public const string ExecuteRoot = "execute";

        private readonly Dictionary<BlockNode, string> _addresses = new Dictionary<BlockNode, string>();
        private readonly Dictionary<string, BlockNode> _blocks = new Dictionary<string, BlockNode>(StringComparer.Ordinal);
        private readonly HashSet<string> _roots = new HashSet<string>(StringComparer.Ordinal);

        // The bodies of `next turn:` and `in N turns:`: the only blocks that are ever left waiting.
        private readonly HashSet<BlockNode> _canWait = new HashSet<BlockNode>();

        // The addresses of the bodies of schedule blocks, in document order with definitions sorted by
        // kind and name, so that a search finds the same block whatever order the content dictionary
        // enumerates in. Those of `next turn:` and `in N turns:` can wait as scheduled work. An `until`
        // body never waits, but a search that matches one is still sound: the same statements do the same.
        private readonly List<(string Root, string Address)> _waiting = new List<(string, string)>();

        // Hash to the first address with those statements, built the first time a search needs it.
        private Dictionary<string, string>? _firstByHash;

        public static BlockAddressBook Build(ContentLibrary content)
        {
            var book = new BlockAddressBook();

            foreach (EntityDefinition definition in content.Definitions.OrderBy(d => d.KindName + ":" + d.Name, StringComparer.OrdinalIgnoreCase))
            {
                string root = definition.KindName + ":" + definition.Name;
                book._roots.Add(root);
                int listener = 0;

                foreach (MemberNode member in definition.Syntax.Members)
                {
                    switch (member)
                    {
                        case BlockMemberNode block when block.Name == "move":
                            string move = block.Arguments.Count > 0 ? EntityDefinition.ReadWords(block.Arguments[0]).FirstOrDefault() ?? "?" : "?";
                            book.Add(root, root + "/move:" + move, block.Body);
                            break;
                        case BlockMemberNode block:
                            book.Add(root, root + "/" + block.Name, block.Body);
                            break;
                        case ListenerNode on:
                            book.Add(root, root + "/on#" + listener++, on.Body);
                            break;
                    }
                }
            }

            foreach (VerbDefinition verb in content.Verbs.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                string root = "verb:" + verb.Name;
                book._roots.Add(root);
                book.Add(root, root, verb.Body);
            }
            return book;
        }

        /// <summary>Addresses for statements parsed outside content, rooted at <see cref="ExecuteRoot"/>.</summary>
        public static BlockAddressBook ForStatements(BlockNode statements)
        {
            var book = new BlockAddressBook();
            book._roots.Add(ExecuteRoot);
            book.Add(ExecuteRoot, ExecuteRoot, statements);
            return book;
        }

        /// <summary>
        /// The bodies of schedule blocks: those of <c>next turn:</c> and <c>in N turns:</c>, which can
        /// wait as scheduled work, and those of <c>until</c>.
        /// </summary>
        public IEnumerable<BlockNode> ScheduleBodies => _waiting.Select(w => _blocks[w.Address]);

        public string? AddressOf(BlockNode block) => _addresses.TryGetValue(block, out string? address) ? address : null;

        public BlockNode? Resolve(string address) => _blocks.TryGetValue(address, out BlockNode? block) ? block : null;

        /// <summary>Whether a definition, as <c>kind:name</c>, has blocks in this book.</summary>
        public bool HasRoot(string root) => _roots.Contains(root);

        /// <summary>The <see cref="BlockHash"/> of one of this book's blocks.</summary>
        public string HashOf(BlockNode block) => BlockHash.Of(block);

        /// <summary>Whether a block is the body of a <c>next turn:</c> or <c>in N turns:</c>, and so can be left waiting.</summary>
        public bool CanWait(BlockNode block) => _canWait.Contains(block);

        /// <summary>
        /// The definition an address belongs to, as <c>kind:name</c>: the longest one in this book
        /// that the address starts with, since a quoted name may itself contain a slash. When no
        /// definition in this book matches, the part before the first slash.
        /// </summary>
        public string RootOf(string address)
        {
            if (_roots.Contains(address)) return address;
            for (int slash = address.LastIndexOf('/'); slash > 0; slash = address.LastIndexOf('/', slash - 1))
            {
                string root = address.Substring(0, slash);
                if (_roots.Contains(root)) return root;
            }

            int first = address.IndexOf('/');
            return first < 0 ? address : address.Substring(0, first);
        }

        /// <summary>
        /// The first schedule body (<see cref="ScheduleBodies"/>) in document order that has these
        /// statements, within one definition when <paramref name="root"/> is given, else anywhere.
        /// Null when none has.
        /// </summary>
        public string? Find(string hash, string? root = null)
        {
            if (root != null)
            {
                foreach (var (owner, address) in _waiting)
                {
                    if (owner == root && HashOf(_blocks[address]) == hash) return address;
                }
                return null;
            }

            if (_firstByHash == null)
            {
                _firstByHash = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (_, address) in _waiting)
                {
                    string each = HashOf(_blocks[address]);
                    if (!_firstByHash.ContainsKey(each)) _firstByHash[each] = address;
                }
            }
            return _firstByHash.TryGetValue(hash, out string? found) ? found : null;
        }

        private void Add(string root, string address, BlockNode block, bool waiting = false)
        {
            if (_blocks.ContainsKey(address)) return;
            _blocks[address] = block;
            _addresses[block] = address;
            if (waiting) _waiting.Add((root, address));

            for (int i = 0; i < block.Statements.Count; i++)
            {
                string at = address + "/" + i;
                switch (block.Statements[i])
                {
                    case IfNode branch:
                        Add(root, at + ".then", branch.Then);
                        if (branch.Else != null) Add(root, at + ".else", branch.Else);
                        break;
                    case ChanceNode chance:
                        Add(root, at + ".body", chance.Body);
                        if (chance.Else != null) Add(root, at + ".else", chance.Else);
                        break;
                    case RepeatNode repeat:
                        Add(root, at + ".body", repeat.Body);
                        break;
                    case ForEachNode loop:
                        Add(root, at + ".body", loop.Body);
                        break;
                    case ScheduleNode schedule:
                        Add(root, at + ".body", schedule.Body, waiting: true);
                        if (schedule.Kind != ScheduleKind.Until) _canWait.Add(schedule.Body);
                        break;
                    case LabeledBlockNode labeled:
                        Add(root, at + ".body", labeled.Body);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Statements that <c>CardRuntime.Execute</c> ran, kept with the blocks parsed from them so that
    /// a block they scheduled can be saved as the text and its address within it, and rebuilt from
    /// the text on restore.
    /// </summary>
    internal sealed class ExecutedStatements
    {
        public ExecutedStatements(string text, BlockNode statements)
        {
            Text = text;
            Book = BlockAddressBook.ForStatements(statements);
        }

        /// <summary>The statements exactly as they were passed to <c>Execute</c>.</summary>
        public string Text { get; }

        public BlockAddressBook Book { get; }

        /// <summary>Whether any statement, at any depth, schedules a block. Only then is there anything to remember.</summary>
        public static bool Schedules(BlockNode block)
        {
            foreach (StatementNode statement in block.Statements)
            {
                switch (statement)
                {
                    case ScheduleNode _:
                        return true;
                    case IfNode branch when Schedules(branch.Then) || (branch.Else != null && Schedules(branch.Else)):
                        return true;
                    case ChanceNode chance when Schedules(chance.Body) || (chance.Else != null && Schedules(chance.Else)):
                        return true;
                    case RepeatNode repeat when Schedules(repeat.Body):
                        return true;
                    case ForEachNode loop when Schedules(loop.Body):
                        return true;
                    case LabeledBlockNode labeled when Schedules(labeled.Body):
                        return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// A stable hash of what a block does: its statements and everything nested in them, with the
    /// file, line and column they were written at left out. Reformatting a block, commenting it or
    /// moving it keeps its hash; changing any word, number or statement in it does not. A listener
    /// is hashed the same way, its <c>on</c> line and its body each on their own.
    /// </summary>
    /// <remarks>
    /// Saves record these hashes, so changing what they cover turns saves with waiting work away and
    /// starts used listener limits afresh: keep them stable, and when a new kind of node, or a new
    /// part of a listener's <c>on</c> line, is added to the syntax, add it here as well.
    /// </remarks>
    internal static class BlockHash
    {
        // Saves and restores ask for the same hashes again and again, and the syntax never changes,
        // so each is computed once and forgotten with the node.
        private static readonly ConditionalWeakTable<BlockNode, string> Blocks = new ConditionalWeakTable<BlockNode, string>();
        private static readonly ConditionalWeakTable<ListenerNode, string> Listeners = new ConditionalWeakTable<ListenerNode, string>();

        public static string Of(BlockNode block) => Blocks.GetValue(block, each =>
        {
            var text = new StringBuilder();
            Block(each, text);
            return Fold(text);
        });

        /// <summary>
        /// A listener's hash: that of its <c>on</c> line (the event and its phase, the filter,
        /// <c>once per ...</c>, <c>priority</c> and the interval of <c>on every</c>), then a colon, then
        /// that of its body. <see cref="OnLineOf"/> takes the first part back out.
        /// </summary>
        public static string Of(ListenerNode listener) => Listeners.GetValue(listener, each =>
        {
            var text = new StringBuilder("listener");
            Word(each.EventName, text);
            Word(each.Phase.ToString(), text);
            Expression(each.Filter, text);
            Word(each.Limit.ToString(), text);
            Word(each.Priority.ToString(CultureInfo.InvariantCulture), text);
            Word(each.Interval.Raw.ToString(CultureInfo.InvariantCulture), text);
            Word(each.IntervalUnit, text);
            return Fold(text) + ":" + Of(each.Body);
        });

        /// <summary>The part of a listener's hash that covers its <c>on</c> line.</summary>
        public static string OnLineOf(string listenerHash)
        {
            int colon = listenerHash.IndexOf(':');
            return colon < 0 ? listenerHash : listenerHash.Substring(0, colon);
        }

        private static string Fold(StringBuilder text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in text.ToString())
            {
                unchecked
                {
                    hash ^= c;
                    hash *= 1099511628211UL;
                }
            }
            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        private static void Block(BlockNode? block, StringBuilder text)
        {
            if (block == null)
            {
                text.Append('~');
                return;
            }

            text.Append('{');
            foreach (StatementNode statement in block.Statements) Statement(statement, text);
            text.Append('}');
        }

        private static void Statement(StatementNode statement, StringBuilder text)
        {
            text.Append('[');
            switch (statement)
            {
                case CommandNode command:
                    text.Append("command");
                    Word(command.Verb, text);
                    foreach (ExprNode argument in command.Arguments) Expression(argument, text);
                    foreach (ClauseNode clause in command.Clauses)
                    {
                        text.Append("clause");
                        Word(clause.Keyword, text);
                        Expression(clause.Value, text);
                    }
                    break;

                case IfNode branch:
                    text.Append("if");
                    Expression(branch.Condition, text);
                    Block(branch.Then, text);
                    Block(branch.Else, text);
                    break;

                case LetNode let:
                    text.Append("let");
                    Word(let.Name, text);
                    Expression(let.Value, text);
                    break;

                case RepeatNode repeat:
                    text.Append("repeat");
                    Expression(repeat.Count, text);
                    Block(repeat.Body, text);
                    break;

                case ForEachNode loop:
                    text.Append("for");
                    Word(loop.Variable, text);
                    Expression(loop.Source, text);
                    Block(loop.Body, text);
                    break;

                case ChanceNode chance:
                    text.Append("chance");
                    Expression(chance.Probability, text);
                    Block(chance.Body, text);
                    Block(chance.Else, text);
                    break;

                case ScheduleNode schedule:
                    text.Append("schedule");
                    Word(schedule.Kind.ToString(), text);
                    Expression(schedule.Delay, text);
                    Word(schedule.Deadline, text);
                    Block(schedule.Body, text);
                    break;

                case LabeledBlockNode labeled:
                    text.Append("label");
                    Word(labeled.Label, text);
                    Block(labeled.Body, text);
                    break;

                case AssignNode assign:
                    text.Append("assign");
                    Expression(assign.Target, text);
                    Word(assign.Operator.ToString(), text);
                    Expression(assign.Value, text);
                    break;

                default:
                    Word(statement.GetType().FullName, text);
                    break;
            }
            text.Append(']');
        }

        private static void Expression(ExprNode? node, StringBuilder text)
        {
            text.Append('(');
            switch (node)
            {
                case null:
                    text.Append('~');
                    break;

                case NumberExpr number:
                    text.Append("number").Append(number.Value.Raw.ToString(CultureInfo.InvariantCulture));
                    Word(number.Unit, text);
                    break;

                case StringExpr quoted:
                    text.Append("string");
                    Word(quoted.Value, text);
                    break;

                case NameExpr name:
                    text.Append("name");
                    Word(name.Name, text);
                    break;

                case QualifiedExpr qualified:
                    text.Append("qualified");
                    Word(qualified.Qualifier, text);
                    Word(qualified.Name, text);
                    break;

                case MemberExpr member:
                    text.Append("member");
                    Expression(member.Target, text);
                    Word(member.Member, text);
                    break;

                case CallExpr call:
                    text.Append("call");
                    Word(call.Name, text);
                    Expression(call.Receiver, text);
                    foreach (ExprNode argument in call.Arguments) Expression(argument, text);
                    break;

                case UnaryExpr unary:
                    text.Append("unary");
                    Word(unary.Operator.ToString(), text);
                    Expression(unary.Operand, text);
                    break;

                case BinaryExpr binary:
                    text.Append("binary");
                    Word(binary.Operator.ToString(), text);
                    Expression(binary.Left, text);
                    Expression(binary.Right, text);
                    break;

                case RangeExpr range:
                    text.Append("range");
                    Expression(range.Low, text);
                    Expression(range.High, text);
                    break;

                case WhereExpr where:
                    text.Append("where");
                    Expression(where.Source, text);
                    Expression(where.Predicate, text);
                    break;

                case SelectorExpr selector:
                    text.Append("selector");
                    Word(selector.Modifier.ToString(), text);
                    Expression(selector.Count, text);
                    Word(selector.Key, text);
                    Expression(selector.Source, text);
                    break;

                default:
                    Word(node.GetType().FullName, text);
                    break;
            }
            text.Append(')');
        }

        /// <summary>A word with its length in front, so that no two sequences of words run together alike.</summary>
        private static void Word(string? word, StringBuilder text)
        {
            if (word == null)
            {
                text.Append('~');
                return;
            }
            text.Append(word.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(word);
        }
    }
}
