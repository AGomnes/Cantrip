using System;
using Cantrip.Content;
using Cantrip.Sim;
using Xunit;

namespace Cantrip.Tests.Sim
{
    /// <summary>
    /// <c>cantrip sim</c> refuses content written for a tick clock, rather than playing it wrongly.
    /// </summary>
    /// <remarks>
    /// It used to load a <c>clock ticks</c> folder without a murmur and play it by calling
    /// <c>EndTurn</c> fifty times. The clock was a <c>TickClock</c> and nothing ever ticked it, so
    /// <c>Now</c> stayed at 0 for the whole run: every <c>on every</c> listener was silent, every
    /// ability used once never came back, and no battle could end. The report was printed in full
    /// and read as a finding about the content: "40 battles reached the turn limit and never
    /// ended", 50.0 turns in a game with no turns, and 0.0 hp lost against enemies dealing
    /// seventeen damage every three seconds: at exit 0. A tool that quietly gets the answer wrong
    /// is worse than one that refuses.
    /// </remarks>
    public sealed class RealTimeRefusalTests
    {
        private const string TickGame = """
            ruleset
              clock ticks

            enemy "Hollow"
              hp 16
              on every 2s:
                deal 5 to player

            card "Zap"
              cost 1
              target enemy
              effect:
                deal 4 to target

            scenario "The hold"
              runs 100
              deck 10 Zap
              player hp 80 energy 3
              battle Hollow
              expect no errors
            """;

        [Fact]
        [Trait("Regression", "sim-plays-a-tick-game-with-endturn")]
        public void Running_a_scenario_from_tick_content_is_refused()
        {
            ContentLibrary content = ScenarioRunnerTests.Load(TickGame);
            var runner = new ScenarioRunner(content, new ScenarioOptions { Runs = 2 });

            InvalidOperationException error =
                Assert.Throws<InvalidOperationException>(() => runner.Run(content.Scenarios[0]));

            Assert.Contains("`clock ticks`", error.Message);
            Assert.Contains("frame loop", error.Message);
            Assert.Contains("`test` blocks", error.Message);
        }

        [Fact]
        [Trait("Regression", "sim-plays-a-tick-game-with-endturn")]
        public void And_so_is_replaying_one_for_watch()
        {
            ContentLibrary content = ScenarioRunnerTests.Load(TickGame);
            var runner = new ScenarioRunner(content, new ScenarioOptions { Runs = 1 });

            Assert.Throws<InvalidOperationException>(
                () => runner.Replay(content.Scenarios[0], 1, _ => { }));
        }

        /// <summary>A turn game is untouched, which is every scenario there has ever been.</summary>
        [Fact]
        public void A_turn_game_plays_as_it_always_did()
        {
            ScenarioOutcome outcome = ScenarioRunnerTests.Play("""
                ruleset
                  clock turns

                enemy "Hollow"
                  hp 16
                  move "Swing":
                    deal 5 to player

                card "Zap"
                  cost 1
                  target enemy
                  effect:
                    deal 4 to target

                scenario "The gauntlet"
                  runs 5
                  deck 10 Zap
                  player hp 80 energy 3
                  battle Hollow
                  expect no errors
                """, new ScenarioOptions { Runs = 5 });

            Assert.NotEmpty(outcome.ByBot);
        }
    }
}
