using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Runtime.RuntimeTestKit;

namespace GameplayEffects.Tests.Runtime
{
    /// <summary>
    /// What declaring a resource actually does for the entities its reset concerns.
    /// </summary>
    public sealed class ResourceResetTests
    {
        [Fact]
        public void A_declared_resource_establishes_its_stat_when_it_resets()
        {
            CardRuntime runtime = Create(
                "resource \"actions\"\n" +
                "  min 0\n" +
                "  reset_to 1\n" +
                "  reset_on turn_start\n");
            Enemy(runtime);
            Start(runtime);

            // Nothing granted `actions` to anyone. The declaration is the whole of it.
            Assert.Equal(1, runtime.Player!.GetInt("actions"));
        }

        [Fact]
        public void A_reset_whose_value_names_a_missing_stat_creates_nothing()
        {
            CardRuntime runtime = Create(
                "resource \"shield\"\n" +
                "  reset_to max_shield\n" +
                "  reset_on turn_start\n");
            Enemy(runtime);
            Start(runtime);

            // This is the guard that keeps an enemy from acquiring `energy` out of `max_energy`.
            Assert.False(runtime.Player!.HasStat("shield"));
        }

        [Fact]
        public void The_built_in_resources_still_behave_as_they_did()
        {
            CardRuntime runtime = Create("card \"Guard\"\n  cost 0\n  effect:\n    block 5\n");
            Entity foe = Enemy(runtime);
            Start(runtime);

            Assert.Equal(3, runtime.Player!.GetInt("energy"));
            Assert.False(foe.HasStat("energy"));
        }
    }
}
