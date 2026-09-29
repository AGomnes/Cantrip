using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// CT326 and CT334: the two things the party added to the linter. CT326 is the rule that keeps
    /// <c>player</c> honest in a game that has a party; CT334 is the answer a designer gets for a
    /// turn setting whose value this release does not implement yet.
    /// </summary>
    public sealed class PartyLintTests
    {
        private const string Party = """
            hero "Knight"
              hp 30

            """;

        private static Diagnostic[] Lint(string content)
        {
            ContentLibrary library = ContentLibrary.FromText(content);
            library.Diagnostics.ThrowIfErrors();
            return Linter.Lint(library).ToArray();
        }

        private static Diagnostic[] Coded(string content, string code) =>
            Lint(content).Where(d => d.Code == code).ToArray();

        // CT326 ----------------------------------------------------------------------------------

        [Theory]
        [InlineData("""
            enemy "Thug"
              hp 20
              move "Hit":
                deal 4 to player
              pattern cycle Hit
            """, "an enemy's move")]
        [InlineData("""
            card "Jab"
              cost 1
              target enemy
              effect:
                deal 3 to target
                heal 1 to player
            """, "a card's effect")]
        [InlineData("""
            ability "Ward"
              target ally
              effect:
                block 3 to player
            """, "an ability's effect")]
        public void Player_where_a_member_is_meant_is_an_error(string body, string place)
        {
            Diagnostic error = Assert.Single(Coded(Party + body, Linter.PlayerWhereAMemberIsMeant));

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains(place, error.Message);
            Assert.Contains("`leader`", error.Message);
            Assert.Contains("`party`", error.Message);
            Assert.Equal("target", error.Suggestion);
        }

        /// <summary>
        /// The whole reason it can be an error rather than a warning: it only ever applies to
        /// content that opted into a party, and nobody has shipped one.
        /// </summary>
        [Fact]
        public void Content_with_no_hero_may_say_player_anywhere()
        {
            Assert.Empty(Coded("""
                enemy "Thug"
                  hp 20
                  move "Hit":
                    deal 4 to player
                  pattern cycle Hit

                card "Jab"
                  cost 1
                  effect:
                    heal 1 to player
                """, Linter.PlayerWhereAMemberIsMeant));
        }

        /// <summary>
        /// <c>player</c> keeps meaning the leader and keeps being the right word wherever the run
        /// itself is meant: relics, gold, anything the party as a whole owns.
        /// </summary>
        [Fact]
        public void Player_is_still_right_outside_the_three_bodies_that_act_on_somebody()
        {
            Assert.Empty(Coded(Party + """
                relic "Purse"
                  on battle_start:
                    gain 5 gold to player

                status "Guarded"
                  stacking duration
                  on owner.turn_start:
                    block 2 to player
                """, Linter.PlayerWhereAMemberIsMeant));
        }

        [Fact]
        public void A_test_may_still_say_player()
        {
            Assert.Empty(Coded(Party + """
                test "the leader is still the leader"
                  player hp 40
                  enemy hp 10
                  expect player.hp == 40
                """, Linter.PlayerWhereAMemberIsMeant));
        }

        // CT334 ----------------------------------------------------------------------------------

        [Theory]
        [InlineData("turns: initiative", "sides")]
        [InlineData("order: speed", "position")]
        public void A_turn_setting_this_release_does_not_implement_says_so(string setting, string instead)
        {
            ContentLibrary library = ContentLibrary.FromText("ruleset\n  " + setting + "\n");

            Diagnostic error = Assert.Single(library.Diagnostics, d => d.Code == Linter.TurnSettingNotYetBuilt);
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("not in this release", error.Message);
            Assert.Equal(instead, error.Suggestion);
        }

        [Theory]
        [InlineData("turns: sides")]
        [InlineData("order: position")]
        public void The_settings_this_release_does_implement_are_accepted(string setting)
        {
            ContentLibrary library = ContentLibrary.FromText("ruleset\n  " + setting + "\n");
            Assert.DoesNotContain(library.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        }

        /// <summary>
        /// Leaving them out is what every game does, and it has to keep meaning what it always did.
        /// </summary>
        [Fact]
        public void Saying_nothing_is_sides_and_position()
        {
            Cantrip.Runtime.Ruleset rules = ContentLibrary.FromText("card \"X\"\n  cost 0\n").BuildRuleset();

            Assert.Equal(Cantrip.Runtime.TurnMode.Sides, rules.Turns);
            Assert.Equal(Cantrip.Runtime.PartyOrder.Position, rules.Order);
        }
    }
}
