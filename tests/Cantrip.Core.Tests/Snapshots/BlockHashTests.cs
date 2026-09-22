using System;
using System.Linq;
using System.Reflection;
using Cantrip.Content;
using Cantrip.Syntax;
using Xunit;

namespace Cantrip.Tests.Snapshots
{
    /// <summary>
    /// The hashes a save records beside each waiting block and each listener's limit or timer: the
    /// same for the same statements however they are laid out, and different as soon as they do
    /// something else.
    /// </summary>
    public sealed class BlockHashTests
    {
        private static string Hash(string statements) => BlockHash.Of(CardRuntime.ParseStatements(statements));

        [Theory]
        [InlineData("draw 1", "draw  1   # the same card")]
        [InlineData("repeat 2:\n  draw 1", "# a comment first\nrepeat 2:\n      draw 1")]
        [InlineData("chance 25%:\n  draw 1", "chance 25%:\n\n  draw 1")]
        [InlineData("next turn: draw 1", "next turn:\n  draw 1")]
        public void Layout_and_comments_leave_the_hash_alone(string written, string relaid) =>
            Assert.Equal(Hash(written), Hash(relaid));

        [Theory]
        [InlineData("draw 1", "draw 2")]
        [InlineData("draw 1", "discard 1")]
        [InlineData("deal 5 to enemy", "deal 5 to player")]
        [InlineData("apply Weak 1 to target", "apply Vulnerable 1 to target")]
        [InlineData("gain 1 block\ndraw 1", "draw 1\ngain 1 block")]
        [InlineData("in 2 turns:\n  draw 1", "in 3 turns:\n  draw 1")]
        [InlineData("next turn:\n  draw 1", "next turn:\n  draw 1\n  draw 1")]
        [InlineData("if hp > 5:\n  draw 1\nelse:\n  draw 2", "if hp > 5:\n  draw 1\nelse:\n  draw 3")]
        [InlineData("chance 25%:\n  draw 1", "chance 25:\n  draw 1")]
        [InlineData("heal 12", "heal \"12\"")]
        public void Any_change_to_what_the_statements_do_changes_the_hash(string written, string changed) =>
            Assert.NotEqual(Hash(written), Hash(changed));

        [Fact]
        public void Every_kind_of_statement_and_expression_is_part_of_the_hash()
        {
            // BlockHash writes each of these out explicitly. A node it has never heard of would count
            // only by its type, so two blocks differing inside one would hash alike, and a save would
            // run the wrong one. A new node therefore needs a case in BlockHash, then a line here.
            string[] known =
            {
                nameof(CommandNode), nameof(IfNode), nameof(LetNode), nameof(RepeatNode), nameof(ForEachNode),
                nameof(ChanceNode), nameof(ScheduleNode), nameof(LabeledBlockNode), nameof(AssignNode),
                nameof(NumberExpr), nameof(StringExpr), nameof(NameExpr), nameof(QualifiedExpr), nameof(MemberExpr),
                nameof(CallExpr), nameof(UnaryExpr), nameof(BinaryExpr), nameof(RangeExpr), nameof(WhereExpr),
                nameof(SelectorExpr),
            };

            string[] nodes = typeof(Node).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && (typeof(StatementNode).IsAssignableFrom(t) || typeof(ExprNode).IsAssignableFrom(t)))
                .Select(t => t.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(known.OrderBy(n => n, StringComparer.Ordinal), nodes);
        }

        // Listeners ------------------------------------------------------------------------------

        private static string Listener(string lines)
        {
            ContentLibrary content = ContentLibrary.FromText("relic \"Tally\"\n" + lines);
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            return BlockHash.Of(Assert.Single(content.Find("Tally", "relic")!.Listeners));
        }

        private const string Plain = "  on card_played(tag:attack) once per battle priority 2:\n    gain 1 gold\n";

        [Theory]
        [InlineData("  # Counts the first attack.\n  on card_played(tag:attack)  once per battle  priority 2 :\n      gain 1 gold   # one\n")]
        [InlineData("  on card_played(tag:attack), once per battle, priority 2:\n    gain 1 gold\n")]
        [InlineData("  on card_played(tag:attack) once per battle priority 2: gain 1 gold\n")]
        public void Layout_and_comments_leave_a_listener_hash_alone(string relaid) =>
            Assert.Equal(Listener(Plain), Listener(relaid));

        [Theory]
        [InlineData("  on card_drawn(tag:attack) once per battle priority 2:\n    gain 1 gold\n")]
        [InlineData("  on before_card_played(tag:attack) once per battle priority 2:\n    gain 1 gold\n")]
        [InlineData("  on card_played(tag:skill) once per battle priority 2:\n    gain 1 gold\n")]
        [InlineData("  on card_played once per battle priority 2:\n    gain 1 gold\n")]
        [InlineData("  on card_played(tag:attack) once per turn priority 2:\n    gain 1 gold\n")]
        [InlineData("  on card_played(tag:attack) priority 2:\n    gain 1 gold\n")]
        [InlineData("  on card_played(tag:attack) once per battle priority 3:\n    gain 1 gold\n")]
        public void Any_change_to_a_listeners_on_line_changes_both_parts_of_its_hash(string changed)
        {
            Assert.NotEqual(BlockHash.OnLineOf(Listener(Plain)), BlockHash.OnLineOf(Listener(changed)));
            Assert.NotEqual(Listener(Plain), Listener(changed));
        }

        [Theory]
        [InlineData("  on every 2s:\n    gain 1 gold\n", "  on every 3s:\n    gain 1 gold\n")]
        [InlineData("  on every 2 turns:\n    gain 1 gold\n", "  on every 2s:\n    gain 1 gold\n")]
        public void The_interval_of_an_on_every_listener_is_part_of_its_on_line(string written, string changed) =>
            Assert.NotEqual(BlockHash.OnLineOf(Listener(written)), BlockHash.OnLineOf(Listener(changed)));

        [Fact]
        public void A_change_to_a_listeners_body_keeps_its_on_line_hash()
        {
            string changed = Listener(Plain.Replace("gain 1 gold", "gain 2 gold"));

            Assert.NotEqual(Listener(Plain), changed);
            Assert.Equal(BlockHash.OnLineOf(Listener(Plain)), BlockHash.OnLineOf(changed));
        }

        [Fact]
        public void A_listeners_hash_ends_with_its_bodys_block_hash()
        {
            ContentLibrary content = ContentLibrary.FromText("relic \"Tally\"\n" + Plain);
            ListenerNode listener = Assert.Single(content.Find("Tally", "relic")!.Listeners);

            Assert.EndsWith(":" + BlockHash.Of(listener.Body), BlockHash.Of(listener));
        }

        [Fact]
        public void Every_part_of_a_listener_is_part_of_its_hash()
        {
            // BlockHash writes each of these out. A part it has never heard of, such as a new
            // modifier on the `on` line, would leave two different listeners hashing alike, so a
            // save could hand one's used limit to the other. A new part therefore needs a line in
            // BlockHash.Of(ListenerNode), then a line here.
            string[] known =
            {
                nameof(ListenerNode.EventName), nameof(ListenerNode.Phase), nameof(ListenerNode.Filter), nameof(ListenerNode.Body),
                nameof(ListenerNode.Limit), nameof(ListenerNode.Priority), nameof(ListenerNode.Interval), nameof(ListenerNode.IntervalUnit),
            };

            string[] parts = typeof(ListenerNode)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(known.OrderBy(n => n, StringComparer.Ordinal), parts);
        }
    }
}
