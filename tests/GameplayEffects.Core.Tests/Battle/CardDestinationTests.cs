#nullable enable
using GameplayEffects.Runtime;
using Xunit;

namespace GameplayEffects.Tests.Battle
{
    /// <summary>
    /// Where cards go after they are played or when the turn ends: discard, exhaust, powers, retain
    /// and ethereal. The ruleset draws no cards per turn here, so a zone only holds what the test put
    /// there.
    /// </summary>
    public sealed class CardDestinationTests
    {
        private const string Content = """
            card "Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 6 to target

            card "Burst"
              cost 1
              tags skill, exhaust
              effect:
                block 3

            card "Demon Form"
              cost 1
              tags power
              on turn_start:
                gain 2 Strength

            card "Keepsake"
              cost 1
              tags retain
              effect:
                block 1

            card "Apparition"
              cost 1
              tags ethereal
              effect:
                block 1

            card "Wait"
              cost 0
              effect:
                block 1

            card "Self Exile"
              cost 0
              effect:
                exhaust self

            keyword "Exhaust"
              tags keyword

            keyword "Retain"
              tags keyword

            status "Strength"
              tags buff
              stacking intensity
              modify damage: +stacks

            relic "Counter"
              on exhausted:
                gain 1 gold

            enemy "Dummy"
              hp 100
              move "Idle":
                block 1
            """;

        private static CardRuntime Setup(out Entity player, out Entity enemy, BattleEventRecorder? recorder = null)
        {
            CardRuntime runtime = BattleKit.Create(Content, rules: new Ruleset { HandSize = 0 }, host: recorder);
            player = runtime.CreatePlayer();
            enemy = runtime.SpawnEnemy("Dummy");
            runtime.AddRelic("Counter");
            return runtime;
        }

        [Fact]
        public void Played_cards_go_to_the_discard_pile()
        {
            CardRuntime runtime = Setup(out _, out Entity enemy);
            Entity strike = runtime.AddCard("Strike", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Play(strike, enemy);

            Assert.Equal(Zones.Discard, strike.Zone);
            Assert.Equal(new[] { "Strike" }, BattleKit.ZoneNames(runtime, Zones.Discard));
            Assert.Empty(BattleKit.Zone(runtime, Zones.Play));
        }

        [Fact]
        public void Exhaust_cards_go_to_the_exhaust_pile_and_raise_exhausted()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = Setup(out Entity player, out _, recorder);
            Entity burst = runtime.AddCard("Burst", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play(burst));

            Assert.Equal(Zones.Exhaust, burst.Zone);
            Assert.Empty(BattleKit.Zone(runtime, Zones.Discard));
            Assert.Equal(1, player.GetInt("gold"));
            Assert.Equal(1, runtime.State.History("cards_exhausted", player).ToInt());

            BattleRecordedEvent exhausted = Assert.Single(recorder.Named("exhausted"));
            Assert.Same(burst, exhausted.Target);
            Assert.Equal(Zones.Play, exhausted.DataText("from"));
            Assert.Equal(Zones.Exhaust, exhausted.DataText("to"));
        }

        [Fact]
        public void An_exhaust_keyword_attached_to_a_card_exhausts_it()
        {
            CardRuntime runtime = Setup(out _, out Entity enemy);
            Entity strike = runtime.AddCard("Strike", Zones.Hand);
            runtime.ApplyStatus("Exhaust", strike);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Play(strike, enemy);

            Assert.Equal(Zones.Exhaust, strike.Zone);
        }

        [Fact]
        public void A_card_that_moves_itself_during_its_effect_stays_where_it_went()
        {
            CardRuntime runtime = Setup(out Entity player, out _);
            Entity exile = runtime.AddCard("Self Exile", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play(exile));

            Assert.Equal(Zones.Exhaust, exile.Zone);
            Assert.Empty(BattleKit.Zone(runtime, Zones.Discard));
            Assert.Equal(1, player.GetInt("gold"));
        }

        [Fact]
        public void Powers_stay_in_the_powers_zone_and_keep_listening()
        {
            CardRuntime runtime = Setup(out Entity player, out _);
            Entity form = runtime.AddCard("Demon Form", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play(form));

            Assert.Equal(Zones.Powers, form.Zone);
            Assert.True(runtime.State.IsActive(form));
            Assert.Single(runtime.State.Events.OwnedBy(form));

            runtime.EndTurn();
            Assert.Equal(2, player.StacksOf("Strength"));
            Assert.Equal(Zones.Powers, form.Zone);

            runtime.EndTurn();
            Assert.Equal(4, player.StacksOf("Strength"));
        }

        [Fact]
        public void Cards_in_the_discard_pile_do_not_listen()
        {
            CardRuntime runtime = Setup(out Entity player, out _);
            Entity form = runtime.AddCard("Demon Form", Zones.Discard);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.False(runtime.State.IsActive(form));
            runtime.EndTurn();

            Assert.Equal(0, player.StacksOf("Strength"));
        }

        [Fact]
        public void Retained_cards_stay_in_hand_at_the_end_of_the_turn()
        {
            CardRuntime runtime = Setup(out _, out _);
            Entity keepsake = runtime.AddCard("Keepsake", Zones.Hand);
            Entity wait = runtime.AddCard("Wait", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.EndTurn();

            Assert.Equal(Zones.Hand, keepsake.Zone);
            Assert.Equal(Zones.Discard, wait.Zone);
        }

        [Fact]
        public void A_retain_keyword_keeps_a_card_in_hand()
        {
            CardRuntime runtime = Setup(out _, out _);
            Entity wait = runtime.AddCard("Wait", Zones.Hand);
            runtime.ApplyStatus("Retain", wait);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.EndTurn();

            Assert.Equal(Zones.Hand, wait.Zone);
        }

        [Fact]
        public void Ethereal_cards_left_in_hand_are_exhausted_at_the_end_of_the_turn()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = Setup(out Entity player, out _, recorder);
            Entity apparition = runtime.AddCard("Apparition", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.EndTurn();

            Assert.Equal(Zones.Exhaust, apparition.Zone);
            Assert.Equal(1, player.GetInt("gold"));
            BattleRecordedEvent exhausted = Assert.Single(recorder.Named("exhausted"));
            Assert.Equal(Zones.Hand, exhausted.DataText("from"));
        }

        [Fact]
        public void Ethereal_cards_that_are_played_are_discarded_normally()
        {
            CardRuntime runtime = Setup(out Entity player, out _);
            Entity apparition = runtime.AddCard("Apparition", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Play(apparition);

            Assert.Equal(Zones.Discard, apparition.Zone);
            Assert.Equal(0, player.GetInt("gold"));
        }
    }
}
