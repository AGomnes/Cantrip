using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// CT320: <c>copy</c>, <c>transform</c>, <c>play</c> and <c>replay</c> act on something that is
    /// already in the game, so a definition written there is a different verb, or a different word,
    /// waiting to be spelt.
    /// </summary>
    /// <remarks>
    /// All four lines read as if they would make or change something already here. They do not: at
    /// runtime each is an error or, for <c>transform</c>, a statement that does nothing at all, and
    /// <c>replay</c> was worse than either, running the printed effect for free. The message says
    /// what to write rather than guessing at a spelling.
    /// </remarks>
    public sealed class InPlayLintTests
    {
        private const string Base = """
            status "Poison"
              tags debuff
              stacking intensity

            card "Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 6 to target

            card "Fire Bolt"
              cost 1
              target enemy
              tags attack
              effect:
                deal 4 to target

            """;

        [Fact]
        public void Copying_a_definition_is_an_error_that_names_create()
        {
            Diagnostic error = Single(Lint("""
                card "Mirror"
                  cost 0
                  effect:
                    copy Strike
                """));

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("`copy` acts on something in the game", error.Message);
            Assert.Contains("`Strike` is content", error.Message);
            Assert.Contains("Write `create Strike` for a fresh one.", error.Message);
        }

        /// <summary>A name with a space is quoted in the fix, because that is how content would write it.</summary>
        [Fact]
        public void A_quoted_name_is_quoted_back_in_the_fix()
        {
            Diagnostic error = Single(Lint("""
                card "Mirror"
                  cost 0
                  effect:
                    copy "Fire Bolt"
                """));

            Assert.Contains("Write `create \"Fire Bolt\"` for a fresh one.", error.Message);
        }

        [Fact]
        public void Playing_a_definition_outside_a_test_is_an_error_that_names_create()
        {
            Diagnostic error = Single(Lint("""
                card "Cheat"
                  cost 0
                  effect:
                    play Strike
                """));

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("`play` acts on a card that is in a pile", error.Message);
            Assert.Contains("create Strike into hand", error.Message);
            Assert.Contains("play created.first", error.Message);
        }

        /// <summary>
        /// <c>replay</c> was the one of the four that worked: it ran a definition's printed effect
        /// for nothing, with no card and no cost behind it. It is refused here like the rest.
        /// </summary>
        [Fact]
        [Trait("Regression", "replay-takes-a-definition")]
        public void Replaying_a_definition_is_an_error_that_names_a_real_card()
        {
            Diagnostic error = Single(Lint("""
                card "Cheat"
                  cost 0
                  effect:
                    replay Strike
                """));

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("`replay` resolves the effect of a card that is in the game", error.Message);
            Assert.Contains("`Strike` is content", error.Message);
            Assert.Contains("create Strike into hand", error.Message);
            Assert.Contains("play created.first, free", error.Message);
        }

        /// <summary>
        /// The same mistake in reverse: <c>create</c> wants content, but only the kinds that stand
        /// in a zone on their own. <c>create Poison 2</c> made two Poisons attached to nobody, which
        /// raised <c>created</c> and went into saves.
        /// </summary>
        [Fact]
        [Trait("Regression", "create-makes-orphan-statuses")]
        public void Creating_a_status_is_an_error_that_names_apply()
        {
            Diagnostic error = Single(Lint("""
                card "Bad"
                  cost 0
                  effect:
                    create Poison 2
                """));

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("`Poison` is a status and belongs to whoever has it, not to a zone", error.Message);
            Assert.Contains("Write `apply Poison 2 to <who>` to give one.", error.Message);
        }

        /// <summary>An ability is granted rather than applied, so the fix says the other thing.</summary>
        [Fact]
        [Trait("Regression", "create-makes-orphan-statuses")]
        public void Creating_an_ability_names_how_an_ability_is_given()
        {
            Diagnostic error = Single(Lint("""
                ability "Zap"
                  cooldown 3
                  target enemy
                  effect:
                    deal 7 to target

                card "Bad"
                  cost 0
                  effect:
                    create Zap
                """));

            Assert.Contains("`Zap` is an ability and belongs to whoever has it", error.Message);
            Assert.Contains("grant Zap", error.Message);
        }

        /// <summary>
        /// A name shared between a card and a status is the card here, because that is the
        /// definition <c>create</c> itself prefers.
        /// </summary>
        [Fact]
        public void A_name_that_is_also_a_card_is_the_card_create_makes()
        {
            None(Lint("""
                card "Poison"
                  cost 0
                  tags unplayable

                card "Fine"
                  cost 0
                  effect:
                    create Poison 2 into draw
                """));
        }

        /// <summary>
        /// Everything these verbs are actually for: a local, a zone word, a selector. None of these
        /// is a definition, so none of them is reported.
        /// </summary>
        [Fact]
        public void Something_that_is_in_the_game_is_never_reported()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                card "Careful"
                  cost 0
                  target enemy
                  effect:
                    choose 1 from hand as picked
                    copy picked
                    copy target
                    copy hand.first into draw
                    play draw.first
                    play discard.first, free
                    replay hand.first on target
                    replay picked
                    copy
                """);

            None(diagnostics);
        }

        /// <summary>
        /// <c>transform Strike into Wound</c> is the same mistake in the third verb: it reads as if it
        /// changed every Strike, and a definition holds no entities, so the whole statement does
        /// nothing at all. The message names the slot rather than guessing at a spelling.
        /// </summary>
        [Fact]
        public void Transforming_a_definition_is_an_error_that_names_the_slot()
        {
            Diagnostic error = Single(Lint("""
                card "Wound"
                  cost 0
                  tags unplayable

                card "Curse It"
                  cost 0
                  effect:
                    transform Strike into Wound
                """));

            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("`transform` changes something that is in the game", error.Message);
            Assert.Contains("`Strike` is content", error.Message);
            Assert.Contains("transform target into Wound", error.Message);
        }

        /// <summary>The subject is what is checked; the <c>into</c> clause is content by design.</summary>
        [Fact]
        public void A_transform_that_names_something_in_the_game_is_never_reported()
        {
            None(Lint("""
                card "Wound"
                  cost 0
                  tags unplayable

                card "Fine"
                  cost 0
                  target enemy
                  effect:
                    choose 1 from hand as picked
                    transform picked into Wound
                    transform target to Wound
                    transform into Wound
                """));
        }

        /// <summary>
        /// Each verb binds its own participle, and the linter has to know them: without that, the
        /// line that reads the result, the line an author writes most, draws CT302 on content that
        /// is correct. <c>discovered</c> was missing too, and is fixed in the same list.
        /// </summary>
        [Fact]
        public void The_names_these_verbs_bind_are_not_unknown_names()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                card "Reads"
                  cost 0
                  target enemy
                  effect:
                    copy target
                    copied.hp = copied.max_hp
                    play draw.first
                    if played != none:
                      exhaust played
                    discover 3 cards
                    create discovered into hand
                """);

            Assert.DoesNotContain(diagnostics, d => d.Code == Linter.UnknownName);
        }

        /// <summary>In a test, <c>play Strike</c> is the test's own verb and naming a card is the point.</summary>
        [Fact]
        public void Playing_a_card_by_name_in_a_test_is_how_a_test_is_written()
        {
            None(Lint("""
                test "fine"
                  enemy hp 10
                  play Strike on enemy
                  expect enemy.hp == 4
                """));
        }

        /// <summary>
        /// In a scenario, <c>play</c> is CT301, a scenario states the fight and a bot plays it, so
        /// CT320 keeps quiet rather than arguing with a message that already says where the line belongs.
        /// </summary>
        [Fact]
        public void A_play_in_a_scenario_stays_the_verb_error_it_already_was()
        {
            IReadOnlyList<Diagnostic> diagnostics = Linter.Lint(
                ContentLibrary.FromText(Base + "enemy \"Ghoul\"\n  hp 20\n\nscenario \"Slip\"\n  battle Ghoul\n  play Strike\n", "lint.cantrip"));

            None(diagnostics);
            Diagnostic error = diagnostics.Single(d => d.Code == Linter.UnknownVerb);
            Assert.Contains("It only exists inside `test` blocks.", error.Message);
            Assert.Contains("A `scenario` states the fight; a bot plays it.", error.Message);
        }

        [Fact]
        [Trait("Regression", "own-verb-of-the-same-name")]
        public void A_game_with_its_own_verb_of_that_name_is_left_alone()
        {
            // These names were free before this release, so content may have a verb of its own by
            // one of them. That verb is what runs, and CT320 would be a false alarm about it.
            None(Lint("""
                verb copy(what):
                  deal 1 to what

                card "Old Ways"
                  cost 1
                  target enemy
                  effect:
                    copy Strike
                """));
        }

        [Fact]
        [Trait("Regression", "own-verb-of-the-same-name")]
        public void A_transform_of_its_own_inside_until_is_left_alone()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                verb transform(who):
                  deal 2 to who

                card "Old Ways"
                  cost 1
                  target enemy
                  effect:
                    until turn_end:
                      transform target
                """);

            Assert.DoesNotContain(diagnostics, d => d.Code == Linter.TransformInsideUntil);
        }

        // Helpers ----------------------------------------------------------------------------

        private static IReadOnlyList<Diagnostic> Lint(string dsl) =>
            Linter.Lint(ContentLibrary.FromText(Base + dsl, "lint.cantrip"));

        private static Diagnostic Single(IReadOnlyList<Diagnostic> diagnostics)
        {
            var matching = diagnostics.Where(d => d.Code == Linter.ContentWhereSomethingInPlayIsMeant).ToList();
            Assert.True(matching.Count == 1, $"expected one CT320, got:\n{string.Join("\n", diagnostics)}");
            return matching[0];
        }

        private static void None(IReadOnlyList<Diagnostic> diagnostics) =>
            Assert.True(
                diagnostics.All(d => d.Code != Linter.ContentWhereSomethingInPlayIsMeant),
                $"expected no CT320, got:\n{string.Join("\n", diagnostics)}");
    }
}
