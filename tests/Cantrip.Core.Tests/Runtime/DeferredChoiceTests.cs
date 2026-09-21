using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// Choices a UI answers. An action that asks for a decision nobody has made rolls
    /// back, reports it, and replays exactly once it is answered.
    /// </summary>
    public sealed class DeferredChoiceTests
    {
        private const string Content = """
            enemy "Dummy"
              hp 100

            card "Strike"
              cost 0
              target enemy
              effect:
                deal 6 to target

            card "Recycle"
              cost 0
              effect:
                choose 1 from hand as picked
                exhaust picked
                energy += 1

            card "Twice"
              cost 0
              effect:
                choose 1 from hand as first
                exhaust first
                choose 1 from hand as second
                exhaust second
            """;

        private sealed class RecordingHost : EffectHostBase
        {
            public List<string> Events { get; } = new List<string>();

            public override void OnEvent(GameEvent gameEvent) => Events.Add(gameEvent.Name);
        }

        private static CardRuntime Start(IChoiceProvider chooser, out RecordingHost host, params string[] hand)
        {
            ContentLibrary library = ContentLibrary.FromText(Content);
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            host = new RecordingHost();
            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 1, Chooser = chooser, Host = host });
            runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            foreach (string card in hand) runtime.AddCard(card, Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            host.Events.Clear();
            return runtime;
        }

        [Fact]
        public void An_unanswered_choice_rolls_the_action_back_and_reports_it()
        {
            var chooser = new DeferredChooser();
            CardRuntime runtime = Start(chooser, out RecordingHost host, "Recycle", "Strike", "Strike");
            ulong before = runtime.State.ComputeHash();

            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Recycle"));

            PendingChoice pending = Assert.IsType<PendingChoice>(runtime.Pending);
            Assert.Equal(1, pending.Min);
            Assert.Equal(1, pending.Max);
            Assert.Equal(new[] { "Strike", "Strike" }, pending.Options.Select(o => o.Name));
            Assert.Equal(runtime.Player, pending.Chooser);

            // Nothing happened: same state, and the game was never told about any event.
            Assert.Equal(before, runtime.State.ComputeHash());
            Assert.Equal(3, runtime.State.ZoneOf(runtime.Player, Zones.Hand).Count);
            Assert.Empty(host.Events);
        }

        [Fact]
        public void Answering_replays_the_action_with_the_answer()
        {
            var chooser = new DeferredChooser();
            CardRuntime runtime = Start(chooser, out RecordingHost host, "Recycle", "Strike", "Strike");

            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Recycle"));
            Entity chosen = runtime.Pending!.Options[1];

            Assert.Equal(PlayResult.Played, runtime.Answer(chosen.Id));
            Assert.Null(runtime.Pending);

            Assert.Equal(Zones.Exhaust, chosen.Zone);
            Assert.Equal(4, runtime.Player!.GetInt("energy"));
            Assert.Contains("card_played", host.Events);
            Assert.Contains("exhausted", host.Events);
        }

        [Fact]
        public void Two_choices_in_one_action_are_answered_one_after_another()
        {
            var chooser = new DeferredChooser();
            CardRuntime runtime = Start(chooser, out _, "Twice", "Strike", "Strike", "Strike");

            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Twice"));
            Entity first = runtime.Pending!.Options[0];

            // Rolled back and replayed: the first answer applied, so the second choice offers the rest.
            Assert.Equal(PlayResult.ChoicePending, runtime.Answer(first.Id));
            Assert.Equal(2, runtime.Pending!.Options.Count);

            Entity second = runtime.Pending.Options[1];
            Assert.Equal(PlayResult.Played, runtime.Answer(second.Id));

            var exhausted = runtime.State.ZoneOf(runtime.Player, Zones.Exhaust).Select(e => e.Id).ToList();
            Assert.Equal(2, exhausted.Count);
            Assert.Contains(first.Id, exhausted);
            Assert.Contains(second.Id, exhausted);
        }

        [Fact]
        public void Choosing_a_target_goes_through_the_same_mechanism()
        {
            var chooser = new DeferredChooser();
            CardRuntime runtime = Start(chooser, out _, "Strike");
            Entity second = runtime.SpawnEnemy("Dummy", hp: 50);

            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Strike"));
            Assert.Equal(2, runtime.Pending!.Options.Count);

            Assert.Equal(PlayResult.Played, runtime.Answer(second.Id));
            Assert.Equal(44, second.GetInt("hp"));
        }

        [Fact]
        public void The_replay_lands_exactly_where_answering_directly_would_have()
        {
            var deferred = new DeferredChooser();
            CardRuntime deferredRun = Start(deferred, out _, "Recycle", "Strike", "Strike");
            Assert.Equal(PlayResult.ChoicePending, deferredRun.Play("Recycle"));
            int chosenId = deferredRun.Pending!.Options[0].Id;   // what ScriptedChooser("Strike") picks too
            Assert.Equal(PlayResult.Played, deferredRun.Answer(chosenId));

            // The same game, answered on the spot by a scripted chooser.
            CardRuntime direct = Start(new ScriptedChooser("Strike"), out _, "Recycle", "Strike", "Strike");
            Assert.Equal(PlayResult.Played, direct.Play("Recycle"));

            Assert.Equal(direct.State.ComputeHash(), deferredRun.State.ComputeHash());
        }

        [Fact]
        public void Cancelling_leaves_the_game_as_it_was()
        {
            var chooser = new DeferredChooser();
            CardRuntime runtime = Start(chooser, out _, "Recycle", "Strike");
            ulong before = runtime.State.ComputeHash();

            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Recycle"));
            runtime.CancelPending();

            Assert.Null(runtime.Pending);
            Assert.Equal(before, runtime.State.ComputeHash());
            Assert.Equal(0, chooser.AnswerCount);

            // The card is still in hand and still playable.
            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Recycle"));
        }

        [Fact]
        public void A_rolled_back_attempt_leaves_nothing_in_the_trace()
        {
            var chooser = new DeferredChooser();
            ContentLibrary library = ContentLibrary.FromText(Content);
            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 1, Chooser = chooser, Trace = true });
            runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            runtime.AddCard("Recycle", Zones.Hand);
            runtime.AddCard("Strike", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            int before = runtime.State.Trace.Entries.Count;
            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Recycle"));
            Assert.Equal(before, runtime.State.Trace.Entries.Count);

            runtime.Answer(runtime.Pending!.Options[0].Id);
            Assert.True(runtime.State.Trace.Entries.Count > before);
        }

        [Fact]
        public void Choices_outside_a_rollback_able_action_fall_back_to_the_first_option()
        {
            // AddRelic is setup, not an action the runtime can replay: it must not throw.
            ContentLibrary library = ContentLibrary.FromText("""
                card "Strike"
                  cost 0
                  effect:
                    block 1

                relic "Sorter"
                  on obtained:
                    choose 1 from hand as picked
                    exhaust picked
                """);
            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 1, Chooser = new DeferredChooser() });
            runtime.CreatePlayer();
            runtime.AddCard("Strike", Zones.Hand);

            runtime.AddRelic("Sorter");

            Assert.Null(runtime.Pending);
            Assert.Single(runtime.State.ZoneOf(runtime.Player, Zones.Exhaust));
        }

        [Fact]
        public void Entities_the_game_holds_survive_the_rollback()
        {
            var chooser = new DeferredChooser();
            CardRuntime runtime = Start(chooser, out _, "Recycle", "Strike");
            Entity player = runtime.Player!;
            Entity enemy = runtime.State.Actors(Team.Enemy).Single();

            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Recycle"));

            // Same objects, not stale copies: a game holding references keeps working.
            Assert.Same(player, runtime.Player);
            Assert.Same(enemy, runtime.State.Actors(Team.Enemy).Single());
            Assert.False(enemy.IsRemoved);

            runtime.Answer(runtime.Pending!.Options[0].Id);
            Assert.Equal(4, player.GetInt("energy"));
        }

        [Fact]
        public void Without_a_deferred_chooser_nothing_changes()
        {
            CardRuntime runtime = Start(new FirstOptionChooser(), out _, "Recycle", "Strike");

            Assert.Equal(PlayResult.Played, runtime.Play("Recycle"));
            Assert.Null(runtime.Pending);
            Assert.Single(runtime.State.ZoneOf(runtime.Player, Zones.Exhaust));
        }
    }
}
