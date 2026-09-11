using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameplayEffects.Content;
using GameplayEffects.Runtime;
using Xunit;

namespace GameplayEffects.Tests.Snapshots
{
    public class SnapshotTests
    {
        [Fact]
        public void Restored_game_continues_exactly_like_the_original()
        {
            CardRuntime original = SnapshotScenario.NewBattle(seed: 42);
            for (int i = 0; i < 6; i++) SnapshotScenario.Step(original);

            CardRuntime restored = SnapshotScenario.RoundTrip(original);
            Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());

            for (int i = 0; i < 40 && original.State.InBattle; i++)
            {
                SnapshotScenario.Step(original);
                SnapshotScenario.Step(restored);
                Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());
            }
        }

        [Fact]
        public void Pending_next_turn_and_until_blocks_survive_a_round_trip()
        {
            CardRuntime original = SnapshotScenario.NewBattle(seed: 3);
            Entity prepare = original.AddCard("Prepare", Zones.Hand);
            Entity flex = original.AddCard("Flex", Zones.Hand);
            Assert.Equal(PlayResult.Played, original.Play(flex));
            Assert.Equal(PlayResult.Played, original.Play(prepare));
            Assert.Equal(2, original.State.Scheduled.Count);

            CardRuntime restored = SnapshotScenario.RoundTrip(original);
            Assert.Equal(2, restored.State.Scheduled.Count);
            Assert.Equal(2, restored.Player!.StacksOf("Strength"));

            original.EndTurn();
            restored.EndTurn();

            Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());
            Assert.Equal(0, restored.Player!.StacksOf("Strength"));
            Assert.Equal(
                original.State.ZoneOf(original.Player, Zones.Hand).Count,
                restored.State.ZoneOf(restored.Player, Zones.Hand).Count);
        }

        [Fact]
        public void Once_per_battle_limits_are_preserved()
        {
            CardRuntime original = SnapshotScenario.NewBattle(seed: 5);
            original.AddRelic("Lizard Tail");
            original.Player!.SetBase("hp", 5);
            original.Execute("deal 10 to player");
            Assert.Equal(40, original.Player.GetInt("hp"));

            CardRuntime restored = SnapshotScenario.RoundTrip(original);
            restored.Player!.SetBase("hp", 5);
            restored.Execute("deal 10 to player");

            Assert.True(restored.Player.IsDead);
        }

        [Fact]
        public void Restoring_content_that_is_not_loaded_fails_clearly()
        {
            CardRuntime original = SnapshotScenario.NewBattle(seed: 1);
            GameSnapshot snapshot = original.Capture();

            var other = new CardRuntime(ContentLibrary.FromText("card \"Unrelated\"\n  cost 1\n"));
            var error = Assert.Throws<InvalidOperationException>(() => other.Restore(snapshot));
            Assert.Contains("not loaded", error.Message);
        }

        [Fact]
        public void Blocks_scheduled_by_ad_hoc_code_cannot_be_saved()
        {
            CardRuntime runtime = SnapshotScenario.NewBattle(seed: 1);
            runtime.Execute("next turn:\n  draw 1");

            Assert.Throws<InvalidOperationException>(() => runtime.Capture());
        }
    }

    internal static class SnapshotScenario
    {
        private static readonly Lazy<string> SampleContent = new Lazy<string>(() =>
            File.ReadAllText(Path.Combine(RepositoryRoot(), "samples", "basic", "content.ge")));

        public static CardRuntime NewBattle(ulong seed)
        {
            var runtime = new CardRuntime(ContentLibrary.FromText(SampleContent.Value, "content.ge"), new RuntimeOptions { Seed = seed });
            runtime.CreatePlayer(hp: 60);
            runtime.AddDeck("Strike", "Strike", "Strike", "Strike", "Defend", "Defend", "Defend", "Fireball", "Flame Jab", "Deadly Poison");
            runtime.AddRelic("Kindling");
            runtime.SpawnEnemy("Jaw Worm");
            runtime.ApplyStatus("Frozen", runtime.State.Actors(Team.Enemy)[0]);
            runtime.StartBattle();
            return runtime;
        }

        /// <summary>A deterministic "player": plays the first affordable card, or ends the turn.</summary>
        public static void Step(CardRuntime runtime)
        {
            if (!runtime.State.InBattle) return;

            Entity player = runtime.Player!;
            Entity? enemy = runtime.State.Actors(Team.Enemy).FirstOrDefault();
            foreach (Entity card in runtime.State.ZoneOf(player, Zones.Hand).ToArray())
            {
                if (runtime.CostOf(card) > player.GetInt("energy")) continue;
                if (runtime.Play(card, card.Definition?.Word("target") == "enemy" ? enemy : null) == PlayResult.Played) return;
            }

            runtime.EndTurn();
        }

        /// <summary>Captures, serializes to JSON, and restores into a brand new runtime with freshly loaded content.</summary>
        public static CardRuntime RoundTrip(CardRuntime original)
        {
            string json = JsonSerializer.Serialize(original.Capture());
            GameSnapshot snapshot = JsonSerializer.Deserialize<GameSnapshot>(json)!;

            var restored = new CardRuntime(ContentLibrary.FromText(SampleContent.Value, "content.ge"), new RuntimeOptions { Seed = 999 });
            restored.Restore(snapshot);
            return restored;
        }

        private static string RepositoryRoot()
        {
            for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "GameplayEffects.sln"))) return directory.FullName;
            }
            throw new InvalidOperationException("Could not find the repository root.");
        }
    }
}
