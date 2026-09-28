using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// CT327, CT328 and CT329: a place no board has, a place written to as if it were a stat, and
    /// the old name for a rank.
    /// </summary>
    public sealed class BoardLintTests
    {
        private const string Floor = """
            board Floor
              lanes 3
              ranks 2

            enemy "Grunt"
              hp 10

            """;

        private static IReadOnlyList<Diagnostic> Lint(string dsl) =>
            Linter.Lint(ContentLibrary.FromText(dsl, "lint.cantrip"));

        private static Diagnostic? Find(IReadOnlyList<Diagnostic> found, string code) =>
            found.FirstOrDefault(d => d.Code == code);

        // CT327 -----------------------------------------------------------------------------

        [Fact]
        public void A_lane_no_board_has_never_matches_and_says_so()
        {
            Diagnostic? found = Find(Lint(Floor + """
                card "Sweep"
                  cost 1
                  target enemy where it.lane == 4
                  effect:
                    deal 3 to target
                """), Linter.OffTheBoard);

            Assert.NotNull(found);
            Assert.Equal(DiagnosticSeverity.Warning, found!.Severity);
            Assert.Contains("No actor is ever there", found.Message);
            Assert.Contains("3 lanes, 2 ranks", found.Message);
            Assert.Contains("lanes count from 0", found.Message);
        }

        [Fact]
        public void A_rank_below_the_board_never_matches()
        {
            Assert.NotNull(Find(Lint(Floor + """
                card "Deep"
                  cost 1
                  target enemy where it.rank > 1
                  effect:
                    deal 3 to target
                """), Linter.OffTheBoard));
        }

        [Fact]
        public void A_limit_wider_than_the_board_limits_nothing_and_says_that_instead()
        {
            Diagnostic? found = Find(Lint(Floor + """
                card "Reach"
                  cost 1
                  target enemy where it.rank <= 3
                  effect:
                    deal 3 to target
                """), Linter.OffTheBoard);

            Assert.NotNull(found);
            Assert.Contains("Every actor is there", found!.Message);
            Assert.Contains("limits nothing", found.Message);
        }

        [Fact]
        public void A_place_that_some_declared_board_can_hold_is_left_alone()
        {
            Assert.Null(Find(Lint(Floor + """
                board Wide
                  lanes 6
                  ranks 6

                card "Sweep"
                  cost 1
                  target enemy where it.lane == 4
                  effect:
                    deal 3 to target
                """), Linter.OffTheBoard));
        }

        [Fact]
        public void The_comparison_reads_the_same_written_the_other_way_round()
        {
            Assert.NotNull(Find(Lint(Floor + """
                card "Sweep"
                  cost 1
                  target enemy where 4 == it.lane
                  effect:
                    deal 3 to target
                """), Linter.OffTheBoard));
        }

        [Fact]
        public void An_unbounded_rank_only_trips_on_a_place_below_the_board()
        {
            // No board declared, so ranks go on for ever and only a negative one is impossible.
            Assert.Null(Find(Lint("""
                card "Deep"
                  cost 1
                  target enemy where it.rank > 99
                  effect:
                    deal 3 to target
                """), Linter.OffTheBoard));

            Assert.NotNull(Find(Lint("""
                card "Silly"
                  cost 1
                  target enemy where it.rank < 0
                  effect:
                    deal 3 to target
                """), Linter.OffTheBoard));
        }

        // CT328 -----------------------------------------------------------------------------

        [Fact]
        public void Assigning_position_is_an_error_that_names_rank()
        {
            Diagnostic? found = Find(Lint("""
                card "Shove"
                  cost 1
                  target enemy
                  effect:
                    target.position = 2
                """), Linter.PlaceAssigned);

            Assert.NotNull(found);
            Assert.Equal(DiagnosticSeverity.Error, found!.Severity);
            Assert.Contains("writes a stat nothing reads", found.Message);
            Assert.Contains("Write `rank`", found.Message);
        }

        /// <summary>One line, one message: CT328 already says to write `rank`.</summary>
        [Fact]
        public void An_assigned_position_is_not_also_noted_as_a_read()
        {
            IReadOnlyList<Diagnostic> found = Lint("""
                card "Shove"
                  cost 1
                  target enemy
                  effect:
                    target.position = 2
                """);

            Assert.NotNull(Find(found, Linter.PlaceAssigned));
            Assert.Null(Find(found, Linter.PositionIsNowRank));
        }

        [Fact]
        public void Assigning_a_lane_or_a_rank_says_a_place_is_not_a_stat()
        {
            foreach (string axis in new[] { "lane", "rank" })
            {
                Diagnostic? found = Find(Lint($"""
                    card "Shove"
                      cost 1
                      target enemy
                      effect:
                        target.{axis} = 2
                    """), Linter.PlaceAssigned);

                Assert.NotNull(found);
                Assert.Contains("is not a stat", found!.Message);
                Assert.Contains($"`{axis}`", found.Message);
            }
        }

        // CT329 -----------------------------------------------------------------------------

        [Fact]
        public void Reading_position_is_a_note_and_never_a_warning()
        {
            IReadOnlyList<Diagnostic> found = Lint("""
                card "Pike"
                  cost 1
                  target enemy where it.position <= 1
                  effect:
                    deal 5 to target
                """);

            Diagnostic? note = Find(found, Linter.PositionIsNowRank);
            Assert.NotNull(note);
            Assert.Equal(DiagnosticSeverity.Info, note!.Severity);
            Assert.Contains("now called `rank`", note.Message);

            // `--warnings-as-errors` is what every sample folder is linted with, and a note that
            // stopped a build would make the old word stop working rather than fade.
            Assert.DoesNotContain(found, d => d.Severity != DiagnosticSeverity.Info);
        }

        [Fact]
        public void Sorting_by_position_is_a_note_too()
        {
            Diagnostic? note = Find(Lint("""
                card "Snipe"
                  cost 1
                  effect:
                    deal 4 to highest position enemies
                """), Linter.PositionIsNowRank);

            Assert.NotNull(note);
            Assert.Contains("lowest rank enemies", note!.Message);
        }

        [Fact]
        public void Reading_rank_says_nothing()
        {
            Assert.Null(Find(Lint("""
                card "Pike"
                  cost 1
                  target enemy where it.rank <= 1
                  effect:
                    deal 5 to target
                """), Linter.PositionIsNowRank));
        }

        // A board is not a thing ------------------------------------------------------------

        [Fact]
        public void A_boards_shape_is_not_a_stat_and_its_name_is_not_a_tag()
        {
            // `lanes` would have become a stat and `Floor` a tag if a board were an ordinary
            // declaration, and then a card that misspelt either would have linted clean.
            IReadOnlyList<Diagnostic> found = Lint(Floor + """
                card "Wrong"
                  cost 1
                  effect:
                    deal lanes to enemies
                """);

            Assert.Contains(found, d => d.Code == Linter.UnknownName);
        }
    }
}
