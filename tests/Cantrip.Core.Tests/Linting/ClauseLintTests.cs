using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Cantrip.Runtime;
using Cantrip.Syntax;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// CT323: a named clause a built-in verb does not read.
    /// </summary>
    /// <remarks>
    /// The parser knows thirteen clause words and attaches no meaning to any of them, so a clause a
    /// verb did not read was simply dropped. <c>block 8 for 2 turns</c> gave ordinary block that
    /// vanished at the next turn start, <c>apply Poison 3 at target</c> ignored the <c>at</c>, and
    /// <c>deal 5 against enemy2</c> hit whatever the card was aimed at. Each of those is a card that
    /// reads as one thing and does another, with nothing said.
    /// </remarks>
    public sealed class ClauseLintTests
    {
        private const string Base = """
            status "Poison"
              tags debuff
              stacking intensity

            """;

        [Fact]
        [Trait("Regression", "clauses-are-silently-ignored")]
        public void A_timed_block_says_that_block_is_not_timed()
        {
            Diagnostic error = Single(Lint("""
                card "Guard"
                  cost 1
                  effect:
                    block 8 for 2 turns
                """));

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("`for` is not a clause `block` reads", error.Message);
            Assert.Contains("Block is not timed", error.Message);
            Assert.Contains("`block` reads `to` and `into`.", error.Message);
        }

        [Fact]
        [Trait("Regression", "clauses-are-silently-ignored")]
        public void An_unread_word_names_the_clause_that_works()
        {
            Diagnostic error = Single(Lint("""
                card "Taint"
                  cost 1
                  target enemy
                  effect:
                    apply Poison 3 at target
                """));

            Assert.Contains("`at` is not a clause `apply` reads", error.Message);
            Assert.Contains("Write `to` instead.", error.Message);
        }

        [Fact]
        [Trait("Regression", "clauses-are-silently-ignored")]
        public void Against_on_deal_names_to()
        {
            Diagnostic error = Single(Lint("""
                card "Swing"
                  cost 1
                  target enemy
                  effect:
                    deal 5 against target
                """));

            Assert.Contains("`against` is not a clause `deal` reads", error.Message);
            Assert.Contains("Write `to` instead.", error.Message);
        }

        /// <summary>A verb that reads no clause at all says so rather than listing nothing.</summary>
        [Fact]
        public void A_verb_that_reads_no_clauses_says_so()
        {
            Diagnostic error = Single(Lint("""
                card "Noisy"
                  cost 0
                  effect:
                    log "hi" to player
                """));

            Assert.Contains("`log` reads no clauses.", error.Message);
        }

        /// <summary>
        /// A flag after a comma is not a clause. Games register verbs in C# that read their own
        /// flags, and the built-in verbs have four of their own, so none of these may be reported.
        /// </summary>
        [Fact]
        public void A_flag_after_a_comma_is_never_a_clause()
        {
            None(Lint("""
                card "Everything"
                  cost 1
                  target enemy
                  effect:
                    deal 5 to target, ignore block
                    deal 5 to target, pierce
                    move target to discard, top
                    discover 3 cards, weighted as picked
                    create picked into hand
                """));
        }

        /// <summary>Every clause the table lists is accepted on the verb that reads it.</summary>
        [Fact]
        public void The_clauses_the_verbs_do_read_are_never_reported()
        {
            None(Lint("""
                card "Busy"
                  cost 1
                  target enemy
                  effect:
                    change hp by -2 to target
                    deal 4 to target as fire into dealt
                    heal dealt into healed
                    block healed into guarded
                    gain guarded gold
                    draw 1 to player
                    apply Poison 2 for 3 turns to target
                    remove Poison from target
                    choose 1 from hand as picked
                    move picked to discard
                    copy picked into draw
                    shuffle hand into discard
                    emit sparked to player
                    kill to target
                """));
        }

        /// <summary>A game whose own verb has one of those names reads whatever it likes.</summary>
        [Fact]
        public void A_game_with_its_own_verb_of_that_name_is_left_alone()
        {
            None(Lint("""
                verb block(who):
                  deal 1 to who

                card "Old Ways"
                  cost 1
                  target enemy
                  effect:
                    block target for 2 turns
                """));
        }

        /// <summary>In a test, `play` is the runner's own verb, so its clauses are not the rules'.</summary>
        [Fact]
        public void A_test_verb_of_the_same_name_is_left_alone()
        {
            None(Lint("""
                card "Strike"
                  cost 1
                  target enemy
                  effect:
                    deal 6 to target

                test "fine"
                  enemy hp 10
                  play Strike on enemy
                  expect enemy.hp == 4
                """));
        }

        /// <summary>
        /// The table has to cover every verb the interpreter registers, or the check quietly stops
        /// applying to whichever one was added last.
        /// </summary>
        [Fact]
        public void Every_builtin_verb_is_in_the_table()
        {
            CardRuntime runtime = CardRuntime.FromText(Base);
            var missing = runtime.Interpreter.VerbNames
                .Where(v => !DslTestRunner.TestVerbs.Contains(v, System.StringComparer.OrdinalIgnoreCase))
                .Where(v => !BuiltinClauses.IsKnownVerb(v))
                .ToList();

            Assert.True(missing.Count == 0, "not in BuiltinClauses: " + string.Join(", ", missing));
        }

        /// <summary>Every clause a built-in verb reads is a word the grammar knows.</summary>
        [Fact]
        public void Every_clause_in_the_table_is_a_clause_word()
        {
            foreach (string verb in BuiltinClauses.Verbs)
            {
                foreach (string clause in BuiltinClauses.ReadBy(verb))
                    Assert.True(Parser.IsClauseWord(clause), $"`{clause}` on `{verb}` is not a clause word");
            }
        }

        /// <summary>
        /// Four of the thirteen clause words are read by no built-in verb. That is deliberate — they
        /// exist for verbs a game registers — and this states it, so the day one of them is wired up
        /// the decision is made again on purpose.
        /// </summary>
        [Fact]
        public void The_four_words_no_builtin_verb_reads_are_stated()
        {
            var unread = Parser.ClauseWords
                .Where(word => !BuiltinClauses.Verbs.Any(verb => BuiltinClauses.Reads(verb, word)))
                .OrderBy(w => w, System.StringComparer.Ordinal)
                .ToList();

            Assert.Equal(new[] { "against", "at", "over", "using" }, unread);
        }

        // Helpers ----------------------------------------------------------------------------

        private static IReadOnlyList<Diagnostic> Lint(string dsl) =>
            Linter.Lint(ContentLibrary.FromText(Base + dsl, "clause-lint.cantrip"));

        private static Diagnostic Single(IReadOnlyList<Diagnostic> diagnostics)
        {
            var matching = diagnostics.Where(d => d.Code == Linter.ClauseNotRead).ToList();
            Assert.True(matching.Count == 1, $"expected one CT323, got:\n{string.Join("\n", diagnostics)}");
            return matching[0];
        }

        private static void None(IReadOnlyList<Diagnostic> diagnostics) =>
            Assert.True(
                diagnostics.All(d => d.Code != Linter.ClauseNotRead),
                $"expected no CT323, got:\n{string.Join("\n", diagnostics)}");
    }
}
