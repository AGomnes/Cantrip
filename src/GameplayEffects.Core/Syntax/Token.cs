using GameplayEffects.Diagnostics;

namespace GameplayEffects.Syntax
{
    public enum TokenKind
    {
        EndOfFile,
        Newline,
        Indent,
        Dedent,

        Identifier,

        /// <summary>A <c>tag:fire</c> style qualified name, lexed as one token.</summary>
        QualifiedName,

        Number,
        String,

        Colon,
        Comma,
        Dot,
        DotDot,
        LeftParen,
        RightParen,
        LeftBracket,
        RightBracket,

        Plus,
        Minus,
        Star,
        Slash,
        Percent,

        Equal,
        EqualEqual,
        BangEqual,
        Less,
        LessEqual,
        Greater,
        GreaterEqual,
        PlusEqual,
        MinusEqual,
        StarEqual,

        Arrow,
    }

    /// <summary>A single lexical token, carrying enough source detail to point an error at it.</summary>
    public readonly struct Token
    {
        public Token(TokenKind kind, string text, SourceSpan span, Num value = default, string? unit = null, string? qualifier = null)
        {
            Kind = kind;
            Text = text;
            Span = span;
            Value = value;
            Unit = unit;
            Qualifier = qualifier;
        }

        public TokenKind Kind { get; }

        /// <summary>
        /// The token's textual payload: the identifier, the decoded string contents, the operator
        /// spelling, or for a qualified name the part after the colon.
        /// </summary>
        public string Text { get; }

        public SourceSpan Span { get; }

        /// <summary>Parsed value for <see cref="TokenKind.Number"/>.</summary>
        public Num Value { get; }

        /// <summary>Unit suffix on a number: <c>%</c>, <c>s</c>, <c>ms</c>, <c>m</c>, <c>turns</c>, ...</summary>
        public string? Unit { get; }

        /// <summary>The part before the colon for <see cref="TokenKind.QualifiedName"/>.</summary>
        public string? Qualifier { get; }

        public bool Is(TokenKind kind) => Kind == kind;

        /// <summary>Case-insensitive keyword test. The DSL treats keywords as case-insensitive.</summary>
        public bool IsKeyword(string keyword) =>
            Kind == TokenKind.Identifier && string.Equals(Text, keyword, System.StringComparison.OrdinalIgnoreCase);

        public override string ToString() => Kind switch
        {
            TokenKind.EndOfFile => "end of file",
            TokenKind.Newline => "end of line",
            TokenKind.Indent => "indent",
            TokenKind.Dedent => "dedent",
            TokenKind.String => $"\"{Text}\"",
            TokenKind.QualifiedName => $"{Qualifier}:{Text}",
            _ => Text,
        };
    }
}
