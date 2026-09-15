using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Runtime.RuntimeTestKit;

namespace GameplayEffects.Tests.Runtime
{
    /// <summary>
    /// Sides inside modifiers are read from the modifier owner. Regression: they were once read
    /// from whoever was acting, so an enemy attacker saw the player's side as "enemies" and every
    /// <c>source:enemies</c> or <c>allies</c> test in a modifier gave the wrong answer.
    /// </summary>
    public sealed class ModifierPerspectiveTests
    {
        private const string Content = """
            relic "Guard"
              modify damage_taken where source:enemies: -2

            relic "Pack Tactics"
              modify damage where allies.count > 1: +1

            relic "Banner"
              modify damage of everyone where source:allies: x1.25
            """;

        [Fact]
        public void Source_enemies_in_a_modifier_means_the_owners_enemies()
        {
            CardRuntime runtime = Create(Content);
            Entity enemy = Enemy(runtime);
            runtime.AddRelic("Guard");

            runtime.Execute("deal 5 to player", self: enemy);
            Assert.Equal(77, Hp(runtime.Player!));

            runtime.Execute("deal 5 to player");
            Assert.Equal(72, Hp(runtime.Player!));
        }

        [Fact]
        public void Group_names_in_a_modifier_are_read_from_the_owners_side()
        {
            CardRuntime runtime = Create(Content);
            Entity enemy = Enemy(runtime);
            runtime.AddRelic("Pack Tactics");

            runtime.Execute("deal 5 to enemy");
            Assert.Equal(45, Hp(enemy));

            runtime.State.Spawn("Squire", EntityKind.Actor, null, Team.Player, Zones.Board);
            runtime.Execute("deal 5 to enemy");
            Assert.Equal(39, Hp(enemy));
        }

        [Fact]
        public void Source_allies_on_an_of_scope_excludes_enemy_attackers()
        {
            CardRuntime runtime = Create(Content);
            Entity enemy = Enemy(runtime);
            runtime.AddRelic("Banner");

            runtime.Execute("deal 4 to enemy");
            Assert.Equal(45, Hp(enemy));

            runtime.Execute("deal 4 to player", self: enemy);
            Assert.Equal(76, Hp(runtime.Player!));
        }
    }
}
