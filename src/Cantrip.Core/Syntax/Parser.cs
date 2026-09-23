using System;
using System.Collections.Generic;
using System.Globalization;
using Cantrip.Diagnostics;

namespace Cantrip.Syntax
{
    /// <summary>
    /// Recursive-descent parser for the effects DSL.
    /// </summary>
    /// <remarks>
    /// The grammar is deliberately shallow. Statements are parsed as a verb name followed by
    /// positional arguments and named clauses (<c>to</c>, <c>from</c>, <c>for</c>, ...), and the
    /// parser attaches no meaning to any of them. Verb semantics live in the interpreter's verb
    /// registry, so content can add verbs without the grammar growing a case for each one.
    /// </remarks>
    public sealed class Parser
    {
        /// <summary>
        /// Words that introduce a named clause and therefore terminate the argument list. These
        /// are the only reserved words inside a command line.
        /// </summary>
        private static readonly HashSet<string> ClauseKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "to", "from", "for", "with", "at", "by", "into", "over", "as", "of", "against", "using", "onto",
        };

        /// <summary>Bare modifiers that may trail a command after a comma, such as <c>ignore block</c>.</summary>
        private static readonly HashSet<string> FlagKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ignore", "pierce", "true", "silent", "hidden", "unblockable", "forced", "optional", "upgraded",
        };

        private static readonly HashSet<string> DeclarationKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "card", "status", "relic", "ability", "enemy", "keyword", "item", "event", "encounter", "actor", "resource",
        };

        /// <summary>Words that open a selector when something to select from follows them.</summary>
        private static readonly HashSet<string> SelectorPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "all", "random", "other", "lowest", "highest",
        };

        /// <summary>
        /// Words that continue an expression rather than start an operand. A selector prefix
        /// followed by one of these is an ordinary name, as in <c>pattern random</c>.
        /// </summary>
        private static readonly HashSet<string> OperatorWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "where", "in", "on", "and", "or", "per", "has", "is", "not", "within", "then", "else", "times",
            "to", "from", "for", "with", "at", "by", "into", "over", "as", "of", "against", "using", "onto",
        };

        /// <summary>Unit words that may follow a number after a space: <c>3 seconds</c> means <c>3s</c>.</summary>
        internal static readonly HashSet<string> UnitWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sec", "secs", "second", "seconds", "ms", "tick", "ticks", "turn", "turns",
        };

        /// <summary>Members that only ever hold statements, so <c>effect: deal 6 to target</c> is an inline block.</summary>
        private static readonly HashSet<string> BlockOnlyMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "effect",
        };

        private static readonly (string Prefix, EventPhase Phase)[] PhasePrefixes =
        {
            ("before_", EventPhase.Before),
            ("instead_of_", EventPhase.Instead),
            ("after_", EventPhase.After),
        };

        /// <summary>
        /// Recursion guard. Hostile or generated content can nest parentheses or blocks thousands
        /// deep; past this depth the parser reports a diagnostic instead of overflowing the stack.
        /// </summary>
        private const int MaxNesting = 256;

        private readonly IReadOnlyList<Token> _tokens;
        private readonly DiagnosticBag _diagnostics;
        private readonly string _file;
        private int _index;
        private int _depth;

        /// <summary>Set after a nesting error, so the unwinding does not report a cascade of follow-on errors.</summary>
        private bool _abortLine;

        public Parser(IReadOnlyList<Token> tokens, string file, DiagnosticBag diagnostics)
        {
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
            _file = file ?? string.Empty;
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        public static SourceFileNode Parse(string text, string file, DiagnosticBag diagnostics)
        {
            IReadOnlyList<Token> tokens = Lexer.Tokenize(text, file, diagnostics);
            return new Parser(tokens, file, diagnostics).ParseFile();
        }

        // -----------------------------------------------------------------------------------
        // Token helpers
        // -----------------------------------------------------------------------------------

        private Token Current => _tokens[_index];

        private Token Peek(int offset = 1)
        {
            int target = _index + offset;
            return target < _tokens.Count ? _tokens[target] : _tokens[_tokens.Count - 1];
        }

        private Token Advance() => _tokens[Math.Min(_index++, _tokens.Count - 1)];

        private bool Check(TokenKind kind) => Current.Kind == kind;

        private bool CheckKeyword(string keyword) => Current.IsKeyword(keyword);

        private bool Match(TokenKind kind)
        {
            if (!Check(kind)) return false;
            _index++;
            return true;
        }

        private bool MatchKeyword(string keyword)
        {
            if (!CheckKeyword(keyword)) return false;
            _index++;
            return true;
        }

        private Token Expect(TokenKind kind, string description)
        {
            if (Check(kind))
            {
                if (kind == TokenKind.Newline) _abortLine = false;
                return Advance();
            }

            if (!_abortLine) _diagnostics.Error("CT0010", $"Expected {description} but found {Current}.", Current.Span);
            return new Token(kind, string.Empty, Current.Span);
        }

        /// <summary>Reports excessive nesting once and skips the rest of the line.</summary>
        private ExprNode TooDeep()
        {
            Token at = Current;
            if (!_abortLine)
            {
                _diagnostics.Error("CT0028", $"This is nested more than {MaxNesting} levels deep.", at.Span);
                _abortLine = true;
            }

            while (!Check(TokenKind.Newline) && !Check(TokenKind.EndOfFile) && !Check(TokenKind.Dedent)) _index++;
            return new NumberExpr(Num.Zero, null, at.Span);
        }

        private void SkipNewlines()
        {
            while (Check(TokenKind.Newline)) _index++;
        }

        /// <summary>Consumes the rest of the current line after an error, so parsing can continue.</summary>
        private void RecoverToLineEnd()
        {
            while (!Check(TokenKind.Newline) && !Check(TokenKind.EndOfFile) && !Check(TokenKind.Dedent)) _index++;
            SkipNewlines();
        }

        /// <summary>Skips a whole indented block after an error inside its header.</summary>
        private void RecoverToBlockEnd()
        {
            RecoverToLineEnd();
            if (!Check(TokenKind.Indent)) return;

            int depth = 0;
            do
            {
                if (Check(TokenKind.Indent)) depth++;
                else if (Check(TokenKind.Dedent)) depth--;
                else if (Check(TokenKind.EndOfFile)) return;
                _index++;
            }
            while (depth > 0);
        }

        // -----------------------------------------------------------------------------------
        // File and declarations
        // -----------------------------------------------------------------------------------

        public SourceFileNode ParseFile()
        {
            var declarations = new List<DeclarationNode>();
            SourceSpan start = Current.Span;

            SkipNewlines();
            while (!Check(TokenKind.EndOfFile))
            {
                // Stray indentation at file scope is an error the lexer already reported;
                // swallow the tokens so we do not report it again for every line.
                if (Check(TokenKind.Indent) || Check(TokenKind.Dedent)) { _index++; continue; }

                int before = _index;
                DeclarationNode? declaration = ParseDeclaration();
                if (declaration != null) declarations.Add(declaration);

                // Guarantee forward progress even if a production consumed nothing.
                if (_index == before) _index++;
                SkipNewlines();
            }

            return new SourceFileNode(_file, declarations, start);
        }

        private DeclarationNode? ParseDeclaration()
        {
            Token token = Current;

            if (token.Kind != TokenKind.Identifier)
            {
                _diagnostics.Error("CT0011", $"Expected a declaration but found {token}.", token.Span);
                RecoverToBlockEnd();
                return null;
            }

            string keyword = token.Text.ToLowerInvariant();

            if (keyword == "verb") return ParseVerbDeclaration();
            if (keyword == "ruleset") return ParseRulesetDeclaration();
            if (keyword == "test") return ParseTestDeclaration();
            if (keyword == "scenario") return ParseScenarioDeclaration();
            if (DeclarationKeywords.Contains(keyword)) return ParseEntityDeclaration();

            _diagnostics.Error(
                "CT0012",
                $"`{token.Text}` does not start a declaration.",
                token.Span,
                Suggest.Closest(token.Text, AllDeclarationKeywords()));
            RecoverToBlockEnd();
            return null;
        }

        private static IEnumerable<string> AllDeclarationKeywords()
        {
            foreach (string keyword in DeclarationKeywords) yield return keyword;
            yield return "verb";
            yield return "ruleset";
            yield return "test";
            yield return "scenario";
        }

        private EntityDeclNode ParseEntityDeclaration()
        {
            Token keyword = Advance();
            string name = ParseDeclarationName(keyword.Text);

            var members = new List<MemberNode>();
            Expect(TokenKind.Newline, "end of line");

            if (Match(TokenKind.Indent))
            {
                int stray = 0;
                while (!Check(TokenKind.EndOfFile))
                {
                    SkipNewlines();
                    if (Check(TokenKind.EndOfFile)) break;
                    if (SkipStrayIndent(ref stray)) continue;
                    if (Check(TokenKind.Dedent)) break;

                    int before = _index;
                    MemberNode? member = ParseMember();
                    if (member != null) members.Add(member);
                    if (_index == before) _index++;
                }
                Match(TokenKind.Dedent);
            }

            return new EntityDeclNode(keyword.Text.ToLowerInvariant(), name, members, keyword.Span);
        }

        private string ParseDeclarationName(string keyword)
        {
            if (Check(TokenKind.String)) return Advance().Text;

            if (Check(TokenKind.Identifier))
            {
                Token first = Advance();
                if (!Check(TokenKind.Identifier)) return first.Text;

                // `card Heavy Blow`: a bare name ends at the first space. Say what to write, then
                // carry on as if it had been quoted, so one slip is one error rather than a cascade.
                var words = new List<string> { first.Text };
                Token last = first;
                while (Check(TokenKind.Identifier))
                {
                    last = Advance();
                    words.Add(last.Text);
                }

                // A verb is called by one word, so a quoted name with a space could never be called:
                // suggest one word instead, and carry on under that name so calls written that way work.
                if (string.Equals(keyword, "verb", StringComparison.OrdinalIgnoreCase))
                {
                    string verb = string.Join("_", words);
                    _diagnostics.Error("CT0029", $"A verb name cannot contain spaces, because a verb is called by one word: verb {verb}.", first.Span.To(last.Span));
                    return verb;
                }

                string name = string.Join(" ", words);
                _diagnostics.Error("CT0029", $"A name with spaces must be in quotes: {keyword} \"{name}\".", first.Span.To(last.Span));
                return name;
            }

            _diagnostics.Error("CT0013", $"`{keyword}` must be followed by a name.", Current.Span);
            return "<unnamed>";
        }

        private VerbDeclNode ParseVerbDeclaration()
        {
            Token keyword = Advance();
            string name = ParseDeclarationName("verb");

            var parameters = new List<string>();
            if (Match(TokenKind.LeftParen))
            {
                while (!Check(TokenKind.RightParen) && !Check(TokenKind.EndOfFile) && !Check(TokenKind.Newline))
                {
                    if (Check(TokenKind.Identifier)) parameters.Add(Advance().Text);
                    else { _diagnostics.Error("CT0014", $"Expected a parameter name but found {Current}.", Current.Span); break; }

                    if (!Match(TokenKind.Comma)) break;
                }
                Expect(TokenKind.RightParen, "`)`");
            }

            Expect(TokenKind.Colon, "`:`");
            BlockNode body = ParseBlock();
            return new VerbDeclNode(name, parameters, body, keyword.Span);
        }

        private RulesetDeclNode ParseRulesetDeclaration()
        {
            Token keyword = Advance();
            var settings = new List<PropertyNode>();

            Expect(TokenKind.Newline, "end of line");
            if (Match(TokenKind.Indent))
            {
                while (!Check(TokenKind.Dedent) && !Check(TokenKind.EndOfFile))
                {
                    SkipNewlines();
                    if (Check(TokenKind.Dedent) || Check(TokenKind.EndOfFile)) break;

                    int before = _index;
                    PropertyNode? setting = ParseSettingLine();
                    if (setting != null) settings.Add(setting);
                    if (_index == before) _index++;
                }
                Match(TokenKind.Dedent);
            }

            return new RulesetDeclNode(settings, keyword.Span);
        }

        /// <summary>A ruleset line: <c>name: value[, value]</c> or <c>name value</c>.</summary>
        private PropertyNode? ParseSettingLine()
        {
            if (!Check(TokenKind.Identifier))
            {
                _diagnostics.Error("CT0015", $"Expected a ruleset setting but found {Current}.", Current.Span);
                RecoverToLineEnd();
                return null;
            }

            Token name = Advance();
            Match(TokenKind.Colon);

            // Values may be comma- or space-separated: `loops: once_per_chain, max_depth 50`.
            var values = new List<ExprNode>();
            while (!Check(TokenKind.Newline) && !Check(TokenKind.EndOfFile) && !Check(TokenKind.Dedent))
            {
                if (Match(TokenKind.Comma)) continue;
                if (!StartsExpression())
                {
                    _diagnostics.Error("CT0027", $"Unexpected {Current} in ruleset setting `{name.Text}`.", Current.Span);
                    RecoverToLineEnd();
                    return new PropertyNode(name.Text.ToLowerInvariant(), values, name.Span);
                }
                values.Add(ParseExpression());
            }

            Expect(TokenKind.Newline, "end of line");
            return new PropertyNode(name.Text.ToLowerInvariant(), values, name.Span);
        }

        private TestDeclNode ParseTestDeclaration()
        {
            Token keyword = Advance();
            string name = ParseDeclarationName("test");
            Expect(TokenKind.Newline, "end of line");

            var statements = new List<StatementNode>();
            if (Match(TokenKind.Indent)) ParseIndentedStatements(statements);

            return new TestDeclNode(name, new BlockNode(statements, keyword.Span), keyword.Span);
        }

        /// <summary>
        /// A <c>scenario</c> body is statements, exactly as a test's is. The verbs that only appear
        /// in one or the other, such as <c>battle</c> and <c>play</c>, are told apart by the linter
        /// and by whoever runs the block, not here: the grammar attaches no meaning to a verb.
        /// </summary>
        private ScenarioDeclNode ParseScenarioDeclaration()
        {
            Token keyword = Advance();
            string name = ParseDeclarationName("scenario");
            Expect(TokenKind.Newline, "end of line");

            var statements = new List<StatementNode>();
            if (Match(TokenKind.Indent)) ParseIndentedStatements(statements);

            return new ScenarioDeclNode(name, new BlockNode(statements, keyword.Span), keyword.Span);
        }

        // -----------------------------------------------------------------------------------
        // Members
        // -----------------------------------------------------------------------------------

        private MemberNode? ParseMember()
        {
            if (!Check(TokenKind.Identifier))
            {
                _diagnostics.Error("CT0016", $"Expected a property or listener but found {Current}.", Current.Span);
                RecoverToLineEnd();
                return null;
            }

            string keyword = Current.Text.ToLowerInvariant();

            if (keyword == "on") return ParseListener();
            if (keyword == "modify") return ParseModify();

            // `effect: deal 6 to target` keeps its body on the same line. Without this it would
            // parse as a property whose values happen to look like a statement, and do nothing.
            if (BlockOnlyMembers.Contains(keyword) && Peek().Kind == TokenKind.Colon) return ParseBlockMember();

            // A line whose last token is a colon opens a nested block: `effect:` or
            // `move "Chomp":`. Anything else is a plain `name value...` property.
            if (LineEndsWithColon()) return ParseBlockMember();

            return ParsePropertyMember();
        }

        /// <summary>True when the current line's final token (before the newline) is a colon.</summary>
        private bool LineEndsWithColon()
        {
            for (int offset = 0; ; offset++)
            {
                TokenKind kind = Peek(offset).Kind;
                if (kind == TokenKind.Newline || kind == TokenKind.EndOfFile) return offset > 0 && Peek(offset - 1).Kind == TokenKind.Colon;
                if (kind == TokenKind.Indent || kind == TokenKind.Dedent) return false;
            }
        }

        private MemberNode ParseBlockMember()
        {
            Token name = Advance();

            var arguments = new List<ExprNode>();
            while (!Check(TokenKind.Colon) && !Check(TokenKind.Newline) && !Check(TokenKind.EndOfFile))
            {
                if (Match(TokenKind.Comma)) continue;
                if (!StartsExpression())
                {
                    _diagnostics.Error("CT0025", $"Unexpected {Current} in `{name.Text}` header.", Current.Span);
                    _index++;
                    continue;
                }
                arguments.Add(ParseExpression());
            }

            Expect(TokenKind.Colon, "`:`");
            BlockNode body = ParseBlock();
            return new BlockMemberNode(name.Text.ToLowerInvariant(), arguments, body, name.Span);
        }

        /// <summary>
        /// <c>name value [value ...]</c>. Values may be separated by commas or plain whitespace, so
        /// <c>tags dot, poison</c> and <c>pattern cycle Chomp, Bellow</c> both read naturally.
        /// </summary>
        private MemberNode ParsePropertyMember()
        {
            Token name = Advance();
            Match(TokenKind.Colon); // `text: "..."` and `text "..."` are both accepted.

            var values = new List<ExprNode>();
            while (!Check(TokenKind.Newline) && !Check(TokenKind.EndOfFile) && !Check(TokenKind.Dedent))
            {
                if (Match(TokenKind.Comma)) continue;
                if (!StartsExpression())
                {
                    _diagnostics.Error("CT0026", $"Unexpected {Current} in `{name.Text}`.", Current.Span);
                    RecoverToLineEnd();
                    return new PropertyNode(name.Text.ToLowerInvariant(), values, name.Span);
                }
                values.Add(ParseExpression());
            }

            Expect(TokenKind.Newline, "end of line");
            return new PropertyNode(name.Text.ToLowerInvariant(), values, name.Span);
        }

        /// <summary>
        /// <c>on &lt;phase_&gt;&lt;event&gt;(&lt;filter&gt;) [once per turn] [priority N]:</c>
        /// </summary>
        private MemberNode ParseListener()
        {
            Token keyword = Advance(); // `on`

            var eventName = new System.Text.StringBuilder();
            SourceSpan nameSpan = Current.Span;

            if (!Check(TokenKind.Identifier))
            {
                _diagnostics.Error("CT0017", $"Expected an event name after `on` but found {Current}.", Current.Span);
                RecoverToBlockEnd();
                return new ListenerNode("<invalid>", EventPhase.After, null, BlockNode.Empty(keyword.Span), LimitScope.None, 0, keyword.Span);
            }

            // `on every 1s:` is a periodic trigger rather than an event name. It registers under the
            // built-in `every` event, but only the listener whose interval has elapsed is run.
            Num interval = default;
            string? intervalUnit = null;

            if (CheckKeyword("every"))
            {
                _index++;
                if (Check(TokenKind.Number))
                {
                    Token amount = Advance();
                    interval = amount.Value;
                    intervalUnit = amount.Unit;

                    // `every 2 turns` writes its unit as a following word; `every 2s` attaches it.
                    if (intervalUnit == null && Check(TokenKind.Identifier) && UnitWords.Contains(Current.Text.ToLowerInvariant()))
                        intervalUnit = Advance().Text.ToLowerInvariant();
                }
                else
                {
                    Expect(TokenKind.Number, "an interval such as `1s` or `2 turns`");
                }

                // Spelled out rather than taken from BuiltinEvents: the syntax layer does not know
                // about the runtime, and this is the name the runtime registers under.
                eventName.Append("every");
            }
            else
            {
                // Event names may be dotted, as in `owner.damaged`.
                eventName.Append(Advance().Text);
                while (Check(TokenKind.Dot) && Peek().Kind == TokenKind.Identifier)
                {
                    _index++;
                    eventName.Append('.').Append(Advance().Text);
                }
            }

            ExprNode? filter = null;
            if (Match(TokenKind.LeftParen))
            {
                if (!Check(TokenKind.RightParen)) filter = ParseFilterList();
                Expect(TokenKind.RightParen, "`)`");
            }

            LimitScope limit = LimitScope.None;
            int priority = 0;

            // Trailing modifiers, optionally comma-separated: `once per turn`, `priority 10`.
            while (true)
            {
                Match(TokenKind.Comma);

                if (MatchKeyword("once"))
                {
                    if (!MatchKeyword("per")) _diagnostics.Error("CT0018", "`once` must be followed by `per turn`, `per battle`, `per run` or `per chain`.", Current.Span);
                    limit = ParseLimitScope();
                    continue;
                }

                if (MatchKeyword("priority"))
                {
                    if (Check(TokenKind.Number)) priority = Advance().Value.ToInt();
                    else _diagnostics.Error("CT0019", "`priority` must be followed by a number.", Current.Span);
                    continue;
                }

                break;
            }

            Expect(TokenKind.Colon, "`:`");
            BlockNode body = ParseBlock();

            (string normalized, EventPhase phase) = NormalizeEventName(eventName.ToString());
            return new ListenerNode(normalized, phase, filter, body, limit, priority, keyword.Span.To(nameSpan), interval, intervalUnit);
        }

        private LimitScope ParseLimitScope()
        {
            if (MatchKeyword("turn")) return LimitScope.Turn;
            if (MatchKeyword("battle")) return LimitScope.Battle;
            if (MatchKeyword("run")) return LimitScope.Run;
            if (MatchKeyword("chain")) return LimitScope.Chain;

            _diagnostics.Error("CT0020", $"Expected `turn`, `battle`, `run` or `chain` but found {Current}.", Current.Span);
            return LimitScope.None;
        }

        /// <summary>
        /// Splits the <c>before_</c> / <c>instead_of_</c> / <c>after_</c> prefix off an event name.
        /// A bare name listens on the <see cref="EventPhase.After"/> phase, which is the common case.
        /// </summary>
        internal static (string Name, EventPhase Phase) NormalizeEventName(string raw)
        {
            // The phase may lead the whole name (`before_owner.damaged`) or just the event part
            // after the scope (`owner.before_damaged`). Both mean the same thing.
            if (TryStripPhase(raw, out string stripped, out EventPhase phase)) return (stripped, phase);

            int dot = raw.LastIndexOf('.');
            if (dot >= 0 && TryStripPhase(raw.Substring(dot + 1), out string eventPart, out phase))
                return (raw.Substring(0, dot + 1) + eventPart, phase);

            return (raw, EventPhase.After);
        }

        private static bool TryStripPhase(string name, out string stripped, out EventPhase phase)
        {
            foreach (var (prefix, candidate) in PhasePrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    stripped = name.Substring(prefix.Length);
                    phase = candidate;
                    return true;
                }
            }

            stripped = name;
            phase = EventPhase.After;
            return false;
        }

        /// <summary>
        /// The comma-separated filter inside a listener's parentheses. Commas mean "and", which
        /// reads naturally in <c>on damaged(tag:fire, source:self)</c>.
        /// </summary>
        private ExprNode ParseFilterList()
        {
            ExprNode filter = ParseExpression();
            while (Match(TokenKind.Comma))
            {
                ExprNode next = ParseExpression();
                filter = new BinaryExpr(BinaryOperator.And, filter, next, filter.Span);
            }
            return filter;
        }

        /// <summary><c>modify &lt;channel&gt; [of &lt;scope&gt;] [where &lt;filter&gt;]: &lt;layer op&gt;&lt;amount&gt;</c></summary>
        private MemberNode ParseModify()
        {
            Token keyword = Advance(); // `modify`

            string channel = "<invalid>";
            if (Check(TokenKind.Identifier)) channel = Advance().Text.ToLowerInvariant();
            else _diagnostics.Error("CT0021", $"Expected something to modify but found {Current}.", Current.Span);

            ExprNode? scope = null;
            ExprNode? filter = null;

            // `modify cost of cards where tag:fire: -1` - the scope keeps its own `where`.
            if (MatchKeyword("of")) scope = ParseUnary();

            if (MatchKeyword("where"))
            {
                ExprNode where = ParseFilterList();
                filter = filter == null ? where : new BinaryExpr(BinaryOperator.And, filter, where, where.Span);
            }
            else if (Check(TokenKind.Comma))
            {
                // `modify damage where tag:fire, source:self: x1.5` - additional comma clauses.
                while (Match(TokenKind.Comma))
                {
                    ExprNode extra = ParseExpression();
                    filter = filter == null ? extra : new BinaryExpr(BinaryOperator.And, filter, extra, extra.Span);
                }
            }

            Expect(TokenKind.Colon, "`:`");
            (ModifierLayer layer, ExprNode amount) = ParseModifierAmount();
            Expect(TokenKind.Newline, "end of line");

            return new ModifyNode(channel, scope, filter, layer, amount, keyword.Span);
        }

        /// <summary>Reads <c>+2</c>, <c>-1</c>, <c>x1.5</c>, <c>=0</c> or <c>clamp 0..10</c>.</summary>
        private (ModifierLayer Layer, ExprNode Amount) ParseModifierAmount()
        {
            if (Match(TokenKind.Star)) return (ModifierLayer.Multiply, ParseExpression());
            if (Match(TokenKind.Plus)) return (ModifierLayer.Add, ParseExpression());
            if (Match(TokenKind.Equal)) return (ModifierLayer.Override, ParseExpression());
            if (MatchKeyword("clamp")) return (ModifierLayer.Clamp, ParseExpression());
            if (MatchKeyword("set")) return (ModifierLayer.Override, ParseExpression());

            // `+2` and `-1` both fall through to the additive layer; the sign rides along in the
            // expression, so `-1` becomes an Add of negative one.
            return (ModifierLayer.Add, ParseExpression());
        }

        // -----------------------------------------------------------------------------------
        // Statements
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// A block is either the rest of the current line (<c>if x: draw 1</c>) or an indented
        /// group of statements on the lines that follow.
        /// </summary>
        private BlockNode ParseBlock()
        {
            SourceSpan span = Current.Span;

            if (_depth >= MaxNesting)
            {
                TooDeep();
                RecoverToBlockEnd();
                _abortLine = false;
                return BlockNode.Empty(span);
            }

            _depth++;
            try
            {
                return ParseBlockBody(span);
            }
            finally
            {
                _depth--;
            }
        }

        private BlockNode ParseBlockBody(SourceSpan span)
        {
            if (!Check(TokenKind.Newline))
            {
                StatementNode? inline = ParseStatement();
                var single = inline == null ? new StatementNode[0] : new[] { inline };
                return new BlockNode(single, span);
            }

            Expect(TokenKind.Newline, "end of line");
            if (!Match(TokenKind.Indent)) return BlockNode.Empty(span);

            var statements = new List<StatementNode>();
            ParseIndentedStatements(statements);
            return new BlockNode(statements, span);
        }

        /// <summary>
        /// Statements up to the dedent that closes the block the caller has just indented into. A
        /// test body and an effect both go through here, so a stray indent reads the same in either.
        /// </summary>
        private void ParseIndentedStatements(List<StatementNode> statements)
        {
            int stray = 0;
            while (!Check(TokenKind.EndOfFile))
            {
                SkipNewlines();
                if (Check(TokenKind.EndOfFile)) break;
                if (SkipStrayIndent(ref stray)) continue;
                if (Check(TokenKind.Dedent)) break;

                int before = _index;
                StatementNode? statement = ParseStatement();
                if (statement != null) statements.Add(statement);
                if (_index == before) _index++;
            }
            Match(TokenKind.Dedent);
        }

        /// <summary>
        /// A line indented under a line that opens no block. It is reported once and read as if it
        /// were not indented, and its matching dedent is consumed here, so the enclosing block does
        /// not end early and scatter errors over the lines that follow. Returns true when it consumed
        /// a token, either the stray indent or the dedent that closes one.
        /// </summary>
        private bool SkipStrayIndent(ref int stray)
        {
            if (Check(TokenKind.Dedent) && stray > 0)
            {
                stray--;
                _index++;
                return true;
            }

            if (!Check(TokenKind.Indent)) return false;

            // An indent straight after a dedent is the lexer's recovery from a dedent that lines up
            // with no block. The lexer has reported that line already (CT0001), and it is less
            // indented than the one above it, not more.
            if (_index == 0 || _tokens[_index - 1].Kind != TokenKind.Dedent)
            {
                _diagnostics.Error(
                    "CT0030",
                    "This line is indented further than the line above it, which does not open a block. Only a line ending in `:`, such as `effect:` or `if target.dead:`, can have lines indented under it.",
                    Current.Span);
            }

            stray++;
            _index++;
            return true;
        }

        private StatementNode? ParseStatement()
        {
            if (Check(TokenKind.EndOfFile)) return null;

            if (Check(TokenKind.Identifier))
            {
                switch (Current.Text.ToLowerInvariant())
                {
                    case "let": return ParseLet();
                    case "if": return ParseIf();
                    case "repeat": return ParseRepeat();
                    case "for": return ParseForEach();
                    case "chance": return ParseChance();
                    case "next": return ParseNextTurn();
                    case "in": return ParseInTurns();
                    case "until": return ParseUntil();
                }

                // `setup:` and similar labelled groups. No verb is ever directly followed by a
                // colon, so this cannot shadow a command.
                if (Peek().Kind == TokenKind.Colon)
                {
                    Token label = Advance();
                    Advance(); // colon
                    return new LabeledBlockNode(label.Text.ToLowerInvariant(), ParseBlock(), label.Span);
                }
            }

            return ParseCommandOrAssignment();
        }

        private StatementNode ParseIf()
        {
            Token keyword = Advance();
            ExprNode condition = ParseExpression();
            Expect(TokenKind.Colon, "`:`");
            BlockNode thenBlock = ParseBlock();

            BlockNode? elseBlock = null;
            SkipNewlines();
            if (CheckKeyword("else"))
            {
                _index++;
                // `else if` chains by nesting the next `if` inside the else block.
                if (CheckKeyword("if"))
                {
                    StatementNode nested = ParseIf();
                    elseBlock = new BlockNode(new[] { nested }, nested.Span);
                }
                else
                {
                    Expect(TokenKind.Colon, "`:`");
                    elseBlock = ParseBlock();
                }
            }

            return new IfNode(condition, thenBlock, elseBlock, keyword.Span);
        }

        /// <summary>
        /// `let name = expression`. The `=` is required: it keeps a binding obviously a binding, and
        /// leaves `let` out of the sign shorthand that bare assignments allow.
        /// </summary>
        private StatementNode ParseLet()
        {
            Token keyword = Advance(); // `let`

            string name = string.Empty;
            if (Check(TokenKind.Identifier)) name = Advance().Text;
            else Expect(TokenKind.Identifier, "a name after `let`");

            Expect(TokenKind.Equal, "`=`");
            ExprNode value = ParseExpression();
            Expect(TokenKind.Newline, "end of line");
            return new LetNode(name, value, keyword.Span);
        }

        private StatementNode ParseRepeat()
        {
            Token keyword = Advance();
            ExprNode count = ParseExpression();
            MatchKeyword("times");
            Expect(TokenKind.Colon, "`:`");
            return new RepeatNode(count, ParseBlock(), keyword.Span);
        }

        private StatementNode ParseForEach()
        {
            Token keyword = Advance(); // `for`
            MatchKeyword("each");

            string variable = "it";
            // `for each card in hand:` binds `card`; `for each in hand:` binds the default `it`.
            if (Check(TokenKind.Identifier) && !CheckKeyword("in")) variable = Advance().Text;

            ExprNode source;
            if (MatchKeyword("in")) source = ParseExpression();
            else source = new NameExpr(variable, keyword.Span);

            Expect(TokenKind.Colon, "`:`");
            return new ForEachNode(variable, source, ParseBlock(), keyword.Span);
        }

        private StatementNode ParseChance()
        {
            Token keyword = Advance();
            ExprNode probability = ParseExpression();
            Expect(TokenKind.Colon, "`:`");
            BlockNode body = ParseBlock();

            BlockNode? elseBody = null;
            SkipNewlines();
            if (CheckKeyword("else"))
            {
                _index++;
                Expect(TokenKind.Colon, "`:`");
                elseBody = ParseBlock();
            }

            return new ChanceNode(probability, body, elseBody, keyword.Span);
        }

        private StatementNode ParseNextTurn()
        {
            Token keyword = Advance(); // `next`
            MatchKeyword("turn");
            Expect(TokenKind.Colon, "`:`");
            return new ScheduleNode(ScheduleKind.NextTurn, null, null, ParseBlock(), keyword.Span);
        }

        private StatementNode ParseInTurns()
        {
            Token keyword = Advance(); // `in`
            ExprNode delay = ParseExpression();
            MatchKeyword("turns");
            MatchKeyword("turn");
            Expect(TokenKind.Colon, "`:`");
            return new ScheduleNode(ScheduleKind.After, delay, null, ParseBlock(), keyword.Span);
        }

        private StatementNode ParseUntil()
        {
            Token keyword = Advance(); // `until`

            string deadline = "turn_end";
            if (Check(TokenKind.Identifier)) deadline = Advance().Text.ToLowerInvariant();
            else _diagnostics.Error("CT0022", $"Expected an event name after `until` but found {Current}.", Current.Span);

            Expect(TokenKind.Colon, "`:`");
            return new ScheduleNode(ScheduleKind.Until, null, deadline, ParseBlock(), keyword.Span);
        }

        /// <summary>
        /// Distinguishes <c>stacks -1</c> (an assignment) from <c>deal 6 to target</c> (a command).
        /// The rule is purely syntactic: a path followed by an assignment operator, or by a signed
        /// number, is an assignment. Everything else is a verb call.
        /// </summary>
        private StatementNode ParseCommandOrAssignment()
        {
            int start = _index;

            if (Check(TokenKind.Identifier) && LooksLikeAssignment(out int pathLength))
            {
                ExprNode target = ParsePath(pathLength);

                if (Match(TokenKind.Equal)) return FinishAssignment(target, AssignOperator.Set, start);
                if (Match(TokenKind.PlusEqual)) return FinishAssignment(target, AssignOperator.Add, start);
                if (Match(TokenKind.MinusEqual)) return FinishAssignment(target, AssignOperator.Subtract, start);
                if (Match(TokenKind.StarEqual)) return FinishAssignment(target, AssignOperator.Multiply, start);
                if (Match(TokenKind.Plus)) return FinishAssignment(target, AssignOperator.Add, start);
                if (Match(TokenKind.Minus)) return FinishAssignment(target, AssignOperator.Subtract, start);
                if (Match(TokenKind.Star)) return FinishAssignment(target, AssignOperator.Multiply, start);
            }

            _index = start;
            return ParseCommand();
        }

        private StatementNode FinishAssignment(ExprNode target, AssignOperator op, int start)
        {
            ExprNode value = ParseExpression();
            Expect(TokenKind.Newline, "end of line");
            return new AssignNode(target, op, value, _tokens[start].Span);
        }

        /// <summary>
        /// Looks ahead over a dotted path to see whether an assignment operator follows.
        /// <paramref name="pathLength"/> receives the number of tokens the path occupies.
        /// </summary>
        private bool LooksLikeAssignment(out int pathLength)
        {
            int offset = 0;
            pathLength = 0;

            if (Peek(offset).Kind != TokenKind.Identifier) return false;
            offset++;

            while (Peek(offset).Kind == TokenKind.Dot && Peek(offset + 1).Kind == TokenKind.Identifier) offset += 2;

            pathLength = offset;
            TokenKind next = Peek(offset).Kind;

            switch (next)
            {
                case TokenKind.Equal:
                case TokenKind.PlusEqual:
                case TokenKind.MinusEqual:
                case TokenKind.StarEqual:
                    return true;

                // `stacks -1` and `energy +1`: a sign directly followed by a number, and nothing
                // else on the line. Requiring the line to end here keeps `deal 6 to x` a command.
                case TokenKind.Plus:
                case TokenKind.Minus:
                case TokenKind.Star:
                    return Peek(offset + 1).Kind == TokenKind.Number && Peek(offset + 2).Kind == TokenKind.Newline;

                default:
                    return false;
            }
        }

        private ExprNode ParsePath(int tokenCount)
        {
            Token first = Advance();
            ExprNode path = new NameExpr(first.Text, first.Span);

            for (int consumed = 1; consumed < tokenCount; consumed += 2)
            {
                Expect(TokenKind.Dot, "`.`");
                Token member = Expect(TokenKind.Identifier, "a property name");
                path = new MemberExpr(path, member.Text, first.Span.To(member.Span));
            }

            return path;
        }

        private StatementNode ParseCommand()
        {
            Token verb = Expect(TokenKind.Identifier, "a verb");
            var arguments = new List<ExprNode>();
            var clauses = new List<ClauseNode>();

            while (!Check(TokenKind.Newline) && !Check(TokenKind.EndOfFile) && !Check(TokenKind.Dedent))
            {
                if (Match(TokenKind.Comma)) { ParseTrailingFlag(clauses); continue; }

                if (Check(TokenKind.Identifier) && ClauseKeywords.Contains(Current.Text))
                {
                    Token clauseKeyword = Advance();
                    ExprNode? value = StartsExpression() ? ParseExpression() : null;
                    clauses.Add(new ClauseNode(clauseKeyword.Text.ToLowerInvariant(), value, clauseKeyword.Span));
                    continue;
                }

                if (!StartsExpression())
                {
                    _diagnostics.Error("CT0023", $"Unexpected {Current} in `{verb.Text}`.", Current.Span);
                    RecoverToLineEnd();
                    return new CommandNode(verb.Text, arguments, clauses, verb.Span);
                }

                arguments.Add(ParseExpression());
            }

            Expect(TokenKind.Newline, "end of line");
            return new CommandNode(verb.Text, arguments, clauses, verb.Span);
        }

        /// <summary>Parses the part after a comma in <c>deal 5 to x, ignore block</c>.</summary>
        private void ParseTrailingFlag(List<ClauseNode> clauses)
        {
            if (!Check(TokenKind.Identifier))
            {
                if (StartsExpression()) clauses.Add(new ClauseNode("and", ParseExpression(), Current.Span));
                return;
            }

            Token first = Advance();
            string keyword = first.Text.ToLowerInvariant();

            // `ignore block` and friends read as flag plus subject.
            if (FlagKeywords.Contains(keyword) && Check(TokenKind.Identifier))
            {
                Token subject = Advance();
                clauses.Add(new ClauseNode(keyword + "_" + subject.Text.ToLowerInvariant(), null, first.Span.To(subject.Span)));
                return;
            }

            clauses.Add(new ClauseNode(keyword, null, first.Span));
        }

        private bool StartsExpression()
        {
            switch (Current.Kind)
            {
                case TokenKind.Identifier:
                case TokenKind.QualifiedName:
                case TokenKind.Number:
                case TokenKind.String:
                case TokenKind.LeftParen:
                case TokenKind.LeftBracket:
                case TokenKind.Minus:
                    return true;
                default:
                    return false;
            }
        }

        // -----------------------------------------------------------------------------------
        // Expressions, lowest precedence first
        // -----------------------------------------------------------------------------------

        public ExprNode ParseExpression()
        {
            if (_depth >= MaxNesting) return TooDeep();

            _depth++;
            try
            {
                return ParseOr();
            }
            finally
            {
                _depth--;
            }
        }

        private ExprNode ParseOr()
        {
            ExprNode left = ParseAnd();
            while (CheckKeyword("or"))
            {
                Token op = Advance();
                ExprNode right = ParseAnd();
                left = new BinaryExpr(BinaryOperator.Or, left, right, op.Span);
            }
            return left;
        }

        private ExprNode ParseAnd()
        {
            ExprNode left = ParseNot();
            while (CheckKeyword("and"))
            {
                Token op = Advance();
                ExprNode right = ParseNot();
                left = new BinaryExpr(BinaryOperator.And, left, right, op.Span);
            }
            return left;
        }

        private ExprNode ParseNot()
        {
            if (CheckKeyword("not"))
            {
                if (_depth >= MaxNesting) return TooDeep();
                Token op = Advance();
                _depth++;
                try
                {
                    return new UnaryExpr(UnaryOperator.Not, ParseNot(), op.Span);
                }
                finally
                {
                    _depth--;
                }
            }
            return ParseComparison();
        }

        private ExprNode ParseComparison()
        {
            ExprNode left = ParsePer();

            while (true)
            {
                BinaryOperator op;
                if (Match(TokenKind.EqualEqual) || Match(TokenKind.Equal)) op = BinaryOperator.Equal;
                else if (Match(TokenKind.BangEqual)) op = BinaryOperator.NotEqual;
                else if (Match(TokenKind.Less)) op = BinaryOperator.Less;
                else if (Match(TokenKind.LessEqual)) op = BinaryOperator.LessOrEqual;
                else if (Match(TokenKind.Greater)) op = BinaryOperator.Greater;
                else if (Match(TokenKind.GreaterEqual)) op = BinaryOperator.GreaterOrEqual;
                else if (MatchKeyword("is")) op = BinaryOperator.Equal;
                else if (MatchKeyword("has")) op = BinaryOperator.Has;
                else break;

                ExprNode right = ParsePer();
                left = new BinaryExpr(op, left, right, left.Span);
            }

            return left;
        }

        /// <summary><c>2 per Poison on target</c> multiplies the left side by a count.</summary>
        private ExprNode ParsePer()
        {
            ExprNode left = ParseOn();
            while (CheckKeyword("per"))
            {
                Token op = Advance();
                ExprNode right = ParseOn();
                left = new BinaryExpr(BinaryOperator.Per, left, right, op.Span);
            }
            return left;
        }

        /// <summary><c>Poison on target</c> and <c>cards in hand</c>.</summary>
        private ExprNode ParseOn()
        {
            ExprNode left = ParseRange();

            while (true)
            {
                if (CheckKeyword("on"))
                {
                    Token op = Advance();
                    left = new BinaryExpr(BinaryOperator.On, left, ParseRange(), op.Span);
                    continue;
                }
                if (CheckKeyword("in"))
                {
                    Token op = Advance();
                    left = new BinaryExpr(BinaryOperator.In, left, ParseRange(), op.Span);
                    continue;
                }
                break;
            }

            return left;
        }

        private ExprNode ParseRange()
        {
            ExprNode low = ParseAdditive();
            if (!Match(TokenKind.DotDot)) return low;

            ExprNode high = ParseAdditive();
            return new RangeExpr(low, high, low.Span.To(high.Span));
        }

        private ExprNode ParseAdditive()
        {
            ExprNode left = ParseMultiplicative();

            while (true)
            {
                BinaryOperator op;
                if (Match(TokenKind.Plus)) op = BinaryOperator.Add;
                else if (Match(TokenKind.Minus)) op = BinaryOperator.Subtract;
                else break;

                ExprNode right = ParseMultiplicative();
                left = new BinaryExpr(op, left, right, left.Span);
            }

            return left;
        }

        private ExprNode ParseMultiplicative()
        {
            ExprNode left = ParseUnary();

            while (true)
            {
                BinaryOperator op;
                if (Match(TokenKind.Star)) op = BinaryOperator.Multiply;
                else if (Match(TokenKind.Slash)) op = BinaryOperator.Divide;
                else if (Match(TokenKind.Percent)) op = BinaryOperator.Modulo;
                else break;

                ExprNode right = ParseUnary();
                left = new BinaryExpr(op, left, right, left.Span);
            }

            return left;
        }

        private ExprNode ParseUnary()
        {
            if (_depth >= MaxNesting) return TooDeep();

            _depth++;
            try
            {
                if (Check(TokenKind.Minus))
                {
                    Token op = Advance();
                    return new UnaryExpr(UnaryOperator.Negate, ParseUnary(), op.Span);
                }

                // Unary plus is accepted and ignored, so `+stacks` reads the way designers write it.
                if (Match(TokenKind.Plus)) return ParseUnary();

                return ParseSelector();
            }
            finally
            {
                _depth--;
            }
        }

        /// <summary>Selector prefixes: <c>all enemies</c>, <c>random 2 cards</c>, <c>lowest hp enemy</c>.</summary>
        private ExprNode ParseSelector()
        {
            // A prefix word only selects when something to select from follows it, so
            // `pattern random` and a stat that happens to be called `highest` stay plain names.
            if (!Check(TokenKind.Identifier) || !SelectorPrefixes.Contains(Current.Text) || !StartsOperand(Peek()))
                return ParsePostfix();

            Token keyword = Advance();
            switch (keyword.Text.ToLowerInvariant())
            {
                case "all":
                    return new SelectorExpr(SelectorModifier.All, ParseSelectorSource(), null, null, keyword.Span);

                case "other":
                    return new SelectorExpr(SelectorModifier.Other, ParseSelectorSource(), null, null, keyword.Span);

                case "random":
                {
                    ExprNode? count = Check(TokenKind.Number) ? ParsePrimary() : null;
                    return new SelectorExpr(SelectorModifier.Random, ParseSelectorSource(), count, null, keyword.Span);
                }

                default:
                {
                    SelectorModifier modifier = string.Equals(keyword.Text, "lowest", StringComparison.OrdinalIgnoreCase)
                        ? SelectorModifier.Lowest
                        : SelectorModifier.Highest;

                    // `lowest hp enemies` names the stat first, then the group. With one name
                    // (`lowest enemies`, `lowest enemies where ...`) it is the group and the stat is hp.
                    string key = "hp";
                    if (Check(TokenKind.Identifier) && Peek().Kind == TokenKind.Identifier && StartsOperand(Peek()))
                        key = Advance().Text;

                    return new SelectorExpr(modifier, ParseSelectorSource(), null, key, keyword.Span);
                }
            }
        }

        /// <summary>
        /// The group a selector picks from. It binds as loosely as <c>in</c>, so
        /// <c>random 2 cards in hand</c> picks two cards from the hand rather than picking two of
        /// every card and then keeping whichever happen to be in hand.
        /// </summary>
        private ExprNode ParseSelectorSource() => ParseOn();

        private static bool StartsOperand(Token token) => token.Kind switch
        {
            TokenKind.Identifier => !OperatorWords.Contains(token.Text),
            TokenKind.QualifiedName or TokenKind.Number or TokenKind.String or TokenKind.LeftParen => true,
            _ => false,
        };

        private ExprNode ParsePostfix()
        {
            ExprNode expression = ParsePrimary();

            while (true)
            {
                if (Check(TokenKind.Dot) && Peek().Kind == TokenKind.Identifier)
                {
                    _index++;
                    Token member = Advance();

                    if (Match(TokenKind.LeftParen))
                    {
                        IReadOnlyList<ExprNode> arguments = ParseArgumentList();
                        expression = new CallExpr(member.Text, arguments, member.Span, expression);
                    }
                    else
                    {
                        expression = new MemberExpr(expression, member.Text, member.Span);
                    }
                    continue;
                }

                if (CheckKeyword("where"))
                {
                    Token where = Advance();
                    expression = new WhereExpr(expression, ParseExpression(), where.Span);
                    continue;
                }

                // Spatial narrowing, as in `enemies within 5m`. Modelled as an ordinary call so
                // that a host which implements spatial queries can supply the semantics without
                // the grammar needing to know anything about geometry.
                if (CheckKeyword("within"))
                {
                    Token within = Advance();
                    ExprNode distance = ParseAdditive();
                    expression = new CallExpr("within", new[] { expression, distance }, within.Span);
                    continue;
                }

                break;
            }

            return expression;
        }

        private IReadOnlyList<ExprNode> ParseArgumentList()
        {
            var arguments = new List<ExprNode>();

            if (!Check(TokenKind.RightParen))
            {
                do
                {
                    arguments.Add(ParseExpression());
                }
                while (Match(TokenKind.Comma));
            }

            Expect(TokenKind.RightParen, "`)`");
            return arguments;
        }

        private ExprNode ParsePrimary()
        {
            Token token = Current;

            switch (token.Kind)
            {
                case TokenKind.Number:
                {
                    _index++;
                    string? unit = token.Unit;
                    // `3 seconds` means `3s`. Without this the unit would become a stray argument
                    // that nothing reads, and the duration would silently be three ticks.
                    if (unit == null && Check(TokenKind.Identifier) && UnitWords.Contains(Current.Text))
                        unit = Advance().Text.ToLowerInvariant();
                    return new NumberExpr(token.Value, unit, token.Span);
                }

                case TokenKind.String:
                    _index++;
                    return new StringExpr(token.Text, token.Span);

                case TokenKind.QualifiedName:
                    _index++;
                    return new QualifiedExpr(token.Qualifier!, token.Text, token.Span);

                case TokenKind.LeftParen:
                {
                    _index++;
                    ExprNode inner = ParseExpression();
                    Expect(TokenKind.RightParen, "`)`");
                    return inner;
                }

                case TokenKind.Identifier:
                {
                    _index++;
                    if (Match(TokenKind.LeftParen))
                    {
                        IReadOnlyList<ExprNode> arguments = ParseArgumentList();
                        return new CallExpr(token.Text, arguments, token.Span);
                    }
                    return new NameExpr(token.Text, token.Span);
                }

                default:
                    if (!_abortLine) _diagnostics.Error("CT0024", $"Expected a value but found {token}.", token.Span);
                    // Leave line and block structure for the caller, so one missing value does not
                    // also produce an "expected end of line" error.
                    if (!Check(TokenKind.Newline) && !Check(TokenKind.Dedent) && !Check(TokenKind.EndOfFile)) _index++;
                    return new NumberExpr(Num.Zero, null, token.Span);
            }
        }

        internal static string FormatNumber(Num value) => value.ToString(null, CultureInfo.InvariantCulture);
    }
}
