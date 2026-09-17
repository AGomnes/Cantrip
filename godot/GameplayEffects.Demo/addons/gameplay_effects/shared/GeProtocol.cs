using System;
using System.Collections.Generic;
using System.Globalization;
using GameplayEffects.Runtime;

namespace GameplayEffects.GodotAdapter
{
    /// <summary>
    /// The names on the wire between the editor and a running game, and the shapes that travel on
    /// it. Engine-free on purpose: the interesting part is the batching, and that is worth testing
    /// without launching Godot.
    /// </summary>
    /// <remarks>
    /// Godot's debugger channel is capped — a live 4.6.1 run reports 2048 queued messages and
    /// 32768 characters a second — so nothing here streams per event. The game keeps a ring buffer
    /// and the editor pulls batches with a cursor, which also means a slow editor cannot back the
    /// game up.
    /// </remarks>
    public static class GeProtocol
    {
        /// <summary>The capture the game registers and the editor plugin claims.</summary>
        public const string Capture = "gameplay_effects";

        /// <summary>Bumped when a shape changes, so an editor and a game of different ages can say so.</summary>
        public const int Version = 1;

        // Editor to game.
        public const string Hello = "hello";
        public const string TraceEnable = "trace_enable";
        public const string TraceFetch = "trace_fetch";
        public const string Reload = "reload";
        public const string Execute = "execute";

        // Game to editor.
        public const string Welcome = "welcome";
        public const string Trace = "trace";
        public const string Reloaded = "reloaded";
        public const string Failed = "failed";

        /// <summary>The full message name, as both sides send it: <c>gameplay_effects:trace</c>.</summary>
        public static string Message(string name) => Capture + ":" + name;

        /// <summary>
        /// The bare name of a message addressed to this capture. Godot hands a capture its messages
        /// with the prefix already stripped in one direction and intact in the other, so this
        /// accepts either rather than making the caller care.
        /// </summary>
        public static bool TryName(string? message, out string name)
        {
            name = string.Empty;
            if (string.IsNullOrEmpty(message)) return false;

            int colon = message!.IndexOf(':');
            if (colon < 0)
            {
                name = message;
                return true;
            }

            if (string.CompareOrdinal(message, 0, Capture, 0, colon) != 0 || colon != Capture.Length) return false;

            name = message.Substring(colon + 1);
            return name.Length > 0;
        }
    }

    /// <summary>One recorded step, flattened for the wire.</summary>
    /// <remarks>
    /// Everything a panel needs to draw a row and open the line behind it. Values arrive as text
    /// because <see cref="TraceEntry.Values"/> is a bag of objects: converting here, once, keeps
    /// the engine boundary from meeting a type it has no rule for while someone is mid-debug.
    /// </remarks>
    public sealed class TraceDto
    {
        private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

        private TraceDto(
            long id,
            long parent,
            long time,
            string kind,
            string text,
            string source,
            string listener,
            string file,
            int line,
            int column,
            IReadOnlyDictionary<string, string> values)
        {
            Id = id;
            Parent = parent;
            Time = time;
            Kind = kind;
            Text = text;
            Source = source;
            Listener = listener;
            File = file;
            Line = line;
            Column = column;
            Values = values;
        }

        public long Id { get; }

        /// <summary>The step this one happened because of, or 0 for a root action.</summary>
        public long Parent { get; }

        public long Time { get; }

        /// <summary>action, event, listener, verb, modifier, warning...</summary>
        public string Kind { get; }

        public string Text { get; }

        public string Source { get; }

        /// <summary>The listener that ran, when this step is a trigger.</summary>
        public string Listener { get; }

        public string File { get; }

        public int Line { get; }

        public int Column { get; }

        public IReadOnlyDictionary<string, string> Values { get; }

        public static TraceDto Of(TraceEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            Dictionary<string, string>? values = null;
            foreach (KeyValuePair<string, object> value in entry.Values)
            {
                values ??= new Dictionary<string, string>(StringComparer.Ordinal);
                values[value.Key] = Format(value.Value);
            }

            return new TraceDto(
                entry.Id,
                entry.ParentId ?? 0,
                entry.Time,
                entry.Kind ?? string.Empty,
                entry.Description ?? string.Empty,
                entry.Source ?? string.Empty,
                entry.Listener ?? string.Empty,
                entry.Span.File ?? string.Empty,
                entry.Span.Line,
                entry.Span.Column,
                values ?? NoValues);
        }

        /// <summary>
        /// A trace value as text. Numbers go through the invariant culture, because a decimal comma
        /// in a debugger panel is a bug report waiting to happen.
        /// </summary>
        private static string Format(object? value)
        {
            switch (value)
            {
                case null: return string.Empty;
                case string text: return text;
                case bool flag: return flag ? "true" : "false";
                case Num number: return number.ToString();
                case IFormattable formattable: return formattable.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString() ?? string.Empty;
            }
        }

        public override string ToString() => Id + " [" + Kind + "] " + Text;
    }

    /// <summary>
    /// A slice of the trace: what happened after the cursor the editor last saw, how much was
    /// dropped before it got there, and whether there is more waiting.
    /// </summary>
    public sealed class TraceBatch
    {
        /// <summary>Kept well inside the channel's queue limit, so one fetch cannot flood it.</summary>
        public const int DefaultMax = 200;

        private static readonly IReadOnlyList<TraceDto> None = new TraceDto[0];

        private TraceBatch(IReadOnlyList<TraceDto> entries, long nextId, long dropped, bool more)
        {
            Entries = entries;
            NextId = nextId;
            Dropped = dropped;
            More = more;
        }

        public IReadOnlyList<TraceDto> Entries { get; }

        /// <summary>The cursor to send with the next fetch.</summary>
        public long NextId { get; }

        /// <summary>
        /// How many entries the ring buffer discarded in total. A panel says "412 dropped" rather
        /// than showing a gap and implying the game did nothing.
        /// </summary>
        public long Dropped { get; }

        public bool More { get; }

        /// <summary>
        /// Everything recorded after <paramref name="sinceId"/>, up to <paramref name="max"/>.
        /// Entries are handed out in the order they were recorded, and ids only ever rise, so the
        /// cursor is enough to resume — even across a buffer that trimmed itself in between.
        /// </summary>
        public static TraceBatch From(TraceLog log, long sinceId = 0, int max = DefaultMax)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));
            if (max <= 0) return new TraceBatch(None, sinceId, log.Dropped, false);

            List<TraceDto>? entries = null;
            long next = sinceId;
            bool more = false;

            IReadOnlyList<TraceEntry> all = log.Entries;
            for (int i = 0; i < all.Count; i++)
            {
                TraceEntry entry = all[i];
                if (entry.Id <= sinceId) continue;

                if (entries != null && entries.Count >= max)
                {
                    more = true;
                    break;
                }

                entries ??= new List<TraceDto>(Math.Min(max, all.Count));
                entries.Add(TraceDto.Of(entry));
                next = entry.Id;
            }

            return new TraceBatch(entries ?? None, next, log.Dropped, more);
        }

        public override string ToString() => Entries.Count + " entries, next " + NextId + (More ? ", more" : string.Empty);
    }
}
