using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cantrip.Content;
using Cantrip.Sim.Scenarios;
using Xunit;

namespace Cantrip.Tests.Sim
{
    /// <summary>
    /// What <c>cantrip sim</c> may be believed about: the same seed plays the same run, a run that
    /// throws is counted rather than thrown, a battle that never ends is a stall rather than a
    /// hang, and content with no randomness in it is reported as such instead of being measured
    /// a hundred times over.
    /// </summary>
    public sealed class ScenarioRunnerTests
    {
        /// <summary>
        /// A fight with a die in it: the Slime hits for a random amount, so two seeds part company.
        /// </summary>
        private const string Chancy = @"
card Zap
  cost 1
  target enemy
  effect:
    deal 6 to target

enemy Slime
  hp 30
  move Splat:
    deal 1..8 to player
  pattern cycle Splat

scenario ""A fight with a die in it""
  runs 4
  player hp 60 energy 3
  deck 6 Zap
  battle Slime
  expect no stalls
  expect no errors
";

        [Fact]
        public void The_same_seed_plays_the_same_run()
        {
            ScenarioResult first = Play(Chancy, new ScenarioOptions { FirstSeed = 12, Runs = 6 });
            ScenarioResult again = Play(Chancy, new ScenarioOptions { FirstSeed = 12, Runs = 6 });

            Assert.Equal(Transcript(first), Transcript(again));
        }

        [Fact]
        public void A_different_first_seed_plays_different_runs()
        {
            ScenarioResult first = Play(Chancy, new ScenarioOptions { FirstSeed = 12, Runs = 6 });
            ScenarioResult other = Play(Chancy, new ScenarioOptions { FirstSeed = 500, Runs = 6 });

            Assert.NotEqual(Transcript(first), Transcript(other));
        }

        [Fact]
        public void Watching_a_seed_replays_the_run_the_report_counted()
        {
            var options = new ScenarioOptions { FirstSeed = 40, Runs = 3 };
            ContentLibrary content = Load(Chancy);
            var runner = new ScenarioRunner(content, options);
            ScenarioResult result = runner.Run(content.Scenarios[0]);

            var lines = new List<string>();
            RunResult watched = runner.Replay(content.Scenarios[0], 41, lines.Add);

            Assert.Equal(result.Runs[1].Seed, watched.Seed);
            Assert.Equal(result.Runs[1].HpLeft, watched.HpLeft);
            Assert.Equal(result.Runs[1].Battles[0].Turns, watched.Battles[0].Turns);
            Assert.Contains(lines, line => line.Contains("turn 1"));
            Assert.Contains(lines, line => line.Contains("deck 6 Zap"));
        }

        /// <summary>
        /// The linter reports a misspelt enemy as CT302, but <c>--suppress</c> can switch that off
        /// and a game's own verbs can throw for reasons no check foresees. Either way the run is
        /// counted and the next one starts.
        /// </summary>
        [Fact]
        public void A_run_that_throws_is_counted_rather_than_thrown()
        {
            ScenarioResult result = Play(@"
enemy Slime
  hp 10
  move Splat:
    deal 1 to player
  pattern cycle Splat

scenario ""A typo""
  runs 3
  player hp 20
  battle Slim
", new ScenarioOptions());

            Assert.Equal(3, result.Errors);
            Assert.All(result.Runs, run => Assert.Contains("No enemy named `Slim` is defined", run.Error));
            Assert.All(result.Runs, run => Assert.Equal("battle Slim", run.Statement));

            // Each failure is kept with the seed that produced it, which is what makes it
            // reproducible: `--watch 2` plays the second of these back.
            Assert.Equal(new ulong[] { 1, 2, 3 }, result.Runs.Select(run => run.Seed));
            Assert.All(result.Runs, run => Assert.Equal(11, run.At.Line));
            Assert.True(result.Failed);
        }

        [Fact]
        public void A_battle_that_never_ends_is_a_stall_and_not_a_hang()
        {
            ScenarioResult result = Play(@"
enemy Wall
  hp 500
  move Wait:
    block 1
  pattern cycle Wait

scenario ""A wall""
  runs 2
  player hp 50
  battle Wall
  expect no stalls
", new ScenarioOptions { TurnLimit = 6 });

            Assert.Equal(2, result.Stalls);
            Assert.All(result.Runs, run => Assert.True(run.Stalled));
            Assert.All(result.Runs, run => Assert.Equal(6, run.Battles[0].Turns));
            Assert.Equal(0, result.Errors);
            Assert.Contains(result.Expectations, e => e.Text == "expect no stalls" && e.Held == false);
        }

        /// <summary>
        /// A run that ends in a loss stops there: the statements after it would be played by a dead
        /// player, and the battles after it were never reached.
        /// </summary>
        [Fact]
        public void A_lost_battle_ends_the_run_where_it_stands()
        {
            ScenarioResult result = Play(@"
enemy Hammer
  hp 100
  move Smash:
    deal 50 to player
  pattern cycle Smash

scenario ""Two fights, one survivable""
  runs 2
  player hp 10
  battle Hammer
  battle Hammer
", new ScenarioOptions());

            Assert.All(result.Runs, run => Assert.Single(run.Battles));
            Assert.All(result.Runs, run => Assert.False(run.Won));
            Assert.Equal(0, result.Errors);
            Assert.Equal(0, result.Stalls);
        }

        /// <summary>
        /// samples/abilities is a fight with no cards and no dice: the Ghast cycles its moves, so
        /// every run goes the same way. Two hundred runs of it say exactly what one says, and the
        /// runner has to know that rather than offer the repetition as evidence.
        /// </summary>
        [Fact]
        public void Content_with_no_randomness_in_it_plays_the_same_run_every_time()
        {
            ContentLibrary content = new ContentLibrary();
            content.LoadFolder(Path.Combine(RepositoryRoot(), "samples", "abilities"));
            content.Diagnostics.ThrowIfErrors();

            ScenarioDefinition scenario = Assert.Single(content.Scenarios);
            ScenarioResult result = new ScenarioRunner(content, new ScenarioOptions { Runs = 20 }).Run(scenario);

            Assert.True(result.EveryRunIdentical);
            Assert.Equal(0, result.Errors);
            Assert.Equal(0, result.Stalls);
            Assert.All(result.Runs, run => Assert.True(run.Won));
        }

        /// <summary>
        /// A fight with no cards is still a fight. Without abilities among a turn's options the bot
        /// would pass every turn and every run would be a stall.
        /// </summary>
        [Fact]
        public void A_bot_uses_abilities_where_there_are_no_cards()
        {
            ScenarioResult result = Play(@"
ability Smite
  cooldown 1 turns
  target enemy
  effect:
    deal 9 to target

enemy Idol
  hp 30
  move Stare:
    block 1
  pattern cycle Stare

scenario ""No cards anywhere""
  runs 2
  player hp 40
  grant Smite
  battle Idol
", new ScenarioOptions());

            Assert.Equal(0, result.Stalls);
            Assert.All(result.Runs, run => Assert.True(run.Won));
            Assert.Equal(0, result.Facts.CardCount);
        }

        /// <summary>
        /// The facts the report leads with. A move behind a phase the fight never reaches, and a
        /// card whose cost can never be paid, are things about the content that hold whoever plays.
        /// </summary>
        [Fact]
        public void A_move_that_never_fires_and_a_card_that_is_never_playable_are_both_reported()
        {
            ScenarioResult result = Play(@"
card Zap
  cost 1
  target enemy
  effect:
    deal 6 to target

card Ruinous
  cost 9
  target enemy
  effect:
    deal 99 to target

enemy Idol
  hp 40
  phase Broken when hp <= 0
  move Stare:
    block 1
  move Wail phase Broken:
    deal 5 to player
  pattern cycle Stare

scenario ""What never happens""
  runs 2
  player hp 40 energy 3
  deck 4 Zap, Ruinous
  battle Idol
", new ScenarioOptions());

            Assert.Equal(new[] { "Ruinous" }, result.Facts.NeverPlayable);
            Assert.Contains(("Idol", "Wail"), result.Facts.NeverFired);
            Assert.DoesNotContain(("Idol", "Stare"), result.Facts.NeverFired);
        }

        [Fact]
        public void A_scenario_plays_as_many_runs_as_it_asks_for_unless_told_otherwise()
        {
            ContentLibrary content = Load(Chancy);
            ScenarioDefinition scenario = content.Scenarios[0];

            Assert.Equal(4, new ScenarioRunner(content, new ScenarioOptions()).RunCount(scenario));
            Assert.Equal(9, new ScenarioRunner(content, new ScenarioOptions { Runs = 9 }).RunCount(scenario));
        }

        /// <summary>
        /// <c>enemy</c> and <c>realtime</c> belong to a test. The linter reports both as CT301, but
        /// the runner has to refuse them too, because <c>--suppress</c> can switch the linter off.
        /// </summary>
        [Theory]
        [InlineData("enemy hp 10", "battle")]
        [InlineData("realtime 60", "turn-based")]
        public void A_line_a_scenario_cannot_have_is_refused_by_the_runner(string line, string says)
        {
            ScenarioResult result = Play(@"
enemy Slime
  hp 10
  move Splat:
    deal 1 to player
  pattern cycle Splat

scenario ""Wrong block""
  runs 2
  player hp 20
  " + line + @"
  battle Slime
", new ScenarioOptions());

            Assert.Equal(2, result.Errors);
            Assert.All(result.Runs, run => Assert.Contains(says, run.Error));
        }

        private static ScenarioResult Play(string dsl, ScenarioOptions options)
        {
            ContentLibrary content = Load(dsl);
            return new ScenarioRunner(content, options).Run(content.Scenarios[0]);
        }

        private static ContentLibrary Load(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl);
            content.Diagnostics.ThrowIfErrors();
            return content;
        }

        /// <summary>Everything a run came to, as text, so two runs can be compared whole.</summary>
        private static string Transcript(ScenarioResult result) =>
            string.Join("\n", result.Runs.Select(run =>
                $"{run.Seed} {run.Won} {run.HpLeft} {run.Error} " +
                string.Join(",", run.Battles.Select(b => $"{b.Label}:{b.Won}:{b.Turns}:{b.HpLost}"))));

        private static string RepositoryRoot()
        {
            for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Cantrip.sln"))) return directory.FullName;
            }
            throw new InvalidOperationException("Could not find the repository root.");
        }
    }
}
