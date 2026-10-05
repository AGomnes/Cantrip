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
    /// CT324: a bare percentage where a built-in verb counts whole things.
    /// </summary>
    /// <remarks>
    /// <c>apply Slow 40%</c> dropped the unit and applied forty stacks, while the card's generated
    /// text said "Apply 40% Slow": text a player reads, stating a percentage nothing in the engine
    /// implements. A percentage is a fraction to multiply by; there is no percentage of a stack.
    /// </remarks>
    public sealed class PercentageLintTests
    {
        private const string Base = """
            status "Slow"
              tags debuff
              stacking intensity

            """;

        [Fact]
        [Trait("Regression", "percentage-where-a-count-is-meant")]
        public void A_percentage_of_stacks_is_an_error_that_shows_what_it_really_did()
        {
            Diagnostic error = Single(Lint("""
                card "Chill"
                  cost 1
                  target enemy
                  effect:
                    apply Slow 40% to target
                """));

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("`40%` is a percentage", error.Message);
            Assert.Contains("It read as 40", error.Message);
            Assert.Contains("target.max_hp * 40%", error.Message);
        }

        [Fact]
        [Trait("Regression", "percentage-where-a-count-is-meant")]
        public void A_percentage_of_damage_is_an_error_too()
        {
            Diagnostic error = Single(Lint("""
                card "Rend"
                  cost 1
                  target enemy
                  effect:
                    deal 25% to target
                """));

            Assert.Contains("`25%` is a percentage, and `deal` counts in whole things", error.Message);
        }

        /// <summary>A percentage inside a sum is a fraction by the time the verb sees it.</summary>
        [Fact]
        public void A_percentage_that_multiplies_something_is_what_a_percentage_is_for()
        {
            None(Lint("""
                card "Execute"
                  cost 1
                  target enemy
                  effect:
                    deal target.max_hp * 40% to target
                    apply Slow 2 to target
                """));
        }

        /// <summary>A percentage in a modifier or a chance is untouched: both read the unit.</summary>
        [Fact]
        public void Modifiers_and_chances_still_take_percentages()
        {
            None(Lint("""
                status "Feeble"
                  tags debuff
                  stacking intensity
                  modify damage: x75%

                card "Gamble"
                  cost 1
                  target enemy
                  effect:
                    40% chance:
                      deal 10 to target
                """));
        }

        /// <summary>The runtime refuses it as well, which is what catches it through a content verb.</summary>
        [Fact]
        [Trait("Regression", "percentage-where-a-count-is-meant")]
        public void The_runtime_refuses_it_as_well()
        {
            ContentLibrary content = ContentLibrary.FromText(Base + """
                verb chill(who):
                  apply Slow 40% to who

                card "Chill"
                  cost 0
                  target enemy
                  effect:
                    chill target

                test "a percentage of stacks is refused at run"
                  enemy hp 50
                  hand Chill
                  player energy 9
                  play Chill on enemy
                """, "percent.cantrip");

            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            DslTestResult result = new DslTestRunner(content).RunAll().Last();
            Assert.False(result.Passed, "expected the percentage to be refused");
            Assert.Contains("`40%` is a percentage", result.Failure!);
        }

        // Helpers ----------------------------------------------------------------------------

        private static IReadOnlyList<Diagnostic> Lint(string dsl) =>
            Linter.Lint(ContentLibrary.FromText(Base + dsl, "percent.cantrip"));

        private static Diagnostic Single(IReadOnlyList<Diagnostic> diagnostics)
        {
            var matching = diagnostics.Where(d => d.Code == Linter.PercentageWhereACountIsMeant).ToList();
            Assert.True(matching.Count == 1, $"expected one CT324, got:\n{string.Join("\n", diagnostics)}");
            return matching[0];
        }

        private static void None(IReadOnlyList<Diagnostic> diagnostics) =>
            Assert.True(
                diagnostics.All(d => d.Code != Linter.PercentageWhereACountIsMeant),
                $"expected no CT324, got:\n{string.Join("\n", diagnostics)}");
    }
}
