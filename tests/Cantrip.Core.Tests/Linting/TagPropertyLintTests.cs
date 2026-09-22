using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// CT316: a card with <c>exhaust</c> on a line of its own loads without a word, and the card is
    /// discarded as usual, because only <c>tags exhaust</c> gives it that behaviour.
    /// </summary>
    public sealed class TagPropertyLintTests
    {
        private static List<Diagnostic> Tagged(string dsl) =>
            Linter.Lint(ContentLibrary.FromText(dsl, "tags.cantrip")).Where(d => d.Code == Linter.TagWrittenAsProperty).ToList();

        private static string Insight(string line) => $$"""
            card Strike
              cost 1
              target enemy
              effect:
                deal 6 to target

            card Insight
              cost 0
              {{line}}
              effect:
                draw 1

            test "Insight is exhausted"
              enemy hp 20
              deck Strike
              play Insight
              expect count(exhaust) == 1
            """;

        [Fact]
        public void A_bare_exhaust_line_does_nothing_and_is_now_reported()
        {
            // As a tag, it works and nothing is reported.
            ContentLibrary right = ContentLibrary.FromText(Insight("tags exhaust"), "tags.cantrip");
            Assert.True(new DslTestRunner(right).RunAll().Single().Passed);
            Assert.DoesNotContain(Linter.Lint(right), d => d.Code == Linter.TagWrittenAsProperty);

            // On a line of its own, it loads cleanly and the card is discarded instead.
            ContentLibrary wrong = ContentLibrary.FromText(Insight("exhaust"), "tags.cantrip");
            Assert.False(wrong.Diagnostics.HasErrors, wrong.Diagnostics.ToString());
            Assert.False(new DslTestRunner(wrong).RunAll().Single().Passed);

            Diagnostic warning = Assert.Single(Linter.Lint(wrong), d => d.Code == Linter.TagWrittenAsProperty);
            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.Equal(9, warning.Span.Line);
            Assert.Equal("tags exhaust", warning.Suggestion);
            Assert.Contains("card \"Insight\"", warning.Message);
        }

        [Theory]
        [InlineData("retain")]
        [InlineData("ethereal")]
        [InlineData("unplayable")]
        [InlineData("power")]
        [InlineData("attack")]
        [InlineData("exhaust true")]
        [InlineData("Exhaust")]
        public void Every_card_tag_with_behaviour_is_checked(string line)
        {
            Diagnostic warning = Assert.Single(Tagged($"card Probe\n  cost 1\n  {line}\n"));
            Assert.Equal("tags " + line.Split(' ')[0].ToLowerInvariant(), warning.Suggestion);
        }

        [Fact]
        public void The_suggestion_keeps_the_tags_a_card_already_has()
        {
            Diagnostic warning = Assert.Single(Tagged("card Probe\n  cost 1\n  tags attack, fire\n  retain\n"));
            Assert.Equal("tags attack, fire, retain", warning.Suggestion);
        }

        [Fact]
        public void A_bare_debuff_line_on_a_status_is_reported()
        {
            Diagnostic warning = Assert.Single(Tagged("status Weak\n  stacking duration\n  debuff\n"));
            Assert.Equal("tags debuff", warning.Suggestion);
        }

        [Theory]
        [InlineData("card Probe\n  cost 1\n  power 3\n")]           // a stat content may read
        [InlineData("card Probe\n  cost 1\n  attack 2\n")]          // the `attack` verb reads this stat
        [InlineData("card Probe\n  cost 1\n  innate\n")]            // not a tag Cantrip gives meaning to
        [InlineData("card Probe\n  tags exhaust\n  exhaust\n")]     // already tagged: the line is harmless
        [InlineData("relic Probe\n  exhaust\n")]                    // relics are never exhausted
        [InlineData("status Probe\n  stacking duration\n  retain\n")]
        public void Other_lines_are_left_alone(string dsl)
        {
            Assert.Empty(Tagged(dsl));
        }

        [Theory]
        [InlineData("basic")]
        [InlineData("corpus")]
        [InlineData("slice")]
        [InlineData("recipes")]
        public void No_sample_writes_a_tag_as_a_property(string folder)
        {
            var content = new ContentLibrary();
            content.LoadFolder(Path.Combine(LintTestPaths.RepositoryRoot(), "samples", folder));

            List<Diagnostic> found = Linter.Lint(content).Where(d => d.Code == Linter.TagWrittenAsProperty).ToList();
            Assert.True(found.Count == 0, string.Join("\n", found));
        }
    }
}
