using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// What <c>lint</c> makes of a <c>scenario</c>. A scenario names cards, relics, abilities and
    /// enemies the way a test does, and is checked with the same codes; on top of that it has three
    /// lines nothing else has, and three codes of its own: CT317 a scenario that fights nothing,
    /// CT318 a run count, CT319 an <c>expect</c> a scenario cannot check.
    /// </summary>
    public sealed class ScenarioLintTests
    {
        private const string Content = """
            card Zap
              cost 1
              target enemy
              effect:
                deal 6 to target

            card Ward
              cost 1
              effect:
                block 5

            status Weak
              stacking duration

            relic Anchor
              on turn_start once per battle:
                block 10

            ability Surge
              cooldown 2 turns
              effect:
                deal 1 to all enemies

            enemy Archmage
              hp 30
              move Bolt:
                deal 5 to player

            """;

        private static IReadOnlyList<Diagnostic> Lint(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(Content + dsl, "scenario.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            return Linter.Lint(content);
        }

        private static Diagnostic Single(IReadOnlyList<Diagnostic> diagnostics, string code)
        {
            var matching = diagnostics.Where(d => d.Code == code).ToList();
            Assert.True(matching.Count == 1, $"expected one {code}, got:\n{string.Join("\n", diagnostics)}");
            return matching[0];
        }

        // A scenario that says what it means -----------------------------------------------------

        [Fact]
        public void A_whole_scenario_lints_clean()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                scenario "The tower, starter deck"
                  runs 500

                  player hp 60 energy 3
                  deck 4 Zap, 4 Ward
                  relic Anchor
                  grant Surge
                  seed 7

                  battle Archmage
                  heal 12
                  apply Weak 1 to player
                  battle Archmage, Archmage

                  expect no stalls
                  expect no errors
                  expect wins >= 55%
                  expect turns <= 12
                """);

            Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics));
        }

        /// <summary>
        /// The count in <c>deck 4 Zap, 4 Ward</c> is a repetition, not a name: the linter reads past
        /// it to both cards, in a scenario and in a test alike.
        /// </summary>
        [Fact]
        public void A_leading_count_does_not_hide_the_cards_it_repeats()
        {
            Diagnostic error = Single(Lint("""
                scenario "Counted"
                  deck 4 Zap, 4 Wrad
                  battle Archmage
                """), Linter.UnknownName);

            Assert.Contains("`Wrad`", error.Message);
            Assert.Equal("Ward", error.Suggestion);
        }

        // CT302: the names a scenario gets wrong -------------------------------------------------

        [Theory]
        [InlineData("  deck Zapp\n", "Zapp", "Zap")]
        [InlineData("  hand \"Wrad\"\n", "Wrad", "Ward")]
        [InlineData("  relic Anchr\n", "Anchr", "Anchor")]
        [InlineData("  grant Serge\n", "Serge", "Surge")]
        [InlineData("  battle Archmag\n", "Archmag", "Archmage")]
        [InlineData("  battle Archmage, Archmag\n", "archmag", "Archmage")]
        public void A_misspelt_definition_in_a_scenario_is_the_same_error_as_in_a_test(string line, string written, string nearest)
        {
            Diagnostic error = Single(Lint("scenario \"Slip\"\n  battle Archmage\n" + line), Linter.UnknownName);

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains($"`{written}`", error.Message);
            Assert.Equal(nearest, error.Suggestion);
        }

        // CT301: a verb written in the wrong kind of block ---------------------------------------

        [Theory]
        [InlineData("play Zap", null)]
        [InlineData("cast Surge", null)]
        [InlineData("end turn", null)]
        [InlineData("tick 3", null)]
        [InlineData("realtime 30", null)]
        [InlineData("enemy Archmage hp 30", "battle")]
        public void A_test_verb_in_a_scenario_says_where_it_belongs(string line, string? suggestion)
        {
            Diagnostic error = Single(Lint($"scenario \"Slip\"\n  battle Archmage\n  {line}\n"), Linter.UnknownVerb);

            Assert.Contains("It only exists inside `test` blocks.", error.Message);
            Assert.Contains("A `scenario` states the fight; a bot plays it.", error.Message);

            // The message has already said where the verb lives, so there is no spelling guess to
            // argue with it: `play` is not a misspelt `replay`, and `tick` is not a misspelt `deck`.
            Assert.Equal(suggestion, error.Suggestion);
        }

        [Theory]
        [InlineData("test \"Slip\"\n  enemy Archmage\n  battle Archmage\n")]
        [InlineData("card Trap\n  cost 0\n  effect:\n    runs 500\n")]
        public void A_scenario_verb_anywhere_else_says_where_it_belongs(string dsl)
        {
            Diagnostic error = Single(Lint(dsl), Linter.UnknownVerb);

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("It only exists inside `scenario` blocks.", error.Message);
        }

        // CT317: a scenario that fights nothing --------------------------------------------------

        [Fact]
        public void A_scenario_with_no_battle_fights_nothing()
        {
            Diagnostic warning = Single(Lint("""
                scenario "All setup, no fight"
                  runs 500
                  deck 4 Zap
                """), Linter.NothingToFight);

            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.Contains("All setup, no fight", warning.Message);
            Assert.Contains("no `battle` line", warning.Message);
            Assert.Equal("battle \"Name\"", warning.Suggestion);
        }

        [Fact]
        public void A_battle_line_with_no_enemy_fights_nothing()
        {
            Diagnostic warning = Single(Lint("""
                scenario "Empty fight"
                  battle Archmage
                  battle
                """), Linter.NothingToFight);

            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.Contains("`battle` names no enemy", warning.Message);
        }

        // CT318: the run count -------------------------------------------------------------------

        [Fact]
        public void A_small_run_count_is_a_note()
        {
            Diagnostic note = Single(Lint("scenario \"Few\"\n  runs 40\n  battle Archmage\n"), Linter.RunCount);

            Assert.Equal(DiagnosticSeverity.Info, note.Severity);
            Assert.Contains("40 runs", note.Message);
            Assert.Contains("100 or more", note.Message);
        }

        [Theory]
        [InlineData("runs 0")]
        [InlineData("runs")]
        [InlineData("runs 2.5")]
        [InlineData("runs 500 turns")]
        public void A_run_count_that_is_not_a_whole_number_of_runs_is_an_error(string line)
        {
            Diagnostic error = Single(Lint($"scenario \"Broken\"\n  {line}\n  battle Archmage\n"), Linter.RunCount);

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Equal("runs 500", error.Suggestion);
        }

        [Fact]
        public void A_run_count_of_a_hundred_or_more_says_nothing()
        {
            Assert.Empty(Lint("scenario \"Enough\"\n  runs 100\n  battle Archmage\n"));
        }

        // CT319: what a scenario can be asked to measure -----------------------------------------

        [Theory]
        [InlineData("stalls")]
        [InlineData("errors")]
        [InlineData("wins")]
        [InlineData("hp_left")]
        [InlineData("turns")]
        public void Every_measurement_is_accepted_both_ways_round(string metric)
        {
            Assert.Empty(Lint($"scenario \"Measured\"\n  runs 500\n  battle Archmage\n  expect no {metric}\n  expect {metric} >= 1\n"));
        }

        [Fact]
        public void A_misspelt_measurement_suggests_the_nearest_one()
        {
            Diagnostic error = Single(Lint("scenario \"Slip\"\n  battle Archmage\n  expect no stals\n"), Linter.UnknownMeasurement);

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("does not measure `stals`", error.Message);
            Assert.Equal("stalls", error.Suggestion);
        }

        /// <summary>
        /// The slip a test writer will make first: a scenario plays hundreds of games, so there is
        /// no one <c>enemy.hp</c> for it to compare.
        /// </summary>
        [Fact]
        public void A_condition_about_one_game_is_not_something_a_scenario_measures()
        {
            Diagnostic error = Single(Lint("scenario \"Slip\"\n  battle Archmage\n  expect enemy.hp == 3\n"), Linter.UnknownMeasurement);

            Assert.Contains("`enemy.hp`", error.Message);
            Assert.Contains("stalls, errors, wins, hp_left, turns", error.Message);
        }

        [Theory]
        [InlineData("expect wins", "checks one measurement over every run")]
        [InlineData("expect turns <= 12 seconds", "a plain number or a percentage")]
        public void An_expect_a_scenario_cannot_read_says_what_it_takes(string line, string expected)
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint($"scenario \"Slip\"\n  battle Archmage\n  {line}\n");
            Diagnostic error = Single(diagnostics, Linter.UnknownMeasurement);

            Assert.Contains(expected, error.Message);

            // One complaint, not two. CT319 has said why the line is wrong, so CT302 must not go on
            // to say that `wins` is not a stat the rules know: a scenario's `expect` never reads it
            // as one.
            Assert.True(diagnostics.Count == 1, string.Join("\n", diagnostics));
        }

        /// <summary>A measurement is a word the scenario runner answers, so CT302 must leave it alone.</summary>
        [Fact]
        public void A_measurement_is_never_reported_as_an_unknown_name()
        {
            Assert.Empty(Lint("scenario \"Quiet\"\n  runs 500\n  battle Archmage\n  expect no stalls\n  expect wins >= 55%\n"));
        }

        // The two vocabularies ------------------------------------------------------------------

        /// <summary>
        /// A scenario's setup verbs are the test verbs that only set the game up, and mean the same
        /// thing in both. Whatever comes to run scenarios has to keep it that way.
        /// </summary>
        [Fact]
        public void Every_scenario_setup_verb_is_also_a_test_verb()
        {
            Assert.Empty(Scenario.SetupVerbs.Except(DslTestRunner.TestVerbs, System.StringComparer.OrdinalIgnoreCase));
            Assert.Equal(new[] { "battle", "runs" }, Scenario.Verbs.Except(DslTestRunner.TestVerbs, System.StringComparer.OrdinalIgnoreCase));
        }
    }
}
