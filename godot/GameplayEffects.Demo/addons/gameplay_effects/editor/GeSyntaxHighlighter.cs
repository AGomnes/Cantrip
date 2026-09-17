#if TOOLS
using System;
using System.Collections.Generic;
using GameplayEffects.Diagnostics;
using GameplayEffects.Syntax;
using Godot;

namespace GameplayEffects.GodotAdapter
{
    /// <summary>
    /// Colours <c>.ge</c> source by running the core's own <see cref="Lexer"/> over it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Highlighting that re-implements the grammar drifts from it: the day the lexer learns a new
    /// literal, a hand-written scanner still paints the old one. Every shape here therefore comes
    /// from the real token stream, including the two the DSL is easy to get wrong, <c>tag:fire</c>
    /// as a single token and <c>x1.5</c> as a multiplication.
    /// </para>
    /// <para>
    /// The word lists below are the one part that can fall behind, because the parser's own tables
    /// (its declaration keywords, clause keywords and operator words) and the interpreter's reserved
    /// names are internal to the core. A word missing from them costs a colour and nothing else.
    /// </para>
    /// <para>
    /// Registering this with the script editor is belt and braces, and honestly does nothing for
    /// <c>.ge</c> files today: the script editor only opens <see cref="Script"/> resources, and a
    /// <c>.ge</c> file imports as a plain resource. It costs nothing, and the day the editor opens
    /// arbitrary text it will already be right. The view that does use it is
    /// <see cref="GeSourceView"/>, which owns a <see cref="CodeEdit"/> of its own.
    /// </para>
    /// </remarks>
    [Tool]
    public partial class GeSyntaxHighlighter : EditorSyntaxHighlighter
    {
        /// <summary>Declarations and the members inside them: the skeleton of a file.</summary>
        private static readonly HashSet<string> Structure = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "card", "status", "relic", "ability", "enemy", "keyword", "item", "event", "encounter", "actor",
            "resource", "verb", "ruleset", "test", "effect", "move", "on", "modify", "setup", "once", "per",
            "priority", "and", "or", "not", "is", "has", "where", "within", "of",
        };

        /// <summary>Words that decide what runs next.</summary>
        private static readonly HashSet<string> ControlFlow = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "if", "else", "repeat", "for", "each", "in", "chance", "next", "until", "then", "times",
        };

        /// <summary>Names the runtime gives a meaning wherever they appear.</summary>
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "self", "owner", "source", "target", "it", "card", "player", "controller", "true", "false", "none",
            "nothing", "turn", "now", "enemies", "allies", "everyone", "actors", "enemy", "hand", "draw",
            "draw_pile", "discard", "discard_pile", "exhaust", "exhaust_pile", "powers", "relics", "deck",
            "cards", "statuses", "stacks", "event",
        };

        /// <summary>Words that introduce a named clause, as in <c>deal 6 to target</c>.</summary>
        private static readonly HashSet<string> Clauses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "to", "from", "with", "at", "by", "into", "over", "as", "against", "using", "onto",
        };

        private readonly List<List<KeyValuePair<int, Color>>> _lines = new List<List<KeyValuePair<int, Color>>>();
        private readonly List<KeyValuePair<int, Color>> _empty = new List<KeyValuePair<int, Color>>();

        private string? _source;
        private bool _built;
        private Palette? _palette;

        public override string _GetName() => "Gameplay Effects";

        public override string[] _GetSupportedLanguages() => new[] { "ge" };

        /// <summary>
        /// The text to colour. Set by the view that owns the editor, because a
        /// <see cref="CodeEdit"/> hands out a fresh string on every read and this is asked for one
        /// line at a time.
        /// </summary>
        public void SetSource(string? text)
        {
            _source = text;
            _built = false;
        }

        /// <summary>Colours a whole file and reports how many runs came out, for headless checks.</summary>
        public int Highlight(string text)
        {
            SetSource(text);
            Build();

            int runs = 0;
            foreach (List<KeyValuePair<int, Color>> line in _lines) runs += line.Count;
            return runs;
        }

        public override void _ClearHighlightingCache()
        {
            _built = false;
            _palette = null;
        }

        public override void _UpdateCache() => _built = false;

        public override Godot.Collections.Dictionary _GetLineSyntaxHighlighting(int line)
        {
            Build();

            var result = new Godot.Collections.Dictionary();
            foreach (KeyValuePair<int, Color> run in LineRuns(line))
            {
                var entry = new Godot.Collections.Dictionary();
                entry["color"] = run.Value;
                result[run.Key] = entry;
            }
            return result;
        }

        private List<KeyValuePair<int, Color>> LineRuns(int line) =>
            line >= 0 && line < _lines.Count ? _lines[line] : _empty;

        // Building ---------------------------------------------------------------------------------

        private void Build()
        {
            if (_built) return;
            _built = true;
            _lines.Clear();

            // The source view sets the text; anything else (the script editor) is asked for it.
            string text = _source ?? GetTextEdit()?.Text ?? string.Empty;
            Palette palette = _palette ??= Palette.FromEditor();

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            var maps = new SortedDictionary<int, Color>[lines.Length];
            var strings = new List<(int Start, int End)>[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                maps[i] = new SortedDictionary<int, Color>();
                strings[i] = new List<(int, int)>();
            }

            // Lexing cannot be allowed to fail here: a file is half-written most of the time it is
            // being looked at, and a designer mid-keystroke still wants their colours.
            IReadOnlyList<Token> tokens;
            try
            {
                tokens = Lexer.Tokenize(text, "<highlight>", new DiagnosticBag());
            }
            catch (Exception)
            {
                tokens = new Token[0];
            }

            foreach (Token token in tokens)
            {
                if (token.Span.Length <= 0) continue;

                int line = token.Span.Line - 1;
                if (line < 0 || line >= lines.Length) continue;

                int start = Math.Max(0, token.Span.Column - 1);
                int end = start + token.Span.Length;
                if (token.Kind == TokenKind.String) strings[line].Add((start, end));

                Color? colour = ColourOf(token, palette);
                if (colour == null) continue;

                SortedDictionary<int, Color> map = maps[line];
                map[start] = colour.Value;
                if (!map.ContainsKey(end)) map[end] = palette.Text;
            }

            // Comments never reach the token stream, so they are found here. A `#` inside a string
            // is text, which is why the string spans were kept.
            for (int i = 0; i < lines.Length; i++)
            {
                int hash = FindComment(lines[i], strings[i]);
                if (hash < 0) continue;

                SortedDictionary<int, Color> map = maps[i];
                foreach (int column in new List<int>(map.Keys))
                {
                    if (column > hash) map.Remove(column);
                }
                map[hash] = palette.Comment;
            }

            foreach (SortedDictionary<int, Color> map in maps)
            {
                var runs = new List<KeyValuePair<int, Color>>(map.Count);
                foreach (KeyValuePair<int, Color> entry in map) runs.Add(entry);
                _lines.Add(runs);
            }
        }

        private static int FindComment(string line, List<(int Start, int End)> strings)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] != '#') continue;

                bool quoted = false;
                foreach ((int start, int end) in strings)
                {
                    if (i >= start && i < end) { quoted = true; break; }
                }
                if (!quoted) return i;
            }
            return -1;
        }

        private static Color? ColourOf(Token token, Palette palette)
        {
            switch (token.Kind)
            {
                case TokenKind.Number:
                    return palette.Number;

                case TokenKind.String:
                    return palette.String;

                case TokenKind.QualifiedName:
                    // `tag:fire` is one token, and colouring it as one is the point: it reads as a
                    // single test rather than a name, a colon and another name.
                    return palette.Qualifier;

                case TokenKind.Identifier:
                    if (Structure.Contains(token.Text)) return palette.Keyword;
                    if (ControlFlow.Contains(token.Text)) return palette.Control;
                    if (Clauses.Contains(token.Text)) return palette.Symbol;
                    if (Reserved.Contains(token.Text)) return palette.Member;
                    // Definitions are written capitalised, and that is how content refers to them.
                    if (token.Text.Length > 0 && char.IsUpper(token.Text[0])) return palette.Definition;
                    return null;

                case TokenKind.Newline:
                case TokenKind.Indent:
                case TokenKind.Dedent:
                case TokenKind.EndOfFile:
                    return null;

                default:
                    return palette.Symbol;
            }
        }

        /// <summary>
        /// The colours the person using the editor already chose for their code, so <c>.ge</c> looks
        /// like everything else they have open. Each falls back to Godot's default dark theme.
        /// </summary>
        private sealed class Palette
        {
            private const string Prefix = "text_editor/theme/highlighting/";

            private Palette(
                Color keyword, Color control, Color number, Color text,
                Color str, Color comment, Color member, Color definition, Color symbol, Color qualifier)
            {
                Keyword = keyword;
                Control = control;
                Number = number;
                Text = text;
                String = str;
                Comment = comment;
                Member = member;
                Definition = definition;
                Symbol = symbol;
                Qualifier = qualifier;
            }

            public Color Keyword { get; }
            public Color Control { get; }
            public Color Number { get; }
            public Color Text { get; }
            public Color String { get; }
            public Color Comment { get; }
            public Color Member { get; }
            public Color Definition { get; }
            public Color Symbol { get; }
            public Color Qualifier { get; }

            public static Palette FromEditor()
            {
                Color keyword = Setting("keyword_color", new Color(1.0f, 0.44f, 0.52f));
                Color control = Setting("control_flow_keyword_color", new Color(1.0f, 0.55f, 0.8f));
                Color number = Setting("number_color", new Color(0.63f, 1.0f, 0.88f));
                Color text = Setting("text_color", new Color(0.88f, 0.88f, 0.92f));
                Color str = Setting("string_color", new Color(1.0f, 0.93f, 0.63f));
                Color comment = Setting("comment_color", new Color(0.8f, 0.81f, 0.83f, 0.5f));
                Color member = Setting("member_variable_color", new Color(0.74f, 0.53f, 0.55f));
                Color definition = Setting("base_type_color", new Color(0.26f, 1.0f, 0.76f));
                Color symbol = Setting("symbol_color", new Color(0.67f, 0.79f, 1.0f));

                return new Palette(keyword, control, number, text, str, comment, member, definition, symbol, definition);
            }

            private static Color Setting(string name, Color fallback)
            {
                EditorSettings? settings = EditorInterface.Singleton?.GetEditorSettings();
                string key = Prefix + name;
                if (settings == null || !settings.HasSetting(key)) return fallback;

                Variant value = settings.GetSetting(key);
                return value.VariantType == Variant.Type.Color ? value.AsColor() : fallback;
            }
        }
    }
}
#endif
