using System;
using System.Linq;
using System.Text.Json;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Snapshots
{
    /// <summary>
    /// Format 3 carries the board: everyone's lane beside their rank, and the board's name and
    /// shape. A save is self-describing, so a game whose content has moved on still loads — except
    /// where the board it was played on has been reshaped under an actor's feet.
    /// </summary>
    public sealed class BoardSaveTests
    {
        private const string Shapes = """
            board Floor
              lanes 3
              ranks 3

            board Narrow
              lanes 3
              ranks 1

            board Wide
              lanes 5
              ranks 5

            enemy "Grunt"
              hp 10
            """;

        [Fact]
        public void A_save_written_now_is_in_format_3_and_says_which_board()
        {
            CardRuntime runtime = Fresh(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");
            Entity grunt = runtime.SpawnEnemy("Grunt");
            runtime.State.Assign(grunt, 2, 1);

            GameSnapshot save = runtime.Capture();

            Assert.Equal(3, save.FormatVersion);
            Assert.Equal(3, save.MinimumReader);
            Assert.Equal("Floor", save.BoardName);
            Assert.Equal(3, save.Board!.Lanes);
            Assert.Equal(3, save.Board.Ranks);

            EntitySnapshot record = save.Entities.Single(e => e.Id == grunt.Id);
            Assert.Equal(2, record.Lane);
            Assert.Equal(1, record.Position);   // the rank, under the name format 1 gave it
        }

        [Fact]
        public void A_board_survives_the_round_trip_through_json()
        {
            CardRuntime runtime = Fresh(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");
            Entity a = runtime.SpawnEnemy("Grunt");
            Entity b = runtime.SpawnEnemy("Grunt");
            runtime.State.Assign(a, 2, 2);
            runtime.State.Assign(b, 1, 0);
            ulong before = runtime.State.ComputeHash();

            GameSnapshot again = Roundtrip(runtime.Capture());
            CardRuntime restored = Fresh(Shapes);
            restored.Restore(again);

            Assert.Equal("Floor", restored.State.Board.Name);
            Assert.Equal(before, restored.State.ComputeHash());
            Assert.Equal((2, 2), restored.State.Find(a.Id)!.Slot);
            Assert.Equal((1, 0), restored.State.Find(b.Id)!.Slot);
            Assert.Same(restored.State.Find(a.Id), restored.State.At(Team.Enemy, 2, 2));
        }

        /// <summary>
        /// A patch that grows a board loads every save made on the old one: everybody who fitted
        /// still fits, and nobody moves.
        /// </summary>
        [Fact]
        public void Growing_a_board_loads_fine()
        {
            CardRuntime runtime = Fresh(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");
            Entity grunt = runtime.SpawnEnemy("Grunt");
            runtime.State.Assign(grunt, 2, 2);
            GameSnapshot save = Roundtrip(runtime.Capture());

            CardRuntime patched = Fresh(Shapes.Replace("board Floor\n  lanes 3\n  ranks 3", "board Floor\n  lanes 4\n  ranks 6"));
            patched.Restore(save);

            Assert.Equal(4, patched.State.Board.Lanes);
            Assert.Equal(6, patched.State.Board.Ranks);
            Assert.Equal((2, 2), patched.State.Find(grunt.Id)!.Slot);
        }

        /// <summary>
        /// And a patch that shrinks one below where somebody is standing refuses the save by name,
        /// the way a missing definition does, instead of quietly moving them.
        /// </summary>
        [Fact]
        public void A_reshaped_board_refuses_a_save_whose_actor_is_off_it()
        {
            CardRuntime runtime = Fresh(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");
            Entity grunt = runtime.SpawnEnemy("Grunt");
            runtime.State.Assign(grunt, 2, 2);
            GameSnapshot save = Roundtrip(runtime.Capture());

            CardRuntime patched = Fresh(Shapes.Replace("board Floor\n  lanes 3\n  ranks 3", "board Floor\n  lanes 3\n  ranks 2"));
            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => patched.Restore(save));

            Assert.Contains("board \"Floor\"", refused.Message);
            Assert.Contains("3 lanes, 3 ranks", refused.Message);
            Assert.Contains("3 lanes, 2 ranks", refused.Message);
            Assert.Contains("Grunt", refused.Message);
            Assert.Contains("rank 2", refused.Message);
        }

        /// <summary>The game is left exactly as it was when a restore is refused.</summary>
        [Fact]
        public void A_refused_save_leaves_the_running_game_alone()
        {
            CardRuntime runtime = Fresh(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");
            Entity grunt = runtime.SpawnEnemy("Grunt");
            runtime.State.Assign(grunt, 2, 2);
            GameSnapshot save = Roundtrip(runtime.Capture());

            CardRuntime patched = Fresh(Shapes.Replace("board Floor\n  lanes 3\n  ranks 3", "board Floor\n  lanes 3\n  ranks 2"));
            patched.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");
            Entity standing = patched.SpawnEnemy("Grunt");
            ulong before = patched.State.ComputeHash();

            Assert.Throws<InvalidOperationException>(() => patched.Restore(save));

            Assert.Equal(before, patched.State.ComputeHash());
            Assert.Equal((0, 0), standing.Slot);
        }

        /// <summary>
        /// Folding a facing board into a shared one puts two sides on one grid, where slots that
        /// were a side apart become the same slot. Two actors on one slot is not a board.
        /// </summary>
        [Fact]
        public void A_board_that_becomes_shared_refuses_a_save_where_that_would_stack_two_actors()
        {
            CardRuntime runtime = Fresh(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");
            runtime.SpawnEnemy("Grunt");   // enemy side, lane 0 rank 0; the player is there too
            GameSnapshot save = Roundtrip(runtime.Capture());

            CardRuntime patched = Fresh(Shapes.Replace("board Floor\n  lanes 3\n  ranks 3", "board Floor\n  lanes 3\n  ranks 3\n  shared"));
            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => patched.Restore(save));
            Assert.Contains("is also standing on the new board", refused.Message);
        }

        /// <summary>
        /// A save whose board the content no longer declares at all still loads, on the shape the
        /// save itself carries: the format is self-describing so that an edit never strands a save.
        /// </summary>
        [Fact]
        public void A_save_naming_a_board_content_has_dropped_loads_on_its_own_shape()
        {
            CardRuntime runtime = Fresh(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Narrow");
            Entity grunt = runtime.SpawnEnemy("Grunt");
            runtime.State.Assign(grunt, 2, 0);
            GameSnapshot save = Roundtrip(runtime.Capture());

            CardRuntime patched = Fresh("enemy \"Grunt\"\n  hp 10\n");
            patched.Restore(save);

            Assert.Equal("Narrow", patched.State.Board.Name);
            Assert.Equal(3, patched.State.Board.Lanes);
            Assert.Equal(1, patched.State.Board.Ranks);
            Assert.Equal((2, 0), patched.State.Find(grunt.Id)!.Slot);
        }

        /// <summary>
        /// A format 2 reader is told it cannot read this, rather than reading it and putting every
        /// actor in lane 0 — which is what the minimum reader is for.
        /// </summary>
        [Fact]
        public void A_format_3_save_says_it_needs_a_format_3_reader()
        {
            CardRuntime runtime = Fresh(Shapes);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false, board: "Floor");

            Assert.Equal(3, GameSnapshot.ReaderNeededBy(runtime.Capture()));
        }

        private static CardRuntime Fresh(string content)
        {
            CardRuntime runtime = CardRuntime.FromText(content, new RuntimeOptions { Seed = 7 });
            runtime.CreatePlayer();
            return runtime;
        }

        private static GameSnapshot Roundtrip(GameSnapshot save) =>
            JsonSerializer.Deserialize<GameSnapshot>(JsonSerializer.Serialize(save))!;
    }
}
