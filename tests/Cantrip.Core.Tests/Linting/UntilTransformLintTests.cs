using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// CT321: a <c>transform</c> inside an <c>until</c> block. <c>until</c> puts back what it did, and
    /// a transform cannot be put back — the stats, the statuses and the spent <c>once per ...</c>
    /// windows are gone.
    /// </summary>
    public sealed class UntilTransformLintTests
    {
        private const string Base = """
            enemy "Ogre"
              hp 30
              move "Smash":
                deal 9 to player

            enemy "Sheepling"
              hp 1
              move "Baa":
                deal 1 to player

            """;

        [Fact]
        public void A_transform_written_inside_until_is_an_error()
        {
            Diagnostic error = Single(Lint("""
                card "Temporary"
                  cost 0
                  target enemy
                  effect:
                    until turn_end:
                      transform target into Sheepling
                """));

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("cannot be undone, so an `until` block cannot hold one", error.Message);
            Assert.Contains("Transform it outside the block, or apply a status instead.", error.Message);
        }

        /// <summary>The walker descends the blocks that run inside the <c>until</c>, at any depth.</summary>
        [Theory]
        [InlineData("    if target.hp > 1:\n      transform target into Sheepling")]
        [InlineData("    repeat 2:\n      transform target into Sheepling")]
        [InlineData("    for e in enemies:\n      transform e into Sheepling")]
        [InlineData("    chance 50%:\n      transform target into Sheepling")]
        [InlineData("    label:\n      transform target into Sheepling")]
        public void It_reaches_through_every_block_that_runs_inside_the_until(string body)
        {
            Single(Lint("card \"Temporary\"\n  cost 0\n  target enemy\n  effect:\n    until turn_end:\n" + body.Replace("    ", "      ")));
        }

        /// <summary>
        /// A <c>next turn:</c> or <c>in N turns:</c> block runs later, on its own, with none of the
        /// <c>until</c>'s undo scope — so it is not inside the block and is not reported.
        /// </summary>
        [Theory]
        [InlineData("next turn:")]
        [InlineData("in 2 turns:")]
        public void A_block_that_runs_later_starts_clear(string schedule)
        {
            None(Lint($"""
                card "Later"
                  cost 0
                  target enemy
                  effect:
                    until turn_end:
                      {schedule}
                        transform target into Sheepling
                """));
        }

        [Fact]
        public void A_transform_outside_any_until_is_fine()
        {
            None(Lint("""
                card "Polymorph"
                  cost 0
                  target enemy
                  effect:
                    transform target into Sheepling

                card "Mixed"
                  cost 0
                  target enemy
                  effect:
                    until turn_end:
                      target.hp -= 1
                    transform target into Sheepling
                """));
        }

        /// <summary>A content verb's body is linted too, so the same line in one is reported there.</summary>
        [Fact]
        public void A_content_verb_holding_one_inside_until_is_reported_in_the_verb()
        {
            Diagnostic error = Single(Lint("""
                verb sheepify(who):
                  until turn_end:
                    transform who into Sheepling

                card "Caller"
                  cost 0
                  target enemy
                  effect:
                    sheepify target
                """));

            Assert.Contains("cannot be undone", error.Message);
        }

        // Helpers ----------------------------------------------------------------------------

        private static IReadOnlyList<Diagnostic> Lint(string dsl) =>
            Linter.Lint(ContentLibrary.FromText(Base + dsl, "lint.cantrip"));

        private static Diagnostic Single(IReadOnlyList<Diagnostic> diagnostics)
        {
            var matching = diagnostics.Where(d => d.Code == Linter.TransformInsideUntil).ToList();
            Assert.True(matching.Count == 1, $"expected one CT321, got:\n{string.Join("\n", diagnostics)}");
            return matching[0];
        }

        private static void None(IReadOnlyList<Diagnostic> diagnostics) =>
            Assert.True(
                diagnostics.All(d => d.Code != Linter.TransformInsideUntil),
                $"expected no CT321, got:\n{string.Join("\n", diagnostics)}");
    }
}
