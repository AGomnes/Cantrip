using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Testing;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// <c>create</c>: a fresh one from a definition, standing in a zone of its own.
    /// </summary>
    /// <remarks>
    /// "Of its own" is the part that rules out three kinds. A status, keyword or ability belongs to
    /// whoever has it, and <c>create Poison 2</c> made two Poisons attached to nobody: they raised
    /// <c>created</c>, so listeners fired about them, and they persisted into saves. <c>copy</c>
    /// already refused those three by name; this is the same refusal in the verb that makes things.
    /// </remarks>
    public sealed class CreateVerbTests
    {
        [Fact]
        [Trait("Regression", "create-makes-orphan-statuses")]
        public void A_status_is_refused_and_names_apply()
        {
            string failure = FirstFailure("""
                status "Poison"
                  tags debuff
                  stacking intensity

                card "Bad"
                  cost 0
                  effect:
                    create Poison 2

                test "a status is not made on its own"
                  enemy hp 50
                  hand Bad
                  player energy 9
                  play Bad
                """);

            Assert.Contains("`Poison` is a status and belongs to whoever has it, not to a zone", failure);
            Assert.Contains("Write `apply Poison 2 to <who>` to give one.", failure);
        }

        [Fact]
        [Trait("Regression", "create-makes-orphan-statuses")]
        public void A_keyword_is_refused_the_same_way()
        {
            string failure = FirstFailure("""
                keyword "Taunt"

                card "Bad"
                  cost 0
                  effect:
                    create Taunt

                test "a keyword is not made on its own"
                  enemy hp 50
                  hand Bad
                  player energy 9
                  play Bad
                """);

            Assert.Contains("`Taunt` is a keyword and belongs to whoever has it", failure);
        }

        [Fact]
        [Trait("Regression", "create-makes-orphan-statuses")]
        public void An_ability_is_refused_and_names_how_one_is_granted()
        {
            string failure = FirstFailure("""
                ability "Zap"
                  cooldown 3
                  target enemy
                  effect:
                    deal 7 to target

                card "Bad"
                  cost 0
                  effect:
                    create Zap

                test "an ability is granted, not made"
                  enemy hp 50
                  hand Bad
                  player energy 9
                  play Bad
                """);

            Assert.Contains("`Zap` is an ability and belongs to whoever has it", failure);
            Assert.Contains("grant Zap", failure);
        }

        /// <summary>
        /// Nothing the verb is for moves: cards, actors, relics and items are made as they were, and
        /// a name shared between a card and a status still makes the card.
        /// </summary>
        [Fact]
        public void The_kinds_that_stand_in_a_zone_are_untouched() => Passes("""
            status "Shiv"
              stacking intensity

            card "Shiv"
              cost 0
              target enemy
              effect:
                deal 4 to target

            actor "Imp"
              hp 10

            card "Knives"
              cost 0
              effect:
                create Shiv 3 into hand
                create Imp

            test "cards and actors are made as they were"
              enemy hp 50
              hand Knives
              player energy 9
              play Knives
              expect hand.count == 3
              expect hand.first.name == "Shiv"
              expect allies.count == 2
            """);

        /// <summary>
        /// Slay the Spire's Wound, Monster Train's Pyre damage, Inscryption's decay: an enemy puts a
        /// card into the player's deck. <c>into</c> is the pile and <c>to</c> is whose.
        /// </summary>
        /// <remarks>
        /// All three clause words used to mean the pile, so <c>to</c> was read and thrown away and
        /// the curse was made in the <em>enemy's</em> discard pile, where the player would never see
        /// it. A clause a verb does not read is CT323 precisely so that a card cannot read as one
        /// thing and do another; this one was read, just not for this.
        /// </remarks>
        [Fact]
        [Trait("Regression", "create-to-is-ignored")]
        public void To_says_whose_pile_a_card_lands_in()
        {
            CardRuntime runtime = CardRuntime.FromText("""
                card "Brine"
                  cost 0
                  tags curse

                enemy "Tidewalker"
                  hp 40
                """);
            Entity player = runtime.CreatePlayer();
            Entity tidewalker = runtime.SpawnEnemy("Tidewalker");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Execute("create Brine into discard to leader", self: tidewalker);

            IReadOnlyList<Entity> ours = runtime.State.ZoneOf(player, Zones.Discard);
            Assert.Empty(runtime.State.ZoneOf(tidewalker, Zones.Discard));
            Assert.Single(ours);
            Assert.Equal("Brine", ours[0].Name);
            Assert.Same(player, ours[0].Controller);
        }

        [Fact]
        public void And_with_no_to_whoever_made_it_keeps_it()
        {
            CardRuntime runtime = CardRuntime.FromText("""
                card "Brine"
                  cost 0

                enemy "Tidewalker"
                  hp 40
                """);
            Entity player = runtime.CreatePlayer();
            Entity tidewalker = runtime.SpawnEnemy("Tidewalker");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Execute("create Brine into discard", self: tidewalker);

            Assert.Single(runtime.State.ZoneOf(tidewalker, Zones.Discard));
            Assert.Empty(runtime.State.ZoneOf(player, Zones.Discard));
        }

        /// <summary>The old spelling is refused by name rather than left to mean two things.</summary>
        [Fact]
        public void To_naming_a_pile_says_to_write_into()
        {
            string failure = FirstFailure("""
                card "Shiv"
                  cost 0

                card "Knives"
                  cost 0
                  effect:
                    create Shiv 2 to hand

                test "to a pile is refused"
                  enemy hp 50
                  hand Knives
                  player energy 9
                  play Knives
                """);

            Assert.Contains("`to` says whose it is; `hand` is a pile", failure);
            Assert.Contains("Write `into hand` for the pile", failure);
        }

        // Helpers ----------------------------------------------------------------------------

        private static void Passes(string dsl)
        {
            IReadOnlyList<DslTestResult> results = new DslTestRunner(Load(dsl)).RunAll();
            Assert.NotEmpty(results);
            foreach (DslTestResult result in results) Assert.True(result.Passed, result.ToString());
        }

        private static string FirstFailure(string dsl)
        {
            DslTestResult result = new DslTestRunner(Load(dsl)).RunAll().Last();
            Assert.False(result.Passed, "expected the test to be refused, but it passed");
            return result.Failure!;
        }

        private static ContentLibrary Load(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl, "create-verb.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            return content;
        }
    }
}
