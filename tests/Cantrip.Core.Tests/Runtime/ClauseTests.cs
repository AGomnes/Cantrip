using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// Named clauses at run: the ones a built-in verb reads, and the refusal for the ones it does not.
    /// </summary>
    /// <remarks>
    /// The runtime holds the same table as the linter, because content reaches a built-in verb
    /// through a content verb, through the REPL and through <c>CardRuntime.Execute</c>, none of
    /// which lint first.
    /// </remarks>
    public sealed class ClauseTests
    {
        [Fact]
        [Trait("Regression", "clauses-are-silently-ignored")]
        public void A_clause_the_verb_does_not_read_is_refused_at_run()
        {
            string failure = FirstFailure("""
                verb guard(n):
                  block n for 2 turns

                card "Guard"
                  cost 0
                  effect:
                    guard 8

                test "a clause nothing reads is refused"
                  enemy hp 50
                  hand Guard
                  player energy 9
                  play Guard
                """);

            Assert.Contains("`for` is not a clause `block` reads", failure);
            Assert.Contains("Block is not timed", failure);
        }

        /// <summary>A game's own verb of the same name reads whatever it likes, at run as at lint.</summary>
        [Fact]
        public void A_game_verb_of_the_same_name_reads_its_own_clauses()
        {
            CardRuntime runtime = RuntimeTestKit.Create("""
                card "Anything"
                  cost 0
                """);

            var seen = new List<string>();
            runtime.Interpreter.RegisterVerb("block", call =>
            {
                seen.Add(call.Node.Clause("for") == null ? "no for" : "for");
            });

            runtime.Execute("block 8 for 2 turns");
            Assert.Equal(new[] { "for" }, seen);
        }

        /// <summary>
        /// <c>into</c> binds what a verb achieved, and heal and block are exactly the verbs worth
        /// asking about: a heal stops at full health, and block goes through its own modifiers.
        /// </summary>
        [Fact]
        [Trait("Regression", "clauses-are-silently-ignored")]
        public void Into_binds_what_heal_and_block_really_did() => Passes("""
            card "Patch"
              cost 0
              effect:
                heal 30 into healed
                gain healed gold

            card "Brace"
              cost 0
              effect:
                block 6 into guarded
                gain guarded gold

            test "a heal that hits the ceiling binds what it restored"
              enemy hp 50
              hand Patch
              player energy 9
              player hp 60
              player gold 0
              play Patch
              expect player.hp == 80
              expect player.gold == 20

            test "block binds what was gained"
              enemy hp 50
              hand Brace
              player energy 9
              player gold 0
              play Brace
              expect player.block == 6
              expect player.gold == 6
            """);

        /// <summary>
        /// <c>shuffle hand into discard</c> is a form the language reference lists, and it used to
        /// shuffle into the draw pile whatever the clause said.
        /// </summary>
        [Fact]
        [Trait("Regression", "clauses-are-silently-ignored")]
        public void Shuffle_puts_the_cards_where_into_says() => Passes("""
            card "Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 6 to target

            card "Tidy"
              cost 0
              effect:
                shuffle hand into discard

            test "the hand goes to the discard pile, not the draw pile"
              enemy hp 50
              hand Strike, Strike, Tidy
              player energy 9
              play Tidy
              expect hand.count == 0
              expect discard.count == 3
              expect draw.count == 0
            """);

        /// <summary>The bare `shuffle` and the draw-pile form are exactly as they were.</summary>
        [Fact]
        public void Shuffle_still_means_the_draw_pile_when_nothing_says_otherwise() => Passes("""
            card "Wound"
              cost 0
              tags unplayable

            card "Curse"
              cost 0
              effect:
                shuffle Wound 2

            card "Sort"
              cost 0
              effect:
                shuffle hand into draw

            test "copies land in the draw pile"
              enemy hp 50
              hand Curse
              player energy 9
              play Curse
              expect draw.count == 2

            test "named cards land in the draw pile"
              enemy hp 50
              hand Wound, Wound, Sort
              player energy 9
              play Sort
              expect draw.count == 2
              expect hand.count == 0
            """);

        // Helpers ----------------------------------------------------------------------------

        private static void Passes(string dsl)
        {
            IReadOnlyList<DslTestResult> results = new DslTestRunner(Load(dsl)).RunAll();
            Assert.NotEmpty(results);
            foreach (DslTestResult result in results) Assert.True(result.Passed, result.ToString());
        }

        private static string FirstFailure(string dsl)
        {
            DslTestResult result = new DslTestRunner(Load(dsl)).RunAll().Last();
            Assert.False(result.Passed, "expected the clause to be refused, but the test passed");
            return result.Failure!;
        }

        private static ContentLibrary Load(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl, "clauses.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            return content;
        }
    }
}
