#nullable enable
using System.Collections.Generic;
using System.Linq;
using GameplayEffects.Content;
using GameplayEffects.Runtime;
using Xunit;

namespace GameplayEffects.Tests.Battle
{
    /// <summary>
    /// The same content, seed and inputs must produce the same game, step for step. These tests
    /// play many short battles with random (but seeded) decisions against the sample content, so
    /// they also shake out crashes in combinations nobody wrote a test for.
    /// </summary>
    public sealed class DeterminismTests
    {
        private static readonly string[] Deck =
        {
            "Strike", "Strike", "Strike", "Defend", "Defend", "Fireball", "Flame Jab", "Deadly Poison", "Flex", "Prepare", "Shatter",
        };

        [Fact]
        public void Random_battles_replay_exactly_and_never_throw()
        {
            ContentLibrary content = BattleKit.LoadSamples();

            for (ulong seed = 1; seed <= 100; seed++)
            {
                ulong[] first = Simulate(content, seed);
                ulong[] second = Simulate(content, seed);
                Assert.True(first.SequenceEqual(second), $"seed {seed} diverged");
            }
        }

        [Fact]
        public void Different_seeds_play_different_games()
        {
            ContentLibrary content = BattleKit.LoadSamples();

            Assert.False(Simulate(content, 1).SequenceEqual(Simulate(content, 2)));
        }

        /// <summary>Plays up to 60 decisions and returns the state hash after each one.</summary>
        private static ulong[] Simulate(ContentLibrary content, ulong seed)
        {
            var runtime = new CardRuntime(content, new RuntimeOptions { Seed = seed, Chooser = new RandomChooser(seed ^ 0x5EEDUL) });
            var decisions = new Rng(seed * 7919UL);

            Entity player = runtime.CreatePlayer(hp: 60);
            runtime.AddDeck(Deck);
            runtime.AddRelic("Kindling");
            runtime.AddRelic("Pyromancer's Codex");
            runtime.AddRelic("Echo Chamber");
            runtime.AddRelic("Lizard Tail");

            Entity worm = runtime.SpawnEnemy("Jaw Worm");
            if (decisions.Chance(50)) runtime.ApplyStatus("Frozen", worm);
            if (decisions.Chance(50)) runtime.SpawnEnemy("Jaw Worm", hp: 20);

            runtime.StartBattle();
            var hashes = new List<ulong> { runtime.State.ComputeHash() };

            for (int step = 0; step < 60 && runtime.State.InBattle; step++)
            {
                List<Entity> playable = runtime.State.ZoneOf(player, Zones.Hand)
                    .Where(card => runtime.CostOf(card) <= player.GetInt("energy"))
                    .ToList();

                if (playable.Count == 0 || decisions.Chance(25))
                {
                    runtime.EndTurn();
                }
                else
                {
                    Entity card = decisions.Pick(playable);
                    IReadOnlyList<Entity> enemies = runtime.State.Actors(Team.Enemy);
                    Entity? target = card.Definition?.Word("target") == "enemy" && enemies.Count > 0 ? decisions.Pick(enemies) : null;
                    runtime.Play(card, target);
                }

                hashes.Add(runtime.State.ComputeHash());
            }

            return hashes.ToArray();
        }
    }
}
