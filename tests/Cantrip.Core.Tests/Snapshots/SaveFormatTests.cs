using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Snapshots
{
    /// <summary>
    /// What a save says about itself, and which saves a build agrees to read. The promise in
    /// docs/stability.md is that a save made by any 1.x release loads in every later 1.x, so the
    /// only save a build refuses is one that needs a reader it is not, and it says which.
    /// </summary>
    public sealed class SaveFormatTests
    {
        // The format numbers ----------------------------------------------------------------

        [Fact]
        public void A_save_in_the_format_before_this_one_is_read_rather_than_refused()
        {
            CardRuntime original = SnapshotScenario.NewBattle(seed: 11);
            for (int i = 0; i < 4; i++) SnapshotScenario.Step(original);

            GameSnapshot save = Read(Written(original.Capture(), format: GameSnapshot.CurrentFormat - 1));

            CardRuntime restored = SnapshotScenario.Fresh();
            restored.Restore(save);

            Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());

            // The restore brought it forward, so what it left behind is in this build's format.
            Assert.Equal(GameSnapshot.CurrentFormat, save.FormatVersion);
        }

        [Fact]
        public void A_save_that_needs_a_newer_Cantrip_is_refused_by_name_and_changes_nothing()
        {
            CardRuntime playing = SnapshotScenario.NewBattle(seed: 12);
            ulong untouched = playing.State.ComputeHash();

            GameSnapshot save = SnapshotScenario.NewBattle(seed: 13).Capture();
            save.FormatVersion = GameSnapshot.CurrentFormat + 3;
            save.MinimumReader = GameSnapshot.CurrentFormat + 2;
            save.WrittenBy = "9.9.9";

            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => playing.Restore(save));

            Assert.Contains("format " + (GameSnapshot.CurrentFormat + 3), refused.Message);
            Assert.Contains("reads format " + (GameSnapshot.CurrentFormat + 2), refused.Message);
            Assert.Contains("up to format " + GameSnapshot.CurrentFormat, refused.Message);
            Assert.Contains("Cantrip 9.9.9", refused.Message);
            Assert.Equal(untouched, playing.State.ComputeHash());
        }

        /// <summary>
        /// The reason there are two numbers. A release that only adds a field moves the format,
        /// which is what honestly describes the file, and leaves the minimum reader where it is,
        /// so every build since that minimum keeps loading the save instead of being locked out by
        /// a number that moved for something it can safely not know about.
        /// </summary>
        [Fact]
        public void A_newer_save_that_only_added_something_this_build_need_not_know_is_read()
        {
            CardRuntime original = SnapshotScenario.NewBattle(seed: 14);
            for (int i = 0; i < 3; i++) SnapshotScenario.Step(original);

            JsonObject newer = JsonNode.Parse(JsonSerializer.Serialize(original.Capture()))!.AsObject();
            newer["FormatVersion"] = GameSnapshot.CurrentFormat + 1;
            newer["MinimumReader"] = GameSnapshot.CurrentMinimumReader;
            newer["SomethingAddedLater"] = "a field this build has never heard of";

            CardRuntime restored = SnapshotScenario.Fresh();
            restored.Restore(Read(newer));

            Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());
        }

        /// <summary>
        /// A save from a build that recorded no minimum reader needs a reader of its own format,
        /// so an older save is welcome and a newer one that says nothing is not.
        /// </summary>
        [Fact]
        public void A_save_that_does_not_say_which_reader_it_needs_is_read_as_needing_its_own_format()
        {
            var save = new GameSnapshot { FormatVersion = 1, MinimumReader = 0 };
            Assert.Equal(1, GameSnapshot.ReaderNeededBy(save));

            save.FormatVersion = GameSnapshot.CurrentFormat + 1;
            Assert.Equal(GameSnapshot.CurrentFormat + 1, GameSnapshot.ReaderNeededBy(save));

            save.MinimumReader = GameSnapshot.CurrentFormat;
            Assert.Equal(GameSnapshot.CurrentFormat, GameSnapshot.ReaderNeededBy(save));
        }

        [Fact]
        public void The_upgrade_step_brings_an_older_save_forward_and_leaves_the_game_in_it_alone()
        {
            GameSnapshot save = SnapshotScenario.NewBattle(seed: 15).Capture();
            string game = JsonSerializer.Serialize(save);

            save.FormatVersion = 1;
            save.MinimumReader = 0;
            save.WrittenBy = string.Empty;
            GameSnapshot.Upgrade(save);

            Assert.Equal(GameSnapshot.CurrentFormat, save.FormatVersion);

            // It is a no-op on the game itself today, which is the claim it has to keep making.
            save.MinimumReader = GameSnapshot.CurrentMinimumReader;
            save.WrittenBy = GameSnapshot.CurrentWriter;
            Assert.Equal(game, JsonSerializer.Serialize(save));

            Assert.Throws<ArgumentNullException>(() => GameSnapshot.Upgrade(null!));
        }

        // Who wrote it ----------------------------------------------------------------------

        [Fact]
        public void A_save_says_which_format_it_is_in_which_reader_it_needs_and_who_wrote_it()
        {
            GameSnapshot save = SnapshotScenario.NewBattle(seed: 16).Capture();

            Assert.Equal(GameSnapshot.CurrentFormat, save.FormatVersion);
            Assert.Equal(GameSnapshot.CurrentMinimumReader, save.MinimumReader);
            Assert.Equal(GameSnapshot.CurrentWriter, save.WrittenBy);
            Assert.Equal(GameSnapshot.CurrentRng, save.RngGenerator);

            // The writer is this build's version, the one `cantrip --version` prints, without the
            // build metadata: what a changelog line, a bug report and a package all call it.
            Assert.NotEqual(string.Empty, GameSnapshot.CurrentWriter);
            Assert.DoesNotContain("+", GameSnapshot.CurrentWriter);
            Assert.Matches(@"^\d+\.\d+\.\d+", GameSnapshot.CurrentWriter);

            // A snapshot built by hand rather than captured makes none of those three claims.
            var built = new GameSnapshot();
            Assert.Equal(GameSnapshot.CurrentFormat, built.FormatVersion);
            Assert.Equal(0, built.MinimumReader);
            Assert.Equal(string.Empty, built.WrittenBy);
            Assert.Equal(string.Empty, built.RngGenerator);
        }

        [Fact]
        public void A_refusal_says_so_when_the_save_does_not_record_who_wrote_it()
        {
            GameSnapshot save = SnapshotScenario.NewBattle(seed: 17).Capture();
            save.MinimumReader = GameSnapshot.CurrentFormat + 1;
            save.WrittenBy = string.Empty;

            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => SnapshotScenario.Fresh().Restore(save));
            Assert.Contains("did not record which", refused.Message);
        }

        // The names on disk -----------------------------------------------------------------

        [Fact]
        public void Waiting_work_is_saved_under_names_that_say_what_it_holds()
        {
            CardRuntime runtime = SnapshotScenario.NewBattle(seed: 18);
            runtime.Play(runtime.AddCard("Flex", Zones.Hand));      // leaves an `until turn_end:`
            runtime.Play(runtime.AddCard("Prepare", Zones.Hand));   // leaves a `next turn:`

            GameSnapshot save = runtime.Capture();

            Assert.Equal("card:Prepare/effect/0.body", save.Scheduled.Single(s => s.BlockAddress != null).BlockAddress);
            Assert.Equal(BuiltinEvents.TurnEnd, save.Scheduled.Single(s => s.UntilEvent != null).UntilEvent);

            // And the old names, which mean something else in this language, are not written.
            JsonArray scheduled = JsonNode.Parse(JsonSerializer.Serialize(save))!["Scheduled"]!.AsArray();
            Assert.Equal(2, scheduled.Count);
            foreach (JsonNode? record in scheduled)
            {
                List<string> names = record!.AsObject().Select(field => field.Key).ToList();
                Assert.DoesNotContain("Block", names);
                Assert.DoesNotContain("Deadline", names);
                Assert.Contains("BlockAddress", names);
                Assert.Contains("UntilEvent", names);
            }
        }

        /// <summary>
        /// A save written before the rename carries the two fields under the names format 1 gave
        /// them. Restoring one has to give back the same game, waiting work and all.
        /// </summary>
        [Fact]
        public void A_save_written_before_the_names_changed_still_restores()
        {
            CardRuntime original = SnapshotScenario.NewBattle(seed: 19);
            original.Play(original.AddCard("Flex", Zones.Hand));
            original.Play(original.AddCard("Prepare", Zones.Hand));
            Assert.Equal(2, original.State.Scheduled.Count);

            JsonObject written = Written(original.Capture(), format: 1);
            Assert.Contains("card:Prepare/effect/0.body", written["Scheduled"]!.ToJsonString());
            Assert.DoesNotContain("BlockAddress", written["Scheduled"]!.ToJsonString());

            GameSnapshot save = Read(written);
            Assert.Equal("card:Prepare/effect/0.body", save.Scheduled.Single(s => s.BlockAddress != null).BlockAddress);
            Assert.Equal(BuiltinEvents.TurnEnd, save.Scheduled.Single(s => s.UntilEvent != null).UntilEvent);

            CardRuntime restored = SnapshotScenario.Fresh();
            restored.Restore(save);
            Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());

            // The `next turn:` block really is found again, not merely recorded.
            original.EndTurn();
            restored.EndTurn();
            Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());
        }

        /// <summary>The name that means it wins if a save somehow carries both.</summary>
        [Fact]
        public void A_save_carrying_both_names_is_read_by_the_one_that_means_it()
        {
            var both = new ScheduledSnapshot { BlockAddress = "card:Prepare/effect/0.body", UntilEvent = "turn_end" };
            both.Block = "card:Elsewhere/effect/0.body";
            both.Deadline = "battle_end";

            Assert.Equal("card:Prepare/effect/0.body", both.BlockAddress);
            Assert.Equal("turn_end", both.UntilEvent);
        }

        // The generator ---------------------------------------------------------------------

        [Fact]
        public void A_save_from_a_generator_this_build_does_not_have_is_refused_and_changes_nothing()
        {
            CardRuntime playing = SnapshotScenario.NewBattle(seed: 20);
            ulong untouched = playing.State.ComputeHash();

            GameSnapshot save = SnapshotScenario.NewBattle(seed: 21).Capture();
            save.RngGenerator = "pcg64";

            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => playing.Restore(save));

            Assert.Contains("pcg64", refused.Message);
            Assert.Contains(GameSnapshot.CurrentRng, refused.Message);
            Assert.Equal(untouched, playing.State.ComputeHash());
        }

        [Fact]
        public void A_save_that_names_no_generator_is_read_as_this_one()
        {
            CardRuntime original = SnapshotScenario.NewBattle(seed: 22);
            for (int i = 0; i < 3; i++) SnapshotScenario.Step(original);

            GameSnapshot save = Read(Written(original.Capture(), format: 1));
            Assert.Equal(string.Empty, save.RngGenerator);

            CardRuntime restored = SnapshotScenario.Fresh();
            restored.Restore(save);

            // The same generator means the same cards next, not merely the same state now.
            Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());
            for (int i = 0; i < 12 && original.State.InBattle; i++)
            {
                SnapshotScenario.Step(original);
                SnapshotScenario.Step(restored);
                Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());
            }
        }

        [Fact]
        public void A_state_that_is_not_four_numbers_is_refused_by_the_generator_that_wanted_four()
        {
            GameSnapshot save = SnapshotScenario.NewBattle(seed: 23).Capture();
            save.Rng = new ulong[] { 1, 2, 3 };

            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => SnapshotScenario.Fresh().Restore(save));
            Assert.Contains(GameSnapshot.CurrentRng, refused.Message);
        }

        // Where a card is -------------------------------------------------------------------

        public static TheoryData<string, Action<GameSnapshot>> Disagreements => new TheoryData<string, Action<GameSnapshot>>
        {
            { "an entity that says it is somewhere else", save => InHand(save).Zone = Zones.Exhaust },
            { "an entity that says it is nowhere", save => InHand(save).Zone = Zones.None },
            { "a zone that has forgotten one of its cards", save => Hand(save).Entities.RemoveAt(0) },
            { "a card in two zones at once", save => save.Zones.Single(z => z.Zone == Zones.Draw).Entities.Add(InHand(save).Id) },
            { "a card in one zone twice", save => Hand(save).Entities.Add(InHand(save).Id) },
        };

        [Theory]
        [MemberData(nameof(Disagreements))]
        public void A_save_that_does_not_agree_with_itself_about_where_a_card_is_is_refused(string what, Action<GameSnapshot> spoil)
        {
            Assert.NotEqual(string.Empty, what);

            CardRuntime playing = SnapshotScenario.NewBattle(seed: 24);
            SnapshotScenario.Step(playing);
            ulong untouched = playing.State.ComputeHash();

            GameSnapshot save = playing.Capture();
            int card = InHand(save).Id;
            spoil(save);

            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => playing.Restore(save));

            Assert.Contains("does not agree with itself about where a card is", refused.Message);
            Assert.Contains("#" + card, refused.Message);
            Assert.Equal(untouched, playing.State.ComputeHash());
        }

        /// <summary>
        /// The other half of that check: a save this engine makes never disagrees with itself, at
        /// any point in a battle, so the refusal above can never turn a real save away.
        /// </summary>
        [Fact]
        public void A_save_this_engine_makes_agrees_with_itself_all_the_way_through_a_battle()
        {
            CardRuntime runtime = SnapshotScenario.NewBattle(seed: 25);
            for (int step = 0; step < 40 && runtime.State.InBattle; step++)
            {
                GameSnapshot save = runtime.Capture();

                var listed = new Dictionary<int, string>();
                foreach (ZoneSnapshot zone in save.Zones)
                {
                    foreach (int id in zone.Entities)
                    {
                        Assert.False(listed.ContainsKey(id));
                        listed[id] = zone.Zone;
                    }
                }
                foreach (EntitySnapshot entity in save.Entities)
                {
                    Assert.Equal(entity.Zone, listed.TryGetValue(entity.Id, out string? where) ? where : Zones.None);
                }

                SnapshotScenario.Step(runtime);
            }
        }

        // Helpers ---------------------------------------------------------------------------

        private static ZoneSnapshot Hand(GameSnapshot save) => save.Zones.Single(zone => zone.Zone == Zones.Hand);

        private static EntitySnapshot InHand(GameSnapshot save)
        {
            int id = Hand(save).Entities[0];
            return save.Entities.Single(entity => entity.Id == id);
        }

        /// <summary>
        /// A save as an older Cantrip would have written it: in the format given, without the
        /// fields that build had never heard of, and, before format 2, with the two renamed fields
        /// under the names format 1 gave them.
        /// </summary>
        private static JsonObject Written(GameSnapshot save, int format)
        {
            JsonObject written = JsonNode.Parse(JsonSerializer.Serialize(save))!.AsObject();
            written["FormatVersion"] = format;
            written.Remove("MinimumReader");
            written.Remove("RngGenerator");
            written.Remove("WrittenBy");

            if (format >= 2) return written;

            foreach (JsonNode? record in written["Scheduled"]!.AsArray())
            {
                JsonObject waiting = record!.AsObject();
                Rename(waiting, "BlockAddress", "Block");
                Rename(waiting, "UntilEvent", "Deadline");
            }
            return written;
        }

        private static void Rename(JsonObject record, string from, string to)
        {
            JsonNode? value = record[from];
            record.Remove(from);
            record[to] = value?.DeepClone();
        }

        private static GameSnapshot Read(JsonObject save) =>
            JsonSerializer.Deserialize<GameSnapshot>(save.ToJsonString(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
