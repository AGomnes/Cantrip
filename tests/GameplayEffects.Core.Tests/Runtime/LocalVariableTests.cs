using System.Linq;
using GameplayEffects.Diagnostics;
using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Runtime.RuntimeTestKit;

namespace GameplayEffects.Tests.Runtime
{
    /// <summary>
    /// `let`: a name bound for the rest of a body, so a scratch value no longer has to be a stat
    /// declared on something.
    /// </summary>
    public sealed class LocalVariableTests
    {
        [Fact]
        public void A_local_holds_a_value_for_the_rest_of_the_effect()
        {
            CardRuntime runtime = Create(
                "card \"Ledger\"\n" +
                "  cost 0\n" +
                "  effect:\n" +
                "    let bonus = 3\n" +
                "    gain bonus gold\n" +
                "    gain bonus gold\n");
            Enemy(runtime);
            Start(runtime);

            runtime.Play(runtime.AddCard("Ledger", Zones.Hand), null);

            Assert.Equal(6, runtime.Player!.GetInt("gold"));
        }

        [Fact]
        public void A_local_is_not_a_stat_on_anything()
        {
            CardRuntime runtime = Create(
                "card \"Ledger\"\n" +
                "  cost 0\n" +
                "  effect:\n" +
                "    let scratch = 5\n" +
                "    scratch += 1\n" +
                "    gain scratch gold\n");
            Enemy(runtime);
            Start(runtime);

            runtime.Play(runtime.AddCard("Ledger", Zones.Hand), null);

            // The whole point. Before `let`, this needed a declared stat, and the assignment above
            // would have quietly written one instead of changing a temporary.
            Assert.Equal(6, runtime.Player!.GetInt("gold"));
            Assert.DoesNotContain("scratch", runtime.Player.StatNames);
        }

        [Fact]
        public void A_local_shadows_a_stat_of_the_same_name()
        {
            CardRuntime runtime = Create(
                "card \"Trick\"\n" +
                "  cost 0\n" +
                "  effect:\n" +
                "    let hp = 99\n" +
                "    gain hp gold\n");
            Enemy(runtime);
            Start(runtime);

            runtime.Play(runtime.AddCard("Trick", Zones.Hand), null);

            Assert.Equal(99, runtime.Player!.GetInt("gold"));
            Assert.Equal(80, Hp(runtime.Player));
        }

        [Fact]
        public void A_local_can_hold_an_entity_and_not_only_a_number()
        {
            CardRuntime runtime = Create(
                "card \"Focus\"\n" +
                "  cost 0\n" +
                "  effect:\n" +
                "    let foe = enemy\n" +
                "    deal 4 to foe\n");
            Entity enemy = Enemy(runtime);
            Start(runtime);

            runtime.Play(runtime.AddCard("Focus", Zones.Hand), null);

            Assert.Equal(46, Hp(enemy));
        }

        [Fact]
        public void A_local_bound_inside_a_branch_stays_visible_after_it()
        {
            CardRuntime runtime = Create(
                "card \"Branchy\"\n" +
                "  cost 0\n" +
                "  effect:\n" +
                "    let found = 0\n" +
                "    if player.hp > 0:\n" +
                "      found = 7\n" +
                "    gain found gold\n");
            Enemy(runtime);
            Start(runtime);

            runtime.Play(runtime.AddCard("Branchy", Zones.Hand), null);

            // Blocks do not derive a scope of their own, so this is the documented behaviour rather
            // than an accident: the assignment changed the local, not a stat.
            Assert.Equal(7, runtime.Player!.GetInt("gold"));
        }

        [Fact]
        public void A_local_does_not_outlive_the_body_that_bound_it()
        {
            CardRuntime runtime = Create(
                "relic \"Counter\"\n" +
                "  on damaged:\n" +
                "    let seen = 1\n" +
                "    gain seen gold\n");
            runtime.AddRelic("Counter");
            Enemy(runtime);

            runtime.Execute("deal 1 to enemy");
            runtime.Execute("deal 1 to enemy");
            Assert.Equal(2, runtime.Player!.GetInt("gold"));

            // Each listener run gets a fresh context, so the name is gone outside it.
            Assert.Throws<RuntimeError>(() => runtime.Execute("gain seen gold"));
        }
    }
}
