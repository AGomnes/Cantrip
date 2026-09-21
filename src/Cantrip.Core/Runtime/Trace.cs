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

        public long Id { get; }
        public long? ParentId { get; }

        /// <summary>Clock time when the step happened.</summary>
        public long Time { get; }

        /// <summary>Category: <c>action</c>, <c>event</c>, <c>listener</c>, <c>verb</c>, <c>modifier</c>, <c>warning</c>...</summary>
        public string Kind { get; }

        public string Description { get; }

        /// <summary>The entity responsible, formatted as <c>Name#id</c>.</summary>
        public string? Source { get; }

        /// <summary>The listener that ran, if this step is a trigger.</summary>
        public string? Listener { get; }

        /// <summary>Content location, so tools can jump from any number to the line that produced it.</summary>
        public SourceSpan Span { get; }

        public IReadOnlyDictionary<string, object> Values { get; }

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

        public bool Enabled { get; set; }

        /// <summary>
        /// When set, only the most recent entries are kept. Real-time games use this as the
        /// "last few seconds" ring buffer.
        /// </summary>
        public int? Capacity { get; set; }

        public IReadOnlyList<TraceEntry> Entries => _entries;

        /// <summary>
        /// How many entries the ring buffer has discarded since the log was last cleared. A viewer
        /// can say "412 entries dropped" rather than showing a gap and implying nothing happened.
        /// </summary>
        public long Dropped { get; private set; }

        /// <summary>The entry new records attach to as children.</summary>
        public long? CurrentParent => _scope.Count > 0 ? _scope.Peek() : (long?)null;

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
