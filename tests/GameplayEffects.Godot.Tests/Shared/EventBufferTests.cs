using System;
using System.Collections.Generic;
using System.Linq;
using GameplayEffects.Runtime;
using Xunit;

namespace GameplayEffects.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// The buffer between resolution and presentation: what it keeps, in what order, and what each
    /// record remembers of a game that has already moved on.
    /// </summary>
    public sealed class EventBufferTests
    {
        // Ordering and lifecycle -----------------------------------------------------------------

        [Fact]
        public void Records_come_out_in_order_with_rising_sequence_numbers()
        {
            GameState state = AdapterTestKit.BareState();
            Entity hero = AdapterTestKit.Actor(state);
            var buffer = new EventBuffer();

            buffer.Add(new GameEvent("damaged") { Target = hero, Amount = 3 }, state);
            buffer.Add(new GameEvent("healed") { Target = hero, Amount = 1 }, state);

            List<EventRecord> records = AdapterTestKit.DrainAll(buffer);

            Assert.Equal(new[] { "damaged", "healed" }, records.Select(r => r.Name));
            Assert.Equal(new long[] { 1, 2 }, records.Select(r => r.Sequence));
            Assert.Equal(2, buffer.LastSequence);
            Assert.Equal(0, buffer.Count);
        }

        [Fact]
        public void A_drained_buffer_hands_nothing_over_twice()
        {
            GameState state = AdapterTestKit.BareState();
            Entity hero = AdapterTestKit.Actor(state);
            var buffer = new EventBuffer();
            buffer.Add(new GameEvent("damaged") { Target = hero }, state);

            Assert.Single(AdapterTestKit.DrainAll(buffer));
            Assert.Empty(AdapterTestKit.DrainAll(buffer));
        }

        [Fact]
        public void Discard_throws_away_what_was_never_drained()
        {
            GameState state = AdapterTestKit.BareState();
            Entity hero = AdapterTestKit.Actor(state);
            var buffer = new EventBuffer();
            buffer.Add(new GameEvent("damaged") { Target = hero }, state);

            buffer.Discard();

            Assert.Equal(0, buffer.Count);
            Assert.Empty(AdapterTestKit.DrainAll(buffer));

            // Sequence numbers are not reused: a UI can still tell the gap from a repeat.
            buffer.Add(new GameEvent("healed") { Target = hero }, state);
            Assert.Equal(2, AdapterTestKit.DrainAll(buffer)[0].Sequence);
        }

        // Filtering ------------------------------------------------------------------------------

        [Fact]
        public void A_masked_name_never_reaches_the_buffer()
        {
            GameState state = AdapterTestKit.BareState();
            Entity hero = AdapterTestKit.Actor(state);
            var buffer = new EventBuffer();
            buffer.Mask("hp_changed");

            buffer.Add(new GameEvent("hp_changed") { Target = hero }, state);
            buffer.Add(new GameEvent("damaged") { Target = hero }, state);

            Assert.False(buffer.IsRecorded("hp_changed"));
            Assert.Equal(new[] { "damaged" }, AdapterTestKit.DrainAll(buffer).Select(r => r.Name));

            buffer.Unmask("hp_changed");
            Assert.True(buffer.IsRecorded("hp_changed"));
        }

        [Fact]
        public void A_watch_list_records_only_the_names_the_game_presents()
        {
            GameState state = AdapterTestKit.BareState();
            Entity hero = AdapterTestKit.Actor(state);
            var buffer = new EventBuffer();
            buffer.Watch("damaged", "died");

            buffer.Add(new GameEvent("damaged") { Target = hero }, state);
            buffer.Add(new GameEvent("drawn") { Target = hero }, state);
            buffer.Add(new GameEvent("died") { Target = hero }, state);

            Assert.Equal(new[] { "damaged", "died" }, AdapterTestKit.DrainAll(buffer).Select(r => r.Name));

            buffer.ClearFilters();
            Assert.True(buffer.IsRecorded("drawn"));
        }

        [Fact]
        public void Masking_beats_watching()
        {
            var buffer = new EventBuffer();
            buffer.Watch("damaged", "drawn");
            buffer.Mask("drawn");

            Assert.True(buffer.IsRecorded("damaged"));
            Assert.False(buffer.IsRecorded("drawn"));
        }

        [Fact]
        public void Names_are_matched_the_way_the_core_matches_them()
        {
            var buffer = new EventBuffer();
            buffer.Mask("Damaged");

            // Event names are case-insensitive everywhere else in the engine.
            Assert.False(buffer.IsRecorded("damaged"));
            Assert.False(buffer.IsRecorded((string?)null));
        }

        // Draining -------------------------------------------------------------------------------

        [Fact]
        public void The_sink_can_see_that_a_drain_is_running_and_cannot_start_another()
        {
            GameState state = AdapterTestKit.BareState();
            Entity hero = AdapterTestKit.Actor(state);
            var buffer = new EventBuffer();
            buffer.Add(new GameEvent("damaged") { Target = hero }, state);

            bool sawDraining = false;
            Exception? nested = null;
            buffer.Drain(record =>
            {
                sawDraining = buffer.Draining;
                nested = Record.Exception(() => buffer.Drain(_ => { }));
            });

            Assert.True(sawDraining);
            Assert.IsType<InvalidOperationException>(nested);
            Assert.False(buffer.Draining);
        }

        [Fact]
        public void Events_recorded_while_draining_wait_for_the_next_drain()
        {
            GameState state = AdapterTestKit.BareState();
            Entity hero = AdapterTestKit.Actor(state);
            var buffer = new EventBuffer();
            buffer.Add(new GameEvent("damaged") { Target = hero }, state);

            int delivered = 0;
            buffer.Drain(record =>
            {
                delivered++;
                if (delivered == 1) buffer.Add(new GameEvent("healed") { Target = hero }, state);
            });

            Assert.Equal(1, delivered);
            Assert.Equal(new[] { "healed" }, AdapterTestKit.DrainAll(buffer).Select(r => r.Name));
        }

        [Fact]
        public void A_sink_that_fails_keeps_the_records_it_never_saw()
        {
            GameState state = AdapterTestKit.BareState();
            Entity hero = AdapterTestKit.Actor(state);
            var buffer = new EventBuffer();
            for (int i = 0; i < 3; i++) buffer.Add(new GameEvent("damaged") { Target = hero, Amount = i }, state);

            var seen = new List<long>();
            Assert.Throws<InvalidOperationException>(() => buffer.Drain(record =>
            {
                if (record.Sequence == 2) throw new InvalidOperationException("presentation failed");
                seen.Add(record.Sequence);
            }));

            Assert.Equal(new long[] { 1 }, seen);
            Assert.False(buffer.Draining);

            // The failed record and everything behind it are still there to try again.
            Assert.Equal(new long[] { 2, 3 }, AdapterTestKit.DrainAll(buffer).Select(r => r.Sequence));
        }

        [Fact]
        public void The_oldest_records_go_when_the_buffer_overflows()
        {
            GameState state = AdapterTestKit.BareState();
            Entity hero = AdapterTestKit.Actor(state);
            var buffer = new EventBuffer { Capacity = 2 };

            for (int i = 0; i < 4; i++) buffer.Add(new GameEvent("damaged") { Target = hero }, state);

            Assert.Equal(2, buffer.Count);
            Assert.Equal(2, buffer.Dropped);

            // Presentation this far behind can only catch up by skipping to the recent past.
            Assert.Equal(new long[] { 3, 4 }, AdapterTestKit.DrainAll(buffer).Select(r => r.Sequence));
        }

        [Fact]
        public void A_capacity_of_zero_is_no_limit()
        {
            GameState state = AdapterTestKit.BareState();
            Entity hero = AdapterTestKit.Actor(state);
            var buffer = new EventBuffer { Capacity = 0 };

            for (int i = 0; i < 100; i++) buffer.Add(new GameEvent("damaged") { Target = hero }, state);

            Assert.Equal(100, buffer.Count);
            Assert.Equal(0, buffer.Dropped);
        }

        // Snapshots ------------------------------------------------------------------------------

        [Fact]
        public void Each_record_snapshots_the_stats_of_its_own_moment()
        {
            var buffer = new EventBuffer();
            CardRuntime runtime = AdapterTestKit.Start(buffer, out _, "Twin");
            Entity enemy = AdapterTestKit.Enemy(runtime);

            Assert.Equal(PlayResult.Played, runtime.Play("Twin", enemy));

            List<EventRecord> hits = AdapterTestKit.DrainAll(buffer).Where(r => r.Name == "damaged").ToList();

            Assert.Equal(2, hits.Count);
            Assert.Equal(14, hits[0].StatOf(enemy.Id, "hp"));
            Assert.Equal(10, hits[1].StatOf(enemy.Id, "hp"));

            // The live entity only knows where the whole action ended, which is the point of all this.
            Assert.Equal(10, enemy.GetInt("hp"));
        }

        [Fact]
        public void The_snapshot_covers_both_sides_of_the_event()
        {
            var buffer = new EventBuffer();
            CardRuntime runtime = AdapterTestKit.Start(buffer, out _, "Twin");
            Entity enemy = AdapterTestKit.Enemy(runtime);
            Entity player = runtime.Player!;

            runtime.Play("Twin", enemy);
            EventRecord hit = AdapterTestKit.DrainAll(buffer).First(r => r.Name == "damaged");

            Assert.Equal(80, hit.StatOf(player.Id, "hp"));
            Assert.Equal(3, hit.StatOf(player.Id, "energy"));
            Assert.Equal(0, hit.StatOf(enemy.Id, "block"));
        }

        [Fact]
        public void Only_the_tracked_stats_are_snapshotted()
        {
            var buffer = new EventBuffer();
            CardRuntime runtime = AdapterTestKit.Start(buffer, out BufferHost host, "Twin");
            Entity enemy = AdapterTestKit.Enemy(runtime);
            host.TrackedStats = new[] { "hp" };

            runtime.Play("Twin", enemy);
            EventRecord hit = AdapterTestKit.DrainAll(buffer).First(r => r.Name == "damaged");

            Assert.True(hit.TryGetStat(enemy.Id, "hp", out int hp));
            Assert.Equal(14, hp);
            Assert.False(hit.TryGetStat(enemy.Id, "block", out _));

            // Asking for a stat nobody tracked says so rather than pretending it was zero.
            Assert.Equal(-1, hit.StatOf(enemy.Id, "block", fallback: -1));
        }

        [Fact]
        public void An_entity_with_none_of_the_tracked_stats_is_left_out()
        {
            var buffer = new EventBuffer();
            CardRuntime runtime = AdapterTestKit.Start(buffer, out _, "Twin");
            Entity enemy = AdapterTestKit.Enemy(runtime);

            runtime.Play("Twin", enemy);
            EventRecord played = AdapterTestKit.DrainAll(buffer).First(r => r.Name == "card_played");

            // The card is named by the event, but a card has no hp, block or energy to show.
            Assert.NotEqual(0, played.Card);
            Assert.False(played.After.ContainsKey(played.Card));
            Assert.Empty(played.StatsOf(played.Card));
        }

        // What a record carries ------------------------------------------------------------------

        [Fact]
        public void A_record_names_everyone_by_id_and_zero_means_nobody()
        {
            var buffer = new EventBuffer();
            CardRuntime runtime = AdapterTestKit.Start(buffer, out _, "Twin");
            Entity enemy = AdapterTestKit.Enemy(runtime);
            Entity player = runtime.Player!;

            runtime.Play("Twin", enemy);
            List<EventRecord> records = AdapterTestKit.DrainAll(buffer);
            EventRecord played = records.First(r => r.Name == "card_played");

            Assert.Equal(player.Id, played.Source);
            Assert.Equal(enemy.Id, played.Target);
            Assert.NotEqual(0, played.Card);
            Assert.Equal(0, played.AmountInt);      // Twin costs nothing

            EventRecord hit = records.First(r => r.Name == "damaged");
            Assert.Equal(6, hit.AmountInt);
            Assert.Equal(6.0, hit.AmountRaw);
        }

        [Fact]
        public void A_record_carries_the_tags_and_values_of_its_event()
        {
            var buffer = new EventBuffer();
            CardRuntime runtime = AdapterTestKit.Start(buffer, out _, "Twin");
            Entity enemy = AdapterTestKit.Enemy(runtime);

            runtime.Play("Twin", enemy);
            EventRecord hit = AdapterTestKit.DrainAll(buffer).First(r => r.Name == "damaged");

            // Sorted, because the order a HashSet enumerates in is not a fact about the game.
            Assert.Equal(new[] { "attack", "fire" }, hit.Tags);
            Assert.Equal(6, hit.Values["total"].Number.ToInt());
            Assert.Equal(0, hit.Values["blocked"].Number.ToInt());
        }

        [Fact]
        public void A_buffered_record_is_always_in_the_after_phase()
        {
            var buffer = new EventBuffer();
            CardRuntime runtime = AdapterTestKit.Start(buffer, out _, "Twin");

            runtime.Play("Twin", AdapterTestKit.Enemy(runtime));

            foreach (EventRecord record in AdapterTestKit.DrainAll(buffer))
            {
                // The host only hears an event that fully resolved, whoever did or did not listen.
                Assert.Equal(EventPhase.After, record.Phase);
                Assert.Equal("after", record.PhaseName);
            }
        }

        [Fact]
        public void Values_are_copied_rather_than_watched()
        {
            GameState state = AdapterTestKit.BareState();
            Entity hero = AdapterTestKit.Actor(state);
            var buffer = new EventBuffer();

            var gameEvent = new GameEvent("damaged") { Target = hero };
            gameEvent.Data["total"] = Value.FromNumber(6);
            gameEvent.Tags.Add("fire");
            buffer.Add(gameEvent, state);

            // The core reuses events freely once they have resolved; a record must not follow along.
            gameEvent.Data["total"] = Value.FromNumber(99);
            gameEvent.Tags.Add("ice");

            EventRecord record = AdapterTestKit.DrainAll(buffer)[0];
            Assert.Equal(6, record.Values["total"].Number.ToInt());
            Assert.Equal(new[] { "fire" }, record.Tags);
        }

        [Fact]
        public void Recording_needs_an_event_and_a_game()
        {
            GameState state = AdapterTestKit.BareState();
            var buffer = new EventBuffer();

            Assert.Throws<ArgumentNullException>(() => buffer.Add(null!, state));
            Assert.Throws<ArgumentNullException>(() => buffer.Add(new GameEvent("damaged"), null!));
            Assert.Throws<ArgumentNullException>(() => buffer.Drain(null!));
        }
    }
}
