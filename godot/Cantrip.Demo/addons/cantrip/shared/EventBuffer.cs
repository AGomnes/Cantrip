#nullable enable
using System;
using System.Collections.Generic;
using Cantrip.Runtime;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// Holds the events of one action until it has finished resolving.
    /// </summary>
    /// <remarks>
    /// The core calls <c>IEffectHost.OnEvent</c> from inside <c>Raise</c>, with the interpreter
    /// half way through an action. Emitting a signal there would let a handler play another card
    /// and re-enter an interpreter that is not re-entrant. So the host only appends here, and the
    /// runtime node drains to signals after the top-level call has returned.
    /// <para>
    /// Rolled-back attempts need no filtering: the core buffers host notifications for an attempt
    /// of its own accord and drops them if the action is abandoned, so nothing that did not happen
    /// ever reaches <see cref="Add"/>. <see cref="Discard"/> is for the adapter's own error paths.
    /// </para>
    /// </remarks>
    public sealed class EventBuffer
    {
        /// <summary>What a card game shows next to every actor, and so what is worth snapshotting.</summary>
        public static readonly IReadOnlyList<string> DefaultTrackedStats = new[] { "hp", "block", "energy" };

        private readonly List<EventRecord> _pending = new List<EventRecord>();
        private readonly HashSet<string> _watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _masked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private long _sequence;
        private int _capacity = 4096;

        /// <summary>Records waiting to be drained.</summary>
        public int Count => _pending.Count;

        /// <summary>True while <see cref="Drain"/> is delivering, which is when a sink must not re-enter.</summary>
        public bool Draining { get; private set; }

        /// <summary>The sequence number of the last recorded event; 0 before the first.</summary>
        public long LastSequence => _sequence;

        /// <summary>
        /// How many records may wait at once, or 0 for no limit. A game that never drains would
        /// otherwise grow without bound.
        /// </summary>
        public int Capacity
        {
            get => _capacity;
            set
            {
                _capacity = Math.Max(0, value);
                Trim();
            }
        }

        /// <summary>
        /// Records lost to <see cref="Capacity"/>. Presentation this far behind can never catch up,
        /// so the oldest go first and the game keeps animating towards the state it is actually in.
        /// </summary>
        public long Dropped { get; private set; }

        // Filtering ------------------------------------------------------------------------------

        /// <summary>Names the game presents. Empty means every name that is not masked.</summary>
        public IReadOnlyCollection<string> Watched => _watched;

        /// <summary>Names never recorded, whatever the watch list says.</summary>
        public IReadOnlyCollection<string> Masked => _masked;

        /// <summary>
        /// Records only these event names from now on. A real-time game raising events every tick
        /// wants this: a snapshot it never presents is pure allocation.
        /// </summary>
        public void Watch(params string[] names) => AddNames(_watched, names);

        /// <summary>Never record these event names, however noisy the content that raises them.</summary>
        public void Mask(params string[] names) => AddNames(_masked, names);

        public void Unwatch(params string[] names) => RemoveNames(_watched, names);

        public void Unmask(params string[] names) => RemoveNames(_masked, names);

        /// <summary>Back to recording everything.</summary>
        public void ClearFilters()
        {
            _watched.Clear();
            _masked.Clear();
        }

        /// <summary>Whether an event of this name would be kept.</summary>
        public bool IsRecorded(string? name)
        {
            if (name == null) return false;
            if (_masked.Contains(name)) return false;
            return _watched.Count == 0 || _watched.Contains(name);
        }

        private static void AddNames(HashSet<string> set, string[]? names)
        {
            if (names == null) return;
            foreach (string name in names)
            {
                if (!string.IsNullOrEmpty(name)) set.Add(name);
            }
        }

        private static void RemoveNames(HashSet<string> set, string[]? names)
        {
            if (names == null) return;
            foreach (string name in names)
            {
                if (name != null) set.Remove(name);
            }
        }

        // Recording ------------------------------------------------------------------------------

        /// <summary>
        /// Records one resolved event, snapshotting the tracked stats of everyone it involved.
        /// </summary>
        /// <param name="trackedStats">Null for <see cref="DefaultTrackedStats"/>.</param>
        /// <remarks>
        /// Reading a stat here runs the modifier pipeline, which is a pure evaluation the
        /// interpreter itself performs constantly during resolution. Nothing in this method queues
        /// work, raises an event or calls back into the runtime, which is the one rule the host
        /// side of the adapter has to keep.
        /// </remarks>
        public void Add(GameEvent gameEvent, GameState state, IReadOnlyList<string>? trackedStats = null)
        {
            if (gameEvent == null) throw new ArgumentNullException(nameof(gameEvent));
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!IsRecorded(gameEvent.Name)) return;

            var record = new EventRecord(
                ++_sequence,
                gameEvent.Name,
                // The host is notified once an event has fully resolved, so this is the only phase
                // a record can be in; GameEvent.Phase at this point is a leftover of dispatch.
                EventPhase.After,
                state.Clock.Now,
                gameEvent.Source?.Id ?? 0,
                gameEvent.Target?.Id ?? 0,
                gameEvent.Card?.Id ?? 0,
                gameEvent.Amount,
                gameEvent.Replaced,
                CopyTags(gameEvent),
                CopyValues(gameEvent),
                Snapshot(gameEvent, trackedStats ?? DefaultTrackedStats));

            _pending.Add(record);
            Trim();
        }

        private static IReadOnlyList<string> CopyTags(GameEvent gameEvent)
        {
            if (gameEvent.Tags.Count == 0) return EventRecord.NoTags;

            var tags = new List<string>(gameEvent.Tags);
            // Ordinal, like the trace: the order a set enumerates in is not a fact about the game.
            tags.Sort(StringComparer.Ordinal);
            return tags;
        }

        private static IReadOnlyDictionary<string, Value> CopyValues(GameEvent gameEvent)
        {
            if (gameEvent.Data.Count == 0) return EventRecord.NoValues;
            return new Dictionary<string, Value>(gameEvent.Data, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The tracked stats of everyone the event names, read now. Entities with none of the
        /// tracked stats (a card has no hp) are left out rather than stored empty.
        /// </summary>
        private static IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>> Snapshot(
            GameEvent gameEvent, IReadOnlyList<string> trackedStats)
        {
            if (trackedStats.Count == 0) return EventRecord.NoStats;

            Dictionary<int, IReadOnlyDictionary<string, int>>? stats = null;
            Capture(gameEvent.Target, trackedStats, ref stats);
            Capture(gameEvent.Source, trackedStats, ref stats);
            Capture(gameEvent.Card, trackedStats, ref stats);
            return stats ?? EventRecord.NoStats;
        }

        private static void Capture(
            Entity? entity,
            IReadOnlyList<string> trackedStats,
            ref Dictionary<int, IReadOnlyDictionary<string, int>>? stats)
        {
            if (entity == null || (stats != null && stats.ContainsKey(entity.Id))) return;

            Dictionary<string, int>? values = null;
            for (int i = 0; i < trackedStats.Count; i++)
            {
                string stat = trackedStats[i];
                if (string.IsNullOrEmpty(stat) || !entity.HasStat(stat)) continue;

                values ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                values[stat] = entity.GetInt(stat);
            }

            if (values == null) return;
            stats ??= new Dictionary<int, IReadOnlyDictionary<string, int>>();
            stats[entity.Id] = values;
        }

        private void Trim()
        {
            if (_capacity <= 0 || _pending.Count <= _capacity) return;

            int excess = _pending.Count - _capacity;
            _pending.RemoveRange(0, excess);
            Dropped += excess;
        }

        // Draining -------------------------------------------------------------------------------

        /// <summary>
        /// Hands every waiting record to <paramref name="sink"/>, oldest first, and empties the
        /// buffer. Anything recorded while it runs waits for the next drain, so a sink that causes
        /// more events cannot spin here for ever.
        /// </summary>
        public void Drain(Action<EventRecord> sink)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            if (Draining) throw new InvalidOperationException(
                "EventBuffer.Drain is already running. Draining from inside a drain would deliver events out of order; queue the work instead.");
            if (_pending.Count == 0) return;

            EventRecord[] batch = _pending.ToArray();
            _pending.Clear();

            int delivered = 0;
            Draining = true;
            try
            {
                for (; delivered < batch.Length; delivered++) sink(batch[delivered]);
            }
            finally
            {
                Draining = false;

                // A sink that failed must not swallow the rest of the battle: what it never saw
                // goes back in front of whatever it caused.
                if (delivered < batch.Length)
                {
                    var remainder = new List<EventRecord>(batch.Length - delivered);
                    for (int i = delivered; i < batch.Length; i++) remainder.Add(batch[i]);
                    _pending.InsertRange(0, remainder);
                }
            }
        }

        /// <summary>
        /// Throws away everything waiting. For the adapter's own error paths, where an action failed
        /// and its half-told story must not be animated.
        /// </summary>
        public void Discard() => _pending.Clear();

        /// <summary>Forgets the dropped count as well as the records, for a fresh game.</summary>
        public void Reset()
        {
            _pending.Clear();
            Dropped = 0;
            _sequence = 0;
        }
    }
}
