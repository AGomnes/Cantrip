using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;
using static Cantrip.Tests.Runtime.RuntimeTestKit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// The board: where actors stand, who is next to whom, how far apart two of them are, and what
    /// happens to a slot when its occupant leaves.
    /// </summary>
    public sealed class BoardTests
    {
        private const string Shapes = """
            board Floor
              lanes 3
              ranks 2

            board Arena
              lanes 4
              ranks 4
              shared
              metric chebyshev

            board Tunnel
              lanes 2
              ranks 3
              on_vacated close_ranks

            enemy "Grunt"
              hp 10
              attack 2

            card "Summon"
              cost 0
              effect:
                create Grunt
            """;

        // Content and the default board ---------------------------------------------------

        [Fact]
        public void Content_that_declares_no_board_is_played_on_the_board_it_always_had()
        {
            CardRuntime runtime = Create("""card "Strike"\n  cost 1""".Replace("\\n", "\n"));

            BoardShape board = runtime.State.Board;
            Assert.Equal(BoardShape.DefaultName, board.Name);
            Assert.Equal(1, board.Lanes);
            Assert.True(board.RanksAreUnbounded);
            Assert.Equal(BoardSides.Facing, board.Sides);
            Assert.Equal(BoardMetric.Manhattan, board.Metric);
            Assert.Equal(OnVacated.Gap, board.OnVacated);
            Assert.Equal("lane", board.LaneWord);
            Assert.Equal("rank", board.RankWord);
        }

        [Fact]
        public void A_declared_board_is_read_off_the_declaration()
        {
            ContentLibrary content = ContentLibrary.FromText(Shapes);
            content.Diagnostics.ThrowIfErrors();

            BoardShape floor = content.Board("Floor")!;
            Assert.Equal(3, floor.Lanes);
            Assert.Equal(2, floor.Ranks);
            Assert.False(floor.RanksAreUnbounded);
            Assert.Equal(BoardSides.Facing, floor.Sides);

            BoardShape arena = content.Board("Arena")!;
            Assert.Equal(BoardSides.Shared, arena.Sides);
            Assert.Equal(BoardMetric.Chebyshev, arena.Metric);

            Assert.Equal(OnVacated.CloseRanks, content.Board("Tunnel")!.OnVacated);

            // Several by name, and the first declared is what a game that names none is played on.
            Assert.Equal(new[] { "Floor", "Arena", "Tunnel" }, content.Boards.Select(b => b.Name));
            Assert.Equal("Floor", content.DefaultBoard.Name);
        }

        [Fact]
        public void A_board_with_lane_and_rank_words_says_them()
        {
            ContentLibrary content = ContentLibrary.FromText("""
                board Train
                  lanes 3
                  ranks 7
                  lane_word "floor"
                  rank_word "slot"
                """);
            content.Diagnostics.ThrowIfErrors();

            BoardShape train = content.Board("Train")!;
            Assert.Equal("floor", train.LaneWord);
            Assert.Equal("slot", train.RankWord);
        }

        [Theory]
        [InlineData("board Bad\n  lanes 0", "whole number of 1 or more")]
        [InlineData("board Bad\n  ranks -2", "whole number of 1 or more")]
        [InlineData("board Bad\n  metric euclidean", "`manhattan` or `chebyshev`")]
        [InlineData("board Bad\n  on_vacated shove", "`gap`")]
        [InlineData("board Bad\n  facing yes", "written on its own")]
        [InlineData("board Bad\n  lanez 3", "not something a board says")]
        public void A_board_that_says_something_it_cannot_mean_is_refused(string text, string expected)
        {
            ContentLibrary content = ContentLibrary.FromText(text);
            Assert.Contains(content.Diagnostics.Errors, d => d.Code == "CT0114" && d.Message.Contains(expected));
        }

        [Fact]
        public void A_board_is_not_a_thing_a_card_can_name()
        {
            ContentLibrary content = ContentLibrary.FromText(Shapes);
            content.Diagnostics.ThrowIfErrors();

            // Out of name lookup, exactly as a resource is: `Floor` in an expression is not content.
            Assert.Null(content.Find("Floor"));
            Assert.DoesNotContain(content.Definitions.Where(d => d.IsThing), d => d.Name == "Floor");
        }

        // Where a new actor lands ----------------------------------------------------------

        [Fact]
        public void Actors_take_the_lowest_free_rank_in_the_first_lane_with_room()
        {
            CardRuntime runtime = Create(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");

            Entity a = runtime.SpawnEnemy("Grunt");
            Entity b = runtime.SpawnEnemy("Grunt");
            Entity c = runtime.SpawnEnemy("Grunt");

            Assert.Equal((0, 0), a.Slot);
            Assert.Equal((0, 1), b.Slot);
            Assert.Equal((1, 0), c.Slot);   // lane 0 is two deep and full
        }

        [Fact]
        public void One_actor_stands_on_one_slot()
        {
            CardRuntime runtime = Create(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");

            Entity a = runtime.SpawnEnemy("Grunt");
            Entity b = runtime.SpawnEnemy("Grunt");

            Assert.Same(a, runtime.State.At(Team.Enemy, 0, 0));
            Assert.Same(b, runtime.State.At(Team.Enemy, 0, 1));
            Assert.Null(runtime.State.At(Team.Enemy, 2, 1));

            // A facing board gives each side its own grid, so the player is not on the enemy's.
            Assert.Same(runtime.Player, runtime.State.At(Team.Player, 0, 0));
        }

        [Fact]
        public void Assigning_a_slot_someone_else_is_on_swaps_the_two_and_is_its_own_inverse()
        {
            CardRuntime runtime = Create(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");

            Entity a = runtime.SpawnEnemy("Grunt");
            Entity b = runtime.SpawnEnemy("Grunt");
            ulong before = runtime.State.ComputeHash();

            runtime.State.Assign(a, 0, 1);
            Assert.Equal((0, 1), a.Slot);
            Assert.Equal((0, 0), b.Slot);

            runtime.State.Assign(a, 0, 0);
            Assert.Equal((0, 0), a.Slot);
            Assert.Equal((0, 1), b.Slot);
            Assert.Equal(before, runtime.State.ComputeHash());
        }

        [Fact]
        public void A_slot_the_board_does_not_have_is_refused()
        {
            CardRuntime runtime = Create(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");
            Entity grunt = runtime.SpawnEnemy("Grunt");

            Assert.Throws<System.ArgumentOutOfRangeException>(() => runtime.State.Assign(grunt, 3, 0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => runtime.State.Assign(grunt, 0, 2));
        }

        // A freed slot is reusable ---------------------------------------------------------

        /// <summary>
        /// The change decisions.md records: a dead actor leaves the board, so its slot is free and
        /// the next summon fills the hole instead of landing past it. Before this, <c>Place</c>
        /// assigned one past the highest live slot, the row grew without bound, and the hole stayed.
        /// </summary>
        [Fact]
        public void A_summon_fills_the_hole_a_dead_actor_left()
        {
            // No board declared, so this is the one-lane board every game had before boards existed.
            CardRuntime runtime = Create("enemy \"Grunt\"\n  hp 10\n");
            Start(runtime);

            Entity front = runtime.SpawnEnemy("Grunt");
            Entity middle = runtime.SpawnEnemy("Grunt");
            Entity back = runtime.SpawnEnemy("Grunt");
            Assert.Equal(new[] { 0, 1, 2 }, new[] { front.Rank, middle.Rank, back.Rank });

            runtime.Execute("kill target", target: middle);
            Assert.Equal(Zones.Dead, middle.Zone);
            Assert.Null(runtime.State.At(Team.Enemy, 0, 1));

            Entity filled = runtime.SpawnEnemy("Grunt");
            Assert.Equal((0, 1), filled.Slot);   // was rank 3, past the row, leaving the hole
        }

        /// <summary>
        /// Hearthstone's Dire Wolf Alpha, which <c>docs/coverage.md</c> lists as <em>Works</em>: it
        /// buffs the minions beside it. A minion summoned after a neighbour died used to land one
        /// past the highest live slot, so with a survivor standing behind the hole it went two slots
        /// further out and got nothing — a card listed as working, silently not working, with no
        /// message anywhere. At 802530c the last two assertions here read "Expected: 3, Actual: 5".
        /// </summary>
        [Fact]
        public void A_summon_after_a_death_stands_beside_the_wolf()
        {
            CardRuntime runtime = Create("""
                actor "Dire Wolf"
                  hp 2
                  attack 2
                  modify attack of adjacent(self): +1

                actor "Pup"
                  hp 5
                  attack 1
                """);
            Start(runtime);

            // The player holds rank 0 of the player's lane, so the pack starts at 1.
            Entity first = Pup(runtime);
            Entity wolf = runtime.State.Instantiate(runtime.Content.Find("Dire Wolf", "actor")!, null, Team.Player, Zones.Board);
            Entity doomed = Pup(runtime);
            Entity tail = Pup(runtime);

            Assert.Equal(new[] { 1, 2, 3, 4 }, new[] { first.Rank, wolf.Rank, doomed.Rank, tail.Rank });
            Assert.Equal(2, first.GetInt("attack"));    // rank 1, beside the wolf at 2
            Assert.Equal(2, doomed.GetInt("attack"));   // rank 3, beside it on the other side

            runtime.Execute("kill target", target: doomed);
            Assert.Equal(4, tail.Rank);                 // the survivor behind the hole never moves
            Assert.Null(runtime.State.At(Team.Player, 0, 3));

            Entity replacement = Pup(runtime);
            Assert.Equal(3, replacement.Rank);
            Assert.Equal(2, replacement.GetInt("attack"));
            Assert.Contains(wolf, runtime.State.Neighbours(replacement));
        }

        private static Entity Pup(CardRuntime runtime) =>
            runtime.State.Instantiate(runtime.Content.Find("Pup", "actor")!, null, Team.Player, Zones.Board);

        [Fact]
        public void Survivors_never_shift_unless_the_board_says_close_ranks()
        {
            CardRuntime runtime = Create(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Tunnel");

            Entity a = runtime.SpawnEnemy("Grunt");
            Entity b = runtime.SpawnEnemy("Grunt");
            Entity c = runtime.SpawnEnemy("Grunt");
            Assert.Equal(new[] { 0, 1, 2 }, new[] { a.Rank, b.Rank, c.Rank });

            runtime.Execute("kill target", target: a);

            // Tunnel says close_ranks, so everyone behind steps forward.
            Assert.Equal(0, b.Rank);
            Assert.Equal(1, c.Rank);
            Assert.Same(b, runtime.State.At(Team.Enemy, 0, 0));
            Assert.Null(runtime.State.At(Team.Enemy, 0, 2));
        }

        [Fact]
        public void The_default_board_leaves_the_gap_where_it_is()
        {
            CardRuntime runtime = Create(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");

            Entity a = runtime.SpawnEnemy("Grunt");
            Entity b = runtime.SpawnEnemy("Grunt");
            runtime.Execute("kill target", target: a);

            Assert.Equal(1, b.Rank);
            Assert.Null(runtime.State.At(Team.Enemy, 0, 0));
        }

        // Turn order -----------------------------------------------------------------------

        /// <summary>
        /// <c>Actors</c> is ordered by where actors stand, which its own summary always claimed and
        /// which only became true when a freed slot could be filled behind a survivor.
        /// </summary>
        [Fact]
        public void Actors_come_back_in_slot_order_not_the_order_they_arrived()
        {
            CardRuntime runtime = Create(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");

            Entity front = runtime.SpawnEnemy("Grunt", 11);
            Entity back = runtime.SpawnEnemy("Grunt", 12);
            runtime.Execute("kill target", target: front);

            Entity late = runtime.SpawnEnemy("Grunt", 13);
            Assert.Equal((0, 0), late.Slot);

            // Arrived last, stands first.
            Assert.Equal(new[] { late, back }, runtime.State.Actors(Team.Enemy));
        }

        // Distance and neighbours ----------------------------------------------------------

        [Fact]
        public void A_facing_board_mirrors_so_the_two_front_ranks_are_one_step_apart()
        {
            CardRuntime runtime = Create(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");

            Entity ally = runtime.Player!;
            Entity enemy = runtime.SpawnEnemy("Grunt");
            Assert.Equal((0, 0), ally.Slot);
            Assert.Equal((0, 0), enemy.Slot);

            // Both at the front of lane 0, facing each other: one step.
            Assert.Equal(1, runtime.State.Distance(ally, enemy));

            runtime.State.Assign(enemy, 0, 1);
            Assert.Equal(2, runtime.State.Distance(ally, enemy));   // 0 + 1 + 1

            runtime.State.Assign(enemy, 2, 1);
            Assert.Equal(4, runtime.State.Distance(ally, enemy));   // |0-2| + (0 + 1 + 1)
        }

        [Fact]
        public void A_shared_board_is_absolute_and_chebyshev_counts_a_diagonal_as_one()
        {
            CardRuntime runtime = Create(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Arena");

            Entity ally = runtime.Player!;
            Entity enemy = runtime.SpawnEnemy("Grunt");
            Assert.Equal((0, 0), ally.Slot);
            runtime.State.Assign(enemy, 1, 1);

            // No mirroring, so a rank is the same place for both; chebyshev makes this one step.
            Assert.Equal(1, runtime.State.Distance(ally, enemy));

            runtime.State.Assign(enemy, 3, 1);
            Assert.Equal(3, runtime.State.Distance(ally, enemy));   // max(3, 1)

            // One grid, so the two sides share slots: this one is taken, whoever asks.
            Assert.Same(enemy, runtime.State.At(Team.Player, 3, 1));
            Assert.Same(ally, runtime.State.At(Team.Enemy, 0, 0));
        }

        [Fact]
        public void Neighbours_are_one_step_away_and_on_the_same_side()
        {
            CardRuntime runtime = Create(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");

            // Well away from the player, which stands at the front of lane 0 on its own side.
            Entity ally = runtime.State.Spawn("Ally", EntityKind.Actor, null, Team.Player, Zones.Board);
            Entity mate = runtime.State.Spawn("Mate", EntityKind.Actor, null, Team.Player, Zones.Board);
            Entity enemy = runtime.SpawnEnemy("Grunt");
            runtime.State.Assign(ally, 2, 0);
            runtime.State.Assign(mate, 2, 1);
            runtime.State.Assign(enemy, 2, 0);

            // The enemy is one step away and is deliberately not a neighbour: `adjacent` is no
            // wider than it was, and reaching across the board is what `distance` is for.
            Assert.Equal(1, runtime.State.Distance(ally, enemy));
            Assert.Equal(new[] { mate }, runtime.State.Neighbours(ally));

            runtime.State.Assign(mate, 1, 0);
            Assert.Equal(new[] { mate }, runtime.State.Neighbours(ally));   // one lane across

            runtime.State.Assign(mate, 0, 1);
            Assert.Empty(runtime.State.Neighbours(ally));
        }

        // Capacity -------------------------------------------------------------------------

        /// <summary>
        /// Monster Train's floor capacity, with no new concept: a summon into a full lane makes
        /// nothing, binds <c>created</c> empty and says so in the trace — a refusal, the way
        /// <c>play</c> on an empty pile is, rather than an error that stops the card.
        /// </summary>
        [Fact]
        public void A_summon_into_a_full_lane_makes_nothing_and_says_so()
        {
            CardRuntime runtime = Create(Shapes + """

                actor "Recruit"
                  hp 6

                card "Overfill"
                  cost 0
                  effect:
                    create Recruit
                    log "made" created.count
                """, new RuntimeOptions { Trace = true });
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");

            // The player's own lane 0 is two deep: the player is on it, and one summon fills it.
            System.Collections.Generic.List<string> log = CaptureLog(runtime);
            runtime.AddCard("Overfill", Zones.Hand);
            runtime.AddCard("Overfill", Zones.Hand);
            runtime.Play(runtime.State.ZoneOf(runtime.Player, Zones.Hand)[0]);
            Assert.Equal("made 1", log[0]);

            runtime.Play(runtime.State.ZoneOf(runtime.Player, Zones.Hand)[0]);
            Assert.Equal("made 0", log[1]);

            Assert.Equal(2, runtime.State.Actors(Team.Player).Count);
            Assert.Contains("no room for Recruit", runtime.State.Trace.FormatTree());
        }

        /// <summary>
        /// A <c>before created:</c> listener that summons something can fill the lane between the
        /// summon being asked for and the summon landing, so the room is checked at the moment the
        /// actor is made rather than before the event.
        /// </summary>
        [Fact]
        public void A_lane_filled_during_the_created_event_still_refuses_the_summon()
        {
            CardRuntime runtime = Create(Shapes + """

                board Ledge
                  lanes 1
                  ranks 2

                actor "Recruit"
                  hp 6

                relic "Echo"
                  on before_created once per battle:
                    create Recruit

                card "Call"
                  cost 0
                  effect:
                    create Recruit
                    log "made" created.count
                """, new RuntimeOptions { Trace = true });
            runtime.AddRelic("Echo");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Ledge");

            // The player holds rank 0 of its own lane, so one slot is free. The relic takes it
            // while the card's own summon is still being announced.
            System.Collections.Generic.List<string> log = CaptureLog(runtime);
            runtime.AddCard("Call", Zones.Hand);
            runtime.Play(runtime.State.ZoneOf(runtime.Player, Zones.Hand)[0]);

            Assert.Equal("made 0", log[0]);
            Assert.Equal(2, runtime.State.Actors(Team.Player).Count);
            Assert.Contains("no room for Recruit", runtime.State.Trace.FormatTree());
        }

        [Fact]
        public void A_summon_arrives_in_its_makers_lane()
        {
            CardRuntime runtime = Create(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");

            Entity splitter = runtime.SpawnEnemy("Grunt");
            runtime.State.Assign(splitter, 2, 0);

            runtime.Execute("create Grunt", self: splitter, target: splitter);

            Entity spawn = runtime.State.Actors(Team.Enemy).Single(a => a != splitter);
            Assert.Equal((2, 1), spawn.Slot);
        }

        // The `position` alias ---------------------------------------------------------------

        [Fact]
        public void Position_reads_rank_and_keeps_working()
        {
            CardRuntime runtime = Create(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");

            Entity grunt = runtime.SpawnEnemy("Grunt");
            runtime.State.Assign(grunt, 1, 1);

            Assert.Equal(1, grunt.Rank);
            Assert.Equal(grunt.Rank, grunt.Position);
            Assert.Equal(1, EvalInt(runtime, "target.position", target: grunt));
            Assert.Equal(1, EvalInt(runtime, "target.rank", target: grunt));
            Assert.Equal(1, EvalInt(runtime, "target.lane", target: grunt));
        }

        // Picking a board --------------------------------------------------------------------

        [Fact]
        public void A_game_picks_a_board_per_battle_and_cannot_invent_one()
        {
            CardRuntime runtime = Create(Shapes);

            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Arena");
            Assert.Equal("Arena", runtime.State.Board.Name);

            System.ArgumentException bad = Assert.Throws<System.ArgumentException>(
                () => runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Foor"));
            Assert.Contains("No board named \"Foor\" is declared", bad.Message);
            Assert.Contains("Did you mean \"Floor\"?", bad.Message);
        }

        [Fact]
        public void Changing_board_keeps_everyone_who_still_fits_where_they_are()
        {
            CardRuntime runtime = Create(Shapes + "\nboard Deep\n  lanes 3\n  ranks 5\n");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Deep");

            Entity a = runtime.SpawnEnemy("Grunt");
            Entity b = runtime.SpawnEnemy("Grunt");
            Entity c = runtime.SpawnEnemy("Grunt");
            Assert.Equal((0, 2), c.Slot);   // Deep is five ranks, so all three fit in one lane

            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");

            // Floor is two deep. The two who still fit stay exactly where they were; the one who
            // does not takes the lowest free slot rather than being lost.
            Assert.Equal((0, 0), a.Slot);
            Assert.Equal((0, 1), b.Slot);
            Assert.Equal((1, 0), c.Slot);
            Assert.Equal(3, runtime.State.Actors(Team.Enemy).Count);
        }

        [Fact]
        public void Growing_a_board_moves_nobody()
        {
            CardRuntime runtime = Create(Shapes + "\nboard Deep\n  lanes 3\n  ranks 5\n");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");

            Entity a = runtime.SpawnEnemy("Grunt");
            Entity b = runtime.SpawnEnemy("Grunt");
            Entity c = runtime.SpawnEnemy("Grunt");
            Assert.Equal(new[] { (0, 0), (0, 1), (1, 0) }, new[] { a.Slot, b.Slot, c.Slot });

            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Deep");
            Assert.Equal(new[] { (0, 0), (0, 1), (1, 0) }, new[] { a.Slot, b.Slot, c.Slot });
        }
    }
}
