using System.Linq;
using Cantrip.Sim;
using Xunit;

namespace Cantrip.Tests.Sim
{
    /// <summary>
    /// A bot playing a party rather than one hero. Until this release it played the leader and
    /// nobody else, so a party scenario measured two heroes standing still while a third acted.
    /// </summary>
    /// <remarks>
    /// <c>wins</c>, <c>hp_left</c> and <c>turns</c> stay reported-unchecked for a party, for the
    /// reason every bot shares: they all score the player's hp against the enemies', and more
    /// members make that heuristic worse rather than better. What these pin is that every member is
    /// asked, that a turn is still one turn, and that the hp column counts the whole side.
    /// </remarks>
    public sealed class PartyBotTests
    {
        /// <summary>
        /// Two heroes whose only plays are on a one-turn cooldown, against a Dummy with exactly ten
        /// turns of their combined damage in it. So the turn count is "how many members were
        /// asked", with no room for a bot's judgement to change the number: eight a turn is both of
        /// them and ten turns, five a turn is the Crusader alone and sixteen.
        /// </summary>
        private const string Party = @"
hero Crusader
  hp 30
  abilities Smite

hero Vestal
  hp 30
  abilities Judgement

ability Smite
  cooldown 1 turns
  target enemy
  effect:
    deal 5 to target

ability Judgement
  cooldown 1 turns
  target enemy
  effect:
    deal 3 to target

enemy Dummy
  hp 80
  move Poke at lowest hp enemies:
    deal 1 to target
  pattern cycle Poke

scenario ""Two heroes and a dummy""
  runs 1
  player hp 40 energy 3
  hero Crusader
  hero Vestal
  battle Dummy
";

        private static BattleResult Fought(string bot)
        {
            ScenarioOutcome result = ScenarioRunnerTests.Play(Party, new ScenarioOptions().Bot(bot));
            RunResult run = result.Runs.First();
            Assert.Null(run.Error);
            return run.Battles[0];
        }

        [Theory]
        [InlineData(Bots.Cautious)]
        [InlineData(Bots.Patient)]
        [InlineData(Bots.Random)]
        public void Every_member_acts_before_the_turn_ends(string bot)
        {
            BattleResult battle = Fought(bot);

            Assert.True(battle.Won);
            Assert.Equal(10, battle.Turns);
        }

        /// <summary>
        /// The trap this nearly fell into: the last member's pass ends the turn, and the next
        /// round's members are waiting again at once, so a bot that simply kept asking played a
        /// whole battle inside one turn and the runner counted it as one. It showed up in
        /// <c>cantrip sim samples/party</c> as a party losing a three-enemy fight in "1.0 turns",
        /// and the turn count above is what says the bot stops at the end of the turn it was given.
        /// </summary>
        [Fact]
        public void A_turn_that_ends_under_the_bot_is_not_played_on_into()
        {
            Assert.Equal(10, Fought(Bots.Cautious).Turns);
        }

        /// <summary>
        /// The hp column is the party's, not the leader's. The Dummy aims at the lowest hp, which
        /// is never the 40 hp leader here, so a leader-only column would read 0 lost in a fight the
        /// heroes spent ten turns taking hits in.
        /// </summary>
        [Fact]
        public void The_hp_column_counts_the_whole_party()
        {
            Assert.Equal(9, Fought(Bots.Cautious).HpLost);
        }
    }
}
