using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// The linter's view of `into`, which binds a name and therefore has to be recorded as one.
    /// </summary>
    /// <remarks>
    /// Nothing in this codebase matches node types or clauses exhaustively, so a new binding form
    /// that the linter does not know about makes every use of the name it binds warn CT302. That is
    /// what happened when `into` landed, and these pin both sides of the fix: the name is known when
    /// a verb honours the clause, and still unknown when one does not.
    /// </remarks>
    public sealed class IntoLintTests
    {
        private static IReadOnlyList<Diagnostic> Lint(string dsl) =>
            Linter.Lint(ContentLibrary.FromText(dsl, "into-lint.cantrip"), null);

        private static void None(IReadOnlyList<Diagnostic> diagnostics, string code) =>
            Assert.True(diagnostics.All(d => d.Code != code), $"expected no {code}, got:\n{string.Join("\n", diagnostics)}");

        [Fact]
        public void A_name_bound_by_into_is_known_to_the_linter()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                card "Reap"
                  cost 2
                  effect:
                    deal 4 to all enemies into dealt
                    heal dealt
                """);

            None(diagnostics, "CT302");
        }

        [Fact]
        public void An_attack_binding_is_known_too()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                enemy "Biter"
                  hp 10
                  attack 3
                  move "Bite":
                    attack player into bit
                    gain bit gold to player
                """);

            None(diagnostics, "CT302");
        }

        [Fact]
        public void A_name_written_on_a_verb_that_ignores_into_is_still_unknown()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                card "Confused"
                  cost 1
                  effect:
                    draw 1 into drawn
                    heal drawn
                """);

            // `draw` does not honour the clause, so nothing is bound and the use is a real mistake.
            // Recording every `into` name regardless would have traded a false positive for a
            // false negative.
            Assert.Contains(diagnostics, d => d.Code == "CT302");
        }
    }
}
