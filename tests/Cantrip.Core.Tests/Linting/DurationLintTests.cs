using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// CT314 and CT315: two ways of writing how long a status lasts that load cleanly and have no
    /// effect. Each test first shows the behaviour with a DSL test, then the warning.
    /// </summary>
    public sealed class DurationLintTests
    {
        private const string Statuses = """
            status Weak
              tags debuff
              stacking duration
              modify damage: x0.75

            enemy Brute
              hp 50
              move "Hit":
                deal 8 to player

            """;

        private static IReadOnlyList<Diagnostic> Lint(string dsl, LintOptions? options = null) =>
            Linter.Lint(ContentLibrary.FromText(Statuses + dsl, "durations.cantrip"), options);

        private static List<Diagnostic> Findings(string dsl, string code) =>
            Lint(dsl).Where(d => d.Code == code).ToList();

        private static void AssertTestsPass(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(Statuses + dsl, "durations.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            foreach (DslTestResult result in new DslTestRunner(content).RunAll())
                Assert.True(result.Passed, $"{result.Name}: {result.Failure}");
        }

        // CT314 for N turns on a duration status ---------------------------------------------

        [Fact]
        public void For_two_turns_on_a_duration_status_lasts_one_turn_and_is_reported()
        {
            const string dsl = """
                card Sap
                  cost 0
                  target enemy
                  effect:
                    apply Weak for 2 turns to target

                test "for 2 turns covers one enemy turn, as a duration of 1 does"
                  enemy Brute
                  play Sap on enemy
                  end turn
                  expect player.hp == 74
                  expect enemy.has(Weak) == false
                  end turn
                  expect player.hp == 66
                """;

            AssertTestsPass(dsl);

            Diagnostic warning = Assert.Single(Findings(dsl, Linter.ForOnDurationStatus));
            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.StartsWith("`for 2 turns` does not make Weak last 2 turns.", warning.Message);
            Assert.Contains("here 1", warning.Message);
            Assert.Equal("apply Weak 2 to target", warning.Suggestion);
        }

        [Fact]
        public void An_amount_as_long_as_the_deadline_is_not_reported()
        {
            Assert.Empty(Findings("""
                card Sap
                  cost 0
                  target enemy
                  effect:
                    apply Weak 2 for 2 turns to target
                    apply Weak 3 for 1 turn to target
                    apply Weak 2
                """, Linter.ForOnDurationStatus));
        }

        [Fact]
        public void For_is_left_alone_where_it_is_what_removes_the_status()
        {
            Assert.Empty(Findings("""
                status Burn
                  stacking intensity

                status Charged
                  stacking duration
                  decay 1 on card_played

                status Stuck
                  stacking duration
                  decay 0

                status Hasted
                  stacking both

                card Sap
                  cost 0
                  target enemy
                  effect:
                    apply Burn for 2 turns to target
                    apply Charged for 2 turns to target
                    apply Stuck for 2 turns to target
                    apply Hasted for 2 turns to target
                    apply Weak for 3s to target
                    apply Weak x for 2 turns to target
                    for each c in hand:
                      apply Weak for 2 turns to c

                relic Anchor
                  on card_played:
                    apply Weak for 2 turns to target
                """, Linter.ForOnDurationStatus));
        }

        [Fact]
        public void Groups_of_actors_and_refresh_statuses_are_reported()
        {
            List<Diagnostic> found = Findings("""
                status Dazed
                  stacking refresh

                relic Anchor
                  on turn_start:
                    apply Weak for 2 turns to all enemies
                    add Dazed 1 for 3 turns to player
                """, Linter.ForOnDurationStatus);

            Assert.Equal(new[] { "apply Weak 2 to all enemies", "add Dazed 3 to player" }, found.Select(d => d.Suggestion));
        }

        // CT315 duration on a duration status ------------------------------------------------

        [Fact]
        public void A_duration_line_on_a_duration_status_does_nothing_and_is_reported()
        {
            const string dsl = """
                status Long
                  stacking duration
                  duration 5
                  modify damage: x0.5

                test "the status lasts as long as the amount applied"
                  enemy Brute
                  apply Long to enemy
                  expect enemy.Long == 1
                  end turn
                  expect enemy.has(Long) == false
                """;

            AssertTestsPass(dsl);

            Diagnostic warning = Assert.Single(Findings(dsl, Linter.IgnoredDuration));
            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.StartsWith("`duration` on status \"Long\" does nothing", warning.Message);
        }

        /// <summary>Each stacking mode whose duration is set by the application ignores the line alike.</summary>
        [Theory]
        [InlineData("duration")]
        [InlineData("refresh")]
        [InlineData("both")]
        public void Every_stacking_that_takes_its_length_from_the_application_is_reported(string stacking)
        {
            Diagnostic warning = Assert.Single(Findings($$"""
                status Long
                  stacking {{stacking}}
                  duration 5
                """, Linter.IgnoredDuration));

            Assert.Contains($"`stacking {stacking}` status takes its duration from whoever applies it", warning.Message);
        }

        [Fact]
        public void A_duration_line_that_something_reads_is_not_reported()
        {
            // On an intensity status it is a stat like any other, which content can read.
            Assert.Empty(Findings("""
                status Stacky
                  stacking intensity
                  duration 5
                """, Linter.IgnoredDuration));

            // Text that prints it, and content that reads a `duration` member, both use it.
            Assert.Empty(Findings("""
                status Long
                  stacking refresh
                  duration 5
                  text: "Lasts up to {duration} turns."
                """, Linter.IgnoredDuration));

            Assert.Empty(Findings("""
                status Long
                  stacking duration
                  duration 5

                card Study
                  cost 0
                  effect:
                    discover 1 statuses where it.duration > 2
                """, Linter.IgnoredDuration));

            var options = new LintOptions();
            options.HostNames.Add("duration");
            Assert.DoesNotContain(Lint("""
                status Long
                  stacking duration
                  duration 5
                """, options), d => d.Code == Linter.IgnoredDuration);
        }

        [Theory]
        [InlineData("basic")]
        [InlineData("corpus")]
        [InlineData("slice")]
        [InlineData("recipes")]
        public void No_sample_writes_a_length_that_does_nothing(string folder)
        {
            var content = new ContentLibrary();
            content.LoadFolder(Path.Combine(LintTestPaths.RepositoryRoot(), "samples", folder));

            var found = Linter.Lint(content).Where(d => d.Code == Linter.ForOnDurationStatus || d.Code == Linter.IgnoredDuration).ToList();
            Assert.True(found.Count == 0, string.Join("\n", found));
        }
    }
}
