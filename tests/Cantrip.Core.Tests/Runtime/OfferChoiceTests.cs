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

        // Found by review before the release -----------------------------------------------------

        private const string Spells = "card Spark\n  cost 0\n  tags spell\n\ncard Flare\n  cost 0\n  tags spell\n\ncard Glint\n  cost 0\n  tags spell\n\ncard Frost\n  cost 0\n  tags spell\n";

        private const string Scholar = "enemy Dummy\n  hp 100\n\ncard Scholar\n  cost 0\n  effect:\n    discover 3 cards where tag:spell as found\n    create found into hand\n\nrelic Charm\n  on obtained:\n    log \"charmed\"\n";

        private sealed class RecordingHost : EffectHostBase
        {
            public List<string> Events { get; } = new List<string>();

            public override void OnEvent(GameEvent gameEvent) => Events.Add(gameEvent.Name);
        }

        /// <summary>Spells in a file of their own, so a test can reload just them mid-choice.</summary>
        private static CardRuntime StartReloadable(out ContentLibrary library, RecordingHost? host = null, bool trace = false)
        {
            library = new ContentLibrary();
            library.LoadText(Spells, "spells.cantrip");
            library.LoadText(Scholar, "scholar.cantrip");
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 1, Chooser = new DeferredChooser(), Host = host, Trace = trace });
            runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            runtime.AddCard("Scholar", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            host?.Events.Clear();
            return runtime;
        }

        [Theory]
        [Trait("Regression", "offer-replayed-by-position-after-reload")]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void A_reload_during_an_offer_never_creates_a_card_other_than_the_one_picked(int position)
        {
            CardRuntime runtime = StartReloadable(out ContentLibrary library);
            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Scholar"));
            string picked = runtime.Pending!.Definitions[position].Name;

            // A designer adds a spell while the offer is on screen: the replay draws from a new pool.
            library.LoadText(Spells + "\ncard Arc\n  cost 0\n  tags spell\n", "spells.cantrip");
            runtime.ApplyContentChanges();

            PlayResult result = runtime.Answer(runtime.Pending!.Definitions[position]);
            if (result == PlayResult.ChoicePending)
            {
                // The pick is no longer among the candidates: the player is asked again, afresh.
                Assert.True(runtime.Pending!.IsOffer);
                string repicked = runtime.Pending.Definitions[0].Name;
                Assert.Equal(PlayResult.Played, runtime.Answer(runtime.Pending.Definitions[0]));
                Assert.Equal(new[] { repicked }, Hand(runtime));
            }
            else
            {
                Assert.Equal(PlayResult.Played, result);
                Assert.Equal(new[] { picked }, Hand(runtime));
            }
        }

        [Fact]
        [Trait("Regression", "offer-replayed-by-position-after-reload")]
        public void A_pick_removed_by_a_reload_is_asked_again_rather_than_swapped()
        {
            CardRuntime runtime = StartReloadable(out ContentLibrary library);
            runtime.Play("Scholar");
            EntityDefinition picked = runtime.Pending!.Definitions[0];

            // The picked spell stops being a spell.
            string retagged = Spells.Replace("card " + picked.Name + "\n  cost 0\n  tags spell", "card " + picked.Name + "\n  cost 0\n  tags curse");
            library.LoadText(retagged, "spells.cantrip");
            runtime.ApplyContentChanges();

            Assert.Equal(PlayResult.ChoicePending, runtime.Answer(picked));
            Assert.DoesNotContain(runtime.Pending!.Definitions, d => d.Name == picked.Name);

            EntityDefinition second = runtime.Pending.Definitions[0];
            Assert.Equal(PlayResult.Played, runtime.Answer(second));
            Assert.Equal(new[] { second.Name }, Hand(runtime));
        }

        [Fact]
        public void An_offer_can_be_answered_with_the_definition_a_reload_put_in_its_place()
        {
            CardRuntime runtime = StartReloadable(out ContentLibrary library);
            runtime.Play("Scholar");
            string picked = runtime.Pending!.Definitions[1].Name;

            library.LoadText(Spells, "spells.cantrip");
            runtime.ApplyContentChanges();

            Assert.Equal(PlayResult.Played, runtime.Answer(library.Find(picked, "card")!));
            Assert.Equal(new[] { picked }, Hand(runtime));
        }

        [Fact]
        [Trait("Regression", "stale-answers-reach-the-next-action")]
        public void A_new_action_does_not_inherit_the_answers_of_an_abandoned_one()
        {
            CardRuntime runtime = Start(new DeferredChooser(), "Study", "Seer", "Scholar");
            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Study"));
            Assert.Equal(PlayResult.ChoicePending, runtime.Answer(runtime.Pending!.Definitions[1]));
            Assert.False(runtime.Pending!.IsOffer);   // Study now waits on its second question

            // The player walks away from Study and plays Scholar: its offer must be asked, not answered
            // with Study's leftover pick.
            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Scholar"));
            Assert.True(runtime.Pending!.IsOffer);
            Assert.Equal(new[] { "Study", "Seer", "Scholar" }, Hand(runtime));
        }

        [Fact]
        [Trait("Regression", "pending-choice-survives-restore")]
        public void Restoring_a_snapshot_abandons_the_pending_choice()
        {
            CardRuntime runtime = StartReloadable(out _);
            GameSnapshot save = runtime.Capture();
            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Scholar"));
            EntityDefinition picked = runtime.Pending!.Definitions[0];

            runtime.Restore(save);

            Assert.Null(runtime.Pending);
            Assert.Throws<InvalidOperationException>(() => runtime.Answer(picked));
        }

        [Fact]
        public void A_rolled_back_offer_leaves_nothing_in_the_trace()
        {
            CardRuntime runtime = StartReloadable(out _, trace: true);
            int before = runtime.State.Trace.Entries.Count;

            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Scholar"));
            Assert.Equal(before, runtime.State.Trace.Entries.Count);

            runtime.Answer(runtime.Pending!.Definitions[0]);
            Assert.True(runtime.State.Trace.Entries.Count > before);
        }

        [Fact]
        public void A_rolled_back_offer_holds_back_no_host_events_afterwards()
        {
            var host = new RecordingHost();
            CardRuntime runtime = StartReloadable(out _, host);

            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Scholar"));
            Assert.Empty(host.Events);

            // Outside any action: if the rollback left its event buffer open, this would vanish into it.
            runtime.CancelPending();
            runtime.AddRelic("Charm");
            Assert.Equal(new[] { "obtained" }, host.Events);
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
