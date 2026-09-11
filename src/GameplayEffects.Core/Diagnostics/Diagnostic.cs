using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GameplayEffects.Diagnostics
{
    public enum DiagnosticSeverity
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>A single parser or linter message, tied to a source location.</summary>
    public sealed class Diagnostic
    {
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

        public DiagnosticSeverity Severity { get; }

        /// <summary>Stable identifier such as <c>GE0104</c>, so rules can be suppressed by code.</summary>
        public string Code { get; }

        public string Message { get; }

        public SourceSpan Span { get; }

        /// <summary>Optional "did you mean ...?" hint.</summary>
        public string? Suggestion { get; }

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

        public int Count => _items.Count;

        public bool HasErrors => _items.Any(d => d.Severity == DiagnosticSeverity.Error);

        public IEnumerable<Diagnostic> Errors => _items.Where(d => d.Severity == DiagnosticSeverity.Error);

        public IEnumerable<Diagnostic> Warnings => _items.Where(d => d.Severity == DiagnosticSeverity.Warning);

        public void Add(Diagnostic diagnostic) => _items.Add(diagnostic);

        public void Error(string code, string message, SourceSpan span, string? suggestion = null) =>
            Add(new Diagnostic(DiagnosticSeverity.Error, code, message, span, suggestion));

        public void Warn(string code, string message, SourceSpan span, string? suggestion = null) =>
            Add(new Diagnostic(DiagnosticSeverity.Warning, code, message, span, suggestion));

        public void Info(string code, string message, SourceSpan span, string? suggestion = null) =>
            Add(new Diagnostic(DiagnosticSeverity.Info, code, message, span, suggestion));

        public void AddRange(IEnumerable<Diagnostic> diagnostics) => _items.AddRange(diagnostics);

        /// <summary>Throws if anything in the bag is an error. Used by the strict loading paths.</summary>
        public void ThrowIfErrors()
        {
            if (!HasErrors) return;
            throw new DslException(Errors.ToList());
        }

        public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => string.Join(Environment.NewLine, _items);
    }

    /// <summary>Thrown when content cannot be loaded. Carries every error, not just the first.</summary>
    public sealed class DslException : Exception
    {
        public DslException(IReadOnlyList<Diagnostic> diagnostics)
            : base(Describe(diagnostics))
        {
            Diagnostics = diagnostics;
        }

        public DslException(Diagnostic diagnostic)
            : this(new[] { diagnostic })
        {
        }

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
    /// "unknown verb" error into the "did you mean `deal`?" hint from the design notes.
    /// </summary>
    public static class Suggest
    {
        public static string? Closest(string input, IEnumerable<string> candidates, int maxDistance = 3)
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
