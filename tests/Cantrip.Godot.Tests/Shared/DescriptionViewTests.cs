using System.Linq;
using Cantrip.Descriptions;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// The description mapping: whether a card frame can show what a card says, what it will
    /// actually do, and the difference between the two.
    /// </summary>
    public sealed class DescriptionViewTests
    {
        [Fact]
        public void A_printed_card_carries_its_words_flavour_and_cost()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity _);
            Entity fireball = runtime.AddCard("Fireball", Zones.Hand);

            DescriptionView view = DescriptionView.Of(ViewTestKit.Descriptions(runtime).Describe(fireball, runtime));

            Assert.Equal("Fireball", view.Name);
            Assert.Equal("custom", view.Level);
            Assert.Equal("Hurl a ball of flame for 6 damage.", view.Plain);
            Assert.Equal("Warm regards.", view.Flavour);
            Assert.False(view.IsEmpty);

            Assert.NotNull(view.Cost);
            Assert.Equal(2, view.Cost!.Base);
            Assert.Equal(2, view.Cost.Current);
            Assert.Equal("unchanged", view.Cost.Trend);
            Assert.True(view.Cost.LowerIsBetter);
        }

        [Fact]
        public void A_buffed_value_keeps_the_printed_number_beside_the_real_one()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity _);
            runtime.AddRelic("Pyromancer's Codex");
            Entity fireball = runtime.AddCard("Fireball", Zones.Hand);

            DescriptionView view = DescriptionView.Of(ViewTestKit.Descriptions(runtime).Describe(fireball, runtime));

            SegmentView damage = view.Segments.Single(s => s.Placeholder == "damage");
            Assert.Equal("value", damage.Kind);
            Assert.True(damage.HasNumber);
            Assert.Equal(6, damage.Base);
            Assert.Equal(9, damage.Current);
            Assert.Equal("6", damage.BaseText);
            Assert.Equal("9", damage.Text);
            Assert.True(damage.Changed);
            Assert.Equal("buffed", damage.Trend);

            Assert.Equal("Hurl a ball of flame for 9 damage.", view.Plain);
        }

        [Fact]
        public void A_cheaper_cost_counts_as_buffed_because_lower_is_better_there()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity _);
            runtime.AddRelic("Pyromancer's Codex");
            Entity fireball = runtime.AddCard("Fireball", Zones.Hand);

            SegmentView cost = DescriptionView.Of(ViewTestKit.Descriptions(runtime).Describe(fireball, runtime)).Cost!;

            Assert.Equal(2, cost.Base);
            Assert.Equal(1, cost.Current);
            Assert.True(cost.Changed);
            Assert.Equal("buffed", cost.Trend);
        }

        [Fact]
        public void Damage_that_will_land_harder_reads_as_buffed_for_the_side_dealing_it()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity enemy);
            Entity strike = runtime.AddCard("Strike", Zones.Hand);
            runtime.ApplyStatus("Vulnerable", enemy, 2);

            // Described against the target it would hit, so damage_taken applies as well.
            DescriptionView against = DescriptionView.Of(ViewTestKit.Descriptions(runtime).Describe(strike, runtime, enemy));
            SegmentView damage = against.Segments.Single(s => s.Placeholder == "damage");

            Assert.Equal(6, damage.Base);
            Assert.Equal(9, damage.Current);
            Assert.Equal("buffed", damage.Trend);

            // The same card with no target described is back to its printed number.
            DescriptionView printed = DescriptionView.Of(ViewTestKit.Descriptions(runtime).Describe(strike, runtime));
            Assert.Equal("unchanged", printed.Segments.Single(s => s.Placeholder == "damage").Trend);
        }

        // BBCode -------------------------------------------------------------------------------

        [Fact]
        public void BBCode_strikes_the_printed_value_and_colours_the_real_one()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity _);
            runtime.AddRelic("Pyromancer's Codex");
            Entity fireball = runtime.AddCard("Fireball", Zones.Hand);

            DescriptionView view = DescriptionView.Of(ViewTestKit.Descriptions(runtime).Describe(fireball, runtime));

            Assert.Equal(
                "Hurl a ball of flame for [s]6[/s] [color=" + DescriptionView.BuffedColour + "]9[/color] damage.",
                view.BBCode);
        }

        [Fact]
        public void BBCode_of_an_unchanged_description_is_just_its_words()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity _);
            Entity strike = runtime.AddCard("Strike", Zones.Hand);

            DescriptionView view = DescriptionView.Of(ViewTestKit.Descriptions(runtime).Describe(strike, runtime));

            Assert.Equal("Deal 6 damage.", view.Plain);
            Assert.Equal("Deal 6 damage.", view.BBCode);
        }

        [Fact]
        public void A_bracket_a_designer_wrote_survives_the_bbcode()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity _);
            Entity footnote = runtime.AddCard("Footnote", Zones.Hand);

            DescriptionView view = DescriptionView.Of(ViewTestKit.Descriptions(runtime).Describe(footnote, runtime));

            Assert.Equal("override", view.Level);
            Assert.Equal("Draw a card [see the rules].", view.Plain);

            // Left raw, RichTextLabel would read "[see the rules]" as an unknown tag and eat it.
            Assert.Equal("Draw a card [lb]see the rules].", view.BBCode);
        }

        // Tooltips and intents -------------------------------------------------------------------

        [Fact]
        public void Keywords_the_text_leans_on_come_with_it()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity _);
            Entity fireball = runtime.AddCard("Fireball", Zones.Hand);

            DescriptionView view = DescriptionView.Of(ViewTestKit.Descriptions(runtime).Describe(fireball, runtime));

            TooltipView burn = Assert.Single(view.Tooltips);
            Assert.Equal("Burn", burn.Name);
            Assert.Equal("At the end of its holder's turn: Deal X damage to its holder, ignoring Block.", burn.Plain);
            Assert.Equal(burn.Plain, burn.BBCode);
        }

        [Fact]
        public void An_intent_panel_shows_the_move_the_enemy_rolled()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity enemy);

            // Nothing to show until intents are rolled, which is also what a UI should draw then.
            Assert.True(DescriptionView.Of(ViewTestKit.Descriptions(runtime).DescribeIntent(enemy, runtime)).IsEmpty);

            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            DescriptionView intent = DescriptionView.Of(ViewTestKit.Descriptions(runtime).DescribeIntent(enemy, runtime));

            Assert.Equal("Chomp", intent.Name);
            Assert.Equal("Deal 11 damage to the player.", intent.Plain);
            Assert.Null(intent.Cost);
        }

        [Fact]
        public void An_intent_against_a_vulnerable_player_shows_what_it_will_really_do()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity enemy);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Vulnerable", runtime.Player!, 2);

            Description described = ViewTestKit.Descriptions(runtime).DescribeIntent(enemy, runtime);

            // The rules answer for whoever owns the effect, so to the enemy this is an improvement.
            SegmentView dealt = DescriptionView.Of(described).Segments.Single(s => s.Placeholder == "damage");
            Assert.Equal(11, dealt.Base);
            Assert.Equal(16, dealt.Current);
            Assert.Equal("buffed", dealt.Trend);

            // An intent panel is read by the person being hit, so the view inverts it for them.
            SegmentView taken = DescriptionView.Of(described, forOpponent: true).Segments.Single(s => s.Placeholder == "damage");
            Assert.Equal(16, taken.Current);
            Assert.Equal("debuffed", taken.Trend);
        }

        [Fact]
        public void Describing_nothing_is_a_mistake_worth_hearing_about()
        {
            Assert.Throws<System.ArgumentNullException>(() => DescriptionView.Of(null!));
            Assert.Throws<System.ArgumentNullException>(() => SegmentView.Of(null!));
            Assert.Throws<System.ArgumentNullException>(() => TooltipView.Of(null!));
        }
    }
}
