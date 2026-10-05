#nullable enable
using System;
using System.Linq;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Battle
{
    /// <summary>
    /// <c>discover</c>: picking content that nothing has been made from yet, and binding the choice.
    /// </summary>
    /// <remarks>
    /// The rolls themselves are never asserted against a golden value. What is pinned instead is what
    /// content can rely on: a zero weight is never drawn, a single candidate needs no chooser at all,
    /// a named answer wins, and the same seed discovers the same thing twice. Asserting "seed 1 picks
    /// Frost" would pin the RNG's internals rather than the feature, and would break for the wrong
    /// reason the first time anything upstream drew a number.
    /// </remarks>
    public sealed class DiscoverTests
    {
        private const string Content = """
            card "Spark"
              cost 1
              tags spell
              effect:
                deal 1 to target

            card "Frost"
              cost 2
              tags spell
              effect:
                deal 2 to target

            card "Gale"
              cost 3
              tags spell
              effect:
                deal 3 to target

            card "Anvil"
              cost 1
              tags tool

            status "Dread"
              tags affliction
              weight 0

            status "Panic"
              tags affliction
              weight 5

            status "Calm"
              tags virtue
              weight 5

            card "Rite"
              cost 0
              effect:
                discover 1 statuses where tag:affliction, weighted as curse
                apply curse 1 to player

            card "Insight"
              cost 0
              effect:
                discover 3 cards where tag:spell as found
                create found into hand

            card "Thrift"
              cost 0
              effect:
                discover 1 cards where tag:spell and it.cost <= 1 as cheap
                create cheap into hand

            card "Vain"
              cost 0
              effect:
                discover 1 relics where tag:nothing_at_all as nope
                create nope into hand

            card "Confused"
              cost 0
              effect:
                discover 1 cards where zone:hand as huh
                create huh into hand

            enemy "Dummy"
              hp 30
            """;

        private static CardRuntime Setup(out Entity player, IChoiceProvider? chooser = null)
        {
            CardRuntime runtime = BattleKit.Create(Content, chooser: chooser);
            player = runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            return runtime;
        }

        [Fact]
        public void A_weight_of_zero_is_never_drawn()
        {
            CardRuntime runtime = Setup(out Entity player);
            runtime.AddCard("Rite", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(ActionResult.Played, runtime.Play("Rite"));

            // Dread and Panic both match; Dread's weight is 0, so the draw can only land on Panic.
            Assert.NotNull(player.FindAttached("Panic"));
            Assert.Null(player.FindAttached("Dread"));
            Assert.Null(player.FindAttached("Calm"));
        }

        [Fact]
        public void One_candidate_is_taken_without_asking_anyone()
        {
            var chooser = new BattleRecordingChooser();
            CardRuntime runtime = Setup(out Entity player, chooser);
            runtime.AddCard("Rite", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(ActionResult.Played, runtime.Play("Rite"));

            // A random pick is a choice with nothing to decide, so no request is made at all.
            Assert.Empty(chooser.Requests);
            Assert.NotNull(player.FindAttached("Panic"));
        }

        [Fact]
        public void A_named_answer_wins_the_offer()
        {
            var chooser = new ScriptedChooser("Frost");
            CardRuntime runtime = Setup(out _, chooser);
            runtime.AddCard("Insight", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(ActionResult.Played, runtime.Play("Insight"));

            // All three spells are offered, so the answer decides regardless of the roll.
            Assert.Contains("Frost", BattleKit.Zone(runtime, Zones.Hand).Select(c => c.Name));
        }

        [Fact]
        public void A_filter_reads_a_printed_property_and_the_kind_it_was_declared_with()
        {
            CardRuntime runtime = Setup(out _, new ScriptedChooser());
            runtime.AddCard("Thrift", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(ActionResult.Played, runtime.Play("Thrift"));

            // Only Spark is a spell costing 1 or less; Anvil is cheap but not a spell.
            Assert.Equal(new[] { "Spark" }, BattleKit.Zone(runtime, Zones.Hand).Select(c => c.Name).ToArray());
        }

        [Fact]
        public void The_same_seed_discovers_the_same_thing_twice()
        {
            string[] First = Discovered();
            string[] Again = Discovered();

            Assert.Equal(First, Again);
            Assert.Single(First);

            static string[] Discovered()
            {
                CardRuntime runtime = BattleKit.Create(Content, chooser: new ScriptedChooser());
                runtime.CreatePlayer();
                runtime.SpawnEnemy("Dummy");
                runtime.AddCard("Insight", Zones.Hand);
                runtime.StartBattle(shuffle: false, drawOpeningHand: false);
                runtime.Play("Insight");
                return BattleKit.Zone(runtime, Zones.Hand).Select(c => c.Name).ToArray();
            }
        }

        /// <summary>
        /// A game's own chooser is a single interface. Before <c>ChooseDefinition</c> moved onto
        /// <see cref="IChoiceProvider"/> it lived on a second one, so a chooser that wrote the method
        /// without also naming that interface compiled, ran, and was never asked: every
        /// <c>discover</c> silently took option one.
        /// </summary>
        [Fact]
        public void A_chooser_that_only_implements_IChoiceProvider_is_asked_about_an_offer()
        {
            var chooser = new LastOfferChooser();
            CardRuntime runtime = Setup(out _, chooser);
            runtime.AddCard("Insight", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(ActionResult.Played, runtime.Play("Insight"));

            Assert.Equal(1, chooser.Offers);
            Assert.Equal(chooser.Picked, BattleKit.Zone(runtime, Zones.Hand).Single().Name);
        }

        /// <summary>
        /// The other half: a chooser written before the member existed, which answers entity choices
        /// and nothing else, still runs, and still gets the first candidate: now from the interface's
        /// own default rather than from a type test that could not see it.
        /// </summary>
        [Fact]
        public void A_chooser_with_only_Choose_takes_the_first_offer_through_the_default()
        {
            Assert.Equal(Discovered(new ChooseOnlyChooser()), Discovered(new FirstOptionChooser()));

            static string Discovered(IChoiceProvider chooser)
            {
                CardRuntime runtime = BattleKit.Create(Content, seed: 7, chooser: chooser);
                runtime.CreatePlayer();
                runtime.SpawnEnemy("Dummy");
                runtime.AddCard("Insight", Zones.Hand);
                runtime.StartBattle(shuffle: false, drawOpeningHand: false);
                Assert.Equal(ActionResult.Played, runtime.Play("Insight"));
                return BattleKit.Zone(runtime, Zones.Hand).Single().Name;
            }
        }

        /// <summary>Implements the one member every chooser has had since 0.1.0-preview.1, and no more.</summary>
        private sealed class ChooseOnlyChooser : IChoiceProvider
        {
            public System.Collections.Generic.IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state) =>
                request.Options.Take(request.Max).ToList();
        }

        /// <summary>Answers offers without naming any interface but <see cref="IChoiceProvider"/>.</summary>
        private sealed class LastOfferChooser : IChoiceProvider
        {
            public int Offers { get; private set; }

            public string? Picked { get; private set; }

            public System.Collections.Generic.IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state) =>
                request.Options.Take(request.Max).ToList();

            public Content.EntityDefinition? ChooseDefinition(DefinitionChoice request, GameState state)
            {
                Offers++;
                Content.EntityDefinition last = request.Options[request.Options.Count - 1];
                Picked = last.Name;
                return last;
            }
        }

        [Fact]
        public void Nothing_to_discover_says_so_instead_of_picking_nothing()
        {
            CardRuntime runtime = Setup(out _);
            runtime.AddCard("Vain", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            // An empty pool is content's mistake, and this repo's habit is to say so rather than
            // evaluate to nothing quietly.
            Exception error = Assert.ThrowsAny<Exception>(() => runtime.Play("Vain"));
            Assert.Contains("nothing to discover", error.Message);
        }

        [Fact]
        public void A_qualifier_about_being_in_play_is_refused_not_ignored()
        {
            CardRuntime runtime = Setup(out _);
            runtime.AddCard("Confused", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            // `zone:` cannot mean anything to a definition; answering false would silently offer
            // nothing and read like an empty pool.
            Exception error = Assert.ThrowsAny<Exception>(() => runtime.Play("Confused"));
            Assert.Contains("in play", error.Message);
        }
    }
}
