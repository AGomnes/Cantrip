using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Runtime.RuntimeTestKit;

namespace GameplayEffects.Tests.Runtime
{
    /// <summary>
    /// `into`: binding what a verb actually achieved, rather than inferring it from a counter read
    /// either side of the line.
    /// </summary>
    /// <remarks>
    /// The number that matters is what landed, which is rarely what was asked for — block absorbs
    /// some of it, modifiers change it, and a dying target cannot take the rest.
    /// </remarks>
    public sealed class IntoBindingTests
    {
        private const string Content =
            "card \"Reap\"\n" +
            "  cost 0\n" +
            "  effect:\n" +
            "    deal 4 to all enemies into dealt\n" +
            "    heal dealt\n" +
            "\n" +
            "card \"Probe\"\n" +
            "  cost 0\n" +
            "  target enemy\n" +
            "  effect:\n" +
            "    deal 4 to target into dealt\n" +
            "    gain dealt gold\n";

        [Fact]
        public void The_bound_amount_is_what_landed_across_every_target()
        {
            CardRuntime runtime = Create(Content);
            Entity soft = Enemy(runtime, 30);
            Entity frail = Enemy(runtime, 3, "Frail");
            runtime.Player!.SetBase("hp", 50);
            Start(runtime);

            runtime.Play(runtime.AddCard("Reap", Zones.Hand), null);

            // 4 into the first, but only 3 into the one that had 3 left: 7 healed, not 8.
            Assert.Equal(26, Hp(soft));
            Assert.True(frail.IsDead);
            Assert.Equal(57, Hp(runtime.Player));
        }

        [Fact]
        public void Block_absorbed_damage_is_not_counted_as_landed()
        {
            CardRuntime runtime = Create(Content);
            Entity guarded = Enemy(runtime, 30);
            guarded.SetBase("block", 3);
            Start(runtime);

            runtime.Play(runtime.AddCard("Probe", Zones.Hand), guarded);

            // 4 dealt, 3 absorbed, so 1 landed and 1 gold.
            Assert.Equal(29, Hp(guarded));
            Assert.Equal(1, runtime.Player!.GetInt("gold"));
        }

        [Fact]
        public void A_verb_with_no_into_clause_binds_nothing_and_behaves_as_before()
        {
            CardRuntime runtime = Create(
                "card \"Plain\"\n" +
                "  cost 0\n" +
                "  target enemy\n" +
                "  effect:\n" +
                "    deal 4 to target\n");
            Entity foe = Enemy(runtime, 30);
            Start(runtime);

            runtime.Play(runtime.AddCard("Plain", Zones.Hand), foe);

            Assert.Equal(26, Hp(foe));
        }

        [Fact]
        public void An_attack_binds_what_it_landed_too()
        {
            CardRuntime runtime = Create(
                "enemy \"Biter\"\n" +
                "  hp 10\n" +
                "  attack 6\n" +
                "  move \"Bite\":\n" +
                "    attack player into bit\n" +
                "    gain bit gold to player\n");
            runtime.SpawnEnemy("Biter");
            Start(runtime);

            // After the battle starts, not before: the player's turn_start would have reset it.
            // The enemy's turn_start resets the enemy's block, not the player's, so this survives.
            runtime.Player!.SetBase("block", 2);

            runtime.EndTurn();

            // 6 swung, 2 blocked: the enemy's own move reports the 4 that landed.
            Assert.Equal(4, runtime.Player.GetInt("gold"));
        }
    }
}
