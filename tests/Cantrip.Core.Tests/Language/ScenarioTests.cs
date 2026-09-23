using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Syntax;
using Cantrip.Testing;
using Cantrip.Tests.Review;
using Xunit;

namespace Cantrip.Tests.Language
{
    /// <summary>
    /// The <c>scenario</c> declaration: a gauntlet stated once and played many times. It parses,
    /// loads and walks exactly as a <c>test</c> does, and a folder that has one runs its tests and
    /// describes its definitions as if it did not.
    /// </summary>
    public sealed class ScenarioTests
    {
        private const string Content = """
            card Zap
              cost 1
              target enemy
              effect:
                deal 6 to target

            enemy Archmage
              hp 30
              move Bolt:
                deal 5 to player

            """;

        private const string Tower = """
            scenario "The tower, starter deck"
              runs 500

              player hp 60 energy 3
              deck 4 Zap

              battle Archmage
              heal 12
              battle Archmage

              expect no stalls

            """;

        private static ContentLibrary Load(string dsl, string file = "scenario.cantrip")
        {
            ContentLibrary content = ContentLibrary.FromText(dsl, file);
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            return content;
        }

        [Fact]
        public void A_scenario_parses_as_a_declaration_with_a_statement_body()
        {
            SourceFileNode file = Parser.Parse(Content + Tower, "scenario.cantrip", new Diagnostics.DiagnosticBag());

            ScenarioDeclNode scenario = Assert.Single(file.Declarations.OfType<ScenarioDeclNode>());
            Assert.Equal("The tower, starter deck", scenario.Name);
            Assert.Equal(
                new[] { "runs", "player", "deck", "battle", "heal", "battle", "expect" },
                scenario.Body.Statements.Cast<CommandNode>().Select(c => c.Verb));
        }

        /// <summary>
        /// A scenario's body is a plain block of statements, so the walker the linter and every
        /// other tool is built on reaches into it without knowing what a scenario is.
        /// </summary>
        [Fact]
        public void A_scenario_body_is_walked_like_any_other_block()
        {
            ContentLibrary content = Load(Content + """
                scenario "Branching"
                  battle Archmage
                  if player.hp > 20:
                    heal 5
                  else:
                    heal 12
                """);

            var walk = new Verbs();
            walk.VisitBlock(content.Scenarios.Single().Syntax.Body);

            Assert.Equal(new[] { "battle", "heal", "heal" }, walk.Found);
        }

        private sealed class Verbs : AstWalker
        {
            public List<string> Found { get; } = new List<string>();

            public override void VisitStatement(StatementNode statement)
            {
                if (statement is CommandNode command) Found.Add(command.Verb);
                base.VisitStatement(statement);
            }
        }

        [Fact]
        public void The_library_counts_scenarios_beside_tests()
        {
            ContentLibrary content = Load(Content + Tower + """
                scenario "One fight"
                  battle Archmage

                test "Zap hurts"
                  enemy Archmage
                  play Zap on enemy
                  expect enemy.hp == 24

                """);

            Assert.Equal(new[] { "The tower, starter deck", "One fight" }, content.Scenarios.Select(s => s.Name));
            Assert.Equal("Zap hurts", Assert.Single(content.Tests).Name);
            Assert.All(content.Scenarios, scenario => Assert.Equal("scenario.cantrip", scenario.File));
        }

        [Fact]
        public void Reloading_a_file_replaces_the_scenarios_it_contributed()
        {
            var content = new ContentLibrary();
            content.LoadText(Content + Tower, "tower.cantrip");
            Assert.Single(content.Scenarios);

            content.LoadText(Content, "tower.cantrip");
            Assert.Empty(content.Scenarios);
        }

        [Fact]
        public void A_misspelt_declaration_keyword_suggests_scenario()
        {
            ContentLibrary content = ContentLibrary.FromText("scenerio \"Typo\"\n  battle Archmage\n", "typo.cantrip");

            Diagnostics.Diagnostic error = content.Diagnostics.First(d => d.Code == "CT0012");
            Assert.Equal("scenario", error.Suggestion);
        }

        [Fact]
        public void A_scenario_in_the_file_leaves_the_tests_alone()
        {
            ContentLibrary content = Load(Content + Tower + """
                test "Zap hurts"
                  enemy Archmage
                  play Zap on enemy
                  expect enemy.hp == 24

                """);

            DslTestResult result = Assert.Single(new DslTestRunner(content).RunAll());
            Assert.True(result.Passed, result.Failure);
            Assert.Equal("Zap hurts", result.Test.Name);
        }

        /// <summary>
        /// <c>validate</c> counts scenarios the way it counts tests, and neither <c>test</c> nor
        /// <c>describe</c> notices them.
        /// </summary>
        [Fact]
        public void Validate_counts_a_scenario_that_test_and_describe_ignore()
        {
            const string dsl = Content + Tower + """
                test "Zap hurts"
                  enemy Archmage
                  play Zap on enemy
                  expect enemy.hp == 24

                """;

            (int validate, string counts) = ReviewCli.Run("validate", dsl);
            Assert.True(validate == 0, $"exit code {validate}:\n{ReviewCli.Head(counts)}");
            Assert.Contains("1 test(s), 1 scenario(s): 0 error(s)", counts);

            (int test, string tests) = ReviewCli.Run("test", dsl);
            Assert.True(test == 0, $"exit code {test}:\n{ReviewCli.Head(tests)}");
            Assert.Contains("1 passed, 0 failed", tests);

            (int describe, string described) = ReviewCli.Run("describe", dsl);
            Assert.True(describe == 0, $"exit code {describe}:\n{ReviewCli.Head(described)}");
            Assert.Contains("Zap [card]", described);
            Assert.DoesNotContain("tower", described);
        }
    }
}
