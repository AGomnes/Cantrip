#nullable enable
using System;
using System.Collections.Generic;
using Cantrip.Runtime;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// One resolved event, as the game will present it: what happened, to whom, and what the numbers
    /// were at that instant.
    /// </summary>
    /// <remarks>
    /// The stat snapshot is why this type exists at all. <c>IEffectHost.OnEvent</c> fires in the
    /// middle of resolution, but an animation plays long after the whole action has finished, by
    /// which time the live entities show only its end state. A card that hits twice would otherwise
    /// play two hits against the same final hp.
    /// <para>
    /// Identities are kept as ids and numbers are copied. An entity's id never changes, so it can be
    /// resolved safely whenever the animation gets round to it; its stats change with every line of
    /// content that runs, so they are taken now or not at all.
    /// </para>
    /// </remarks>
    public sealed class EventRecord
    {
        internal static readonly IReadOnlyList<string> NoTags = new string[0];

        internal static readonly IReadOnlyDictionary<string, Value> NoValues =
            new Dictionary<string, Value>(0);

        internal static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>> NoStats =
            new Dictionary<int, IReadOnlyDictionary<string, int>>(0);

        private static readonly IReadOnlyDictionary<string, int> NoEntityStats = new Dictionary<string, int>(0);

        public EventRecord(
            long sequence,
            string name,
            EventPhase phase,
            long time,
            int source,
            int target,
            int card,
            Num amount,
            bool replaced = false,
            IReadOnlyList<string>? tags = null,
            IReadOnlyDictionary<string, Value>? values = null,
            IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>>? after = null)
        {
            Sequence = sequence;
            Name = name ?? string.Empty;
            Phase = phase;
            Time = time;
            Source = source;
            Target = target;
            Card = card;
            Amount = amount;
            Replaced = replaced;
            Tags = tags ?? NoTags;
            Values = values ?? NoValues;
            After = after ?? NoStats;
        }

        /// <summary>Position in the run of events, rising by one per recorded event. Never reused.</summary>
        public long Sequence { get; }

        public string Name { get; }

        /// <summary>
        /// Always <see cref="EventPhase.After"/> for a buffered event: the host is notified once an
        /// event has fully resolved. It is carried explicitly rather than read back off the event,
        /// because <c>GameEvent.Phase</c> is only written while listeners are being dispatched and so
        /// still reads "before" for any event nobody listened to.
        /// </summary>
        public EventPhase Phase { get; }

        /// <summary>Lower-case phase name, for the dictionary that crosses into GDScript.</summary>
        public string PhaseName => Phase.ToString().ToLowerInvariant();

        /// <summary>The clock when the event resolved: turns for a turn game, ticks for a real-time one.</summary>
        public long Time { get; }

        /// <summary>Entity id of whoever caused it, or 0.</summary>
        public int Source { get; }

        /// <summary>Entity id of whoever it happened to, or 0.</summary>
        public int Target { get; }

        /// <summary>Entity id of the card involved, or 0.</summary>
        public int Card { get; }

        public Num Amount { get; }

        /// <summary>The amount rounded, which is what damage numbers and counters show.</summary>
        public int AmountInt => Amount.ToInt();

        /// <summary>The unrounded amount, for anything that scales an animation by it.</summary>
        public double AmountRaw => Amount.ToDouble();

        /// <summary>True when an <c>instead</c> listener ran in place of the default action.</summary>
        public bool Replaced { get; }

        /// <summary>The event's type tags, ordinal-sorted so two runs of the same game agree.</summary>
        public IReadOnlyList<string> Tags { get; }

        /// <summary>Whatever the verb exposed as <c>event.&lt;name&gt;</c>, copied at the time.</summary>
        public IReadOnlyDictionary<string, Value> Values { get; }

        /// <summary>
        /// Tracked stats of the event's participants as they stood when it fired, keyed by entity id
        /// then stat name.
        /// </summary>
        public IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>> After { get; }

        /// <summary>The snapshot for one entity, or an empty map when it took no part in the event.</summary>
        public IReadOnlyDictionary<string, int> StatsOf(int entityId) =>
            After.TryGetValue(entityId, out IReadOnlyDictionary<string, int>? stats) ? stats : NoEntityStats;

        public bool TryGetStat(int entityId, string stat, out int value)
        {
            value = 0;
            return stat != null
                && After.TryGetValue(entityId, out IReadOnlyDictionary<string, int>? stats)
                && stats.TryGetValue(stat, out value);
        }

        /// <summary>The snapshotted stat, or <paramref name="fallback"/> when it was not tracked.</summary>
        public int StatOf(int entityId, string stat, int fallback = 0) =>
            TryGetStat(entityId, stat, out int value) ? value : fallback;

        public override string ToString() => Sequence + " " + Name;
    }
}
