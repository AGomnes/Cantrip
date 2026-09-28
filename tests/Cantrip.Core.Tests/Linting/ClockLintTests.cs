using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// CT325 and the <c>clock</c> ruleset setting: content says which clock it is written for, and
    /// a length in the other clock's units stops being a surprise.
    /// </summary>
    /// <remarks>
    /// The two halves used to fail differently and neither said so. <c>on every 1s:</c> in a turn
    /// game registered nothing at all, in silence — the listener simply never ran — while
    /// <c>apply Weak 1 for 3s</c> in the same game threw the day that line was reached.
    /// </remarks>
    public sealed class ClockLintTests
    {
        private const string TurnGame = """
            ruleset
              clock turns

            status "Weak"
              tags debuff
              stacking duration

            """;

        private const string TickGame = """
            ruleset
              clock ticks

            status "Weak"
              tags debuff
              stacking duration

            """;

        [Fact]
        [Trait("Regression", "every-registers-nothing-on-a-turn-clock")]
        public void An_interval_in_seconds_is_an_error_in_a_turn_game()
        {
            Diagnostic error = Single(Lint(TurnGame + """
                relic "Ticker"
                  counter 0
                  on every 1s:
                    counter +1
                """));

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("`on every ...:` interval is written in `s`", error.Message);
            Assert.Contains("This game says `clock turns`", error.Message);
        }

        [Fact]
        [Trait("Regression", "every-registers-nothing-on-a-turn-clock")]
        public void A_duration_in_seconds_is_an_error_in_a_turn_game()
        {
            Diagnostic error = Single(Lint(TurnGame + """
                card "Chill"
                  cost 1
                  target enemy
                  effect:
                    apply Weak 1 for 3s to target
                """));

            Assert.Contains("The `for` on this `apply` is written in `s`", error.Message);
        }

        [Fact]
        public void A_delay_and_a_cooldown_are_checked_too()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint(TurnGame + """
                ability "Zap"
                  cooldown 8s
                  target enemy
                  effect:
                    in 2s:
                      deal 1 to target
                """);

            var found = diagnostics.Where(d => d.Code == Linter.WrongClock).ToList();
            Assert.Equal(2, found.Count);
            Assert.Contains(found, d => d.Message.Contains("The cooldown of `Zap`"));
            Assert.Contains(found, d => d.Message.Contains("This delay"));
        }

        /// <summary>The other way round: turns mean nothing to a tick clock.</summary>
        [Fact]
        public void Turns_are_an_error_in_a_tick_game()
        {
            Diagnostic error = Single(Lint(TickGame + """
                ability "Zap"
                  cooldown 2 turns
                  target enemy
                  effect:
                    deal 7 to target
                """));

            Assert.Contains("written in `turns`", error.Message);
            Assert.Contains("This game says `clock ticks`", error.Message);
        }

        [Fact]
        public void Units_the_stated_clock_measures_are_never_reported()
        {
            None(Lint(TurnGame + """
                ability "Zap"
                  cooldown 2 turns
                  target enemy
                  effect:
                    apply Weak 1 for 2 turns to target
                    in 1 turns:
                      deal 1 to target

                relic "Ticker"
                  counter 0
                  on every 2 turns:
                    counter +1
                """));

            None(Lint(TickGame + """
                ability "Zap"
                  cooldown 8s
                  target enemy
                  effect:
                    apply Weak 1 for 250ms to target

                relic "Ticker"
                  counter 0
                  on every 1s:
                    counter +1
                """));
        }

        /// <summary>
        /// Content that says nothing is exactly as it was. Every game written before the setting
        /// existed is in this case, and none of them may start failing.
        /// </summary>
        [Fact]
        public void Content_that_states_no_clock_is_not_checked()
        {
            None(Lint("""
                status "Weak"
                  tags debuff
                  stacking duration

                relic "Ticker"
                  counter 0
                  on every 1s:
                    counter +1

                ability "Zap"
                  cooldown 2 turns
                  target enemy
                  effect:
                    apply Weak 1 for 3s to target
                """));
        }

        // The runtime ------------------------------------------------------------------------

        [Fact]
        public void A_runtime_with_no_clock_of_its_own_starts_the_one_content_asked_for()
        {
            Assert.IsType<TickClock>(CardRuntime.FromText(TickGame).State.Clock);
            Assert.IsType<TurnClock>(CardRuntime.FromText(TurnGame).State.Clock);

            // Unstated is still a turn clock, which is what every game before the setting gets.
            Assert.IsType<TurnClock>(CardRuntime.FromText("status \"Weak\"\n  stacking duration\n").State.Clock);
        }

        [Fact]
        [Trait("Regression", "every-registers-nothing-on-a-turn-clock")]
        public void A_runtime_given_the_other_clock_says_so_as_it_is_built()
        {
            InvalidOperationException wrongWay = Assert.Throws<InvalidOperationException>(
                () => CardRuntime.FromText(TurnGame, new RuntimeOptions { Clock = new TickClock(60) }));
            Assert.Contains("says `clock turns`", wrongWay.Message);

            InvalidOperationException otherWay = Assert.Throws<InvalidOperationException>(
                () => CardRuntime.FromText(TickGame, new RuntimeOptions { Clock = new TurnClock() }));
            Assert.Contains("says `clock ticks`", otherWay.Message);
        }

        [Fact]
        public void An_unknown_clock_value_is_reported_rather_than_guessed()
        {
            ContentLibrary content = ContentLibrary.FromText("ruleset\n  clock minutes\n", "clock.cantrip");
            var diagnostics = new DiagnosticBag();
            content.BuildRuleset(diagnostics);

            Diagnostic error = Assert.Single(diagnostics.Errors);
            Assert.Equal("CT0202", error.Code);
            Assert.Contains("Write `clock turns` or `clock ticks`", error.Message);
        }

        // Helpers ----------------------------------------------------------------------------

        private static IReadOnlyList<Diagnostic> Lint(string dsl) =>
            Linter.Lint(ContentLibrary.FromText(dsl, "clock.cantrip"));

        private static Diagnostic Single(IReadOnlyList<Diagnostic> diagnostics)
        {
            var matching = diagnostics.Where(d => d.Code == Linter.WrongClock).ToList();
            Assert.True(matching.Count == 1, $"expected one CT325, got:\n{string.Join("\n", diagnostics)}");
            return matching[0];
        }

        private static void None(IReadOnlyList<Diagnostic> diagnostics) =>
            Assert.True(
                diagnostics.All(d => d.Code != Linter.WrongClock),
                $"expected no CT325, got:\n{string.Join("\n", diagnostics)}");
    }
}
