using System.Linq;
using Cantrip.Content;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Language
{
    /// <summary>
    /// <c>who.is_ready(Ability)</c> and <c>who.can_use(Ability)</c>: the two questions a real-time
    /// game is made of, asked from content.
    /// </summary>
    /// <remarks>
    /// A test could say what an ability does and never that it may not be used yet: <c>cast</c>
    /// fails the test outright when the ability is on cooldown (<c>NotReady</c>) or has nothing
    /// legal to aim at (<c>InvalidTarget</c>), and there was no predicate in content either —
    /// <c>leader.is_ready(Bulwark)</c> was <c>runtime error: Unknown method 'is_ready'</c>. In a
    /// game whose whole design is about what you may not do yet, that is half the test surface
    /// missing, and the workaround was to write the test inside out: cast at nobody and check
    /// which of two enemies it picked.
    /// </remarks>
    public sealed class AbilityReadinessTests
    {
        private const string Game = """
            ruleset
              clock ticks

            board "Line"
              lanes 3
              ranks 4
              facing

            ability "Bulwark"
              cooldown 6s
              effect:
                block 4

            ability "Bolt"
              cooldown 1s
              target enemy
              range 2
              effect:
                deal 5 to target

            enemy "Hollow"
              hp 16

            """;

        [Fact]
        [Trait("Regression", "a-test-cannot-say-an-ability-is-not-ready")]
        public void A_test_can_say_that_an_ability_is_not_ready_yet()
        {
            Passes("""
                test "Bulwark waits six seconds"
                  realtime 20
                  grant "Bulwark"
                  expect leader.is_ready(Bulwark)
                  cast Bulwark
                  expect not leader.is_ready(Bulwark)
                  tick 100
                  expect not leader.is_ready(Bulwark)
                  tick 20
                  expect leader.is_ready(Bulwark)
                """);
        }

        [Fact]
        [Trait("Regression", "a-test-cannot-say-an-ability-cannot-reach")]
        public void And_that_it_has_nothing_in_reach()
        {
            Passes("""
                test "Bolt reaches two ranks and no further"
                  realtime 20
                  grant "Bolt"
                  expect leader.is_ready(Bolt)
                  expect not leader.can_use(Bolt)
                  enemy Hollow
                  enemy.lane = 0
                  enemy.rank = 3
                  expect distance(leader, enemy) == 4
                  expect not leader.can_use(Bolt)
                  enemy.rank = 1
                  expect distance(leader, enemy) == 2
                  expect leader.can_use(Bolt)
                """);
        }

        /// <summary>
        /// The two are different questions, and a real-time UI greys a button out on the second
        /// while a cooldown sweep is drawn from the first.
        /// </summary>
        [Fact]
        public void Ready_and_usable_are_not_the_same_question()
        {
            Passes("""
                test "ready with nobody to aim at"
                  realtime 20
                  grant "Bolt"
                  expect leader.is_ready(Bolt)
                  expect not leader.can_use(Bolt)
                """);
        }

        [Fact]
        public void An_ability_the_actor_does_not_have_is_said_out_loud()
        {
            DslTestResult result = Run("""
                test "a name nobody has"
                  realtime 20
                  expect leader.is_ready(Bulwark)
                """);

            Assert.False(result.Passed);
            Assert.Contains("has no ability `Bulwark`", result.Failure);
        }

        // Helpers --------------------------------------------------------------------------------

        private static DslTestResult Run(string test)
        {
            ContentLibrary content = ContentLibrary.FromText(Game + test, "readiness.cantrip");
            content.Diagnostics.ThrowIfErrors();
            return new DslTestRunner(content).Run(content.Tests.Single());
        }

        private static void Passes(string test)
        {
            DslTestResult result = Run(test);
            Assert.True(result.Passed, result.ToString());
        }
    }
}
