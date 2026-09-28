#nullable enable
using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// Every top-level action answers with an <see cref="ActionResult"/>. Before, only
    /// <c>Play</c> did: <c>StartBattle</c>, <c>EndTurn</c> and <c>Execute</c> returned nothing and
    /// <c>UseAbility</c> returned one <c>false</c> for four different endings, so a game that
    /// checked what it was handed never learned that the rules were waiting for the player.
    /// </summary>
    public sealed class ActionReportingTests
    {
        private const string Content = """
            enemy "Dummy"
              hp 100

            card "Strike"
              cost 0
              target enemy
              effect:
                deal 6 to target

            relic "Opener"
              on battle_start:
                choose 1 from hand as picked
                exhaust picked

            relic "Sorter"
              on turn_end:
                choose 1 from hand as picked
                exhaust picked

            ability "Ponder"
              cooldown 2 turns
              effect:
                choose 1 from hand as picked
                exhaust picked

            ability "Brace"
              cooldown 2 turns
              effect:
                block 3
            """;

        private static CardRuntime Fresh(IChoiceProvider chooser, out Entity player, string? relic = null, params string[] hand)
        {
            ContentLibrary library = ContentLibrary.FromText(Content);
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 1, Chooser = chooser });
            player = runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            foreach (string card in hand) runtime.AddCard(card, Zones.Hand);
            if (relic != null) runtime.AddRelic(relic);
            return runtime;
        }

        [Fact]
        public void StartBattle_reports_a_choice_instead_of_only_setting_Pending()
        {
            CardRuntime runtime = Fresh(new DeferredChooser(), out _, "Opener", "Strike", "Strike");

            ActionResult started = runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(ActionResult.ChoicePending, started);
            Assert.NotNull(runtime.Pending);

            Assert.Equal(ActionResult.Played, runtime.Answer(runtime.Pending!.Options[0].Id));
            Assert.True(runtime.State.InBattle);
        }

        [Fact]
        public void EndTurn_reports_a_choice_the_next_turn_asks_for()
        {
            CardRuntime runtime = Fresh(new DeferredChooser(), out _, "Sorter", "Strike", "Strike");
            Assert.Equal(ActionResult.Played, runtime.StartBattle(shuffle: false, drawOpeningHand: false));

            // The relic asks as the player's turn ends, inside EndTurn rather than inside Play.
            ActionResult ended = runtime.EndTurn();

            Assert.Equal(ActionResult.ChoicePending, ended);
            Assert.NotNull(runtime.Pending);
            Assert.Equal(ActionResult.Played, runtime.Answer(runtime.Pending!.Options[0].Id));
            Assert.Null(runtime.Pending);
        }

        [Fact]
        public void EndTurn_says_it_finished_when_nothing_asks()
        {
            CardRuntime runtime = Fresh(new DeferredChooser(), out _, null, "Strike");
            Assert.Equal(ActionResult.Played, runtime.StartBattle(shuffle: false, drawOpeningHand: false));

            Assert.Equal(ActionResult.Played, runtime.EndTurn());
            Assert.Null(runtime.Pending);
        }

        [Fact]
        public void Execute_reports_a_choice_its_statements_ask_for()
        {
            CardRuntime runtime = Fresh(new DeferredChooser(), out _, null, "Strike", "Strike");
            Assert.Equal(ActionResult.Played, runtime.StartBattle(shuffle: false, drawOpeningHand: false));

            ActionResult ran = runtime.Execute("choose 1 from hand as picked\nexhaust picked");

            Assert.Equal(ActionResult.ChoicePending, ran);
            Assert.Equal(ActionResult.Played, runtime.Answer(runtime.Pending!.Options[0].Id));
            Assert.Single(runtime.State.ZoneOf(runtime.Player, Zones.Exhaust));
        }

        [Fact]
        public void Execute_says_it_finished_when_nothing_asks()
        {
            CardRuntime runtime = Fresh(new DeferredChooser(), out _, null, "Strike");
            Assert.Equal(ActionResult.Played, runtime.StartBattle(shuffle: false, drawOpeningHand: false));

            Assert.Equal(ActionResult.Played, runtime.Execute("block 4"));
        }

        /// <summary>
        /// The four endings <c>UseAbility</c> used to answer with one <c>false</c>. "Pending" is the
        /// one that mattered: a game could not tell a cooldown from a question it had to answer.
        /// </summary>
        [Fact]
        public void UseAbility_tells_played_from_not_ready_from_pending_from_gone()
        {
            CardRuntime runtime = Fresh(new DeferredChooser(), out Entity player, null, "Strike", "Strike");
            Assert.Equal(ActionResult.Played, runtime.StartBattle(shuffle: false, drawOpeningHand: false));

            Entity brace = runtime.GrantAbility("Brace", player);
            Assert.Equal(ActionResult.Played, runtime.UseAbility(brace));
            Assert.Equal(ActionResult.NotReady, runtime.UseAbility(brace));

            Entity ponder = runtime.GrantAbility("Ponder", player);
            Assert.Equal(ActionResult.ChoicePending, runtime.UseAbility(ponder));
            Assert.NotNull(runtime.Pending);
            Assert.Equal(ActionResult.Played, runtime.Answer(runtime.Pending!.Options[0].Id));

            runtime.State.Remove(brace);
            Assert.Equal(ActionResult.NotACard, runtime.UseAbility(brace));
        }

        /// <summary>Without a deferred chooser the same calls still answer, and never say pending.</summary>
        [Fact]
        public void Every_action_answers_played_under_a_chooser_that_decides_on_the_spot()
        {
            CardRuntime runtime = Fresh(new FirstOptionChooser(), out Entity player, "Sorter", "Strike", "Strike");

            Assert.Equal(ActionResult.Played, runtime.StartBattle(shuffle: false, drawOpeningHand: false));
            Assert.Equal(ActionResult.Played, runtime.Execute("block 1"));
            Assert.Equal(ActionResult.Played, runtime.UseAbility(runtime.GrantAbility("Brace", player)));
            Assert.Equal(ActionResult.Played, runtime.EndTurn());
            Assert.Null(runtime.Pending);
        }
    }
}
