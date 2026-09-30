using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// CT326 and CT335: the two things the party added to the linter. CT326 is the rule that keeps
    /// <c>player</c> honest in a game that has a party; CT335 is what a designer is told when a
    /// turn order is written into a game whose clock has no turns to order.
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
        // A real-time enemy has no `move` at all -- a move runs on a turn, and CT337 refuses one
        // under `clock ticks` -- so its whole behaviour is written in listeners. This check did
        // not look there, which left the party guarantee absent from the entire surface a
        // real-time party game lives on.
        [InlineData("""
            enemy "Sniper"
              hp 10
              on every 2s:
                deal 5 to player
            """, "an enemy's listener")]
        [InlineData("""
            enemy "Sniper"
              hp 10
              on damaged:
                deal 5 to player
            """, "an enemy's listener")]
        [InlineData("""
            card "Echo"
              cost 1
              on card_played:
                deal 2 to player
            """, "a card's listener")]
        [InlineData("""
            ability "Aura"
              on damaged:
                block 1 to player
            """, "an ability's listener")]
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
        /// The hole the list left, and the worst one: a listener on a <c>hero</c>.
        /// <c>hero "Cleric" / on damaged: block 2 to player</c> blocked the <em>leader</em> whenever
        /// the Cleric was damaged, quietly, in the one declaration a party game has most of. The rule
        /// is every body on every declaration now, so an <c>actor</c> and a <c>keyword</c> are in too,
        /// and so are a <c>modify</c> line and a <c>target … where</c> filter.
        /// </summary>
        [Theory]
        [Trait("Regression", "ct326-was-a-list-of-places-with-holes")]
        [InlineData("""
            hero "Cleric"
              hp 24
              on damaged:
                block 2 to player
            """, "a hero's listener", "self")]
        [InlineData("""
            hero "Cleric"
              hp 24
              effect:
                block 2 to player
            """, "a hero's effect", "self")]
        [InlineData("""
            actor "Squire"
              hp 12
              on turn_start:
                heal 1 to player
            """, "an actor's listener", "self")]
        [InlineData("""
            keyword "Thorned"
              on damaged:
                deal 1 to player
            """, "a keyword's listener", "owner")]
        [InlineData("""
            card "Ward"
              cost 1
              modify block of player: +2
            """, "a card's `modify` line", "target")]
        [InlineData("""
            card "Snipe"
              cost 1
              target enemy where it != player
              effect:
                deal 3 to target
            """, "a card's `target` filter", "target")]
        public void Every_body_on_every_declaration_is_checked(string body, string place, string fix)
        {
            Diagnostic error = Assert.Single(Coded(Party + body, Linter.PlayerWhereAMemberIsMeant));

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains(place, error.Message);
            Assert.Contains("`leader`", error.Message);
            Assert.Contains("`party`", error.Message);
            Assert.Equal(fix, error.Suggestion);
        }

        /// <summary>
        /// A <c>keyword</c> is carried, so it keeps the owner exception a <c>status</c> and a
        /// <c>relic</c> have: there is no member in view in a listener about the holder alone.
        /// </summary>
        [Fact]
        public void A_carried_listener_about_its_own_owner_keeps_player_whatever_carries_it()
        {
            Assert.Empty(Coded(Party + """
                keyword "Blessed"
                  on owner.turn_start:
                    block 1 to player

                item "Flask"
                  on battle_start:
                    heal 2 to player
                """, Linter.PlayerWhereAMemberIsMeant));
        }

        /// <summary>
        /// A content <c>verb</c> is deliberately left out, and this pins that decision rather than
        /// leaving it to omission. A verb has no owner, so neither <c>self</c> nor <c>owner</c> exists
        /// inside one and its <c>target</c> is whatever its caller bound: there is no word CT326 could
        /// name, and it is an error rather than a warning precisely because it can always name one.
        /// <c>player</c> in a verb is often right, too — a run's own pool — and the body that calls it
        /// is checked, which is where the leader-or-member decision is actually written. Severity may
        /// rise in a 1.x release, so a verb can still be warned about later; naming the wrong word now
        /// could not be taken back.
        /// </summary>
        [Fact]
        public void A_verb_may_still_say_player_and_that_is_on_purpose()
        {
            Assert.Empty(Coded(Party + """
                verb score(c):
                  gain 1 gold to player

                card "Cash In"
                  cost 0
                  effect:
                    score self
                """, Linter.PlayerWhereAMemberIsMeant));
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
        public void Player_is_still_right_wherever_the_run_itself_is_meant()
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

        /// <summary>
        /// A status's listener is where a real-time enemy's whole behaviour lives, and where damage
        /// over time is spelled on any clock. <c>deal 5 to player</c> there burns the leader rather
        /// than whoever the status is on, every tick, in silence — which is the exact shape CT326
        /// exists to refuse.
        /// </summary>
        [Theory]
        [InlineData("""
            status "Burn"
              stacking duration
              on every 2s:
                deal 5 to player
            """, "a status's listener")]
        [InlineData("""
            status "Spite"
              stacking intensity
              on damaged:
                deal 2 to player
            """, "a status's listener")]
        [InlineData("""
            status "Vigil"
              stacking duration
              on turn_start:
                block 2 to player
            """, "a status's listener")]
        [InlineData("""
            relic "Thorns"
              on damaged:
                deal 2 to player
            """, "a relic's listener")]
        [InlineData("""
            relic "Pulse"
              on every 2s:
                heal 1 to player
            """, "a relic's listener")]
        public void Player_in_a_carried_listener_is_an_error_too(string body, string place)
        {
            Diagnostic error = Assert.Single(Coded(Party + body, Linter.PlayerWhereAMemberIsMeant));

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains(place, error.Message);

            // `target` names nothing inside `on every 2s:`, so a carried listener is told the word
            // that does mean "whoever this is on", and told the exception in the same breath.
            Assert.Equal("owner", error.Suggestion);
            Assert.Contains("`owner`", error.Message);
            Assert.Contains("on owner.turn_start:", error.Message);
        }

        /// <summary>
        /// The exception, in both of the two shapes that make it up: an event scoped to the holder,
        /// and a run-level event where the leader is the only actor the engine puts in view.
        /// </summary>
        [Theory]
        [InlineData("""
            status "Guarded"
              stacking duration
              on owner.turn_start:
                block 2 to player
            """)]
        [InlineData("""
            status "Mirror"
              stacking duration
              on self.damaged:
                block 2 to player
            """)]
        [InlineData("""
            relic "Aegis"
              on owner.turn_start:
                block 2 to player
            """)]
        [InlineData("""
            relic "Purse"
              on battle_start:
                gain 5 gold to player
            """)]
        [InlineData("""
            relic "Ledger"
              on battle_end:
                gain 5 gold to player
            """)]
        [InlineData("""
            relic "Trinket"
              on obtained:
                gain 2 max_hp to player
            """)]
        public void A_listener_about_its_own_owner_may_still_say_player(string body)
        {
            Assert.Empty(Coded(Party + body, Linter.PlayerWhereAMemberIsMeant));
        }

        /// <summary>
        /// A scope is not the exception on its own: <c>controller</c> and <c>player</c> are scopes
        /// the runtime resolves too, and they name somebody who is not the holder, so a member is
        /// still in view and the word still has to be the right one.
        /// </summary>
        [Fact]
        public void A_scope_that_is_not_the_holder_is_not_the_exception()
        {
            Assert.Single(Coded(Party + """
                status "Echo"
                  stacking duration
                  on controller.turn_start:
                    block 2 to player
                """, Linter.PlayerWhereAMemberIsMeant));
        }

        /// <summary>
        /// The widening is still bounded by the party: content with no <c>hero</c> is untouched,
        /// which is why an error is affordable at all.
        /// </summary>
        [Fact]
        public void A_carried_listener_with_no_hero_may_still_say_player()
        {
            Assert.Empty(Coded("""
                status "Burn"
                  stacking duration
                  on every 2s:
                    deal 5 to player
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

        // CT335 ----------------------------------------------------------------------------------

        [Theory]
        [InlineData("turns: sides")]
        [InlineData("turns: initiative")]
        [InlineData("order: position")]
        [InlineData("order: speed")]
        public void Every_turn_setting_is_accepted_on_a_turn_clock(string setting)
        {
            Assert.Empty(Coded("ruleset\n  clock turns\n  " + setting + "\n", Linter.TurnOrderWithoutTurns));
            Assert.DoesNotContain(ContentLibrary.FromText("ruleset\n  clock turns\n  " + setting + "\n").Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        }

        [Theory]
        [InlineData("turns: initiative", "never fire")]
        [InlineData("turns: sides", "never fire")]
        [InlineData("order: speed", "continuous time")]
        public void A_turn_order_in_a_game_with_no_turns_says_so(string setting, string because)
        {
            Diagnostic error = Assert.Single(Coded("ruleset\n  clock ticks\n  " + setting + "\n", Linter.TurnOrderWithoutTurns));
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("clock ticks", error.Message);
            Assert.Contains(because, error.Message);
        }

        /// <summary>
        /// A game that says nothing about its clock is a turn game, where the setting is not idle,
        /// so nothing is reported and content that never mentions a clock is left alone.
        /// </summary>
        [Fact]
        public void A_turn_order_with_no_clock_stated_is_left_alone()
        {
            Assert.Empty(Coded("ruleset\n  turns: initiative\n", Linter.TurnOrderWithoutTurns));
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
