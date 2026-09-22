using System;
using System.Collections.Generic;
using System.Text;
using Cantrip.Diagnostics;

namespace Cantrip.Syntax
{
    /// <summary>
    /// Turns DSL source text into a token stream with explicit
    /// <see cref="TokenKind.Indent"/>/<see cref="TokenKind.Dedent"/> tokens.
    /// </summary>
    /// <remarks>
    /// Blocks are indentation-delimited, so the parser never has to guess where a listener body
    /// ends. Blank lines and comment-only lines produce no tokens at all, which means a designer
    /// can space content out freely without changing its meaning.
    /// </remarks>
    public sealed class Lexer
    {
        /// <summary>
        /// Prefixes that bind directly to a following name, as in <c>tag:fire</c>. Keeping this a
        /// closed set is what lets the lexer tell a qualified name apart from a block-opening
        /// colon without any parser feedback.
        /// </summary>
        private static readonly HashSet<string> QualifierPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tag", "status", "kind", "zone", "type", "rarity", "source", "target", "name", "id", "card", "team", "keyword",
        };

        private const int TabWidth = 4;

        private readonly string _text;
        private readonly string _file;
        private readonly DiagnosticBag _diagnostics;
        private readonly List<Token> _tokens = new List<Token>();
        private readonly Stack<int> _indents = new Stack<int>();

        private int _position;
        private int _line = 1;
        private int _lineStart;

        public Lexer(string text, string file, DiagnosticBag diagnostics)
        {
            _text = text ?? throw new ArgumentNullException(nameof(text));
            _file = file ?? string.Empty;
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _indents.Push(0);
        }

        public static IReadOnlyList<Token> Tokenize(string text, string file, DiagnosticBag diagnostics) =>
            new Lexer(text, file, diagnostics).Tokenize();

        public IReadOnlyList<Token> Tokenize()
        {
            while (true)
            {
                if (!ReadLineStart()) break;
                ReadLineBody();
            }

            // Close every open block so the parser always sees balanced indentation.
            SourceSpan end = SpanAt(_position, 0);
            while (_indents.Count > 1)
            {
                _indents.Pop();
                _tokens.Add(new Token(TokenKind.Dedent, string.Empty, end));
            }

            _tokens.Add(new Token(TokenKind.EndOfFile, string.Empty, end));
            return _tokens;
        }

        /// <summary>
        /// Consumes leading whitespace on a line and emits any indent/dedent tokens it implies.
        /// Returns false at end of input.
        /// </summary>
        private bool ReadLineStart()
        {
            while (true)
            {
                if (_position >= _text.Length) return false;

                _lineStart = _position;
                int width = 0;

                while (_position < _text.Length)
                {
                    char c = _text[_position];
                    if (c == ' ') { width++; _position++; }
                    else if (c == '\t') { width += TabWidth - (width % TabWidth); _position++; }
                    else break;
                }

                // Skip lines that hold nothing but whitespace or a comment: they carry no
                // indentation meaning, so a designer can align comments however they like.
                if (_position >= _text.Length) return false;
                char first = _text[_position];
                if (first == '\r' || first == '\n')
                {
                    ConsumeNewline();
                    continue;
                }
                if (first == '#')
                {
                    while (_position < _text.Length && _text[_position] != '\n' && _text[_position] != '\r') _position++;
                    continue;
                }

                EmitIndentation(width);
                return true;
            }
        }

        private void EmitIndentation(int width)
        {
            int current = _indents.Peek();
            SourceSpan span = SpanAt(_position, 0);

            if (width > current)
            {
                _indents.Push(width);
                _tokens.Add(new Token(TokenKind.Indent, string.Empty, span));
                return;
            }

            while (width < _indents.Peek())
            {
                _indents.Pop();
                _tokens.Add(new Token(TokenKind.Dedent, string.Empty, span));
            }

            if (width != _indents.Peek())
            {
                _diagnostics.Error(
                    "CT0001",
                    $"Indentation of {width} spaces does not line up with any enclosing block.",
                    span);
                // Recover by accepting the width, so one bad line does not cascade.
                _indents.Push(width);
                _tokens.Add(new Token(TokenKind.Indent, string.Empty, span));
            }
        }

        private void ReadLineBody()
        {
            while (_position < _text.Length)
            {
                char c = _text[_position];

                if (c == ' ' || c == '\t') { _position++; continue; }

                if (c == '#')
                {
                    while (_position < _text.Length && _text[_position] != '\n' && _text[_position] != '\r') _position++;
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    _tokens.Add(new Token(TokenKind.Newline, string.Empty, SpanAt(_position, 0)));
                    ConsumeNewline();
                    return;
                }

                ReadToken();
            }

            _tokens.Add(new Token(TokenKind.Newline, string.Empty, SpanAt(_position, 0)));
        }

        private void ReadToken()
        {
            int start = _position;
            char c = _text[_position];

            if (char.IsDigit(c)) { ReadNumber(); return; }
            if (c == '"' || c == '\'') { ReadString(c); return; }

            // `x1.5` is the multiply form used by modifiers. Without this case the identifier
            // rule would swallow the digit and produce `x1` followed by `.5`.
            if ((c == 'x' || c == 'X') && _position + 1 < _text.Length && char.IsDigit(_text[_position + 1]))
            {
                _position++;
                Add(TokenKind.Star, "x", start);
                return;
            }

            if (IsIdentifierStart(c)) { ReadIdentifier(); return; }

            _position++;
            switch (c)
            {
                case ':': Add(TokenKind.Colon, ":", start); return;
                case ',': Add(TokenKind.Comma, ",", start); return;
                case '(': Add(TokenKind.LeftParen, "(", start); return;
                case ')': Add(TokenKind.RightParen, ")", start); return;
                case '[': Add(TokenKind.LeftBracket, "[", start); return;
                case ']': Add(TokenKind.RightBracket, "]", start); return;
                case '%': Add(TokenKind.Percent, "%", start); return;
                case '/': Add(TokenKind.Slash, "/", start); return;

                case '*':
                    if (Match('=')) { Add(TokenKind.StarEqual, "*=", start); return; }
                    Add(TokenKind.Star, "*", start);
                    return;

                case '.':
                    if (Match('.')) { Add(TokenKind.DotDot, "..", start); return; }
                    Add(TokenKind.Dot, ".", start);
                    return;

                case '+':
                    if (Match('=')) { Add(TokenKind.PlusEqual, "+=", start); return; }
                    Add(TokenKind.Plus, "+", start);
                    return;

                case '=':
                    if (Match('=')) { Add(TokenKind.EqualEqual, "==", start); return; }
                    Add(TokenKind.Equal, "=", start);
                    return;

                case '<':
                    if (Match('=')) { Add(TokenKind.LessEqual, "<=", start); return; }
                    Add(TokenKind.Less, "<", start);
                    return;

                case '>':
                    if (Match('=')) { Add(TokenKind.GreaterEqual, ">=", start); return; }
                    Add(TokenKind.Greater, ">", start);
                    return;

                case '-':
                    if (Match('=')) { Add(TokenKind.MinusEqual, "-=", start); return; }
                    if (Match('>')) { Add(TokenKind.Arrow, "->", start); return; }
                    Add(TokenKind.Minus, "-", start);
                    return;

                case '!':
                    if (Match('=')) { Add(TokenKind.BangEqual, "!=", start); return; }
                    _diagnostics.Error("CT0002", "`!` must be part of `!=`.", SpanAt(start, 1));
                    return;

                default:
                    _diagnostics.Error("CT0003", $"Unexpected character `{c}`.", SpanAt(start, 1));
                    return;
            }
        }

        private void ReadNumber()
        {
            int start = _position;

            while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '_')) _position++;

            // A single '.' is a decimal point, but '..' is the range operator and must be left alone.
            if (_position + 1 < _text.Length && _text[_position] == '.' && _text[_position + 1] != '.' && char.IsDigit(_text[_position + 1]))
            {
                _position++;
                while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '_')) _position++;
            }

            string digits = _text.Substring(start, _position - start);

            // A unit suffix binds tightly: 40%, 3s, 250ms, 5m, 2turns.
            string? unit = null;
            if (_position < _text.Length && _text[_position] == '%')
            {
                unit = "%";
                _position++;
            }
            else if (_position < _text.Length && char.IsLetter(_text[_position]))
            {
                int unitStart = _position;
                while (_position < _text.Length && char.IsLetter(_text[_position])) _position++;
                unit = _text.Substring(unitStart, _position - unitStart).ToLowerInvariant();
            }

            if (!Num.TryParse(digits, out Num value))
            {
                _diagnostics.Error("CT0004", $"`{digits}` is not a valid number, or is too large to represent.", SpanAt(start, _position - start));
                value = Num.Zero;
            }

            _tokens.Add(new Token(TokenKind.Number, digits, SpanAt(start, _position - start), value, unit));
        }

        private void ReadString(char quote)
        {
            int start = _position;
            _position++; // opening quote

            var content = new StringBuilder();
            while (_position < _text.Length)
            {
                char c = _text[_position];

                if (c == '\n' || c == '\r')
                {
                    _diagnostics.Error("CT0005", "String is missing its closing quote.", SpanAt(start, _position - start));
                    break;
                }

                if (c == '\\' && _position + 1 < _text.Length)
                {
                    char escaped = _text[_position + 1];
                    content.Append(escaped switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '"' => '"',
                        '\'' => '\'',
                        _ => escaped,
                    });
                    _position += 2;
                    continue;
                }

                if (c == quote)
                {
                    _position++;
                    _tokens.Add(new Token(TokenKind.String, content.ToString(), SpanAt(start, _position - start)));
                    return;
                }

                content.Append(c);
                _position++;
            }

            _tokens.Add(new Token(TokenKind.String, content.ToString(), SpanAt(start, _position - start)));
        }

        private void ReadIdentifier()
        {
            int start = _position;
            while (_position < _text.Length && IsIdentifierPart(_text[_position])) _position++;
            string name = _text.Substring(start, _position - start);

            // `tag:fire` is one token. Requiring no space around the colon and a known prefix
            // keeps this unambiguous against the colon that opens a block.
            if (QualifierPrefixes.Contains(name)
                && _position < _text.Length
                && _text[_position] == ':'
                && _position + 1 < _text.Length
                && IsIdentifierStart(_text[_position + 1]))
            {
                _position++; // colon
                int valueStart = _position;
                while (_position < _text.Length && IsIdentifierPart(_text[_position])) _position++;
                string value = _text.Substring(valueStart, _position - valueStart);
                _tokens.Add(new Token(TokenKind.QualifiedName, value, SpanAt(start, _position - start), qualifier: name.ToLowerInvariant()));
                return;
            }

            // `card:"Fire Bolt"` and `name:"Strike+"`: a name that is not one word is quoted, as it
            // is everywhere else. The string is read as usual and becomes the qualifier's value.
            // No line starts with a qualifier, so one that does is a property written without a
            // space, such as `rarity:"rare"`, and stays the property it has always been.
            if (QualifierPrefixes.Contains(name)
                && _position + 1 < _text.Length
                && _text[_position] == ':'
                && (_text[_position + 1] == '"' || _text[_position + 1] == '\'')
                && !AtLineStart())
            {
                _position++; // colon
                ReadString(_text[_position]);
                Token quoted = _tokens[_tokens.Count - 1];
                _tokens[_tokens.Count - 1] = new Token(TokenKind.QualifiedName, quoted.Text, SpanAt(start, _position - start), qualifier: name.ToLowerInvariant());
                return;
            }

            _tokens.Add(new Token(TokenKind.Identifier, name, SpanAt(start, _position - start)));
        }

        /// <summary>True while nothing has been read yet on the current line.</summary>
        private bool AtLineStart() =>
            _tokens.Count == 0 || _tokens[_tokens.Count - 1].Kind is TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent;

        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

        private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        private bool Match(char expected)
        {
            if (_position >= _text.Length || _text[_position] != expected) return false;
            _position++;
            return true;
        }

        private void Add(TokenKind kind, string text, int start) =>
            _tokens.Add(new Token(kind, text, SpanAt(start, _position - start)));

        private void ConsumeNewline()
        {
            if (_position < _text.Length && _text[_position] == '\r') _position++;
            if (_position < _text.Length && _text[_position] == '\n') _position++;
            _line++;
        }

        private SourceSpan SpanAt(int offset, int length) =>
            new SourceSpan(_file, _line, Math.Max(1, offset - _lineStart + 1), length);
    }
}
