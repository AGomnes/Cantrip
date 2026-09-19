using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Runtime.RuntimeTestKit;

namespace GameplayEffects.Tests.Runtime
{
    /// <summary>
    /// Re-telegraphing: a boss that crosses a phase threshold during the player's turn shows the
    /// move its new phase just unlocked, instead of the one it had already committed to.
    /// </summary>
    /// <remarks>
    /// It is opt-in because it trades away a guarantee worth keeping — ordinarily the intent shown
    /// during the player's turn is exactly the move that follows. These tests pin both sides of
    /// that: the boss that asks, and the boss that does not.
    /// </remarks>
    public sealed class RetelegraphTests
    {
        private const string Content =
            "enemy \"Slime King\"\n" +
            "  hp 40\n" +
            "  phase Whole when hp > max_hp / 2\n" +
            "  phase Broken when hp <= max_hp / 2, retelegraph\n" +
            "  move \"Slam\" phase Whole:\n" +
            "    deal 8 to player\n" +
            "  move \"Split\" phase Broken:\n" +
            "    deal 1 to player\n" +
            "\n" +
            "enemy \"Stoic\"\n" +
            "  hp 40\n" +
            "  phase Calm when hp > max_hp / 2\n" +
            "  phase Angry when hp <= max_hp / 2\n" +
            "  move \"Poke\" phase Calm:\n" +
            "    deal 2 to player\n" +
            "  move \"Rage\" phase Angry:\n" +
            "    deal 9 to player\n";

        [Fact]
        public void Crossing_the_threshold_re_telegraphs_the_move_it_unlocked()
        {
            CardRuntime runtime = Create(Content);
            Entity king = runtime.SpawnEnemy("Slime King");
            Start(runtime);
            Assert.Equal("Slam", king.Intent);

            runtime.Execute("deal 25 to enemy");

            // The threshold was crossed mid-turn, so the intent no longer says Slam.
            Assert.Equal("Broken", king.Phase);
            Assert.Equal("Split", king.Intent);
        }

        [Fact]
        public void A_phase_that_does_not_ask_leaves_the_telegraph_alone()
        {
            CardRuntime runtime = Create(Content);
            Entity stoic = runtime.SpawnEnemy("Stoic");
            Start(runtime);
            Assert.Equal("Poke", stoic.Intent);

            runtime.Execute("deal 25 to enemy");

            // The phase still changes; what it does not do is take back the telegraph.
            Assert.Equal("Angry", stoic.Phase);
            Assert.Equal("Poke", stoic.Intent);
        }

        [Fact]
        public void A_killing_blow_re_telegraphs_nothing()
        {
            CardRuntime runtime = Create(Content);
            Entity king = runtime.SpawnEnemy("Slime King");
            Start(runtime);

            runtime.Execute("deal 100 to enemy");

            Assert.True(king.IsDead);
        }

        [Fact]
        public void The_re_telegraphed_move_is_the_one_that_follows()
        {
            CardRuntime runtime = Create(Content);
            Entity king = runtime.SpawnEnemy("Slime King");
            Start(runtime);
            int hp = Hp(runtime.Player!);

            runtime.Execute("deal 25 to enemy");
            Assert.Equal("Split", king.Intent);

            runtime.EndTurn();

            // Split deals 1, where the Slam it replaced would have dealt 8.
            Assert.Equal(hp - 1, Hp(runtime.Player!));
        }
    }
}
