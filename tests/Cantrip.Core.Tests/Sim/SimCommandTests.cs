using System;
using System.IO;
using System.Text.RegularExpressions;
using Cantrip.Tests.Review;
using Xunit;

namespace Cantrip.Tests.Sim
{
    /// <summary>
    /// What a build server sees from <c>cantrip sim</c>. The exit code is the whole contract: 0
    /// when every run finished and every expectation held, 1 when the content is wrong or a run
    /// went wrong, 2 when the command line is.
    /// </summary>
    public sealed class SimCommandTests
    {
        private const string Slime = @"
card Zap
  cost 1
  target enemy
  effect:
    deal 6 to target

enemy Slime
  hp 20
  move Splat:
    deal 1 to player
  pattern cycle Splat
";

        private const string Clean = Slime + @"
scenario ""A winnable fight""
  runs 100
  player hp 40 energy 3
  deck 6 Zap
  battle Slime
  expect no stalls
  expect no errors
";

        /// <summary>
        /// A fight with a die in it, so two runs of it genuinely differ, and with two cards that can
        /// never be paid for and two moves behind a phase nothing reaches, so the report has lists
        /// in it that could come out in a different order from one run of the command to the next.
        /// </summary>
        private const string Chancy = @"
card Zap
  cost 1
  target enemy
  effect:
    deal 1..9 to target

card Ruin
  cost 9
  target enemy
  effect:
    deal 99 to target

card Wrath
  cost 9
  target enemy
  effect:
    deal 98 to target

enemy Slime
  hp 30
  phase Broken when hp <= 0
  move Splat:
    deal 1..8 to player
  move Wail phase Broken:
    deal 5 to player
  move Howl phase Broken:
    deal 4 to player
  pattern cycle Splat

scenario ""A fight with a die in it""
  runs 100
  player hp 60 energy 3
  deck 8 Zap, Ruin, Wrath
  battle Slime
  expect no errors
";

        [Fact]
        public void A_scenario_that_runs_clean_exits_zero()
        {
            (int exitCode, string output) = ReviewCli.Run("sim", Clean, "--runs", "5");

            Assert.True(exitCode == 0, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("nothing threw", output);
            Assert.Contains("expect no stalls", output);

            // The claim the report rests on, printed in the report rather than only in the docs:
            // what it found holds whoever plays, what it did not find is bounded by the bots. It
            // sits with the lines it is about rather than only in a footer a build server may have
            // cut off, so it has to come before the first bot's table.
            Assert.Contains("what happened at least once happened in your content", output);
            Assert.Contains("may instead be something these bots never reached", output);
            Assert.InRange(
                output.IndexOf("may instead be something these bots never reached", StringComparison.Ordinal),
                0,
                output.IndexOf("What the cautious bot did with it", StringComparison.Ordinal));

            // Two bots by default, each with a table of its own, and the warning that goes with
            // any level at all.
            Assert.Contains("What the cautious bot did with it", output);
            Assert.Contains("What the patient bot did with it", output);
            Assert.Contains("A level is a fact about the bot", output);
        }

        /// <summary>
        /// One bot is one answer, and the report says as much rather than leaving the reader to
        /// suppose that the number would have held under another bot.
        /// </summary>
        [Fact]
        public void One_bot_prints_one_table_and_says_a_second_would_disagree()
        {
            (int exitCode, string output) = ReviewCli.Run("sim", Clean, "--runs", "5", "--bot", "random");

            Assert.True(exitCode == 0, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("What the random bot did with it", output);
            Assert.DoesNotContain("What the cautious bot did with it", output);
            Assert.Contains("`--bot both` plays the", output);
            Assert.Contains("may instead be something this bot never reached", output);
        }

        /// <summary>
        /// The meter, as a build server sees it: inside the bot's block, with the numbers the
        /// engine raised and the sentence that says which side of the line they are on.
        /// </summary>
        [Fact]
        public void The_report_says_where_the_hp_went_and_whose_doing_the_plays_were()
        {
            (int exitCode, string output) = ReviewCli.Run("sim", Clean, "--runs", "5", "--bot", "cautious");

            Assert.True(exitCode == 0, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("Where the hp went, under the cautious bot", output);
            Assert.Contains("Which plays were made is this bot's doing; what each one cost is your content's.", output);

            // Zap says 6 and the Slime has 20 hp, so each run takes 6, 6, 6 and then the last 2:
            // 20 a run, 100 over five, which is the enemy's whole hp bar and nothing besides.
            Assert.Matches(@"Zap\s+100\s+100\.0%", output);
            Assert.Contains("dealt to the enemies, by what dealt it", output);

            // The Splat is the Slime's, not a card's, so it is named after the enemy that swung.
            Assert.Contains("taken by the player, by what dealt it", output);
            Assert.Matches(@"Slime\s+\d+\s+100\.0%", output);

            Assert.Contains("also counted, over the same plays", output);
            Assert.Contains("cards", output);
        }

        [Fact]
        public void A_bot_nothing_is_called_is_bad_usage()
        {
            (int exitCode, string output) = ReviewCli.Run("sim", Clean, "--runs", "5", "--bot", "clever");

            Assert.True(exitCode == 2, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("--bot takes cautious, patient, random or both", output);
        }

        [Fact]
        public void A_battle_that_reaches_the_turn_limit_exits_one()
        {
            (int exitCode, string output) = ReviewCli.Run("sim", Clean, "--runs", "2", "--turn-limit", "1");

            Assert.True(exitCode == 1, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("reached the turn limit of 1", output);
        }

        /// <summary>
        /// A scenario that names an enemy nothing defines fails in milliseconds, before a single
        /// run, because <c>sim</c> reports the linter's errors as <c>validate</c> does.
        /// </summary>
        [Fact]
        public void A_misspelt_name_fails_before_anything_is_played()
        {
            string dsl = Slime + @"
scenario ""A typo""
  runs 100
  player hp 40
  battle Slim
";

            (int exitCode, string output) = ReviewCli.Run("sim", dsl, "--runs", "2");
            Assert.True(exitCode == 1, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("CT302", output);
            Assert.DoesNotContain("run(s)", output);

            // Suppressed, the same mistake becomes a run that threw, with the seed to replay it.
            // Both bots play, and both of them throw on the same line: four runs, not two.
            (int suppressed, string report) = ReviewCli.Run("sim", dsl, "--runs", "2", "--suppress", "CT302");
            Assert.True(suppressed == 1, $"exit code {suppressed}:\n{ReviewCli.Head(report)}");
            Assert.Contains("4 run(s) threw", report);
            Assert.Contains("battle Slim", report);

            // The seed is the point: without it the report names a failure nobody can reproduce.
            Assert.Contains("seed 1 ", report);
        }

        /// <summary>
        /// Run the same command over the same content twice and the report is the same, down to the
        /// byte, but for the one field that is a clock reading. An ordering that fell out of a hash
        /// set, or a fact collected in a different order from one pass to the next, would show here.
        /// </summary>
        [Fact]
        public void The_same_command_prints_the_same_report_twice()
        {
            string folder = Path.Combine(Path.GetTempPath(), "ge-sim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, "content.cantrip"), Chancy);

                (int first, string one) = ReviewCli.RunIn(folder, "sim", "--runs", "20");
                (int again, string two) = ReviewCli.RunIn(folder, "sim", "--runs", "20");

                Assert.True(first == 0, $"exit code {first}:\n{ReviewCli.Head(one)}");
                Assert.True(again == 0, $"exit code {again}:\n{ReviewCli.Head(two)}");
                Assert.Equal(WithoutTheClock(one), WithoutTheClock(two));

                // Guards, so this cannot pass for want of anything to disagree about: the runs
                // differ from each other, and the report holds two lists that are put in order by
                // name rather than left in the order a hash set happened to give them.
                Assert.DoesNotContain("every run came out the same way", one);
                Assert.Contains("never playable: Ruin, Wrath", one);
                Assert.Contains("Slime: Howl, Wail", one);

                // The meter's tables are in there too, and they are the ones most likely to come
                // out in a different order twice: they are built from dictionaries.
                Assert.Contains("Where the hp went", one);
                Assert.Contains("dealt to the enemies, by what dealt it", one);
            }
            finally
            {
                try { Directory.Delete(folder, true); }
                catch (Exception) { /* best effort cleanup of a temp folder */ }
            }
        }

        /// <summary>
        /// How long the runs took is the one thing in the report that is not a measurement of the
        /// content, and the one thing that moves between two identical commands.
        /// </summary>
        private static string WithoutTheClock(string report) => Regex.Replace(report, @"\d+\.\d+s", "<time>");

        [Fact]
        public void A_folder_with_no_scenarios_says_so()
        {
            (int exitCode, string output) = ReviewCli.Run("sim", Slime);

            Assert.True(exitCode == 1, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("no `scenario` blocks are loaded", output);
        }

        [Theory]
        [InlineData("--runs", "0")]
        [InlineData("--runs", "many")]
        [InlineData("--turn-limit", "0")]
        [InlineData("--seed", "-1")]
        [InlineData("--runs", "5000000000")] // refused, not quietly cut down to a count it can hold
        public void A_number_that_is_not_one_is_bad_usage(string option, string value)
        {
            (int exitCode, string output) = ReviewCli.Run("sim", Clean, option, value);

            Assert.True(exitCode == 2, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains(option + " takes a whole number", output);
        }

        /// <summary>
        /// The same treatment <c>--warnings-as-errors</c> gets outside <c>lint</c>: an option that
        /// would do nothing here is refused rather than quietly accepted.
        /// </summary>
        [Fact]
        public void A_sim_option_given_to_another_command_is_bad_usage()
        {
            (int exitCode, string output) = ReviewCli.Run("test", Clean, "--runs", "5");

            Assert.True(exitCode == 2, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("only applies to `sim`", output);
        }

        [Fact]
        public void Watching_a_seed_prints_the_run_turn_by_turn()
        {
            (int exitCode, string output) = ReviewCli.Run("sim", Clean, "--watch", "3");

            Assert.True(exitCode == 0, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("seed 3", output);
            Assert.Contains("turn 1", output);
            Assert.Contains("Zap -> Slime", output);
            Assert.Contains("deck 6 Zap", output);
        }

        [Fact]
        public void Watching_needs_one_scenario_to_watch()
        {
            string two = Clean + @"
scenario ""Another fight""
  runs 100
  player hp 40 energy 3
  deck 6 Zap
  battle Slime
";

            (int exitCode, string output) = ReviewCli.Run("sim", two, "--watch", "3");
            Assert.True(exitCode == 2, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("--name", output);

            (int narrowed, string watched) = ReviewCli.Run("sim", two, "--watch", "3", "--name", "Another");
            Assert.True(narrowed == 0, $"exit code {narrowed}:\n{ReviewCli.Head(watched)}");
            Assert.Contains("Another fight, seed 3", watched);
        }

        /// <summary>
        /// A level is a fact about the bot, so `expect wins >= 55%` is not a thing the content can
        /// be held to: the report quotes what each bot reached, under a heading that says not to
        /// quote it, and leaves the expectation unchecked rather than answering it with a number.
        /// </summary>
        [Fact]
        public void A_measurement_that_only_the_bot_decides_is_reported_unchecked()
        {
            string dsl = Slime + @"
scenario ""Levels""
  runs 100
  player hp 40 energy 3
  deck 6 Zap
  battle Slime
  expect wins >= 55%
";

            (int exitCode, string output) = ReviewCli.Run("sim", dsl, "--runs", "5");

            Assert.True(exitCode == 0, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("not checked", output);
            Assert.Contains("a level is a fact about the bot", output);

            // The level is printed, once per bot, and never without the warning beside it.
            Assert.Contains("Levels, for reference only", output);
            Assert.Contains("cautious bot", output);
            Assert.Contains("patient bot", output);
            Assert.Contains("do not quote one", output);
        }
    }
}
