using System.Linq;
using Cantrip.Content;
using Cantrip.Sim;
using Cantrip.Tests.Review;
using Xunit;

namespace Cantrip.Tests.Sim
{
    /// <summary>
    /// <c>cantrip sim --against</c>: one scenario played twice, by the same bots, from the same
    /// seeds, over two versions of the content.
    /// </summary>
    /// <remarks>
    /// The thing being tested is not that two numbers differ, two runs of <c>sim</c> already
    /// gave that, but that the difference is <em>paired</em>: which seeds changed hands, and
    /// which way. A level is a fact about the bot and cannot be quoted; the count of seeds that
    /// came out differently under one bot, holding the seeds still, is a fact about the change.
    /// </remarks>
    public sealed class ComparisonTests
    {
        /// <summary>
        /// A fight on a knife edge and nothing else: one card, one enemy, one move, no dice. At 6
        /// damage the party is two turns short and loses every seed; at 9 it wins every seed. The
        /// numbers are chosen so that the difference is the card and not the shuffle, which is
        /// what makes the paired counts assertable rather than approximately right.
        /// </summary>
        private const string Fight = @"
card Zap
  cost 1
  target enemy
  effect:
    deal {0} to target

enemy Slime
  hp 108
  move Splat:
    deal 8 to player
  pattern cycle Splat

scenario ""A close fight""
  runs 40
  player hp 38 energy 3
  deck 30 Zap
  battle Slime
";

        private static string Deck(int damage) => Fight.Replace("{0}", damage.ToString());

        private static ScenarioOutcome Play(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl);
            content.Diagnostics.ThrowIfErrors();

            var options = new ScenarioOptions { Runs = 40 };
            options.MakeBots.Clear();
            options.MakeBots.Add(Bots.Make(Bots.Cautious));

            return new ScenarioRunner(content, options).Run(content.Scenarios[0]);
        }

        /// <summary>
        /// A stronger card wins more of the same seeds, and every seed it wins was a seed the
        /// weaker one lost. Nothing goes the other way, which is the shape the paired counts exist
        /// to show and the shape two percentages cannot.
        /// </summary>
        [Fact]
        public void A_strictly_better_card_only_ever_gains_seeds()
        {
            ScenarioComparison comparison = Comparisons.Of(Play(Deck(6)), Play(Deck(9)));

            BotComparison bot = Assert.Single(comparison.ByBot);
            Assert.Equal(Bots.Cautious, bot.Bot);
            Assert.Equal(40, bot.Paired);
            Assert.Equal(0, bot.OnlyBefore);
            Assert.Equal(0, bot.OnlyAfter);

            Assert.True(bot.Gained > 0, "a card that deals half as much again has to win seeds the weaker one lost");
            Assert.Equal(0, bot.Lost);
            Assert.Equal(bot.WonBefore + bot.Gained, bot.WonAfter);
            Assert.Equal(bot.Paired - bot.Changed, bot.Unchanged);
            Assert.False(comparison.Identical);
        }

        /// <summary>Content compared with itself moves nothing at all, which is the null result.</summary>
        [Fact]
        public void The_same_content_compares_as_identical()
        {
            ScenarioComparison comparison = Comparisons.Of(Play(Deck(6)), Play(Deck(6)));

            BotComparison bot = Assert.Single(comparison.ByBot);
            Assert.Equal(0, bot.Changed);
            Assert.Equal(bot.WonBefore, bot.WonAfter);
            Assert.Equal(bot.TurnsBefore, bot.TurnsAfter);
            Assert.Equal(1, bot.Coincidence);
            Assert.True(comparison.Identical);
        }

        /// <summary>
        /// A card that can never be paid for is a fact about the content, so it shows up in the
        /// comparison's own block rather than in a bot's numbers.
        /// </summary>
        [Fact]
        public void A_card_that_stops_being_playable_is_reported_as_a_fact()
        {
            const string affordable = @"
card Zap
  cost 1
  target enemy
  effect:
    deal 6 to target

card Ruin
  cost {0}
  target enemy
  effect:
    deal 20 to target

enemy Slime
  hp 40
  move Splat:
    deal 2 to player
  pattern cycle Splat

scenario ""A fight""
  runs 10
  player hp 40 energy 2
  deck 6 Zap, 2 Ruin
  battle Slime
";

            ScenarioComparison comparison = Comparisons.Of(
                Play(affordable.Replace("{0}", "2")),
                Play(affordable.Replace("{0}", "9")));

            Assert.Equal(new[] { "Ruin" }, comparison.StoppedBeingPlayable);
            Assert.Empty(comparison.BecamePlayable);

            ScenarioComparison back = Comparisons.Of(
                Play(affordable.Replace("{0}", "9")),
                Play(affordable.Replace("{0}", "2")));

            Assert.Equal(new[] { "Ruin" }, back.BecamePlayable);
            Assert.Empty(back.StoppedBeingPlayable);
        }

        /// <summary>
        /// The only statistic in the tool, pinned to arithmetic that can be done by hand: twelve
        /// coins landing the same way is 2 / 2^12, which is once in 2,048.
        /// </summary>
        [Theory]
        [InlineData(0, 0, 1.0)]
        [InlineData(1, 1, 1.0)]
        [InlineData(2, 2, 0.5)]
        [InlineData(12, 12, 2.0 / 4096)]
        [InlineData(10, 5, 1.0)]
        public void The_coin_is_the_exact_two_sided_tail(int changed, int gained, double expected)
        {
            Assert.Equal(expected, BotComparison.Coin(changed, gained), 12);
        }

        /// <summary>A thousand tosses does not overflow, underflow or exceed one.</summary>
        [Fact]
        public void The_coin_holds_up_at_a_thousand_seeds()
        {
            Assert.Equal(1.0, BotComparison.Coin(1000, 500), 6);
            Assert.True(BotComparison.Coin(1000, 560) < 0.001);
            Assert.True(BotComparison.Coin(1000, 1000) > 0);
        }

        // The command ----------------------------------------------------------------------------

        [Fact]
        public void The_command_pairs_two_folders_by_scenario_name()
        {
            using var weaker = new Folder(Deck(6));
            using var stronger = new Folder(Deck(9));

            (int exitCode, string output) = ReviewCli.RunIn(stronger.Path, "sim", "--runs", "40", "--against", weaker.Path);

            Assert.True(exitCode == 0, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("Same bots, same seeds, same turn limit; only the content differs.", output);
            Assert.Contains("seeds that changed hands", output);
            Assert.Contains("lost before, won now", output);
            Assert.Contains("a level, for reference only", output);
            Assert.Contains("1 scenario(s) compared.", output);
            Assert.Contains("A comparison never changes the exit code", output);
        }

        /// <summary>
        /// Two decks stated as two scenarios in one folder, which is the other question this
        /// answers and the one that needs both name options to say which is which.
        /// </summary>
        [Fact]
        public void Two_scenarios_pair_one_to_one_when_each_side_is_narrowed_to_one()
        {
            const string twoDecks = @"
card Zap
  cost 1
  target enemy
  effect:
    deal 6 to target

card Bolt
  cost 1
  target enemy
  effect:
    deal 9 to target

enemy Slime
  hp 24
  move Splat:
    deal 5 to player
  pattern cycle Splat

scenario ""Wide""
  runs 20
  player hp 22 energy 1
  deck 8 Zap
  battle Slime

scenario ""Tall""
  runs 20
  player hp 22 energy 1
  deck 8 Bolt
  battle Slime
";

            using var folder = new Folder(twoDecks);

            (int exitCode, string output) = ReviewCli.RunIn(
                folder.Path, "sim", "--runs", "20", "--name", "Tall", "--against", folder.Path, "--against-name", "Wide");

            Assert.True(exitCode == 0, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("Tall  vs  Wide", output);
            Assert.Contains("seeds that changed hands", output);
        }

        [Fact]
        public void Comparing_content_with_itself_says_nothing_moved()
        {
            using var folder = new Folder(Deck(6));

            (int exitCode, string output) = ReviewCli.RunIn(folder.Path, "sim", "--runs", "10", "--against", folder.Path);

            Assert.True(exitCode == 0, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("Nothing moved", output);
        }

        [Fact]
        public void A_baseline_that_does_not_load_is_refused_before_anything_is_played()
        {
            using var subject = new Folder(Deck(6));
            using var broken = new Folder("scenario \"A fight\"\n  runs 5\n  player hp 10\n  battle Nothing\n");

            (int exitCode, string output) = ReviewCli.RunIn(subject.Path, "sim", "--runs", "5", "--against", broken.Path);

            Assert.Equal(1, exitCode);
            Assert.Contains("has errors, so nothing can be compared with it", output);
        }

        [Fact]
        public void Against_and_watch_are_refused_together()
        {
            using var subject = new Folder(Deck(6));
            using var other = new Folder(Deck(9));

            (int exitCode, string output) = ReviewCli.RunIn(subject.Path, "sim", "--watch", "1", "--against", other.Path);

            Assert.Equal(2, exitCode);
            Assert.Contains("Use one or the other", output);
        }

        [Fact]
        public void Against_name_without_against_is_refused()
        {
            using var subject = new Folder(Deck(6));

            (int exitCode, string output) = ReviewCli.RunIn(subject.Path, "sim", "--against-name", "Whatever");

            Assert.Equal(2, exitCode);
            Assert.Contains("there is no --against to look in", output);
        }

        [Fact]
        public void Against_only_applies_to_sim()
        {
            using var subject = new Folder(Deck(6));
            using var other = new Folder(Deck(9));

            (int exitCode, string output) = ReviewCli.RunIn(subject.Path, "lint", "--against", other.Path);

            Assert.Equal(2, exitCode);
            Assert.Contains("--against", output);
            Assert.Contains("only applies to `sim`", output);
        }

        private sealed class Folder : System.IDisposable
        {
            public Folder(string dsl)
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "ge-compare-" + System.Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(Path);
                System.IO.File.WriteAllText(System.IO.Path.Combine(Path, "content.cantrip"), dsl);
            }

            public string Path { get; }

            public void Dispose()
            {
                try { System.IO.Directory.Delete(Path, true); }
                catch (System.Exception) { /* best effort cleanup of a temp folder */ }
            }
        }
    }
}
