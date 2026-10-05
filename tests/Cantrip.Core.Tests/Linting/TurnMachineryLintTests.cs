using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// CT337, CT338 and CT339: a stated <c>clock</c> decides which declarations are live, not only
    /// which units are legal.
    /// </summary>
    /// <remarks>
    /// CT325 already knew the stated clock and checked lengths against it, so <c>2 turns</c> in a
    /// <c>clock ticks</c> game was an error, while a <c>move</c>, a <c>pattern</c>, a
    /// <c>phase</c>, a <c>stacking duration</c> and a <c>next turn:</c> in the same file linted
    /// with zero errors, zero warnings and zero notes and then did nothing for ever. The language
    /// checked the unit an author wrote and not the machinery they used, so
    /// <c>apply Chill 3</c> on a tick clock was a permanent Chill and a designer would chase the
    /// balance bug instead of reading an error.
    /// </remarks>
    public sealed class TurnMachineryLintTests
    {
        private const string Ticks = "ruleset\n  clock ticks\n\n";
        private const string Turns = "ruleset\n  clock turns\n\n";

        public static IEnumerable<object[]> DeadDeclarations => new[]
        {
            new object[] { "enemy \"Beast\"\n  hp 10\n  move \"Bite\":\n    deal 5 to enemies\n", "A `move` is what an enemy does" },
            new object[] { "enemy \"Beast\"\n  hp 10\n  move \"Bite\":\n    deal 5 to enemies\n  pattern cycle Bite\n", "A `pattern` chooses the next move" },
            new object[] { "enemy \"Beast\"\n  hp 10\n  phase Hurt when hp <= 5\n", "A `phase` gates the moves" },
            new object[] { "status \"Chill\"\n  stacking duration\n", "`stacking duration` counts down" },
            new object[] { "status \"Chill\"\n  stacking refresh\n", "`stacking refresh` counts down" },
            new object[] { "status \"Chill\"\n  stacking intensity\n  decay 1 on turn_end\n", "`decay ... on turn_end`" },
            new object[] { "status \"Chill\"\n  stacking intensity\n  decay 1\n", "`decay ... on turn_end`" },
            new object[] { "resource focus\n  max 3\n  reset_on turn_start\n", "`reset_on turn_start` refills" },
            new object[] { "relic \"Clock\"\n  on turn_start:\n    gain 1 gold\n", "`turn_start` is raised when a turn" },
            new object[] { "relic \"Clock\"\n  on damaged once per turn:\n    gain 1 gold\n", "`once per turn` opens again" },
            new object[] { "card \"Brace\"\n  cost 1\n  effect:\n    until turn_end:\n      block 5\n", "`until turn_end:` undoes" },
            new object[] { "card \"Brace\"\n  cost 1\n  effect:\n    next turn:\n      draw 1\n", "`next turn:` runs its body" },
        };

        [Theory]
        [MemberData(nameof(DeadDeclarations))]
        [Trait("Regression", "turn-machinery-lints-clean-under-clock-ticks")]
        public void Turn_machinery_under_clock_ticks_is_an_error(string declaration, string says)
        {
            Diagnostic error = Single(Lint(Ticks + declaration), says);

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains(says, error.Message);
            Assert.Contains("This game says `clock ticks`", error.Message);
            Assert.Contains("Or say `clock turns`", error.Message);
        }

        [Theory]
        [MemberData(nameof(DeadDeclarations))]
        public void And_is_left_entirely_alone_in_a_turn_game(string declaration, string says)
        {
            _ = says;
            None(Lint(Turns + declaration));
        }

        /// <summary>
        /// Content that states no clock is unchanged, which is every game written before the
        /// setting existed.
        /// </summary>
        [Theory]
        [MemberData(nameof(DeadDeclarations))]
        public void And_in_content_that_says_nothing_about_its_clock(string declaration, string says)
        {
            _ = says;
            None(Lint(declaration));
        }

        /// <summary>
        /// The real-time shapes of the same ideas are not reported, or the diagnostic would be
        /// telling authors to write something it also refuses.
        /// </summary>
        [Fact]
        public void What_a_real_time_game_writes_instead_is_clean()
        {
            None(Lint(Ticks +
                "status \"Chill\"\n" +
                "  stacking intensity\n" +
                "  on every 1s:\n" +
                "    deal stacks to owner\n\n" +
                "enemy \"Beast\"\n" +
                "  hp 10\n" +
                "  on every 2s:\n" +
                "    deal 5 to enemies\n" +
                "  on every 3s (self.hp <= self.max_hp / 2):\n" +
                "    apply Chill 2 for 4s to enemies\n\n" +
                "relic \"Clock\"\n" +
                "  on damaged once per battle:\n" +
                "    gain 1 gold\n\n" +
                "card \"Brace\"\n" +
                "  cost 1\n" +
                "  effect:\n" +
                "    in 2s:\n" +
                "      block 5\n"));
        }

        // CT338: a scenario a bot could not play -------------------------------------------------

        [Fact]
        [Trait("Regression", "sim-plays-a-tick-game-with-endturn")]
        public void A_scenario_in_a_tick_game_is_an_error()
        {
            Diagnostic error = Assert.Single(Lint(Ticks +
                "enemy \"Beast\"\n  hp 10\n\n" +
                "scenario \"The hold\"\n" +
                "  runs 100\n" +
                "  battle Beast\n" +
                "  expect no errors\n"),
                d => d.Code == "CT338");

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("cannot be played", error.Message);
            Assert.Contains("`cantrip sim` takes turns", error.Message);
            Assert.Contains("`test` blocks", error.Message);
        }

        [Fact]
        public void And_is_fine_in_a_turn_game()
        {
            Assert.DoesNotContain(Lint(Turns +
                "enemy \"Beast\"\n  hp 10\n\n" +
                "scenario \"The gauntlet\"\n" +
                "  runs 100\n" +
                "  battle Beast\n" +
                "  expect no errors\n"),
                d => d.Code == "CT338");
        }

        // CT339: a price nothing charges ----------------------------------------------------------

        [Fact]
        [Trait("Regression", "cost-on-an-ability-parses-lints-clean-and-does-nothing")]
        public void A_cost_on_an_ability_is_an_error()
        {
            Diagnostic error = Assert.Single(Lint(
                "ability \"Costly\"\n" +
                "  cost 2\n" +
                "  cooldown 1s\n" +
                "  effect:\n" +
                "    deal 3 to enemies\n"),
                d => d.Code == "CT339");

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("`cost` on ability `Costly` is never charged", error.Message);
            Assert.Contains("`cooldown`", error.Message);
        }

        /// <summary>A card's cost is real and is charged, so it is never reported.</summary>
        [Fact]
        public void But_a_card_may_cost_whatever_it_likes()
        {
            Assert.DoesNotContain(Lint(
                "card \"Zap\"\n" +
                "  cost 2\n" +
                "  effect:\n" +
                "    deal 3 to enemies\n"),
                d => d.Code == "CT339");
        }

        // Helpers -------------------------------------------------------------------------------

        private static IReadOnlyList<Diagnostic> Lint(string dsl) =>
            Linter.Lint(ContentLibrary.FromText(dsl, "machinery.cantrip"));

        private static Diagnostic Single(IReadOnlyList<Diagnostic> diagnostics, string says)
        {
            var matching = diagnostics.Where(d => d.Code == "CT337" && d.Message.Contains(says)).ToList();
            Assert.True(matching.Count == 1, $"expected one CT337 saying \"{says}\", got:\n{string.Join("\n", diagnostics)}");
            return matching[0];
        }

        private static void None(IReadOnlyList<Diagnostic> diagnostics) =>
            Assert.True(
                diagnostics.All(d => d.Code != "CT337"),
                $"expected no CT337, got:\n{string.Join("\n", diagnostics)}");
    }
}
