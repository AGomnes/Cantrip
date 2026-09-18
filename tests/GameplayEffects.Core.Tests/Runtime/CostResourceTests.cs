using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Runtime.RuntimeTestKit;

namespace GameplayEffects.Tests.Runtime
{
    /// <summary>
    /// Costs paid in something other than energy, which is the difference between a card that is
    /// unplayable and a card that checks and then does nothing.
    /// </summary>
    public sealed class CostResourceTests
    {
        private const string Content =
            "resource \"bones\"\n" +
            "  min 0\n" +
            "\n" +
            "card \"Bone Bargain\"\n" +
            "  cost 2 bones\n" +
            "  tags spell\n" +
            "  effect:\n" +
            "    gain 1 gold\n" +
            "\n" +
            "card \"Spark\"\n" +
            "  cost 1\n" +
            "  tags spell\n" +
            "  effect:\n" +
            "    gain 1 gold\n" +
            "\n" +
            "relic \"Tax\"\n" +
            "  modify cost: +1\n";

        private static CardRuntime Ready()
        {
            CardRuntime runtime = Create(Content);
            Enemy(runtime);
            Start(runtime);
            return runtime;
        }

        [Fact]
        public void A_card_can_be_priced_in_another_resource()
        {
            CardRuntime runtime = Ready();
            runtime.Player!.SetBase("bones", 2);

            Assert.Equal(PlayResult.Played, runtime.Play(runtime.AddCard("Bone Bargain", Zones.Hand), null));

            Assert.Equal(0, runtime.Player.GetInt("bones"));
            Assert.Equal(1, runtime.Player.GetInt("gold"));
        }

        [Fact]
        public void Paying_in_another_resource_leaves_energy_alone()
        {
            CardRuntime runtime = Ready();
            runtime.Player!.SetBase("bones", 2);
            int energy = runtime.Player.GetInt("energy");

            runtime.Play(runtime.AddCard("Bone Bargain", Zones.Hand), null);

            Assert.Equal(energy, runtime.Player.GetInt("energy"));
        }

        [Fact]
        public void A_card_is_refused_when_the_resource_is_short()
        {
            CardRuntime runtime = Ready();
            runtime.Player!.SetBase("bones", 1);

            Assert.Equal(PlayResult.NotEnoughEnergy, runtime.Play(runtime.AddCard("Bone Bargain", Zones.Hand), null));

            // Refused, not merely ineffective: nothing was spent and nothing happened.
            Assert.Equal(1, runtime.Player.GetInt("bones"));
            Assert.Equal(0, runtime.Player.GetInt("gold"));
        }

        [Fact]
        public void A_cost_with_no_resource_named_is_still_energy()
        {
            CardRuntime runtime = Ready();
            int energy = runtime.Player!.GetInt("energy");

            Assert.Equal(PlayResult.Played, runtime.Play(runtime.AddCard("Spark", Zones.Hand), null));

            Assert.Equal(energy - 1, runtime.Player.GetInt("energy"));
        }

        [Fact]
        public void The_cost_channel_applies_whatever_the_cost_is_paid_in()
        {
            CardRuntime runtime = Ready();
            runtime.AddRelic("Tax");
            runtime.Player!.SetBase("bones", 2);

            // Tax makes it cost three, so two bones is no longer enough.
            Assert.Equal(PlayResult.NotEnoughEnergy, runtime.Play(runtime.AddCard("Bone Bargain", Zones.Hand), null));

            runtime.Player.SetBase("bones", 3);
            Assert.Equal(PlayResult.Played, runtime.Play(runtime.AddCard("Bone Bargain", Zones.Hand), null));
            Assert.Equal(0, runtime.Player.GetInt("bones"));
        }
    }
}
