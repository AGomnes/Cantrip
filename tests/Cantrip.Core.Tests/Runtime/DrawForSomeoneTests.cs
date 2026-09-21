using Cantrip.Runtime;
using Xunit;
using static Cantrip.Tests.Runtime.RuntimeTestKit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// Naming who draws. A creature controls itself, so without this a creature could not draw for
    /// the player at all.
    /// </summary>
    public sealed class DrawForSomeoneTests
    {
        private const string Content =
            "actor \"Giver\"\n" +
            "  hp 1\n" +
            "  attack 1\n" +
            "  on self.died:\n" +
            "    draw 1 to player\n" +
            "\n" +
            "card \"Scrap\"\n" +
            "  cost 0\n" +
            "  effect:\n" +
            "    gain 1 gold\n" +
            "\n" +
            "card \"Study\"\n" +
            "  cost 0\n" +
            "  effect:\n" +
            "    draw 1\n";

        [Fact]
        public void Something_that_is_not_the_player_can_draw_for_the_player()
        {
            CardRuntime runtime = Create(Content);
            Entity scrap = runtime.AddCard("Scrap", Zones.Draw);
            Enemy(runtime);
            Start(runtime);

            // An ally of the player's, as a creature that draws for you would be. The first
            // statement binds `created` for the second.
            runtime.Execute("create Giver\nkill created");

            Assert.Equal(Zones.Hand, scrap.Zone);
        }

        [Fact]
        public void A_bare_draw_still_draws_for_the_controller()
        {
            CardRuntime runtime = Create(Content);
            Entity scrap = runtime.AddCard("Scrap", Zones.Draw);
            Enemy(runtime);
            Start(runtime);

            runtime.Play(runtime.AddCard("Study", Zones.Hand), null);

            Assert.Equal(Zones.Hand, scrap.Zone);
        }

        /// <summary>
        /// Why the test above kills an ally rather than the last enemy. Winning a battle sweeps the
        /// hand back into the draw pile, so a card drawn on the way to that win is found where it
        /// started — which reads exactly like a draw that never happened.
        /// </summary>
        [Fact]
        public void Winning_a_battle_puts_the_hand_back_in_the_draw_pile()
        {
            CardRuntime runtime = Create(Content);
            Entity scrap = runtime.AddCard("Scrap", Zones.Draw);
            Enemy(runtime, 5);
            Start(runtime);

            runtime.Execute("create Giver\nkill created");
            Assert.Equal(Zones.Hand, scrap.Zone);

            runtime.Execute("deal 10 to enemy");

            Assert.True(runtime.Won);
            Assert.Equal(Zones.Draw, scrap.Zone);
        }
    }
}
