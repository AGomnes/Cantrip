using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cantrip.Diagnostics;

namespace Cantrip.Runtime
{
    /// <summary>
    /// One recorded step. Every action records what caused it, which is the foundation
    /// for the causality tree, modifier breakdowns and replays.
    /// </summary>
    public sealed class TraceEntry
    {
        internal TraceEntry(
            long id,
            long? parentId,
            long time,
            string kind,
            string description,
            string? source,
            string? listener,
            SourceSpan span,
            IReadOnlyDictionary<string, object>? values)
        {
            Id = id;
            ParentId = parentId;
            Time = time;
            Kind = kind;
            Description = description;
            Source = source;
            Listener = listener;
            Span = span;
            Values = values ?? EmptyValues;
        }

        private static readonly IReadOnlyDictionary<string, object> EmptyValues = new Dictionary<string, object>();

        /// <summary>This entry's number, unique within the log and increasing. <see cref="TraceLog.Find"/> takes it.</summary>
        public long Id { get; }

        /// <summary>
        /// What caused this step, or null for a top-level action. An entry whose parent has been dropped
        /// by the ring buffer keeps its id, so a viewer has to treat a parent it cannot find as a root.
        /// </summary>
        public long? ParentId { get; }

        /// <summary>Clock time when the step happened.</summary>
        public long Time { get; }

        /// <summary>Category: <c>action</c>, <c>event</c>, <c>listener</c>, <c>verb</c>, <c>modifier</c>, <c>warning</c>...</summary>
        public string Kind { get; }

        /// <summary>What happened, in one line, for a person to read.</summary>
        public string Description { get; }

        /// <summary>The entity responsible, formatted as <c>Name#id</c>.</summary>
        public string? Source { get; }

        /// <summary>The listener that ran, if this step is a trigger.</summary>
        public string? Listener { get; }

        /// <summary>Content location, so tools can jump from any number to the line that produced it.</summary>
        public SourceSpan Span { get; }

        /// <summary>
        /// The numbers behind the step (the amount, the before and after, whatever the site recorded),
        /// so a tool can show a breakdown instead of parsing <see cref="Description"/>. Empty rather
        /// than null when there are none.
        /// </summary>
        public IReadOnlyDictionary<string, object> Values { get; }

        /// <summary>The kind, the description, the values and the source location on one line, as the tree view prints it.</summary>
        public override string ToString()
        {
            var text = new StringBuilder();
            text.Append('[').Append(Kind).Append("] ").Append(Description);
            if (Values.Count > 0)
            {
                text.Append(" {");
                text.Append(string.Join(", ", Values.Select(kv => $"{kv.Key}={kv.Value}")));
                text.Append('}');
            }
            if (!Span.IsNone) text.Append("  @ ").Append(Span);
            return text.ToString();
        }
    }

    /// <summary>
    /// Causality log. Disabled by default and free when disabled: every recording site checks
    /// <see cref="Enabled"/> before allocating anything.
    /// </summary>
    public sealed class TraceLog
    {
        private readonly List<TraceEntry> _entries = new List<TraceEntry>();
        private readonly Stack<long> _scope = new Stack<long>();
        private long _nextId = 1;

        /// <summary>
        /// Whether anything is recorded. Off by default and free when off: every recording site checks
        /// this before allocating. Turning it on mid-game starts a log from that moment, with no history
        /// behind it.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// When set, only the most recent entries are kept. Real-time games use this as the
        /// "last few seconds" ring buffer.
        /// </summary>
        public int? Capacity { get; set; }

        /// <summary>
        /// Everything recorded, oldest first. It is the live list, so its count is also the mark
        /// <see cref="TruncateTo"/> takes.
        /// </summary>
        public IReadOnlyList<TraceEntry> Entries => _entries;

        /// <summary>
        /// How many entries the ring buffer has discarded since the log was last cleared. A viewer
        /// can say "412 entries dropped" rather than showing a gap and implying nothing happened.
        /// </summary>
        public long Dropped { get; private set; }

        /// <summary>The entry new records attach to as children.</summary>
        public long? CurrentParent => _scope.Count > 0 ? _scope.Peek() : (long?)null;

        /// <summary>
        /// Records one step and returns its id, or 0 when the log is disabled. Zero is never a real
        /// id, so it can be passed to <see cref="Scope"/> without checking.
        /// </summary>
        /// <param name="time">The clock time the step happened at.</param>
        /// <param name="kind">A category: <c>action</c>, <c>event</c>, <c>listener</c>, <c>verb</c>, <c>modifier</c>, <c>warning</c>.</param>
        /// <param name="description">One line, for a person.</param>
        /// <param name="source">The entity responsible, formatted as <c>Name#id</c>.</param>
        /// <param name="listener">The listener that ran, when this step is a trigger.</param>
        /// <param name="span">The line of content behind it.</param>
        /// <param name="values">The numbers behind it, for a tool that would rather not parse the description.</param>
        /// <param name="parentOverride">Attaches this to a parent other than the open scope: how work queued earlier is recorded under what queued it.</param>
        public long Record(
            long time,
            string kind,
            string description,
            string? source = null,
            string? listener = null,
            SourceSpan span = default,
            IReadOnlyDictionary<string, object>? values = null,
            long? parentOverride = null)
        {
            if (!Enabled) return 0;

            long id = _nextId++;
            _entries.Add(new TraceEntry(id, parentOverride ?? CurrentParent, time, kind, description, source, listener, span, values));

            if (Capacity.HasValue && _entries.Count > Capacity.Value)
            {
                int excess = _entries.Count - Capacity.Value;
                _entries.RemoveRange(0, excess);
                Dropped += excess;
            }

            return id;
        }

        /// <summary>Makes <paramref name="id"/> the parent of everything recorded until the scope is disposed.</summary>
        public IDisposable Scope(long id)
        {
            if (!Enabled || id == 0) return NullScope.Instance;
            _scope.Push(id);
            return new PopScope(this);
        }

        /// <summary>Drops every entry, every open scope and the dropped count. Ids are not reused, so an id from before a clear finds nothing rather than something else.</summary>
        public void Clear()
        {
            _entries.Clear();
            _scope.Clear();
            Dropped = 0;
        }

        /// <summary>
        /// Drops everything recorded after a mark taken from <see cref="Entries"/>.Count. An action
        /// that is rolled back (a pending player choice) uses this so the log shows only what
        /// actually happened.
        /// </summary>
        public void TruncateTo(int count)
        {
            if (count < 0 || count >= _entries.Count) return;
            _entries.RemoveRange(count, _entries.Count - count);
            _scope.Clear();
        }

        /// <summary>
        /// The entry with that id, or null when it never existed or the ring buffer has dropped it. It
        /// searches from the newest backwards, so recent entries are cheap and old ones are not.
        /// </summary>
        public TraceEntry? Find(long id)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Id == id) return _entries[i];
            }
            return null;
        }

        /// <summary>Walks from an entry up to the root action: the "why did this happen" view.</summary>
        public IEnumerable<TraceEntry> Ancestors(long id)
        {
            TraceEntry? current = Find(id);
            int guard = 0;
            while (current?.ParentId != null && guard++ < 10_000)
            {
                current = Find(current.ParentId.Value);
                if (current != null) yield return current;
            }
        }

        /// <summary>
        /// The steps one entry caused, oldest first: one level, not the whole subtree. It scans the
        /// whole log per call, so building a tree from it is quadratic; <see cref="FormatTree"/> does it
        /// in one pass.
        /// </summary>
        public IEnumerable<TraceEntry> Children(long id) => _entries.Where(e => e.ParentId == id);

        /// <summary>Renders the log as an indented tree, the text form of the causality view.</summary>
        public string FormatTree()
        {
            var text = new StringBuilder();
            var children = _entries.Where(e => e.ParentId != null).ToLookup(e => e.ParentId!.Value);
            var present = new HashSet<long>(_entries.Select(e => e.Id));

            foreach (TraceEntry root in _entries.Where(e => e.ParentId == null || !present.Contains(e.ParentId.Value)))
                Append(root, 0);

            return text.ToString();

            void Append(TraceEntry entry, int depth)
            {
                text.Append(' ', depth * 2).Append(entry).AppendLine();
                foreach (TraceEntry child in children[entry.Id]) Append(child, depth + 1);
            }
        }

        private sealed class PopScope : IDisposable
        {
            private TraceLog? _log;

            public PopScope(TraceLog log) => _log = log;

            public void Dispose()
            {
                if (_log == null) return;
                if (_log._scope.Count > 0) _log._scope.Pop();
                _log = null;
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();

            public void Dispose() { }
        }
    }
}
