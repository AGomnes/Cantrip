using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cantrip.Diagnostics
{
    /// <summary>
    /// How much a diagnostic matters. Only <see cref="Error"/> stops content loading; the other two
    /// are advice a tool may show and a game may ignore.
    /// </summary>
    public enum DiagnosticSeverity
    {
        /// <summary>
        /// Worth knowing, never wrong. The linter uses it for things that are legal and probably not
        /// what was meant.
        /// </summary>
        Info,

        /// <summary>
        /// The content loads and runs, but something in it does less than it looks like it does: a
        /// clause that is accepted and ignored, a modifier that can never match. Most of what the linter
        /// finds is here, and it is the severity worth failing a content build on.
        /// </summary>
        Warning,

        /// <summary>
        /// The content cannot be used as written. <see cref="DiagnosticBag.ThrowIfErrors"/> throws on
        /// these and nothing else.
        /// </summary>
        Error,
    }

    /// <summary>A single parser or linter message, tied to a source location.</summary>
    public sealed class Diagnostic
    {
        /// <summary>
        /// Builds a message a tool can report. Games rarely call this; the one that does is a host
        /// adding a finding of its own to a bag it is about to print alongside the engine's.
        /// </summary>
        /// <param name="severity">Whether this stops content loading. See <see cref="DiagnosticSeverity"/>.</param>
        /// <param name="code">A stable <c>CT</c> code, so the message can be suppressed by code.</param>
        /// <param name="message">One sentence, in the voice the rest of the tool speaks in.</param>
        /// <param name="span">Where in the content. <see cref="SourceSpan.None"/> when there is no line to point at.</param>
        /// <param name="suggestion">A single closest spelling, or null. It is the word alone, not a sentence.</param>
        public Diagnostic(
            DiagnosticSeverity severity,
            string code,
            string message,
            SourceSpan span,
            string? suggestion = null)
        {
            Severity = severity;
            Code = code;
            Message = message;
            Span = span;
            Suggestion = suggestion;
        }

        /// <summary>Whether this one stops content loading. <see cref="DiagnosticBag.ThrowIfErrors"/> looks at nothing else.</summary>
        public DiagnosticSeverity Severity { get; }

        /// <summary>
        /// Stable identifier such as <c>CT0104</c>, so a rule can be suppressed by code and a
        /// message can be looked up without parsing its text.
        /// </summary>
        /// <remarks>
        /// The codes are not a fixed width. Loading and parsing use four digits (<c>CT0001</c> to
        /// <c>CT0202</c>); the linter uses three (<c>CT301</c> upwards), and so do the description
        /// builder's <c>CT4xx</c>. Anything matching them with a pattern has to allow both.
        /// </remarks>
        public string Code { get; }

        /// <summary>
        /// The sentence to show, without the location or the code in front of it.
        /// <see cref="ToString"/> is what assembles the whole line.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Where in the content, or <see cref="SourceSpan.None"/> for a finding about the library as a
        /// whole. A tool that jumps to a diagnostic should check <see cref="SourceSpan.IsNone"/> first.
        /// </summary>
        public SourceSpan Span { get; }

        /// <summary>Optional "did you mean ...?" hint.</summary>
        public string? Suggestion { get; }

        /// <summary>
        /// The whole line, as the CLI prints it: <c>file:line:column: severity code: message</c>, with
        /// the suggestion appended when there is one. It is the form an editor's problem list can parse
        /// back into a location.
        /// </summary>
        public override string ToString()
        {
            var text = new StringBuilder();
            text.Append(Span.ToString()).Append(": ");
            text.Append(Severity.ToString().ToLowerInvariant()).Append(' ').Append(Code).Append(": ");
            text.Append(Message);
            if (!string.IsNullOrEmpty(Suggestion)) text.Append(" Did you mean `").Append(Suggestion).Append("`?");
            return text.ToString();
        }
    }

    /// <summary>Collects diagnostics produced while loading or linting content.</summary>
    public sealed class DiagnosticBag : IReadOnlyCollection<Diagnostic>
    {
        private readonly List<Diagnostic> _items = new List<Diagnostic>();

        /// <summary>How many diagnostics, of every severity. Zero is not the same as "loaded cleanly": see <see cref="HasErrors"/>.</summary>
        public int Count => _items.Count;

        /// <summary>
        /// Whether anything in here stops the content being used. This, not <see cref="Count"/>, is the
        /// check a loader makes: a library with forty warnings is a library that runs.
        /// </summary>
        public bool HasErrors => _items.Any(d => d.Severity == DiagnosticSeverity.Error);

        /// <summary>The errors alone, in the order they were found. Evaluated on each enumeration rather than stored.</summary>
        public IEnumerable<Diagnostic> Errors => _items.Where(d => d.Severity == DiagnosticSeverity.Error);

        /// <summary>The warnings alone. Note that this leaves out <see cref="DiagnosticSeverity.Info"/>, so it is not "everything that is not an error".</summary>
        public IEnumerable<Diagnostic> Warnings => _items.Where(d => d.Severity == DiagnosticSeverity.Warning);

        /// <summary>Appends one already-built diagnostic. Nothing is deduplicated, so the same finding added twice is reported twice.</summary>
        public void Add(Diagnostic diagnostic) => _items.Add(diagnostic);

        /// <summary>Records an error: the content cannot be used as written.</summary>
        public void Error(string code, string message, SourceSpan span, string? suggestion = null) =>
            Add(new Diagnostic(DiagnosticSeverity.Error, code, message, span, suggestion));

        /// <summary>Records a warning: the content runs, but part of it does less than it looks like it does.</summary>
        public void Warn(string code, string message, SourceSpan span, string? suggestion = null) =>
            Add(new Diagnostic(DiagnosticSeverity.Warning, code, message, span, suggestion));

        /// <summary>Records a note. Tools may hide these by default; nothing in the engine acts on them.</summary>
        public void Info(string code, string message, SourceSpan span, string? suggestion = null) =>
            Add(new Diagnostic(DiagnosticSeverity.Info, code, message, span, suggestion));

        /// <summary>Appends a whole run of diagnostics, keeping their order: how a linter's findings join a loader's.</summary>
        public void AddRange(IEnumerable<Diagnostic> diagnostics) => _items.AddRange(diagnostics);

        /// <summary>Throws if anything in the bag is an error. Used by the strict loading paths.</summary>
        public void ThrowIfErrors()
        {
            if (!HasErrors) return;
            throw new DslException(Errors.ToList());
        }

        /// <summary>Every diagnostic, of every severity, in the order it was found.</summary>
        public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Every diagnostic, one per line. Useful in a test's failure message; a tool should format them itself.</summary>
        public override string ToString() => string.Join(Environment.NewLine, _items);
    }

    /// <summary>Thrown when content cannot be loaded. Carries every error, not just the first.</summary>
    public sealed class DslException : Exception
    {
        /// <summary>
        /// Carries a whole batch of errors, so one throw reports every problem in the file rather than
        /// the first. <see cref="Exception.Message"/> lists them all, indented.
        /// </summary>
        public DslException(IReadOnlyList<Diagnostic> diagnostics)
            : base(Describe(diagnostics))
        {
            Diagnostics = diagnostics;
        }

        /// <summary>The single-error form. <see cref="Diagnostics"/> still holds a list, of one.</summary>
        public DslException(Diagnostic diagnostic)
            : this(new[] { diagnostic })
        {
        }

        /// <summary>
        /// Every error that caused this, not just the one the message begins with. A tool that catches
        /// this should report these rather than <see cref="Exception.Message"/>, which is a summary.
        /// </summary>
        public IReadOnlyList<Diagnostic> Diagnostics { get; }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            if (diagnostics == null || diagnostics.Count == 0) return "Content failed to load.";
            if (diagnostics.Count == 1) return diagnostics[0].ToString();
            return $"{diagnostics.Count} problems:{Environment.NewLine}" +
                   string.Join(Environment.NewLine, diagnostics.Select(d => "  " + d));
        }
    }

    /// <summary>
    /// Finds the closest known spelling for an unrecognised name, which turns a bare
    /// "unknown verb" error into the "did you mean `deal`?" hint.
    /// </summary>
    internal static class Suggest
    {
        internal static string? Closest(string input, IEnumerable<string> candidates, int maxDistance = 3)
        {
            if (string.IsNullOrEmpty(input) || candidates == null) return null;

            string? best = null;
            int bestDistance = int.MaxValue;

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                if (string.Equals(candidate, input, StringComparison.OrdinalIgnoreCase)) return candidate;

                int distance = Distance(input, candidate, maxDistance);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            // Allow a looser match for longer words, where a single typo is proportionally smaller.
            int limit = Math.Min(maxDistance, Math.Max(1, input.Length / 2));
            return bestDistance <= limit ? best : null;
        }

        /// <summary>Levenshtein distance, capped at <paramref name="limit"/> for early exit.</summary>
        private static int Distance(string a, string b, int limit)
        {
            a = a.ToLowerInvariant();
            b = b.ToLowerInvariant();

            if (Math.Abs(a.Length - b.Length) > limit) return int.MaxValue;

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];

            for (int j = 0; j <= b.Length; j++) previous[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                int rowBest = current[0];

                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                    if (current[j] < rowBest) rowBest = current[j];
                }

                if (rowBest > limit) return int.MaxValue;

                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[b.Length];
        }
    }
}
