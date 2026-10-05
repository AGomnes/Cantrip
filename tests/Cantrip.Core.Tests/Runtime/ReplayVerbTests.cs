using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// <c>replay</c>: resolving the effect of a card that is in the game a second time, for free.
    /// </summary>
    /// <remarks>
    /// The free part is exactly why the argument has to be something in the game. <c>replay Strike</c>
    /// read as if it repeated a Strike and instead ran the printed effect with no card, no cost and no
    /// play behind it: the free lunch <c>copy</c>, <c>play</c> and <c>transform</c> were all closed
    /// against, left open in the one verb whose whole business is "again, for nothing".
    /// </remarks>
    public sealed class ReplayVerbTests
    {
        [Fact]
        [Trait("Regression", "replay-takes-a-definition")]
        public void A_definition_is_refused_and_names_the_way_to_a_real_card()
        {
            string failure = FirstFailure("""
                card "Strike"
                  cost 1
                  target enemy
                  tags attack
                  effect:
                    deal 6 to target

                card "Cheat"
                  cost 0
                  target enemy
                  effect:
                    replay Strike

                test "content is not a card in the game"
                  enemy hp 50
                  hand Cheat
                  player energy 9
                  play Cheat on enemy
                """);

            Assert.Contains("`replay` resolves the effect of a card that is in the game", failure);
            Assert.Contains("`Strike` is content", failure);
            Assert.Contains("create Strike into hand", failure);
            Assert.Contains("play created.first, free", failure);
        }

        /// <summary>A quoted name is text, not a definition, and text is not a card in the game either.</summary>
        [Fact]
        [Trait("Regression", "replay-takes-a-definition")]
        public void A_quoted_name_is_refused_the_same_way_and_quoted_back()
        {
            string failure = FirstFailure("""
                card "Fire Bolt"
                  cost 1
                  target enemy
                  tags attack
                  effect:
                    deal 4 to target

                card "Cheat"
                  cost 0
                  target enemy
                  effect:
                    replay "Fire Bolt"

                test "a quoted name is content too"
                  enemy hp 50
                  hand Cheat
                  player energy 9
                  play Cheat on enemy
                """);

            Assert.Contains("`replay` resolves the effect of a card that is in the game", failure);
            Assert.Contains("create \"Fire Bolt\" into hand", failure);
        }

        /// <summary>
        /// An ability by name was the other half of the hole: <c>replay Zap</c> ran an ability's
        /// effect with its cooldown untouched.
        /// </summary>
        [Fact]
        [Trait("Regression", "replay-takes-a-definition")]
        public void An_ability_by_name_is_refused_too()
        {
            string failure = FirstFailure("""
                ability "Zap"
                  cooldown 3
                  target enemy
                  effect:
                    deal 7 to target

                card "Cheat"
                  cost 0
                  target enemy
                  effect:
                    replay Zap

                test "an ability is content too"
                  enemy hp 50
                  hand Cheat
                  player energy 9
                  play Cheat on enemy
                """);

            Assert.Contains("`replay` resolves the effect of a card that is in the game", failure);
        }

        /// <summary>
        /// The verb itself is untouched: a card that is really in a pile still resolves again, which
        /// is what Burst, Echo Form and Dominion's Throne Room are made of.
        /// </summary>
        [Fact]
        public void A_card_in_the_game_still_replays() => Passes("""
            card "Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 6 to target

            card "Echo"
              cost 0
              target enemy
              effect:
                replay hand.first on target

            test "a card in hand resolves twice"
              enemy hp 50
              hand Strike, Echo
              player energy 9
              play Echo on enemy
              expect enemy.hp == 44
              expect hand.count == 1
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
            Assert.False(result.Passed, "expected the test to be refused, but it passed");
            return result.Failure!;
        }

        private static ContentLibrary Load(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl, "replay-verb.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            return content;
        }
    }
}
