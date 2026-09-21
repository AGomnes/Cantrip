using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Review
{
    /// <summary>
    /// Mistakes a newcomer makes in their first hour, and what they are told. Each used to produce
    /// a message that named the symptom rather than the fix, or passed silently: found by walking
    /// the quickstart as a stranger would before the first public release.
    /// </summary>
    public sealed class FirstHourErrorTests
    {
        [Fact]
        [Trait("Regression", "first-hour-unquoted-name")]
        public void A_name_with_spaces_is_one_error_that_says_to_quote_it()
        {
            ContentLibrary content = ContentLibrary.FromText("card Heavy Blow\n  cost 2\n  effect:\n    draw 1\n");

            Diagnostic error = Assert.Single(content.Diagnostics.Errors);
            Assert.Equal("CT0029", error.Code);
            Assert.Contains("card \"Heavy Blow\"", error.Message);

            // Parsing carries on as if it had been quoted, so nothing after it cascades.
            Assert.NotNull(content.Find("Heavy Blow", "card"));
        }

        [Fact]
        [Trait("Regression", "first-hour-stray-indent")]
        public void A_line_indented_under_a_plain_statement_is_one_error_and_the_block_goes_on()
        {
            ContentLibrary content = ContentLibrary.FromText(
                "card Zap\n  cost 1\n  effect:\n    draw 1\n      draw 1\n    block 5\n  text: \"Zap.\"\n");

            Diagnostic error = Assert.Single(content.Diagnostics.Errors);
            Assert.Equal("CT0030", error.Code);
            Assert.Contains("ending in `:`", error.Message);

            // The stray line's dedent must not end the block early: the statement after it still
            // belongs to the effect, and the property after the effect still belongs to the card.
            EntityDefinition zap = content.Find("Zap", "card")!;
            Assert.Equal(3, zap.Effect!.Statements.Count);
            Assert.NotNull(zap.Property("text"));
        }

        [Fact]
        [Trait("Regression", "first-hour-undefined-enemy")]
        public void A_test_naming_an_undefined_enemy_says_so_and_how_to_fix_it()
        {
            ContentLibrary content = ContentLibrary.FromText(
                "card Zap\n  cost 1\n  target enemy\n  effect:\n    deal 6 to target\n\n" +
                "test \"zap\"\n  enemy Ghoul\n  play Zap on enemy\n");

            DslTestResult result = Assert.Single(new DslTestRunner(content).RunAll());

            Assert.False(result.Passed);
            Assert.Contains("No enemy named `Ghoul` is defined", result.Failure);
            Assert.Contains("enemy \"Ghoul\" hp 20", result.Failure);
        }

        [Fact]
        public void A_plain_labelled_enemy_in_a_test_still_works()
        {
            ContentLibrary content = ContentLibrary.FromText(
                "card Zap\n  cost 1\n  target enemy\n  effect:\n    deal 6 to target\n\n" +
                "test \"zap\"\n  enemy \"Ghoul\" hp 20\n  play Zap on enemy\n  expect enemy.hp == 14\n");

            DslTestResult result = Assert.Single(new DslTestRunner(content).RunAll());

            Assert.True(result.Passed, result.Failure);
        }

        [Fact]
        [Trait("Regression", "first-hour-validate-misses-typo")]
        public void Validate_reports_a_misspelled_verb()
        {
            (int exitCode, string output) = ReviewCli.Run("validate", "card Zap\n  cost 1\n  target enemy\n  effect:\n    aply Burn 2 to target\n\nstatus Burn\n  stacking intensity\n");

            Assert.True(exitCode == 1, $"exit code {exitCode}:\n{output}");
            Assert.Contains("Unknown verb `aply`", output);
            Assert.Contains("Did you mean `apply`?", output);
        }
    }
}
