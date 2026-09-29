using System.Collections.Generic;
using Cantrip.Content;
using Cantrip.Runtime;
using Cantrip.Testing;
using Xunit;
using static Cantrip.Tests.Runtime.RuntimeTestKit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// One entity answers the group words, so a line written for a group works for one.
    /// </summary>
    /// <remarks>
    /// <c>choose 1 from hand as picked</c> binds an entity while <c>choose 2</c> binds a group, so
    /// <c>picked.first</c> fell through to "a stat nothing has" and read 0 — and only on the
    /// one-card path, which is the path an author tries last. Binding a group for one instead would
    /// have broken every line that reads <c>picked.name</c> or <c>picked.hp</c>, so the group words
    /// answer for a single entity rather than the other way round.
    /// </remarks>
    public sealed class GroupOfOneTests
    {
        [Fact]
        [Trait("Regression", "choose-one-binds-an-entity")]
        public void The_group_words_answer_for_a_single_entity()
        {
            CardRuntime runtime = Create("""
                actor "Imp"
                  hp 10
                """);
            Entity enemy = Enemy(runtime, 30);
            Start(runtime);

            Assert.Equal(1, EvalInt(runtime, "target.count", enemy));
            Assert.Equal(1, EvalInt(runtime, "target.size", enemy));
            Assert.Equal(enemy, Eval(runtime, "target.first", enemy).Entity);
            Assert.Equal(enemy, Eval(runtime, "target.last", enemy).Entity);
            Assert.False(Eval(runtime, "target.empty", enemy).AsBool());
            Assert.True(Eval(runtime, "target.any", enemy).AsBool());

            // Chaining still works, and a real stat is still a real stat.
            Assert.Equal(30, EvalInt(runtime, "target.first.hp", enemy));
        }

        [Fact]
        [Trait("Regression", "choose-one-binds-an-entity")]
        public void A_line_written_for_a_group_works_when_one_card_is_chosen() => Passes("""
            card "Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 6 to target

            card "Sift One"
              cost 0
              effect:
                choose 1 from hand as picked
                exhaust picked.first

            card "Sift Two"
              cost 0
              effect:
                choose 2 from hand as picked
                exhaust picked.first

            test "one chosen card reads like a group of one"
              enemy hp 50
              hand Strike, "Sift One"
              player energy 9
              play "Sift One"
              expect exhaust.count == 1
              expect hand.count == 0

            test "two chosen cards read the same way"
              enemy hp 50
              hand Strike, Strike, "Sift Two"
              player energy 9
              play "Sift Two"
              expect exhaust.count == 1
              expect hand.count == 1
            """);

        /// <summary>The name reads as it always did, which is why one is not bound as a group.</summary>
        [Fact]
        public void A_single_binding_still_reads_its_own_properties() => Passes("""
            card "Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 6 to target

            card "Name It"
              cost 0
              effect:
                choose 1 from hand as picked
                if picked.name == "Strike":
                  gain 3 gold

            test "one chosen card still answers about itself"
              enemy hp 50
              hand Strike, "Name It"
              player energy 9
              player gold 0
              play "Name It"
              expect player.gold == 3
            """);

        /// <summary>
        /// The things a group can be asked that are not numbers. <c>created.zone</c> printed 0 and
        /// <c>created.controller.name</c> failed with "`0` has no property `name`", because both
        /// fell through to the stat sum, where a stat nothing has is 0 — the same trap this class
        /// exists to close, on the members nobody had added.
        /// </summary>
        [Fact]
        public void A_group_answers_where_it_is_and_whose_it_is() => Passes("""
            card "Shiv"
              cost 0

            card "Knives"
              cost 0
              effect:
                create Shiv into discard
                if created.zone == "discard":
                  gain 2 gold
                if created.name == "Shiv":
                  gain 5 gold
                if created.controller.name == created.first.controller.name:
                  gain 3 gold

            test "a group says where it is and whose it is"
              enemy hp 50
              hand Knives
              player energy 9
              player gold 0
              play Knives
              expect player.gold == 10
            """);

        private static void Passes(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl, "group-of-one.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());

            IReadOnlyList<DslTestResult> results = new DslTestRunner(content).RunAll();
            Assert.NotEmpty(results);
            foreach (DslTestResult result in results) Assert.True(result.Passed, result.ToString());
        }
    }
}
