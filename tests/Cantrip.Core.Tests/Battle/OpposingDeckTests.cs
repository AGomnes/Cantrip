using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Battle
{
    /// <summary>
    /// An enemy that plays cards from a hand of its own. Nothing in the engine ties cards to the
    /// player: a card belongs to whoever controls it, and targeting is resolved against that
    /// controller's opposing side. A game whose opponent draws from a deck decides what it plays
    /// and calls <see cref="CardRuntime.Play(Entity, Entity?)"/> for it, as it would for the player.
    /// What is missing for a two-player game is the priority window, not the deck.
    /// </summary>
    public sealed class OpposingDeckTests
    {
        private const string Content = """
            card "Slash"
              cost 0
              target enemy
              tags attack
              effect:
                deal 7 to target

            enemy "Duellist"
              hp 40
              move Wait:
                block 1
              pattern cycle Wait
            """;

        private static CardRuntime Start(out Entity player, out Entity duellist)
        {
            var library = new ContentLibrary();
            library.LoadText(Content, "content.cantrip");
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 5 });
            player = runtime.CreatePlayer(hp: 50);
            duellist = runtime.SpawnEnemy("Duellist");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            return runtime;
        }

        [Fact]
        public void An_enemy_plays_a_card_from_its_own_hand_at_the_player()
        {
            CardRuntime runtime = Start(out Entity player, out Entity duellist);
            Entity slash = runtime.AddCard("Slash", Zones.Hand, owner: duellist);

            Assert.Equal(duellist, slash.Controller);
            Assert.True(runtime.CanPlay(slash));

            // The card names no target, so the runtime picks from the controller's opposing side.
            Assert.Equal(PlayResult.Played, runtime.Play(slash));

            Assert.Equal(43, player.GetInt("hp"));
            Assert.Equal(40, duellist.GetInt("hp"));   // unhurt: its own card hit the player
            Assert.Equal(Zones.Discard, slash.Zone);
        }

        [Fact]
        public void The_player_is_the_only_legal_target_of_an_enemys_card()
        {
            CardRuntime runtime = Start(out Entity player, out Entity duellist);
            Entity slash = runtime.AddCard("Slash", Zones.Hand, owner: duellist);

            Assert.Equal(new[] { player }, runtime.LegalTargets(slash));
        }

        [Fact]
        public void An_enemys_hand_and_draw_pile_are_its_own()
        {
            CardRuntime runtime = Start(out Entity player, out Entity duellist);
            runtime.AddCard("Slash", Zones.Draw, owner: duellist);
            runtime.AddCard("Slash", Zones.Hand, owner: player);

            Assert.Single(runtime.State.ZoneOf(duellist, Zones.Draw));
            Assert.Empty(runtime.State.ZoneOf(player, Zones.Draw));
            Assert.Single(runtime.State.ZoneOf(player, Zones.Hand));
            Assert.Empty(runtime.State.ZoneOf(duellist, Zones.Hand));
        }

        [Fact]
        public void An_enemy_draws_its_own_card()
        {
            CardRuntime runtime = Start(out Entity player, out Entity duellist);
            runtime.AddCard("Slash", Zones.Draw, owner: duellist);

            runtime.Execute("draw 1 to source", duellist);

            Assert.Single(runtime.State.ZoneOf(duellist, Zones.Hand));
        }
    }
}
