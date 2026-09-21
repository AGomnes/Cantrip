using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// Offers of content a UI answers: <c>discover</c> under a <see cref="DeferredChooser"/>. It used
    /// to take the first candidate without asking, because the chooser only knew how to pause for
    /// choices between entities, and the Godot node uses that chooser by default.
    /// </summary>
    public sealed class OfferChoiceTests
    {
        private const string Content = """
            enemy "Dummy"
              hp 100

            card Spark
              cost 0
              tags spell

            card Flare
              cost 0
              tags spell

            card Glint
              cost 0
              tags spell

            card Frost
              cost 0
              tags spell

            card Scholar
              cost 0
              effect:
                discover 3 cards where tag:spell as found
                create found into hand
                energy += 1

            card Seer
              cost 0
              effect:
                discover 1 cards where tag:spell as found
                create found into hand

            card Study
              cost 0
              effect:
                discover 2 cards where tag:spell as found
                create found into hand
                choose 1 from hand as tossed
                exhaust tossed
            """;

        private static CardRuntime Start(IChoiceProvider chooser, params string[] hand)
        {
            ContentLibrary library = ContentLibrary.FromText(Content);
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 1, Chooser = chooser });
            runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            foreach (string card in hand) runtime.AddCard(card, Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            return runtime;
        }

        private static List<string> Hand(CardRuntime runtime) =>
            runtime.State.ZoneOf(runtime.Player, Zones.Hand).Select(c => c.Name).ToList();

        [Fact]
        [Trait("Regression", "discover-not-asked-under-deferred-chooser")]
        public void An_unanswered_offer_rolls_back_and_reports_the_candidates()
        {
            CardRuntime runtime = Start(new DeferredChooser(), "Scholar");
            int energy = runtime.Player!.GetInt("energy");

            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Scholar"));

            PendingChoice pending = runtime.Pending!;
            Assert.True(pending.IsOffer);
            Assert.Equal(3, pending.Definitions.Count);
            Assert.Empty(pending.Options);
            Assert.Equal(1, pending.Min);
            Assert.Equal(1, pending.Max);
            Assert.All(pending.Definitions, d => Assert.True(d.HasTag("spell")));

            // Nothing happened yet: the card is still in hand and nothing was created or gained.
            Assert.Equal(new[] { "Scholar" }, Hand(runtime));
            Assert.Equal(energy, runtime.Player.GetInt("energy"));
        }

        [Fact]
        public void Answering_creates_exactly_the_card_that_was_picked()
        {
            CardRuntime runtime = Start(new DeferredChooser(), "Scholar");
            runtime.Play("Scholar");
            EntityDefinition picked = runtime.Pending!.Definitions[2];

            Assert.Equal(PlayResult.Played, runtime.Answer(picked));

            Assert.Null(runtime.Pending);
            Assert.Equal(new[] { picked.Name }, Hand(runtime));
            Assert.Equal(4, runtime.Player!.GetInt("energy"));
        }

        [Fact]
        public void An_offer_and_then_an_ordinary_choice_are_answered_in_turn()
        {
            // Seer is not a spell, so it cannot be confused with whatever the offer creates.
            CardRuntime runtime = Start(new DeferredChooser(), "Study", "Seer");
            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Study"));
            EntityDefinition picked = runtime.Pending!.Definitions[0];

            Assert.Equal(PlayResult.ChoicePending, runtime.Answer(picked));
            PendingChoice toss = runtime.Pending!;
            Assert.False(toss.IsOffer);
            Entity seer = toss.Options.Single(o => o.Name == "Seer");

            Assert.Equal(PlayResult.Played, runtime.Answer(seer.Id));
            Assert.Equal(new[] { picked.Name }, Hand(runtime));
            Assert.Equal(Zones.Exhaust, seer.Zone);
        }

        [Fact]
        public void An_offer_is_not_answered_with_entity_ids_or_with_something_not_offered()
        {
            CardRuntime runtime = Start(new DeferredChooser(), "Scholar");
            runtime.Play("Scholar");
            PendingChoice pending = runtime.Pending!;

            Assert.Throws<InvalidOperationException>(() => runtime.Answer(1));

            EntityDefinition notOffered = runtime.Content.Pool("card").First(d => d.HasTag("spell") && !pending.Definitions.Contains(d));
            Assert.Throws<ArgumentException>(() => runtime.Answer(notOffered));

            // Neither mistake used the question up.
            Assert.Same(pending, runtime.Pending);
        }

        [Fact]
        public void A_single_candidate_is_no_decision_and_never_asks()
        {
            CardRuntime runtime = Start(new DeferredChooser(), "Seer");

            Assert.Equal(PlayResult.Played, runtime.Play("Seer"));
            Assert.Null(runtime.Pending);
            Assert.Single(Hand(runtime));
        }

        [Fact]
        public void The_random_chooser_picks_among_the_offers_rather_than_always_the_first()
        {
            var picks = new HashSet<string>();
            for (ulong seed = 1; seed <= 12; seed++)
            {
                CardRuntime runtime = Start(new RandomChooser(seed), "Scholar");
                Assert.Equal(PlayResult.Played, runtime.Play("Scholar"));
                picks.Add(Hand(runtime).Single());
            }

            Assert.True(picks.Count > 1, "every seed picked " + string.Join(", ", picks));
        }
    }
}
