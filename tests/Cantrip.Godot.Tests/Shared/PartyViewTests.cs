using Cantrip.Runtime;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// What the views say about a party: which actors the game is asked for input for, which have
    /// taken their step, and who an enemy is telegraphing against.
    /// </summary>
    public sealed class PartyViewTests
    {
        /// <summary>
        /// The kit's content with a second member in it, and the Jaw Worm's <c>deal 11 to player</c>
        /// rewritten as <c>to target</c>, which is what CT326 exists to insist on the moment a
        /// game declares a <c>hero</c>, because <c>player</c> in an enemy move means the leader and
        /// would hit him however the intent was telegraphed.
        /// </summary>
        private const string Party = ViewTestKit.Content + @"
hero ""Scout""
  hp 22
";

        private static CardRuntime Fight(out Entity scout, out Entity enemy)
        {
            var runtime = new CardRuntime(
                Cantrip.Content.ContentLibrary.FromText(Party.Replace("deal 11 to player", "deal 11 to target"), "res://content/party.cantrip"),
                new RuntimeOptions { Seed = 1 });
            runtime.CreatePlayer();
            scout = runtime.AddHero("Scout");
            enemy = runtime.SpawnEnemy("Jaw Worm");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            return runtime;
        }

        [Fact]
        public void A_member_says_it_is_one_and_an_enemy_says_it_is_not()
        {
            CardRuntime runtime = Fight(out Entity scout, out Entity enemy);

            Assert.True(EntityView.Of(scout).PartyMember);
            Assert.True(EntityView.Of(runtime.Player!).PartyMember);
            Assert.False(EntityView.Of(enemy).PartyMember);
        }

        /// <summary>
        /// Whether somebody has acted is a fact about the game rather than about the entity, so it
        /// is false without one: a view read from a choice's options, or from a debug dump between
        /// battles, does not claim the whole party is still waiting.
        /// </summary>
        [Fact]
        public void Acted_is_read_from_the_game_and_false_without_one()
        {
            CardRuntime runtime = Fight(out Entity scout, out _);

            Assert.False(EntityView.Of(scout, null, runtime.State).Acted);
            runtime.Pass(scout);

            Assert.True(EntityView.Of(scout, null, runtime.State).Acted);
            Assert.False(EntityView.Of(scout).Acted);
            Assert.False(EntityView.Of(runtime.Player!, null, runtime.State).Acted);
        }

        /// <summary>
        /// The whole telegraph on one line, which is what an intent panel shows. The Jaw Worm has
        /// no <c>at</c> clause, so with two members it draws one of them, and whichever it drew is
        /// the name in the line.
        /// </summary>
        [Fact]
        public void An_intent_names_the_member_it_is_telegraphed_against()
        {
            CardRuntime runtime = Fight(out _, out Entity enemy);

            DescriptionView view = DescriptionView.Of(
                ViewTestKit.Descriptions(runtime).DescribeIntent(enemy, runtime), forOpponent: true);

            Entity aim = Assert.IsType<Entity>(runtime.IntentTargetOf(enemy));
            Assert.Equal(aim.Name, view.Against);
            Assert.Equal("Chomp -> " + aim.Name + ": Deal 11 damage.", view.Line);
        }

        /// <summary>A card is aimed by whoever plays it, so it has no target of its own to name.</summary>
        [Fact]
        public void A_card_has_no_target_of_its_own()
        {
            CardRuntime runtime = Fight(out _, out _);
            Entity card = runtime.AddCard("Strike", Zones.Hand);

            DescriptionView view = DescriptionView.Of(ViewTestKit.Descriptions(runtime).Describe(card, runtime));

            Assert.Equal(string.Empty, view.Against);
            Assert.Equal("Strike: Deal 6 damage.", view.Line);
        }
    }
}
