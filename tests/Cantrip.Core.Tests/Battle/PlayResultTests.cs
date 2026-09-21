#nullable enable
using System;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Battle
{
    /// <summary>
    /// One test per <see cref="PlayResult"/>. A refused play must leave no trace: no energy spent,
    /// the card still in hand, no effect and no history.
    /// </summary>
    public sealed class PlayResultTests
    {
        private const string Content = """
            card "Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 6 to target

            card "Heavy"
              cost 4
              target enemy
              effect:
                deal 20 to target

            card "Curse"
              cost 0
              tags unplayable

            card "Bandage"
              cost 1
              target ally
              effect:
                heal 3 to target

            relic "Pacifism"
              on before_card_played(tag:attack):
                cancel

            enemy "Dummy"
              hp 30
            """;

        private static (CardRuntime Runtime, Entity Player, Entity Enemy) Setup(BattleEventRecorder? recorder = null)
        {
            CardRuntime runtime = BattleKit.Create(Content, host: recorder);
            Entity player = runtime.CreatePlayer();
            Entity enemy = runtime.SpawnEnemy("Dummy");
            return (runtime, player, enemy);
        }

        private static void AssertNothingHappened(CardRuntime runtime, Entity player, Entity enemy, Entity card)
        {
            Assert.Equal(Zones.Hand, card.Zone);
            Assert.Equal(3, player.GetInt("energy"));
            Assert.Equal(30, enemy.GetInt("hp"));
            Assert.Equal(0, runtime.State.History("cards_played", player).ToInt());
        }

        [Fact]
        public void Played_pays_resolves_and_discards()
        {
            var recorder = new BattleEventRecorder();
            var (runtime, player, enemy) = Setup(recorder);
            Entity strike = runtime.AddCard("Strike", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play(strike, enemy));

            Assert.Equal(2, player.GetInt("energy"));
            Assert.Equal(24, enemy.GetInt("hp"));
            Assert.Equal(Zones.Discard, strike.Zone);
            Assert.Equal(1, runtime.State.History("cards_played", player).ToInt());
            Assert.Equal(1, runtime.State.History("attacks", player).ToInt());

            BattleRecordedEvent played = Assert.Single(recorder.Named("card_played"));
            Assert.Same(player, played.Source);
            Assert.Same(enemy, played.Target);
            Assert.Same(strike, played.Card);
            Assert.Equal(1, played.Amount);
            Assert.Contains("attack", played.Tags);
        }

        [Fact]
        public void Play_by_name_finds_the_card_in_hand()
        {
            var (runtime, _, enemy) = Setup();
            runtime.AddCard("Strike", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play("strike"));
            Assert.Equal(24, enemy.GetInt("hp"));
        }

        [Fact]
        public void NotACard_for_actors_and_removed_cards()
        {
            var (runtime, player, enemy) = Setup();
            Entity strike = runtime.AddCard("Strike", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.NotACard, runtime.Play(enemy));
            Assert.Equal(PlayResult.NotACard, runtime.Play(player));

            runtime.State.Remove(strike);
            Assert.Equal(PlayResult.NotACard, runtime.Play(strike));
            Assert.Equal(3, player.GetInt("energy"));
        }

        [Fact]
        public void NotInHand_for_cards_in_other_zones()
        {
            var (runtime, player, enemy) = Setup();
            Entity inDraw = runtime.AddCard("Strike", Zones.Draw);
            Entity inDiscard = runtime.AddCard("Strike", Zones.Discard);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.NotInHand, runtime.Play(inDraw, enemy));
            Assert.Equal(PlayResult.NotInHand, runtime.Play(inDiscard, enemy));
            Assert.Equal(PlayResult.NotInHand, runtime.Play("Strike", enemy));
            Assert.Equal(3, player.GetInt("energy"));
            Assert.Equal(30, enemy.GetInt("hp"));
        }

        [Fact]
        public void Unplayable_for_cards_tagged_unplayable()
        {
            var (runtime, player, enemy) = Setup();
            Entity curse = runtime.AddCard("Curse", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Unplayable, runtime.Play(curse));
            AssertNothingHappened(runtime, player, enemy, curse);
        }

        [Fact]
        public void NotEnoughEnergy_when_the_cost_exceeds_energy()
        {
            var (runtime, player, enemy) = Setup();
            Entity heavy = runtime.AddCard("Heavy", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.NotEnoughEnergy, runtime.Play(heavy, enemy));
            AssertNothingHappened(runtime, player, enemy, heavy);
        }

        [Fact]
        public void NotEnoughEnergy_once_energy_has_been_spent()
        {
            var (runtime, player, enemy) = Setup();
            for (int i = 0; i < 4; i++) runtime.AddCard("Strike", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            for (int i = 0; i < 3; i++) Assert.Equal(PlayResult.Played, runtime.Play("Strike", enemy));

            Assert.Equal(PlayResult.NotEnoughEnergy, runtime.Play("Strike", enemy));
            Assert.Equal(0, player.GetInt("energy"));
            Assert.Equal(12, enemy.GetInt("hp"));
            Assert.Single(BattleKit.Zone(runtime, Zones.Hand));
        }

        [Fact]
        public void InvalidTarget_for_the_wrong_side_or_non_actors()
        {
            var (runtime, player, enemy) = Setup();
            Entity strike = runtime.AddCard("Strike", Zones.Hand);
            Entity bandage = runtime.AddCard("Bandage", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.InvalidTarget, runtime.Play(strike, player));
            Assert.Equal(PlayResult.InvalidTarget, runtime.Play(strike, bandage));
            Assert.Equal(PlayResult.InvalidTarget, runtime.Play(bandage, enemy));

            AssertNothingHappened(runtime, player, enemy, strike);
            Assert.Equal(Zones.Hand, bandage.Zone);
        }

        [Fact]
        public void InvalidTarget_for_a_dead_enemy()
        {
            var (runtime, player, first) = Setup();
            Entity second = runtime.SpawnEnemy("Dummy");
            Entity strike = runtime.AddCard("Strike", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Execute("deal 100 to target", target: first);
            Assert.True(first.IsDead);
            Assert.True(runtime.State.InBattle);

            Assert.Equal(PlayResult.InvalidTarget, runtime.Play(strike, first));
            Assert.Equal(Zones.Hand, strike.Zone);
            Assert.Equal(3, player.GetInt("energy"));
            Assert.Equal(30, second.GetInt("hp"));
        }

        [Fact]
        public void InvalidTarget_when_no_enemy_is_left_to_target()
        {
            CardRuntime runtime = BattleKit.Create(Content);
            Entity player = runtime.CreatePlayer();
            Entity strike = runtime.AddCard("Strike", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.InvalidTarget, runtime.Play(strike));
            Assert.Equal(3, player.GetInt("energy"));
        }

        [Fact]
        public void Cancelled_by_a_before_card_played_listener()
        {
            var recorder = new BattleEventRecorder();
            var (runtime, player, enemy) = Setup(recorder);
            runtime.AddRelic("Pacifism");
            Entity strike = runtime.AddCard("Strike", Zones.Hand);
            Entity bandage = runtime.AddCard("Bandage", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Cancelled, runtime.Play(strike, enemy));
            AssertNothingHappened(runtime, player, enemy, strike);
            Assert.Empty(recorder.Named("card_played"));

            // The filter only stops attacks.
            Assert.Equal(PlayResult.Played, runtime.Play(bandage));
            Assert.Equal(2, player.GetInt("energy"));
        }

        [Fact]
        public void Play_rejects_a_null_card()
        {
            var (runtime, _, _) = Setup();

            Assert.Throws<ArgumentNullException>(() => runtime.Play((Entity)null!));
        }
    }
}
