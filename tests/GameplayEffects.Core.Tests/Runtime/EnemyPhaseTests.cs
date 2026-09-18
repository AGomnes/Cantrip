using GameplayEffects.Content;
using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Runtime.RuntimeTestKit;

namespace GameplayEffects.Tests.Runtime
{
    /// <summary>
    /// Enemy behaviour phases: which moves an enemy may choose from, and when that changes.
    /// </summary>
    public sealed class EnemyPhaseTests
    {
        private const string Content =
            "enemy \"Slime King\"\n" +
            "  hp 40\n" +
            "  phase Broken when hp <= max_hp / 2\n" +
            "  move \"Chomp\":\n" +
            "    deal 5 to player\n" +
            "  move \"Split\" phase Broken:\n" +
            "    deal 1 to player\n" +
            "  pattern cycle Chomp, Split\n";

        private static CardRuntime Ready(out Entity king)
        {
            CardRuntime runtime = Create(Content);
            king = runtime.SpawnEnemy("Slime King");
            return runtime;
        }

        [Fact]
        public void A_phased_move_is_never_chosen_outside_its_phase()
        {
            CardRuntime runtime = Ready(out Entity king);

            for (int i = 0; i < 6; i++)
            {
                runtime.Interpreter.RollIntent(king);
                Assert.Equal("Chomp", king.Intent);
            }

            Assert.Null(king.Phase);
        }

        [Fact]
        public void A_phase_begins_when_its_condition_holds()
        {
            CardRuntime runtime = Ready(out Entity king);
            runtime.Interpreter.RollIntent(king);
            Assert.Null(king.Phase);

            king.SetBase("hp", 15);
            runtime.Interpreter.RollIntent(king);

            Assert.Equal("Broken", king.Phase);
        }

        [Fact]
        public void A_new_phase_starts_its_sequence_from_the_beginning()
        {
            CardRuntime runtime = Ready(out Entity king);

            // Two rolls into the cycle while only Chomp is available.
            runtime.Interpreter.RollIntent(king);
            runtime.Interpreter.RollIntent(king);

            king.SetBase("hp", 15);
            runtime.Interpreter.RollIntent(king);

            // Both moves are available now, and the cycle restarts rather than landing mid-list.
            Assert.Equal("Broken", king.Phase);
            Assert.Equal("Chomp", king.Intent);

            runtime.Interpreter.RollIntent(king);
            Assert.Equal("Split", king.Intent);
        }

        [Fact]
        public void Leaving_a_phase_puts_its_moves_back_out_of_reach()
        {
            CardRuntime runtime = Ready(out Entity king);
            king.SetBase("hp", 15);
            runtime.Interpreter.RollIntent(king);
            Assert.Equal("Broken", king.Phase);

            // Healing back above the threshold ends the phase.
            king.SetBase("hp", 40);
            runtime.Interpreter.RollIntent(king);

            Assert.Null(king.Phase);
            Assert.Equal("Chomp", king.Intent);
        }

        [Fact]
        public void An_enemy_with_no_phases_behaves_exactly_as_before()
        {
            CardRuntime runtime = Create(
                "enemy \"Dummy\"\n" +
                "  hp 30\n" +
                "  move \"Poke\":\n" +
                "    deal 1 to player\n" +
                "  pattern cycle Poke\n");
            Entity dummy = runtime.SpawnEnemy("Dummy");

            runtime.Interpreter.RollIntent(dummy);

            Assert.Equal("Poke", dummy.Intent);
            Assert.Null(dummy.Phase);
        }

        [Fact]
        public void The_phase_survives_a_save_and_load()
        {
            CardRuntime runtime = Ready(out Entity king);
            king.SetBase("hp", 15);
            runtime.Interpreter.RollIntent(king);
            Assert.Equal("Broken", king.Phase);

            GameSnapshot saved = runtime.Capture();
            king.Phase = null;
            runtime.Restore(saved);

            Assert.Equal("Broken", king.Phase);
        }

        [Fact]
        public void Two_games_that_differ_only_by_phase_do_not_hash_the_same()
        {
            CardRuntime runtime = Ready(out Entity king);
            king.SetBase("hp", 15);
            ulong before = runtime.State.ComputeHash();

            runtime.Interpreter.RollIntent(king);

            // Entering a phase changes what the enemy will do next, so it has to be part of the hash.
            Assert.NotEqual(before, runtime.State.ComputeHash());
        }

        [Fact]
        public void Content_can_read_the_phase_an_enemy_is_in()
        {
            CardRuntime runtime = Ready(out Entity king);
            king.SetBase("hp", 15);
            runtime.Interpreter.RollIntent(king);

            // A card that punishes a broken boss has to be able to ask.
            runtime.Execute("if enemy.phase == \"Broken\": gain 1 gold");

            Assert.Equal(1, runtime.Player!.GetInt("gold"));
        }

        [Fact]
        public void A_phase_line_without_a_condition_is_reported()
        {
            ContentLibrary library = ContentLibrary.FromText(
                "enemy \"Muddle\"\n" +
                "  hp 10\n" +
                "  phase Broken\n" +
                "  move \"Poke\":\n" +
                "    deal 1 to player\n",
                "res://phases.ge");

            Assert.Contains("GE0107", library.Diagnostics.ToString());
        }

        [Fact]
        public void A_move_naming_a_phase_that_does_not_exist_is_reported()
        {
            ContentLibrary library = ContentLibrary.FromText(
                "enemy \"Muddle\"\n" +
                "  hp 10\n" +
                "  phase Broken when hp <= 5\n" +
                "  move \"Poke\" phase Bronken:\n" +
                "    deal 1 to player\n",
                "res://phases.ge");

            Assert.Contains("GE0108", library.Diagnostics.ToString());
        }
    }
}
