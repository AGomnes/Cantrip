using System;
using System.Collections.Generic;
using System.IO;
using Cantrip.Diagnostics;
using Cantrip.Syntax;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// The rules the dock's editor indents by. This is the one part of an editor for a
    /// whitespace-sensitive language that cannot be got wrong quietly: a tab where the file uses
    /// two spaces is four columns to the lexer and two to the eye, and the block ends in the wrong
    /// place with nothing on screen to say so.
    /// </summary>
    public sealed class CantripIndentTests
    {
        /// <summary>
        /// Not an assertion about the constant, but about the two agreeing. A tab and TabWidth
        /// spaces have to reach the same column, so the test mixes them in one file: a line indented
        /// with spaces and the next indented with a tab are siblings only if the width is right. Get
        /// it wrong either way and the second line opens a block or falls out of one, and the lexer
        /// says so.
        /// </summary>
        [Fact]
        public void A_tab_is_worth_what_the_lexer_says_it_is()
        {
            string step = new string(' ', CantripIndent.TabWidth);
            string spaced = "card \"A\"\n" + step + "cost 1\n" + step + "cost 2\n";
            string mixed = "card \"A\"\n" + step + "cost 1\n\tcost 2\n";

            var complaints = new DiagnosticBag();
            Lexer.Tokenize(mixed, "<test>", complaints);

            Assert.Equal(Shape(spaced), Shape(mixed));
            Assert.Empty(complaints);
            Assert.Equal(CantripIndent.TabWidth, CantripIndent.LeadingColumns("\tcost 1"));

            // And the same the other way: one column too few or too many is a different file.
            Assert.NotEqual(Shape(spaced), Shape("card \"A\"\n" + step + "cost 1\n" + new string(' ', CantripIndent.TabWidth - 1) + "cost 2\n"));
            Assert.NotEqual(Shape(spaced), Shape("card \"A\"\n" + step + "cost 1\n" + new string(' ', CantripIndent.TabWidth + 1) + "cost 2\n"));
        }

        /// <summary>
        /// A tab part way along a line's indentation goes to the next stop rather than adding four,
        /// which is the rule the lexer uses and the one the editor has to match when it widens a
        /// pasted block.
        /// </summary>
        [Fact]
        public void A_tab_lands_on_the_next_stop_wherever_it_is_typed()
        {
            string spaced = "card \"A\"\n" + new string(' ', CantripIndent.TabWidth) + "cost 1\n";
            string mixed = "card \"A\"\n  \tcost 1\n";

            Assert.Equal(Shape(spaced), Shape(mixed));
            Assert.Equal(CantripIndent.TabWidth, CantripIndent.LeadingColumns("  \tcost 1"));
            Assert.Equal(CantripIndent.WithoutTabs(mixed), spaced);
        }

        /// <summary>
        /// The whole reason the editor is careful. One tab typed into a file written with two
        /// spaces opens a block that nothing in the file looks like opening, and nothing on screen
        /// says so.
        /// </summary>
        [Fact]
        public void A_tab_in_a_two_space_file_opens_a_block_that_is_not_there()
        {
            string spaces = "card \"A\"\n  cost 1\n  effect:\n    block 1\n";
            string oneTab = "card \"A\"\n  cost 1\n\teffect:\n    block 1\n";

            Assert.NotEqual(Shape(spaces), Shape(oneTab));
            Assert.Equal(2, CantripIndent.LeadingColumns("  effect:"));
            Assert.Equal(4, CantripIndent.LeadingColumns("\teffect:"));
        }

        [Fact]
        public void A_step_of_indentation_is_spaces_and_never_a_tab()
        {
            for (int width = 1; width <= CantripIndent.MaxWidth; width++)
            {
                string unit = CantripIndent.Unit(width);
                Assert.Equal(width, unit.Length);
                Assert.DoesNotContain("\t", unit);
            }

            // A nonsense width still gives spaces rather than a tab or an empty step.
            Assert.Equal(CantripIndent.DefaultWidth, CantripIndent.Unit(0).Length);
            Assert.DoesNotContain("\t", CantripIndent.Unit(-1));
            Assert.Equal(CantripIndent.MaxWidth, CantripIndent.Unit(99).Length);
        }

        [Fact]
        public void The_step_is_read_from_the_buffer_rather_than_assumed()
        {
            Assert.Equal(2, CantripIndent.Detect("card \"A\"\n  cost 1\n  effect:\n    block 1\n"));
            Assert.Equal(4, CantripIndent.Detect("card \"A\"\n    cost 1\n    effect:\n        block 1\n"));
            Assert.Equal(3, CantripIndent.Detect("card \"A\"\n   cost 1\n"));
        }

        [Fact]
        public void A_buffer_with_nothing_indented_yet_gets_the_step_every_sample_uses()
        {
            Assert.Equal(2, CantripIndent.DefaultWidth);
            Assert.Equal(CantripIndent.DefaultWidth, CantripIndent.Detect(null));
            Assert.Equal(CantripIndent.DefaultWidth, CantripIndent.Detect(string.Empty));
            Assert.Equal(CantripIndent.DefaultWidth, CantripIndent.Detect("card \"A\"\n"));

            // A blank line of spaces is not an indented line, so it decides nothing.
            Assert.Equal(2, CantripIndent.Detect("card \"A\"\n \n  cost 1\n"));
        }

        [Fact]
        public void A_file_indented_with_tabs_answers_the_width_its_tabs_already_mean()
        {
            // Spaces typed into it from now on land where its tabs already land.
            Assert.Equal(CantripIndent.TabWidth, CantripIndent.Detect("card \"A\"\n\tcost 1\n\teffect:\n\t\tblock 1\n"));
            Assert.True(CantripIndent.HasIndentingTab("card \"A\"\n\tcost 1\n"));
            Assert.False(CantripIndent.HasIndentingTab("card \"A\"\n  text: \"a\tb\"\n"));
        }

        [Fact]
        public void Pasted_tabs_become_the_columns_they_already_stood_for()
        {
            Assert.Equal(
                "card \"A\"\n    cost 1\n        block 1\n",
                CantripIndent.WithoutTabs("card \"A\"\n\tcost 1\n\t\tblock 1\n"));

            // A tab part way through the indentation goes to the next stop, not four more columns.
            Assert.Equal("    cost 1", CantripIndent.WithoutTabs("  \tcost 1"));

            // Inside the text, a tab is part of the text and is left alone.
            Assert.Equal("  text: \"a\tb\"", CantripIndent.WithoutTabs("  text: \"a\tb\""));
            Assert.False(CantripIndent.HasTab(CantripIndent.WithoutTabs("\tcost 1")));
            Assert.Equal(string.Empty, CantripIndent.WithoutTabs(null));
        }

        [Fact]
        public void A_new_line_after_a_block_opener_goes_one_step_deeper()
        {
            Assert.Equal("  ", CantripIndent.Continue("card \"A\":", 2));
            Assert.Equal("    ", CantripIndent.Continue("  effect:", 2));
            Assert.Equal("        ", CantripIndent.Continue("    effect:", 4));
            Assert.Equal("  ", CantripIndent.Continue("  cost 1", 2));
            Assert.Equal(string.Empty, CantripIndent.Continue(string.Empty, 2));

            // A tabbed line is continued in spaces, at the columns the tab already stood for.
            Assert.Equal(new string(' ', CantripIndent.TabWidth), CantripIndent.Continue("\tcost 1", 2));
        }

        [Fact]
        public void A_colon_in_a_comment_or_a_string_does_not_open_a_block()
        {
            Assert.True(CantripIndent.OpensABlock("  effect:"));
            Assert.True(CantripIndent.OpensABlock("  effect:   # do the thing"));
            Assert.False(CantripIndent.OpensABlock("  # effect:"));
            Assert.False(CantripIndent.OpensABlock("  text: \"Deal 5:\""));
            Assert.False(CantripIndent.OpensABlock("  cost 1"));
            Assert.False(CantripIndent.OpensABlock(null));
        }

        /// <summary>
        /// The claim the editor is built on: everything shipped in this repository is written with
        /// two spaces and has no tab in it, so an editor that indents any other way would be the
        /// odd one out in every file it opens.
        /// </summary>
        [Fact]
        public void Every_cantrip_file_in_the_repository_is_two_spaces_and_no_tabs()
        {
            var offenders = new List<string>();
            string root = RepositoryRoot();

            foreach (string file in Directory.GetFiles(root, "*.cantrip", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(Path.GetRelativePath(root, file))) continue;

                string text = File.ReadAllText(file);
                string name = Path.GetRelativePath(root, file);

                if (CantripIndent.HasTab(text)) offenders.Add(name + " has a tab in it");
                int width = CantripIndent.Detect(text);
                if (width != 2) offenders.Add($"{name} indents by {width}");
            }

            Assert.True(offenders.Count == 0, string.Join("; ", offenders));
        }

        /// <summary>
        /// The token stream by kind and text, which is what indentation decides. Columns are left
        /// out on purpose: the lexer counts them in characters, so a tab and four spaces sit at
        /// different columns while opening exactly the same block, and the block is the question.
        /// </summary>
        private static string Shape(string text)
        {
            var parts = new List<string>();
            foreach (Token token in Lexer.Tokenize(text, "<test>", new DiagnosticBag()))
            {
                parts.Add(token.Text.Length == 0 ? token.Kind.ToString() : $"{token.Kind}({token.Text})");
            }
            return string.Join(" ", parts);
        }

        /// <summary>
        /// A copy the build made rather than a file anybody wrote: Godot's imported resources, the
        /// packaged addon, compiler output. What is in them is not this test's business.
        /// </summary>
        private static bool IsBuildOutput(string relative)
        {
            foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                switch (part)
                {
                    case "bin":
                    case "obj":
                    case ".git":
                    case ".godot":
                    case "artifacts":
                        return true;
                }
            }
            return false;
        }

        private static string RepositoryRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Cantrip.sln")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory != null, "No repository root above " + AppContext.BaseDirectory);
            return directory!.FullName;
        }
    }
}
