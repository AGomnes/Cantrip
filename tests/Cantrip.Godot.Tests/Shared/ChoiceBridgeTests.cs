using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// Request ids over the core's pending choice. Answering replays the action, which may ask again
    /// straight away, so the only thing standing between a late button press and the wrong answer is
    /// the number each question carries.
    /// </summary>
    public sealed class ChoiceBridgeTests
    {
        /// <summary>Plays the first card of the hand, which is one that asks a question.</summary>
        private static CardRuntime Asking(out ChoiceBridge bridge, params string[] hand)
        {
            CardRuntime runtime = AdapterTestKit.Start(null, new DeferredChooser(), hand);
            Assert.Equal(PlayResult.ChoicePending, runtime.Play(hand[0]));

            bridge = new ChoiceBridge();
            bridge.Sync(runtime.Pending);
            return runtime;
        }

        private const string OfferContent = @"enemy ""Dummy""
  hp 20

card ""Spark""
  cost 0
  tags spell

card ""Flare""
  cost 0
  tags spell

card ""Glint""
  cost 0
  tags spell

card ""Scholar""
  cost 0
  effect:
    discover 3 cards where tag:spell as found
    create found into hand
";

        /// <summary>Plays a card that discovers, which asks for one of three offered cards.</summary>
        private static CardRuntime Offering(out ChoiceBridge bridge)
        {
            var runtime = new CardRuntime(Cantrip.Content.ContentLibrary.FromText(OfferContent), new RuntimeOptions { Seed = 1, Chooser = new DeferredChooser() });
            runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            runtime.AddCard("Scholar", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Scholar"));

            bridge = new ChoiceBridge();
            bridge.Sync(runtime.Pending);
            return runtime;
        }

        [Fact]
        public void An_offer_is_answered_by_position()
        {
            CardRuntime runtime = Offering(out ChoiceBridge bridge);

            Assert.True(bridge.Current!.IsOffer);
            Assert.Equal(new[] { 0, 1, 2 }, bridge.OptionIds);

            ChoiceAnswer answer = bridge.Validate(bridge.CurrentId, new[] { 2 });
            Assert.True(answer.Accepted, answer.Message);

            string picked = runtime.Pending!.Definitions[answer.EntityIds[0]].Name;
            Assert.Equal(PlayResult.Played, runtime.Answer(runtime.Pending.Definitions[answer.EntityIds[0]]));
            Assert.Equal(picked, runtime.State.ZoneOf(runtime.Player, Zones.Hand).Single().Name);
        }

        [Fact]
        public void An_offer_refuses_a_position_it_does_not_have_and_more_than_one_pick()
        {
            Offering(out ChoiceBridge bridge);

            ChoiceAnswer outside = bridge.Validate(bridge.CurrentId, new[] { 3 });
            Assert.Equal(ChoiceRejection.UnknownOption, outside.Reason);
            Assert.StartsWith("Offer 3", outside.Message);

            Assert.Equal(ChoiceRejection.TooMany, bridge.Validate(bridge.CurrentId, new[] { 0, 1 }).Reason);
            Assert.Equal(ChoiceRejection.TooFew, bridge.Validate(bridge.CurrentId, new int[0]).Reason);
        }

        [Fact]
        public void A_pending_choice_gets_a_number_and_keeps_it()
        {
            CardRuntime runtime = Asking(out ChoiceBridge bridge, "Recycle", "Strike", "Strike");

            Assert.True(bridge.IsPending);
            Assert.Equal(1, bridge.CurrentId);
            Assert.Same(runtime.Pending, bridge.Current);
            Assert.Equal(runtime.Pending!.Options.Select(o => o.Id), bridge.OptionIds);

            // Syncing again is what a node does after every action; it must not churn the id the UI holds.
            Assert.Equal(1, bridge.Sync(runtime.Pending));
        }

        [Fact]
        public void The_next_question_is_a_different_question()
        {
            CardRuntime runtime = Asking(out ChoiceBridge bridge, "Twice", "Strike", "Strike", "Strike");
            int first = bridge.CurrentId;

            ChoiceAnswer answer = bridge.Validate(first, new[] { bridge.OptionIds[0] });
            Assert.True(answer.Accepted);
            Assert.Equal(PlayResult.ChoicePending, runtime.Answer(answer.EntityIds));
            bridge.Sync(runtime.Pending);

            // Answering replayed the action, which asked its second question: a new number for it.
            Assert.True(bridge.IsPending);
            Assert.Equal(2, bridge.CurrentId);
            Assert.Equal(2, bridge.OptionIds.Count);
        }

        [Fact]
        public void An_answer_to_a_question_that_has_moved_on_is_refused()
        {
            CardRuntime runtime = Asking(out ChoiceBridge bridge, "Twice", "Strike", "Strike", "Strike");
            int first = bridge.CurrentId;
            int chosen = bridge.OptionIds[0];

            runtime.Answer(bridge.Validate(first, new[] { chosen }).EntityIds);
            bridge.Sync(runtime.Pending);

            ChoiceAnswer late = bridge.Validate(first, new[] { bridge.OptionIds[0] });

            Assert.False(late.Accepted);
            Assert.Equal(ChoiceRejection.StaleRequest, late.Reason);
            Assert.Contains("already been dealt with", late.Message);
            Assert.Empty(late.EntityIds);
        }

        [Fact]
        public void An_answer_with_nothing_pending_is_refused()
        {
            var bridge = new ChoiceBridge();

            ChoiceAnswer answer = bridge.Validate(1, new[] { 7 });

            Assert.Equal(ChoiceRejection.NothingPending, answer.Reason);
            Assert.Equal(0, bridge.CurrentId);
        }

        [Fact]
        public void Cancelling_stops_the_old_answer_from_being_accepted()
        {
            CardRuntime runtime = Asking(out ChoiceBridge bridge, "Recycle", "Strike", "Strike");
            int request = bridge.CurrentId;
            int chosen = bridge.OptionIds[0];

            runtime.CancelPending();
            bridge.Sync(runtime.Pending);

            Assert.False(bridge.IsPending);
            Assert.Equal(ChoiceRejection.NothingPending, bridge.Validate(request, new[] { chosen }).Reason);
        }

        [Fact]
        public void An_entity_that_was_not_offered_is_refused()
        {
            CardRuntime runtime = Asking(out ChoiceBridge bridge, "Recycle", "Strike", "Strike");
            Entity enemy = AdapterTestKit.Enemy(runtime);

            ChoiceAnswer answer = bridge.Validate(bridge.CurrentId, new[] { enemy.Id });

            Assert.Equal(ChoiceRejection.UnknownOption, answer.Reason);
            Assert.Contains("not one of the options", answer.Message);
        }

        [Fact]
        public void The_same_option_twice_is_refused()
        {
            Asking(out ChoiceBridge bridge, "Recycle", "Strike", "Strike");
            int option = bridge.OptionIds[0];

            ChoiceAnswer answer = bridge.Validate(bridge.CurrentId, new[] { option, option });

            Assert.Equal(ChoiceRejection.DuplicateOption, answer.Reason);
        }

        [Fact]
        public void Too_few_and_too_many_are_refused()
        {
            Asking(out ChoiceBridge bridge, "Recycle", "Strike", "Strike");

            Assert.Equal(ChoiceRejection.TooFew, bridge.Validate(bridge.CurrentId, new int[0]).Reason);
            Assert.Equal(ChoiceRejection.TooFew, bridge.Validate(bridge.CurrentId, null).Reason);
            Assert.Equal(ChoiceRejection.TooMany, bridge.Validate(bridge.CurrentId, bridge.OptionIds).Reason);
        }

        [Fact]
        public void An_accepted_answer_keeps_the_order_the_player_gave()
        {
            CardRuntime runtime = Asking(out ChoiceBridge bridge, "Recycle", "Strike", "Strike");
            int second = bridge.OptionIds[1];

            ChoiceAnswer answer = bridge.Validate(bridge.CurrentId, new[] { second });

            Assert.True(answer.Accepted);
            Assert.Equal(ChoiceRejection.None, answer.Reason);
            Assert.Equal(string.Empty, answer.Message);
            Assert.Equal(new[] { second }, answer.EntityIds);

            // And it is an answer the runtime accepts: the chosen card is the one that went.
            Assert.Equal(PlayResult.Played, runtime.Answer(answer.EntityIds));
            Assert.Equal(Zones.Exhaust, runtime.State.Find(second)!.Zone);
        }

        /// <summary>
        /// The words <c>AnswerChoice</c> gives script as its <c>reason</c>. A game compares against
        /// them, so they are pinned one by one, and a rejection added without a word fails here
        /// rather than in someone's game.
        /// </summary>
        [Fact]
        public void Every_rejection_has_its_snake_case_word()
        {
            var words = new Dictionary<ChoiceRejection, string>
            {
                [ChoiceRejection.None] = "none",
                [ChoiceRejection.NothingPending] = "nothing_pending",
                [ChoiceRejection.StaleRequest] = "stale_request",
                [ChoiceRejection.UnknownOption] = "unknown_option",
                [ChoiceRejection.DuplicateOption] = "duplicate_option",
                [ChoiceRejection.TooFew] = "too_few",
                [ChoiceRejection.TooMany] = "too_many",
            };

            Assert.Equal(Enum.GetValues<ChoiceRejection>().OrderBy(r => r), words.Keys.OrderBy(r => r));
            foreach (KeyValuePair<ChoiceRejection, string> word in words) Assert.Equal(word.Value, ChoiceAnswer.NameOf(word.Key));
        }

        [Fact]
        public void A_rejected_answer_carries_its_word()
        {
            Asking(out ChoiceBridge bridge, "Recycle", "Strike", "Strike");

            ChoiceAnswer tooFew = bridge.Validate(bridge.CurrentId, new int[0]);
            ChoiceAnswer accepted = bridge.Validate(bridge.CurrentId, new[] { bridge.OptionIds[0] });

            Assert.Equal("too_few", tooFew.ReasonName);
            Assert.StartsWith("too_few: ", tooFew.ToString());
            Assert.Equal("none", accepted.ReasonName);
        }

        [Fact]
        public void Closing_by_hand_gives_up_the_request()
        {
            Asking(out ChoiceBridge bridge, "Recycle", "Strike", "Strike");
            int request = bridge.CurrentId;

            bridge.Close();

            Assert.False(bridge.IsPending);
            Assert.Null(bridge.Current);
            Assert.Empty(bridge.OptionIds);
            Assert.Equal(ChoiceRejection.NothingPending, bridge.Validate(request, new int[0]).Reason);
        }
    }
}
