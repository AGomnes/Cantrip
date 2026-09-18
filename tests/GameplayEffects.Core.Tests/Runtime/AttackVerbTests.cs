using GameplayEffects.Diagnostics;
using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Runtime.RuntimeTestKit;

namespace GameplayEffects.Tests.Runtime
{
    /// <summary>
    /// The `attack` verb, whose reason to exist is that the attacker is the source of its own hit.
    /// A content verb cannot do that: inside one, `deal` comes from whoever called it.
    /// </summary>
    public sealed class AttackVerbTests
    {
        private const string Content =
            "enemy \"Biter\"\n" +
            "  hp 5\n" +
            "  attack 2\n" +
            "  move \"Bite\":\n" +
            "    attack player\n" +
            "\n" +
            "status \"Lifelink\"\n" +
            "  tags buff\n" +
            "  stacking none\n" +
            "  on damaged(source:owner):\n" +
            "    heal event.amount to owner\n" +
            "\n" +
            "status \"Sharpened\"\n" +
            "  tags buff\n" +
            "  stacking none\n" +
            "  modify attack: +1\n";

        [Fact]
        public void The_attacker_is_the_source_of_its_own_hit()
        {
            CardRuntime runtime = Create(Content);
            Entity biter = runtime.SpawnEnemy("Biter");
            runtime.Execute("deal 4 to enemy");
            Assert.Equal(1, Hp(biter));

            runtime.ApplyStatus("Lifelink", biter, 1);
            runtime.Execute("attack player with enemy");

            // Lifelink listens for damage whose source is its owner. Before `attack` existed the
            // source was whoever ran the statement, so this listener could never fire.
            Assert.Equal(78, Hp(runtime.Player!));
            Assert.Equal(3, Hp(biter));
        }

        [Fact]
        public void An_attack_uses_the_attackers_own_attack_stat()
        {
            CardRuntime runtime = Create(Content);
            Entity biter = runtime.SpawnEnemy("Biter");

            runtime.Execute("attack player with enemy");

            Assert.Equal(78, Hp(runtime.Player!));
            Assert.Equal(2, biter.GetInt("attack"));
        }

        [Fact]
        public void The_attack_stat_is_read_through_its_modifiers()
        {
            CardRuntime runtime = Create(Content);
            Entity biter = runtime.SpawnEnemy("Biter");
            runtime.ApplyStatus("Sharpened", biter, 1);

            runtime.Execute("attack player with enemy");

            Assert.Equal(77, Hp(runtime.Player!));
        }

        [Fact]
        public void A_creature_attacking_in_its_own_move_swings_as_itself()
        {
            CardRuntime runtime = Create(Content);
            Entity biter = runtime.SpawnEnemy("Biter");
            runtime.Execute("deal 4 to enemy");
            runtime.ApplyStatus("Lifelink", biter, 1);
            Start(runtime);

            runtime.EndTurn();

            // No `with` clause: the running entity is an actor, so it swings as itself, and its
            // Lifelink sees the hit.
            Assert.Equal(78, Hp(runtime.Player!));
            Assert.Equal(3, Hp(biter));
        }

        [Fact]
        public void Attacking_nobody_is_an_error_rather_than_a_silent_miss()
        {
            CardRuntime runtime = Create(Content);
            runtime.SpawnEnemy("Biter");

            Assert.Throws<RuntimeError>(() => runtime.Execute("attack"));
        }
    }
}
