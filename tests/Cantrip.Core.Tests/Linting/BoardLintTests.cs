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
            Assert.Contains("cannot be written", found.Message);
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

        /// <summary>
        /// Assigning a lane or a rank used to be CT328 as well, because it wrote a stat nothing
        /// read. It is a move now, so refusing it would be refusing the feature.
        /// </summary>
        [Fact]
        public void Assigning_a_lane_or_a_rank_is_a_move_and_not_a_mistake()
        {
            foreach (string axis in new[] { "lane", "rank" })
            {
                IReadOnlyList<Diagnostic> found = Lint($"""
                    board Train
                      lanes 3
                      ranks 3

                    card "Shove"
                      cost 1
                      target enemy
                      effect:
                        target.{axis} = 2
                    """);

                Assert.Null(Find(found, Linter.PlaceAssigned));
                Assert.DoesNotContain(found, d => d.Severity != DiagnosticSeverity.Info);
            }
        }

        /// <summary>
        /// CT327 again, for a move rather than a comparison. The move stops at the edge of the
        /// board rather than failing, so a slot the board does not have reads as "to the back" and
        /// means something else.
        /// </summary>
        [Fact]
        public void Moving_to_a_slot_no_board_has_says_where_it_stops()
        {
            Diagnostic? found = Find(Lint("""
                board Small
                  lanes 2
                  ranks 2

                card "Shove"
                  cost 1
                  target enemy
                  effect:
                    target.rank = 4
                """), Linter.OffTheBoard);

            Assert.NotNull(found);
            Assert.Contains("no rank 4", found!.Message);
            Assert.Contains("stops at the edge", found.Message);
        }

        // CT330 -----------------------------------------------------------------------------

        [Fact]
        public void Within_with_a_plain_number_and_no_board_is_a_warning()
        {
            Diagnostic? found = Find(Lint("""
                card "Blast"
                  cost 1
                  target enemy
                  effect:
                    deal 3 to enemies in within(target, 2)
                """), Linter.SpatialSelector);

            Assert.NotNull(found);
            Assert.Equal(DiagnosticSeverity.Warning, found!.Severity);
            Assert.Contains("declares none", found.Message);
        }

        /// <summary>A board makes the same line a measurement it can make, so it says nothing.</summary>
        [Fact]
        public void Within_with_a_plain_number_and_a_board_is_silent()
        {
            Assert.Null(Find(Lint("""
                board Arena
                  lanes 3
                  ranks 3

                card "Blast"
                  cost 1
                  target enemy
                  effect:
                    deal 3 to enemies in within(target, 2)
                """), Linter.SpatialSelector));
        }

        /// <summary>A length is the game's question, and stays the game's: a note, never a warning.</summary>
        [Fact]
        public void Within_with_a_unit_is_a_note_that_names_the_host()
        {
            Diagnostic? found = Find(Lint("""
                card "Blast"
                  cost 1
                  target enemy
                  effect:
                    deal 3 to within(target, 5m)
                """), Linter.SpatialSelector);

            Assert.NotNull(found);
            Assert.Equal(DiagnosticSeverity.Info, found!.Severity);
            Assert.Contains("IEffectHost.TryCall", found.Message);
        }

        // CT331 and CT332 -------------------------------------------------------------------

        [Fact]
        public void A_range_on_something_that_points_at_nobody_is_never_read()
        {
            Diagnostic? found = Find(Lint("""
                card "Far"
                  cost 1
                  range 2
                  effect:
                    deal 3 to enemies
                """), Linter.ReachWithoutATarget);

            Assert.NotNull(found);
            Assert.Contains("points at nobody", found!.Message);
        }

        [Fact]
        public void A_range_as_wide_as_the_board_limits_nothing()
        {
            Diagnostic? found = Find(Lint("""
                board Small
                  lanes 2
                  ranks 2

                card "Wide"
                  cost 1
                  range 4
                  target enemy
                  effect:
                    deal 3 to target
                """), Linter.ReachLimitsNothing);

            Assert.NotNull(found);
            Assert.Contains("limits nothing", found!.Message);
        }

        [Fact]
        public void A_range_written_backwards_reaches_nobody()
        {
            Diagnostic? found = Find(Lint("""
                card "Backwards"
                  cost 1
                  range 3..1
                  target enemy
                  effect:
                    deal 3 to target
                """), Linter.ReachLimitsNothing);

            Assert.NotNull(found);
            Assert.Contains("starts further away than it ends", found!.Message);
        }

        /// <summary>
        /// On a facing board the two sides never stand on one slot, so `range 0` at an enemy is a
        /// card that can be pointed at nobody, and `range 1` is the spelling that was meant.
        /// </summary>
        [Fact]
        public void A_range_of_nought_at_an_enemy_reaches_nobody()
        {
            Diagnostic? found = Find(Lint("""
                card "Nought"
                  cost 1
                  range 0
                  target enemy
                  effect:
                    deal 3 to target
                """), Linter.ReachLimitsNothing);

            Assert.NotNull(found);
            Assert.Contains("`range 1` is what melee is written as", found!.Message);
        }

        // CT333 -----------------------------------------------------------------------------

        [Fact]
        public void A_rank_on_a_one_lane_board_is_a_group_of_one()
        {
            Diagnostic? found = Find(Lint("""
                card "Sweep"
                  cost 1
                  target enemy
                  effect:
                    deal 3 to rank(target)
                """), Linter.RowOfOne);

            Assert.NotNull(found);
            Assert.Contains("that one actor and nobody else", found!.Message);
        }

        [Fact]
        public void A_lane_on_a_board_one_rank_deep_is_a_group_of_one()
        {
            Diagnostic? found = Find(Lint("""
                board Line
                  lanes 4
                  ranks 1

                card "Sweep"
                  cost 1
                  target enemy
                  effect:
                    deal 3 to lane(target)
                """), Linter.RowOfOne);

            Assert.NotNull(found);
            Assert.Contains("that one actor and nobody else", found!.Message);
        }

        [Fact]
        public void A_rank_on_a_board_with_lanes_is_silent()
        {
            Assert.Null(Find(Lint("""
                board Train
                  lanes 3
                  ranks 3

                card "Sweep"
                  cost 1
                  target enemy
                  effect:
                    deal 3 to rank(target)
                """), Linter.RowOfOne));
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
