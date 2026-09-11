using System;

namespace GameplayEffects.Diagnostics
{
    /// <summary>
    /// A location in a DSL source file. Every AST node and every runtime trace entry carries one
    /// so that any number on screen can be traced back to the line that produced it.
    /// </summary>
    public readonly struct SourceSpan : IEquatable<SourceSpan>
    {
        public static readonly SourceSpan None = new SourceSpan(string.Empty, 0, 0, 0);

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

        public int Length { get; }

        public bool IsNone => Line == 0 && string.IsNullOrEmpty(File);

        /// <summary>Widens this span to cover <paramref name="other"/> as well.</summary>
        public SourceSpan To(SourceSpan other)
        {
            if (IsNone) return other;
            if (other.IsNone || other.File != File || other.Line != Line) return this;
            int end = Math.Max(Column + Length, other.Column + other.Length);
            return new SourceSpan(File, Line, Column, end - Column);
        }

        public bool Equals(SourceSpan other) =>
            File == other.File && Line == other.Line && Column == other.Column && Length == other.Length;

        public override bool Equals(object? obj) => obj is SourceSpan other && Equals(other);

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
