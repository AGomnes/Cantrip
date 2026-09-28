using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Cantrip.Runtime;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// CT322: <c>emit</c> raises a <em>custom</em> event, so a built-in name there is a forgery.
    /// </summary>
    /// <remarks>
    /// <c>emit damaged 99 to player</c> dispatched to every <c>on damaged</c> listener although
    /// nothing was damaged, and the history counters did not move with it: the event was forged and
    /// the record was not, so "whenever you take damage" and "damage taken this turn" disagreed
    /// about the same turn.
    /// </remarks>
    public sealed class EmitLintTests
    {
        [Fact]
        [Trait("Regression", "emit-forges-builtin-events")]
        public void Emitting_a_builtin_event_is_an_error_that_names_the_verb_that_does_it()
        {
            Diagnostic error = Single(Lint("""
                card "Forge"
                  cost 0
                  effect:
                    emit damaged 99 to player
                """));

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("`damaged` is a built-in event, and `emit` raises a custom one", error.Message);
            Assert.Contains("`deal`", error.Message);
            Assert.Contains("my_damaged", error.Message);
        }

        /// <summary>
        /// An event nothing but the engine's own lifecycle raises has no verb to name, so the
        /// message says that instead of an empty list.
        /// </summary>
        [Fact]
        [Trait("Regression", "emit-forges-builtin-events")]
        public void A_lifecycle_event_is_refused_with_the_reason_rather_than_a_verb()
        {
            Diagnostic error = Single(Lint("""
                card "Forge"
                  cost 0
                  effect:
                    emit turn_end to player
                """));

            Assert.Contains("`turn_end` is a built-in event", error.Message);
            Assert.Contains("The engine raises it itself", error.Message);
        }

        /// <summary>A quoted name is the same name.</summary>
        [Fact]
        [Trait("Regression", "emit-forges-builtin-events")]
        public void A_quoted_builtin_name_is_refused_too()
        {
            Diagnostic error = Single(Lint("""
                card "Forge"
                  cost 0
                  effect:
                    emit "card_played"
                """));

            Assert.Contains("`card_played` is a built-in event", error.Message);
        }

        /// <summary>An event of the content's own is what the verb is for, and is untouched.</summary>
        [Fact]
        public void A_custom_event_is_never_reported()
        {
            None(Lint("""
                relic "Ledger"
                  counter 0
                  on hand_scored:
                    counter +1

                card "Score"
                  cost 0
                  effect:
                    emit hand_scored 4
                """));
        }

        /// <summary>A game whose own <c>emit</c> verb runs instead is left alone.</summary>
        [Fact]
        public void A_game_with_its_own_emit_verb_is_left_alone()
        {
            None(Lint("""
                verb emit(who):
                  deal 1 to who

                card "Old Ways"
                  cost 0
                  target enemy
                  effect:
                    emit target
                """));
        }

        /// <summary>The runtime refuses it as well, which is what catches it through a content verb.</summary>
        [Fact]
        [Trait("Regression", "emit-forges-builtin-events")]
        public void The_runtime_refuses_it_as_well()
        {
            ContentLibrary content = ContentLibrary.FromText("""
                verb forge(who):
                  emit damaged 99 to who

                card "Forge"
                  cost 0
                  effect:
                    forge player

                test "a forged event is refused at run"
                  enemy hp 50
                  hand Forge
                  player energy 9
                  play Forge
                """, "emit-lint.cantrip");

            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            DslTestResult result = new DslTestRunner(content).RunAll().Last();
            Assert.False(result.Passed, "expected the forged event to be refused");
            Assert.Contains("`damaged` is a built-in event", result.Failure!);
        }

        /// <summary>Every name the catalogue lists is refused, not just the ones with a test above.</summary>
        [Fact]
        public void Every_builtin_name_is_refused()
        {
            foreach (string name in BuiltinEvents.Names)
            {
                IReadOnlyList<Diagnostic> diagnostics = Lint($"""
                    card "Forge"
                      cost 0
                      effect:
                        emit {name}
                    """);

                Assert.True(
                    diagnostics.Any(d => d.Code == Linter.EmitsBuiltinEvent),
                    $"`emit {name}` was allowed");
            }
        }

        // Helpers ----------------------------------------------------------------------------

        private static IReadOnlyList<Diagnostic> Lint(string dsl) =>
            Linter.Lint(ContentLibrary.FromText(dsl, "emit-lint.cantrip"));

        private static Diagnostic Single(IReadOnlyList<Diagnostic> diagnostics)
        {
            var matching = diagnostics.Where(d => d.Code == Linter.EmitsBuiltinEvent).ToList();
            Assert.True(matching.Count == 1, $"expected one CT322, got:\n{string.Join("\n", diagnostics)}");
            return matching[0];
        }

        private static void None(IReadOnlyList<Diagnostic> diagnostics) =>
            Assert.True(
                diagnostics.All(d => d.Code != Linter.EmitsBuiltinEvent),
                $"expected no CT322, got:\n{string.Join("\n", diagnostics)}");
    }
}
