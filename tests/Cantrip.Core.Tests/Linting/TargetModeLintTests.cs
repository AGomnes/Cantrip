#nullable enable
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
    /// CT340: a <c>target</c> line naming a word that is not one of the five modes.
    /// </summary>
    /// <remarks>
    /// The set is closed and the engine falls through to "nobody" outside it, so before this check
    /// a one-letter typo made an action that asked for no target and validated none: it dealt its
    /// damage to whoever the caller handed it, the party's own leader included, while
    /// <c>lint</c> exited 0 and a <c>test</c> that named its target passed.
    /// </remarks>
    public sealed class TargetModeLintTests
    {
        [Theory]
        [InlineData("freind")]
        [InlineData("enemies")]
        [InlineData("foe")]
        [InlineData("everyone")]
        public void A_target_line_naming_no_mode_is_an_error(string word)
        {
            Diagnostic error = Assert.Single(Lint(
                "card \"Jab\"\n" +
                "  cost 1\n" +
                $"  target {word}\n" +
                "  effect:\n" +
                "    deal 5 to target\n"),
                d => d.Code == "CT340");

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains($"`target {word}`", error.Message);
            Assert.Contains("`enemy`, `ally`, `self`, `any` or `none`", error.Message);
        }

        [Theory]
        [InlineData("enemy")]
        [InlineData("ally")]
        [InlineData("self")]
        [InlineData("any")]
        [InlineData("none")]
        public void Each_of_the_five_modes_is_accepted(string mode)
        {
            Assert.DoesNotContain(Lint(
                "card \"Jab\"\n" +
                "  cost 1\n" +
                $"  target {mode}\n" +
                "  effect:\n" +
                "    deal 5 to target\n"),
                d => d.Code == "CT340");
        }

        /// <summary><c>target enemy where ...</c> parses as the word wrapped in a filter.</summary>
        [Fact]
        public void A_filter_does_not_hide_the_mode_from_the_check()
        {
            Assert.DoesNotContain(Lint(
                "card \"Jab\"\n" +
                "  cost 1\n" +
                "  target enemy where it.hp <= 5\n" +
                "  effect:\n" +
                "    deal 5 to target\n"),
                d => d.Code == "CT340");

            Assert.Contains(Lint(
                "card \"Jab\"\n" +
                "  cost 1\n" +
                "  target freind where it.hp <= 5\n" +
                "  effect:\n" +
                "    deal 5 to target\n"),
                d => d.Code == "CT340");
        }

        /// <summary>
        /// The behaviour the check exists for, pinned so that nobody decides later that a warning
        /// would have done: with no <c>target</c> mode the engine is asked to point at nobody and
        /// checks nothing, so a card written to hit an enemy hits the party's leader instead.
        /// </summary>
        [Fact]
        [Trait("Regression", "an-unknown-target-mode-points-anywhere")]
        public void An_unknown_mode_would_let_an_attack_land_on_the_leader()
        {
            const string Content =
                "card \"Jab\"\n" +
                "  cost 1\n" +
                "  target freind\n" +
                "  effect:\n" +
                "    deal 5 to target\n" +
                "\nenemy \"Dummy\"\n  hp 20\n";

            CardRuntime runtime = CardRuntime.FromText(Content);
            Entity player = runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(ActionResult.Played, runtime.Play(runtime.AddCard("Jab", Zones.Hand), player));
            Assert.Equal(75, player.GetInt("hp"));

            // What the same card does when its mode is spelled: it refuses.
            CardRuntime spelled = CardRuntime.FromText(Content.Replace("target freind", "target enemy"));
            Entity leader = spelled.CreatePlayer();
            spelled.SpawnEnemy("Dummy");
            spelled.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(ActionResult.InvalidTarget, spelled.Play(spelled.AddCard("Jab", Zones.Hand), leader));
            Assert.Equal(80, leader.GetInt("hp"));
        }

        private static IReadOnlyList<Diagnostic> Lint(string dsl) =>
            Linter.Lint(ContentLibrary.FromText(dsl, "targets.cantrip"));
    }
}
