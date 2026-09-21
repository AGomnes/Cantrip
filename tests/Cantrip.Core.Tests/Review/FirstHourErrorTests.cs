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
        [Trait("Regression", "first-hour-stray-indent")]
        public void A_stray_indent_in_a_test_body_is_one_error_and_the_test_goes_on()
        {
            ContentLibrary content = ContentLibrary.FromText(
                "card Zap\n  cost 1\n  effect:\n    draw 1\n\ntest \"zap\"\n  player energy 3\n    hand Zap\n  play Zap\n  expect player.energy == 2\n");

            Diagnostic error = Assert.Single(content.Diagnostics.Errors);
            Assert.Equal("CT0030", error.Code);
            // All four lines survive, the stray one read as if it were not indented.
            Assert.Equal(4, Assert.Single(content.Tests).Syntax.Body.Statements.Count);
        }

        [Fact]
        [Trait("Regression", "first-hour-stray-indent")]
        public void A_property_indented_under_another_is_one_error_and_the_card_keeps_the_rest()
        {
            ContentLibrary content = ContentLibrary.FromText(
                "card Zap\n  cost 1\n    target enemy\n  effect:\n    deal 6 to target\n");

            Diagnostic error = Assert.Single(content.Diagnostics.Errors);
            Assert.Equal("CT0030", error.Code);
            EntityDefinition zap = content.Find("Zap", "card")!;
            Assert.Equal("enemy", zap.Word("target"));
            Assert.NotNull(zap.Effect);
        }

        [Fact]
        public void A_dedent_that_lines_up_with_no_block_is_reported_once_by_the_lexer()
        {
            ContentLibrary content = ContentLibrary.FromText(
                "card Zap\n  cost 1\n  target enemy\n  effect:\n    if target.hp > 0:\n      deal 1 to target\n     deal 2 to target\n");

            Diagnostic error = Assert.Single(content.Diagnostics.Errors);
            Assert.Equal("CT0001", error.Code);
        }

        [Fact]
        public void A_verb_name_with_spaces_is_told_to_use_one_word_not_quotes()
        {
            ContentLibrary content = ContentLibrary.FromText("verb my verb(t):\n  deal 1 to t\n");

            Diagnostic error = Assert.Single(content.Diagnostics.Errors);
            Assert.Equal("CT0029", error.Code);
            Assert.Contains("verb my_verb", error.Message);
            Assert.NotNull(content.FindVerb("my_verb"));
        }

        [Fact]
        public void A_statement_with_no_verb_is_reported_by_the_parser_alone()
        {
            ContentLibrary content = ContentLibrary.FromText("card Zap\n  cost 1\n  target enemy\n  effect:\n    6 damage to target\n");

            Assert.Contains(content.Diagnostics.Errors, d => d.Code == "CT0010");
            Assert.DoesNotContain(Cantrip.Linting.Linter.Lint(content), d => d.Code == "CT301");
        }

        [Theory]
        [InlineData("enemy hp 20 block", "`block` needs a value.")]
        [InlineData("enemy hp 20 Weak", "`Weak` needs a value.")]
        [InlineData("enemy Weak", "`Weak` needs a value.")]
        public void A_missing_stat_value_in_a_test_is_not_mistaken_for_an_undefined_enemy(string line, string message)
        {
            ContentLibrary content = ContentLibrary.FromText(
                "status Weak\n  stacking intensity\n\ncard Zap\n  cost 1\n  target enemy\n  effect:\n    deal 6 to target\n\n" +
                "test \"zap\"\n  " + line + "\n  play Zap on enemy\n");

            DslTestResult result = Assert.Single(new DslTestRunner(content).RunAll());

            Assert.False(result.Passed);
            Assert.Contains(message, result.Failure);
        }

        [Fact]
        public void Validate_lets_a_game_suppress_the_verbs_it_registers_in_csharp()
        {
            const string dsl = "card Zap\n  cost 1\n  target enemy\n  effect:\n    corrupt 2 to target\n";

            (int plain, string _) = ReviewCli.Run("validate", dsl);
            (int suppressed, string output) = ReviewCli.Run("validate", dsl, "--suppress", "CT301");

            Assert.Equal(1, plain);
            Assert.True(suppressed == 0, output);
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
