#nullable enable
using System.Linq;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Battle
{
    /// <summary>Drawing from the top, reshuffling the discard pile, and the hand size limit.</summary>
    public sealed class DrawTests
    {
        private static readonly string Content = BattleKit.PlainCards(12) + BattleKit.PlainCards(3, "Keep").Replace("  cost 1\n", "  cost 1\n  tags retain\n") + """
            card "Curse"
              cost 0
              tags curse, unplayable

            relic "Shuffle Counter"
              on shuffled:
                gain 1 gold

            relic "Warding Charm"
              on before_drawn(tag:curse):
                cancel
            """;

        private static CardRuntime Setup(out Entity player, BattleEventRecorder? recorder = null, Ruleset? rules = null, ulong seed = 1)
        {
            CardRuntime runtime = BattleKit.Create(Content, seed, rules: rules, host: recorder);
            player = runtime.CreatePlayer();
            runtime.AddRelic("Shuffle Counter");
            return runtime;
        }

        [Fact]
        public void Draw_takes_cards_from_the_top_of_the_draw_pile()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = Setup(out Entity player, recorder);
            runtime.AddDeck(BattleKit.CardNames(10));
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Execute("draw 3");

            Assert.Equal(new[] { "Card0", "Card1", "Card2" }, BattleKit.ZoneNames(runtime, Zones.Hand));
            Assert.Equal(7, BattleKit.Zone(runtime, Zones.Draw).Count);
            Assert.Equal(3, runtime.State.History("cards_drawn", player).ToInt());

            var drawn = recorder.Named("drawn");
            Assert.Equal(3, drawn.Count);
            Assert.All(drawn, e => Assert.Same(player, e.Source));
            Assert.All(drawn, e => Assert.Same(e.Target, e.Card));
            Assert.Equal(new[] { "Card0", "Card1", "Card2" }, drawn.Select(e => e.Target!.Name));
        }

        [Fact]
        public void An_empty_draw_pile_is_refilled_by_shuffling_the_discard_pile()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = Setup(out Entity player, recorder);
            runtime.AddCard("Card0", Zones.Draw);
            runtime.AddCard("Card1", Zones.Draw);
            foreach (string name in new[] { "Card2", "Card3", "Card4" }) runtime.AddCard(name, Zones.Discard);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Execute("draw 4");

            string[] hand = BattleKit.ZoneNames(runtime, Zones.Hand);
            Assert.Equal(4, hand.Length);
            Assert.Equal(new[] { "Card0", "Card1" }, hand.Take(2));
            Assert.Empty(BattleKit.Zone(runtime, Zones.Discard));
            Assert.Single(BattleKit.Zone(runtime, Zones.Draw));
            Assert.Equal(
                new[] { "Card2", "Card3", "Card4" },
                hand.Skip(2).Concat(BattleKit.ZoneNames(runtime, Zones.Draw)).OrderBy(n => n, System.StringComparer.Ordinal));

            Assert.Single(recorder.Named("shuffled"));
            Assert.Equal(1, player.GetInt("gold"));
        }

        [Fact]
        public void The_reshuffle_order_follows_the_seed()
        {
            string[] Reshuffled(ulong seed)
            {
                CardRuntime runtime = Setup(out _, seed: seed);
                runtime.AddDeck();
                foreach (string name in BattleKit.CardNames(10)) runtime.AddCard(name, Zones.Discard);
                runtime.StartBattle(shuffle: false, drawOpeningHand: false);
                runtime.Execute("draw 10");
                return BattleKit.ZoneNames(runtime, Zones.Hand);
            }

            Assert.Equal(Reshuffled(11), Reshuffled(11));
            Assert.NotEqual(Reshuffled(11), Reshuffled(12));
        }

        [Fact]
        public void Drawing_with_nothing_left_anywhere_draws_nothing()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = Setup(out Entity player, recorder);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Execute("draw 3");

            Assert.Empty(BattleKit.Zone(runtime, Zones.Hand));
            Assert.Empty(recorder.Named("shuffled"));
            Assert.Equal(0, player.GetInt("gold"));
        }

        [Fact]
        public void Cards_drawn_with_a_full_hand_go_to_the_discard_pile()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = Setup(out _, recorder);
            foreach (string name in BattleKit.CardNames(9)) runtime.AddCard(name, Zones.Hand);
            runtime.AddCard("Card9", Zones.Draw);
            runtime.AddCard("Card10", Zones.Draw);
            runtime.AddCard("Card11", Zones.Draw);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Execute("draw 3");

            Assert.Equal(runtime.State.Rules.MaxHandSize, BattleKit.Zone(runtime, Zones.Hand).Count);
            Assert.Equal("Card9", BattleKit.Zone(runtime, Zones.Hand).Last().Name);
            // The cards that did not fit are still drawn, and land in the discard pile.
            Assert.Empty(BattleKit.Zone(runtime, Zones.Draw));
            Assert.Equal(new[] { "Card10", "Card11" }, BattleKit.ZoneNames(runtime, Zones.Discard));
            Assert.Equal(new[] { "Card10", "Card11" }, recorder.Named("discarded").Select(e => e.Target!.Name));
        }

        [Fact]
        public void The_turn_draw_respects_retained_cards_and_the_maximum_hand_size()
        {
            CardRuntime runtime = Setup(out _, rules: new Ruleset { MaxHandSize = 6 });
            runtime.AddDeck(BattleKit.CardNames(10));
            foreach (string name in BattleKit.CardNames(3, "Keep")) runtime.AddCard(name, Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.EndTurn();

            string[] hand = BattleKit.ZoneNames(runtime, Zones.Hand);
            Assert.Equal(new[] { "Keep0", "Keep1", "Keep2", "Card0", "Card1", "Card2" }, hand);
            Assert.Equal(new[] { "Card3", "Card4" }, BattleKit.ZoneNames(runtime, Zones.Discard));
            Assert.Equal(5, BattleKit.Zone(runtime, Zones.Draw).Count);
        }

        [Fact]
        public void A_cancelled_draw_stops_drawing_and_leaves_the_card_on_top()
        {
            CardRuntime runtime = Setup(out _);
            runtime.AddRelic("Warding Charm");
            runtime.AddCard("Curse", Zones.Draw);
            runtime.AddCard("Card0", Zones.Draw);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Execute("draw 2");

            Assert.Empty(BattleKit.Zone(runtime, Zones.Hand));
            Assert.Equal(new[] { "Curse", "Card0" }, BattleKit.ZoneNames(runtime, Zones.Draw));
        }
    }
}
