using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Runtime.RuntimeTestKit;

namespace GameplayEffects.Tests.Runtime
{
    /// <summary>
    /// `event.reset` on a `&lt;stat&gt;_changed` event: how content stops a resource reset without
    /// having to guess that a reset is what happened.
    /// </summary>
    /// <remarks>
    /// Guessing is the state of the art being replaced. "Block became 0" is how the corpus detects
    /// the turn-start reset, and its own note admits that only holds while nothing else sets block
    /// to 0 — a property of today's engine rather than of the rule being written.
    /// </remarks>
    public sealed class ResetMarkerTests
    {
        private const string Content =
            "relic \"Barricade\"\n" +
            "  on before_block_changed(target:owner):\n" +
            "    if event.reset: cancel\n" +
            "\n" +
            "card \"Strip\"\n" +
            "  cost 0\n" +
            "  effect:\n" +
            "    block = 0\n";

        [Fact]
        public void A_reset_marks_the_change_it_raises_and_can_be_cancelled_by_it()
        {
            CardRuntime runtime = Create(Content);
            runtime.AddRelic("Barricade");
            Enemy(runtime);
            Start(runtime);

            runtime.Execute("block 12");
            Assert.Equal(12, runtime.Player!.GetInt("block"));

            runtime.EndTurn();

            // The turn-start reset said so outright, so Barricade could refuse it.
            Assert.Equal(12, runtime.Player.GetInt("block"));
        }

        [Fact]
        public void An_effect_that_sets_the_stat_to_zero_is_not_a_reset()
        {
            CardRuntime runtime = Create(Content);
            runtime.AddRelic("Barricade");
            Enemy(runtime);
            Start(runtime);

            runtime.Execute("block 12");
            runtime.Play(runtime.AddCard("Strip", Zones.Hand), null);

            // This is the false positive the value test cannot tell apart: Barricade must not
            // protect block from an effect that means to remove it.
            Assert.Equal(0, runtime.Player!.GetInt("block"));
        }

        [Fact]
        public void Without_the_marker_a_reset_still_happens_as_before()
        {
            CardRuntime runtime = Create("card \"Guard\"\n  cost 0\n  effect:\n    block 5\n");
            Enemy(runtime);
            Start(runtime);

            runtime.Execute("block 7");
            runtime.EndTurn();

            Assert.Equal(0, runtime.Player!.GetInt("block"));
        }
    }
}
