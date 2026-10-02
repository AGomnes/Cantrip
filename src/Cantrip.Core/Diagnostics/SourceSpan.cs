using System;

namespace Cantrip.Diagnostics
{
    /// <summary>
    /// A location in a DSL source file. Every AST node and every runtime trace entry carries one
    /// so that any number on screen can be traced back to the line that produced it.
    /// </summary>
    public readonly struct SourceSpan : IEquatable<SourceSpan>
    {
        /// <summary>
        /// No location: a diagnostic about the library as a whole, or a node the engine synthesised
        /// rather than parsed. It is also <c>default(SourceSpan)</c>, so a span nobody set reads as this.
        /// </summary>
        public static readonly SourceSpan None = new SourceSpan(string.Empty, 0, 0, 0);

        /// <summary>
        /// A location in a source file. Nothing is validated: a span the caller got wrong points
        /// somewhere wrong rather than throwing, since a diagnostic that cannot be reported is worse than
        /// one that points at the wrong column.
        /// </summary>
        /// <param name="file">The file, or null for none. Null becomes the empty string and reads as <see cref="None"/>.</param>
        /// <param name="line">1-based. Zero with no file is <see cref="None"/>.</param>
        /// <param name="column">1-based.</param>
        /// <param name="length">How many characters, on that line alone.</param>
        public SourceSpan(string file, int line, int column, int length)
        {
            File = file ?? string.Empty;
            Line = line;
            Column = column;
            Length = length;
        }

        /// <summary>Path the source was loaded from, or a synthetic name for in-memory content.</summary>
        public string File { get; }

        /// <summary>1-based line number.</summary>
        public int Line { get; }

        /// <summary>1-based column number.</summary>
        public int Column { get; }

        /// <summary>
        /// How many characters the span covers, on <see cref="Line"/> alone. A span never crosses a
        /// line break, so an editor can underline it without looking at what follows.
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// Whether there is a place to point at. Worth asking before jumping to a diagnostic: a span
        /// with no file is <see cref="None"/>, not line 0 of the file being read.
        /// </summary>
        public bool IsNone => Line == 0 && string.IsNullOrEmpty(File);

        /// <summary>Widens this span to cover <paramref name="other"/> as well.</summary>
        public SourceSpan To(SourceSpan other)
        {
            if (IsNone) return other;
            if (other.IsNone || other.File != File || other.Line != Line) return this;
            int end = Math.Max(Column + Length, other.Column + other.Length);
            return new SourceSpan(File, Line, Column, end - Column);
        }

        /// <summary>All four fields, exactly. Two spans that overlap but do not coincide are not equal.</summary>
        public bool Equals(SourceSpan other) =>
            File == other.File && Line == other.Line && Column == other.Column && Length == other.Length;

        /// <summary>The boxing form of <see cref="Equals(SourceSpan)"/>.</summary>
        public override bool Equals(object? obj) => obj is SourceSpan other && Equals(other);

        /// <summary>Hashes all four fields, so a span works as a dictionary key, which is how a tool groups diagnostics by location.</summary>
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = File.GetHashCode();
                hash = (hash * 397) ^ Line;
                hash = (hash * 397) ^ Column;
                return (hash * 397) ^ Length;
            }
        }

        /// <summary>Formats as <c>file:line:column</c>, the form editors and terminals can jump to.</summary>
        public override string ToString() =>
            IsNone ? "<unknown>" : $"{(string.IsNullOrEmpty(File) ? "<inline>" : File)}:{Line}:{Column}";
    }
}
