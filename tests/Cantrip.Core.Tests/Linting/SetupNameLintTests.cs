using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// A test line that names a definition, such as <c>hand Strik</c>, used to lint as a warning
    /// (CT302) and exit 0, and the test then failed with the runtime's ArgumentException message,
    /// "(Parameter 'name')" and all. Such a name is now a lint error, and the test fails with a
    /// message that names the missing definition and the nearest one.
    /// </summary>
    public sealed class SetupNameLintTests
    {
        private const string Content = """
            card Strike
              cost 1
              target enemy
              effect:
                deal 6 to target

            relic Anchor
              on turn_start once per battle:
                block 10

            item Lantern
              on turn_start:
                block 1

            ability Zap
              cooldown 2 turns
              effect:
                deal 1 to all enemies

            enemy Ghoul
              hp 20
              move Bite:
                deal 3 to player

            status Poison
              stacking intensity

            """;

        private static ContentLibrary Load(string testBody)
        {
            ContentLibrary content = ContentLibrary.FromText(Content + "test \"probe\"\n" + testBody, "setup.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            return content;
        }

        private static List<Diagnostic> Names(ContentLibrary content) =>
            Linter.Lint(content).Where(d => d.Code == Linter.UnknownName).ToList();

        [Theory]
        [Trait("Regression", "ct302-warning-on-test-lines")]
        [InlineData("  hand Strik\n", "Strik", "Strike", "No card named `Strik` is defined. Did you mean `Strike`?")]
        [InlineData("  deck Strik\n", "Strik", "Strike", "No card named `Strik` is defined. Did you mean `Strike`?")]
        [InlineData("  discard_pile \"Strik\"\n", "Strik", "Strike", "No card named `Strik` is defined. Did you mean `Strike`?")]
        [InlineData("  deck Strike, Strke\n", "strke", "Strike", "No card named `strke` is defined. Did you mean `Strike`?")]
        [InlineData("  relic Anchr\n", "Anchr", "Anchor", "No relic or item named `Anchr` is defined. Did you mean `Anchor`?")]
        [InlineData("  grant Zapp\n", "Zapp", "Zap", "No ability named `Zapp` is defined. Did you mean `Zap`?")]
        [InlineData("  enemy hp 10\n  play Strik on enemy\n", "Strik", "Strike", "No card named `Strik` is defined. Did you mean `Strike`?")]
        [InlineData("  enemy Ghol hp 12\n", "Ghol", "Ghoul", "No enemy named `Ghol` is defined. Did you mean `Ghoul`?")]
        public void A_misspelt_definition_on_a_test_line_is_an_error_and_fails_the_test_plainly(string lines, string written, string nearest, string failure)
        {
            ContentLibrary content = Load(lines);

            Diagnostic error = Assert.Single(Names(content));
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains($"`{written}`", error.Message);
            Assert.Equal(nearest, error.Suggestion);

            DslTestResult result = new DslTestRunner(content).RunAll().Single();
            Assert.False(result.Passed);
            Assert.StartsWith(failure, result.Failure);
            Assert.DoesNotContain("Parameter", result.Failure);
        }

        [Fact]
        public void A_name_listed_several_times_is_one_error()
        {
            ContentLibrary content = Load("  deck Strik Strik, Strik, \"Strik\"\n");

            Diagnostic error = Assert.Single(Names(content));
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        }

        [Fact]
        public void Cast_names_an_ability_too()
        {
            ContentLibrary content = Load("  grant Zap\n  cast Zapp\n");

            Diagnostic error = Assert.Single(Names(content));
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Equal("Zap", error.Suggestion);

            // The test used to fail with "the player has no ability `Zapp`; use `grant` first."
            Assert.Equal("No ability named `Zapp` is defined. Did you mean `Zap`?", new DslTestRunner(content).RunAll().Single().Failure);

            // An ability that is defined but not granted still says so.
            Assert.Equal("the player has no ability `Zap`; use `grant` first.", new DslTestRunner(Load("  cast Zap\n")).RunAll().Single().Failure);
        }

        [Fact]
        public void A_definition_of_the_wrong_kind_is_named_for_what_it_is()
        {
            ContentLibrary content = Load("  relic Strike\n");

            Diagnostic error = Assert.Single(Names(content));
            Assert.Equal("`Strike` is a card, but a relic or item is needed here.", error.Message);
            Assert.Equal("`Strike` is a card, not a relic or item.", new DslTestRunner(content).RunAll().Single().Failure);

            Assert.Equal("`Strike` is a card, but an ability is needed here.", Assert.Single(Names(Load("  grant Strike\n"))).Message);
        }

        /// <summary><c>enemy Strike</c> used to fail with "No enemy named `Strike` is defined", although it is, as a card.</summary>
        [Fact]
        public void An_enemy_line_naming_another_kind_says_what_it_is()
        {
            ContentLibrary content = Load("  enemy Strike hp 12\n");

            Assert.Equal("`Strike` is a card, but an enemy is needed here.", Assert.Single(Names(content)).Message);
            Assert.Equal(
                "`Strike` is a card, not an enemy. Write `enemy \"Strike\" hp 20` for a plain enemy with that label.",
                new DslTestRunner(content).RunAll().Single().Failure);
        }

        [Fact]
        public void Every_way_of_naming_a_definition_that_exists_is_clean()
        {
            ContentLibrary content = Load("""
                  enemy Ghoul hp 12 Poison 2
                  enemy "Training Dummy" hp 30
                  enemy hp 5
                  hand Strike, Strike
                  deck Strike Strike
                  discard_pile "Strike"
                  relic Anchor, Lantern
                  grant Zap
                  play Strike on enemy
                  expect enemy.hp == 6
                  cast Zap
                  expect enemy.hp == 5
                """);

            Assert.DoesNotContain(Linter.Lint(content), d => d.Severity != DiagnosticSeverity.Info);
            DslTestResult result = new DslTestRunner(content).RunAll().Single();
            Assert.True(result.Passed, result.ToString());
        }

        /// <summary>The name in <c>enemy Ghol hp 12</c> used to count as a stat everywhere else in the lint run.</summary>
        [Fact]
        public void An_enemy_name_is_not_taken_for_a_stat()
        {
            ContentLibrary content = Load("  enemy Ghol hp 12\n  expect ghol == 0\n");

            // `ghol` read as a stat is unknown: the enemy line did not make it one.
            Assert.Contains(Names(content), d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("`ghol`"));
        }

        /// <summary>
        /// The docs call the entity a status is on its host, but content calls it <c>owner</c>, so
        /// <c>host.Weak</c> is an unknown name; the warning used to suggest <c>cost</c>.
        /// </summary>
        [Fact]
        [Trait("Regression", "host-suggests-cost")]
        public void Host_suggests_owner()
        {
            ContentLibrary content = ContentLibrary.FromText("""
                status Weak
                  stacking duration

                status Cursed
                  on owner.turn_end:
                    host.Weak -1
                """, "host.cantrip");

            Diagnostic warning = Assert.Single(Names(content));
            Assert.Contains("`host`", warning.Message);
            Assert.Equal("owner", warning.Suggestion);
        }
    }
}
