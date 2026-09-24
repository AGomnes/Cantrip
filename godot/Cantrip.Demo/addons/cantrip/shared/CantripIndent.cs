#nullable enable
using System;
using System.Text;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// What one step of indentation is in a given <c>.cantrip</c> buffer, and how to keep a buffer
    /// written in one step from quietly acquiring another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The language is whitespace-sensitive: the lexer turns a change of indentation into Indent and
    /// Dedent tokens, and it counts a tab as <see cref="TabWidth"/> columns. So an editor that
    /// inserts a tab into a file written with two spaces writes a line that is four columns deep and
    /// looks two deep, and a block ends somewhere nobody meant it to. Every rule here exists to stop
    /// that: the editor asks the buffer what a step already is and inserts exactly that, in spaces.
    /// </para>
    /// <para>
    /// This lives in <c>shared/</c> and names no engine type, so the rules are unit tested without
    /// starting Godot. The editor hands the answer to its <c>CodeEdit</c> once, and Godot's own
    /// indenting does the typing.
    /// </para>
    /// </remarks>
    public static class CantripIndent
    {
        /// <summary>
        /// The columns the core's lexer counts a tab as. It is repeated here because the lexer keeps
        /// it private; a test lexes a tabbed line and checks that the two still agree.
        /// </summary>
        public const int TabWidth = 4;

        /// <summary>What a buffer with nothing indented in it yet is given. Every sample uses it.</summary>
        public const int DefaultWidth = 2;

        /// <summary>Wider than this is a file doing something else, and is not taken as a step.</summary>
        public const int MaxWidth = 8;

        /// <summary>
        /// The step already used in <paramref name="text"/>: the narrowest indentation any line
        /// begins with. A buffer indented with tabs answers <see cref="TabWidth"/>, because that is
        /// what its tabs already mean to the lexer, so spaces typed from now on line up with them.
        /// </summary>
        public static int Detect(string? text)
        {
            if (string.IsNullOrEmpty(text)) return DefaultWidth;

            int narrowest = int.MaxValue;
            int tabbed = 0;
            int spaced = 0;

            foreach (string line in Lines(text!))
            {
                int characters = LeadingCharacters(line);
                if (characters == 0 || characters == line.Length) continue;   // not indented, or blank

                bool tab = line.IndexOf('\t', 0, characters) >= 0;
                if (tab) tabbed++;

                // A line indented with both is not evidence of a width, it is evidence of a mess.
                if (tab) continue;

                spaced++;
                if (characters < narrowest) narrowest = characters;
            }

            if (tabbed > spaced) return TabWidth;
            if (narrowest == int.MaxValue || narrowest > MaxWidth) return DefaultWidth;
            return narrowest;
        }

        /// <summary>True when a tab appears anywhere in the buffer, indenting or not.</summary>
        public static bool HasTab(string? text) =>
            !string.IsNullOrEmpty(text) && text!.IndexOf('\t') >= 0;

        /// <summary>True when a tab indents a line, which is the kind that changes what the file means.</summary>
        /// <remarks>
        /// The cheap question is asked first. This runs on every keystroke, and splitting a file
        /// into lines to find out that it has no tab in it would allocate the whole buffer twice
        /// for nothing.
        /// </remarks>
        public static bool HasIndentingTab(string? text)
        {
            if (!HasTab(text)) return false;

            foreach (string line in Lines(text!))
            {
                if (line.IndexOf('\t', 0, LeadingCharacters(line)) >= 0) return true;
            }
            return false;
        }

        /// <summary>One step of indentation: spaces, never a tab.</summary>
        public static string Unit(int width) => new string(' ', Clamp(width));

        /// <summary>How many characters of whitespace a line begins with.</summary>
        public static int LeadingCharacters(string? line)
        {
            if (string.IsNullOrEmpty(line)) return 0;

            int i = 0;
            while (i < line!.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return i;
        }

        /// <summary>
        /// How many columns a line is indented by, counting a tab the way the lexer counts it: on to
        /// the next multiple of <see cref="TabWidth"/> rather than as one column.
        /// </summary>
        public static int LeadingColumns(string? line)
        {
            if (string.IsNullOrEmpty(line)) return 0;

            int columns = 0;
            foreach (char c in line!)
            {
                if (c == ' ') columns++;
                else if (c == '\t') columns += TabWidth - (columns % TabWidth);
                else break;
            }
            return columns;
        }

        /// <summary>
        /// The same text with every indenting tab replaced by the spaces it already stood for, so
        /// the file reads to the lexer exactly as it did before. A tab after the first non-blank
        /// character is left alone: inside a string it is part of the text.
        /// </summary>
        public static string WithoutTabs(string? text)
        {
            if (!HasTab(text)) return text ?? string.Empty;

            var result = new StringBuilder(text!.Length + 16);
            int columns = 0;
            bool leading = true;

            foreach (char c in text)
            {
                if (c == '\n' || c == '\r')
                {
                    result.Append(c);
                    columns = 0;
                    leading = true;
                }
                else if (leading && c == '\t')
                {
                    int stop = TabWidth - (columns % TabWidth);
                    result.Append(' ', stop);
                    columns += stop;
                }
                else if (leading && c == ' ')
                {
                    result.Append(c);
                    columns++;
                }
                else
                {
                    leading = false;
                    result.Append(c);
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// The whitespace a new line typed after <paramref name="line"/> begins with: as deep as
        /// that line, one step deeper when it opens a block with <c>:</c>. This is the rule the
        /// editor hands to Godot's automatic indenting, stated here so that it can be tested.
        /// </summary>
        public static string Continue(string? line, int width)
        {
            int columns = LeadingColumns(line);
            if (OpensABlock(line)) columns += Clamp(width);
            return new string(' ', columns);
        }

        /// <summary>
        /// True for a line whose last character before any comment is <c>:</c>, which is how every
        /// block in the language opens: <c>effect:</c>, <c>move "Swipe":</c>, <c>on damaged:</c>.
        /// </summary>
        public static bool OpensABlock(string? line)
        {
            string body = WithoutComment(line).TrimEnd();
            return body.Length > 0 && body[body.Length - 1] == ':';
        }

        /// <summary>
        /// The part of a line before a <c>#</c> that is not inside a string. The highlighter reads a
        /// comment the same way, because a comment must not decide whether a block opens.
        /// </summary>
        public static string WithoutComment(string? line)
        {
            if (string.IsNullOrEmpty(line)) return string.Empty;

            bool quoted = false;
            for (int i = 0; i < line!.Length; i++)
            {
                char c = line[i];
                if (c == '"') quoted = !quoted;
                else if (c == '#' && !quoted) return line.Substring(0, i);
            }
            return line!;
        }

        private static int Clamp(int width) => width < 1 ? DefaultWidth : (width > MaxWidth ? MaxWidth : width);

        private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');
    }
}
