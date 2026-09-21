using System.Collections.Generic;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// The editor/game channel: message naming, and the batching that keeps a busy game from
    /// flooding a transport with a hard limit on it.
    /// </summary>
    public sealed class CantripProtocolTests
    {
        private static TraceLog Log(int entries, int? capacity = null)
        {
            var log = new TraceLog { Enabled = true, Capacity = capacity };
            for (int i = 1; i <= entries; i++)
            {
                log.Record(i, "action", "step " + i, "Card#1", span: new SourceSpan("res://content/cards.cantrip", i, 3, 4));
            }
            return log;
        }

        [Fact]
        public void A_message_is_named_for_its_capture()
        {
            Assert.Equal("cantrip:trace", CantripProtocol.Message(CantripProtocol.Trace));

            Assert.True(CantripProtocol.TryName("cantrip:trace", out string withPrefix));
            Assert.Equal("trace", withPrefix);

            // Godot strips the prefix in one direction and not the other, so both are accepted.
            Assert.True(CantripProtocol.TryName("trace", out string bare));
            Assert.Equal("trace", bare);
        }

        [Fact]
        public void Messages_for_another_capture_are_not_ours()
        {
            Assert.False(CantripProtocol.TryName("something_else:trace", out _));
            Assert.False(CantripProtocol.TryName("cantrip:", out _));
            Assert.False(CantripProtocol.TryName("", out _));
            Assert.False(CantripProtocol.TryName(null, out _));
        }

        [Fact]
        public void An_entry_carries_the_line_that_produced_it()
        {
            var log = new TraceLog { Enabled = true };
            log.Record(
                3,
                "verb",
                "deal 6 to Jaw Worm#2",
                "Fireball#7",
                "Frozen#9: on owner.damaged",
                new SourceSpan("res://content/cards.cantrip", 12, 5, 6),
                new Dictionary<string, object> { ["amount"] = Num.FromInt(9), ["blocked"] = 0, ["tag"] = "fire", ["killed"] = true });

            TraceDto entry = TraceDto.Of(log.Entries[0]);

            Assert.Equal("verb", entry.Kind);
            Assert.Equal("deal 6 to Jaw Worm#2", entry.Text);
            Assert.Equal("Fireball#7", entry.Source);
            Assert.Equal("Frozen#9: on owner.damaged", entry.Listener);
            Assert.Equal("res://content/cards.cantrip", entry.File);
            Assert.Equal(12, entry.Line);
            Assert.Equal(5, entry.Column);

            // Values become text here so the engine boundary never meets a type it has no rule for.
            Assert.Equal("9", entry.Values["amount"]);
            Assert.Equal("0", entry.Values["blocked"]);
            Assert.Equal("fire", entry.Values["tag"]);
            Assert.Equal("true", entry.Values["killed"]);
        }

        [Fact]
        public void A_batch_starts_at_the_beginning_and_reports_its_cursor()
        {
            TraceBatch batch = TraceBatch.From(Log(5));

            Assert.Equal(5, batch.Entries.Count);
            Assert.Equal(5, batch.NextId);
            Assert.False(batch.More);
            Assert.Equal(0, batch.Dropped);
        }

        [Fact]
        public void A_batch_stops_at_its_limit_and_says_there_is_more()
        {
            TraceLog log = Log(10);

            TraceBatch first = TraceBatch.From(log, 0, 4);
            Assert.Equal(4, first.Entries.Count);
            Assert.Equal(4, first.NextId);
            Assert.True(first.More);

            TraceBatch second = TraceBatch.From(log, first.NextId, 4);
            Assert.Equal("step 5", second.Entries[0].Text);
            Assert.True(second.More);

            TraceBatch third = TraceBatch.From(log, second.NextId, 4);
            Assert.Equal(2, third.Entries.Count);
            Assert.False(third.More);

            // Caught up: fetching again returns nothing and leaves the cursor where it was.
            TraceBatch caughtUp = TraceBatch.From(log, third.NextId, 4);
            Assert.Empty(caughtUp.Entries);
            Assert.Equal(third.NextId, caughtUp.NextId);
        }

        [Fact]
        public void A_trimmed_ring_buffer_is_reported_rather_than_hidden()
        {
            // Ten steps through a buffer that keeps three: the reader sees the recent past, and is
            // told how much it missed instead of being shown a gap.
            TraceLog log = Log(10, capacity: 3);

            TraceBatch batch = TraceBatch.From(log);

            Assert.Equal(3, batch.Entries.Count);
            Assert.Equal("step 8", batch.Entries[0].Text);
            Assert.Equal(7, batch.Dropped);
            Assert.Equal(10, batch.NextId);
        }

        [Fact]
        public void Asking_for_nothing_returns_nothing()
        {
            TraceBatch batch = TraceBatch.From(Log(5), 2, 0);

            Assert.Empty(batch.Entries);
            Assert.Equal(2, batch.NextId);
            Assert.False(batch.More);
        }
    }
}
