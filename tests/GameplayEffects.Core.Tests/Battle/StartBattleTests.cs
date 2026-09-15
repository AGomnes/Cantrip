#nullable enable
using System;
using System.Linq;
using GameplayEffects.Runtime;
using Xunit;

namespace GameplayEffects.Tests.Battle
{
    /// <summary>StartBattle: the seeded shuffle, the opening hand, battle_start and the state a battle begins in.</summary>
    public sealed class StartBattleTests
    {
        private static readonly string Content = BattleKit.PlainCards(10) + """
            enemy "Dummy"
              hp 20
              move "Wait":
                block 1

            relic "Scout's Map"
              on battle_start:
                draw 2
            """;

        private static string[] ShuffledDrawPile(ulong seed)
        {
            CardRuntime runtime = BattleKit.Create(Content, seed);
            runtime.CreatePlayer();
            runtime.AddDeck(BattleKit.CardNames(10));
            runtime.StartBattle(shuffle: true, drawOpeningHand: false);
            return BattleKit.ZoneNames(runtime, Zones.Draw);
        }

        [Fact]
        public void The_same_seed_always_shuffles_the_same_way()
        {
            string[] first = ShuffledDrawPile(1);

            Assert.Equal(first, ShuffledDrawPile(1));
            Assert.Equal(BattleKit.CardNames(10), first.OrderBy(n => n, StringComparer.Ordinal));
            Assert.NotEqual(BattleKit.CardNames(10), first);
        }

        [Fact]
        public void Different_seeds_shuffle_differently()
        {
            string[] one = ShuffledDrawPile(1);

            Assert.NotEqual(one, ShuffledDrawPile(2));
            Assert.NotEqual(one, ShuffledDrawPile(3));
        }

        [Fact]
        public void Without_a_shuffle_the_first_card_added_is_drawn_first()
        {
            CardRuntime runtime = BattleKit.Create(Content);
            runtime.CreatePlayer();
            runtime.AddDeck(BattleKit.CardNames(10));

            runtime.StartBattle(shuffle: false);

            Assert.Equal(new[] { "Card0", "Card1", "Card2", "Card3", "Card4" }, BattleKit.ZoneNames(runtime, Zones.Hand));
            Assert.Equal(new[] { "Card5", "Card6", "Card7", "Card8", "Card9" }, BattleKit.ZoneNames(runtime, Zones.Draw));
        }

        [Fact]
        public void The_opening_hand_is_the_top_of_the_shuffled_pile()
        {
            string[] shuffled = ShuffledDrawPile(7);

            CardRuntime runtime = BattleKit.Create(Content, seed: 7);
            runtime.CreatePlayer();
            runtime.AddDeck(BattleKit.CardNames(10));
            runtime.StartBattle();

            Assert.Equal(shuffled.Take(5), BattleKit.ZoneNames(runtime, Zones.Hand));
            Assert.Equal(shuffled.Skip(5), BattleKit.ZoneNames(runtime, Zones.Draw));
        }

        [Fact]
        public void The_ruleset_hand_size_sets_the_opening_hand()
        {
            CardRuntime runtime = BattleKit.Create(Content + "\nruleset\n  hand_size 3\n");
            runtime.CreatePlayer();
            runtime.AddDeck(BattleKit.CardNames(10));

            runtime.StartBattle();

            Assert.Equal(3, BattleKit.Zone(runtime, Zones.Hand).Count);
            Assert.Equal(7, BattleKit.Zone(runtime, Zones.Draw).Count);
        }

        [Fact]
        public void Skipping_the_opening_hand_skips_only_the_first_draw()
        {
            CardRuntime runtime = BattleKit.Create(Content);
            runtime.CreatePlayer();
            runtime.AddDeck(BattleKit.CardNames(10));

            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            Assert.Empty(BattleKit.Zone(runtime, Zones.Hand));

            runtime.EndTurn();
            Assert.Equal(5, BattleKit.Zone(runtime, Zones.Hand).Count);
        }

        [Fact]
        public void Battle_start_listeners_resolve_before_the_first_turn_begins()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = BattleKit.Create(Content, host: recorder);
            runtime.CreatePlayer();
            runtime.AddDeck(BattleKit.CardNames(10));
            runtime.AddRelic("Scout's Map");
            recorder.Clear();

            runtime.StartBattle(shuffle: false);

            // Two cards from the relic, then the normal five.
            Assert.Equal(7, BattleKit.Zone(runtime, Zones.Hand).Count);

            var names = recorder.Names();
            Assert.Equal("battle_start", names[0]);
            int turnStart = names.IndexOf("turn_start");
            Assert.True(turnStart > 0);
            Assert.Equal(2, names.Take(turnStart).Count(n => n == "drawn"));
            Assert.Equal(5, names.Skip(turnStart).Count(n => n == "drawn"));
            Assert.Single(recorder.Named("battle_start"));
        }

        [Fact]
        public void A_battle_begins_on_the_players_first_turn_with_resources_reset()
        {
            CardRuntime runtime = BattleKit.Create(Content);
            Entity player = runtime.CreatePlayer();
            Entity enemy = runtime.SpawnEnemy("Dummy");
            player.SetBase("energy", 0);
            player.SetBase("block", 4);
            Assert.Null(enemy.Intent);

            runtime.StartBattle();

            Assert.True(runtime.State.InBattle);
            Assert.Null(runtime.Won);
            Assert.Equal(1, runtime.State.Turn);
            Assert.Equal(1, runtime.State.BattleNumber);
            Assert.Equal(Team.Player, runtime.State.ActiveTeam);
            Assert.Equal(0, runtime.State.Clock.Now);
            Assert.Equal(3, player.GetInt("energy"));
            Assert.Equal(0, player.GetInt("block"));
            Assert.Equal("Wait", enemy.Intent);
        }

        [Fact]
        public void StartBattle_needs_a_player()
        {
            CardRuntime runtime = BattleKit.Create(Content);

            Assert.Throws<InvalidOperationException>(() => runtime.StartBattle());
        }
    }
}
