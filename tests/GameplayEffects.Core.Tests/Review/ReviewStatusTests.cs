using System.Linq;
using GameplayEffects.Content;
using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Review.ReviewSupport;

namespace GameplayEffects.Tests.Review
{
    /// <summary>Status stacking, decay, temporary (<c>until</c>) changes and hot reload of statuses.</summary>
    public sealed class ReviewStatusTests
    {
        private const string FlexContent = @"
status ""Strength""
  tags buff
  stacking intensity

card ""Flex""
  cost 0
  effect:
    until turn_end:
      gain 2 Strength
";

        /// <summary>The first application of a <c>both</c> status writes stacks directly, skipping the max_stacks clamp.</summary>
        [Fact]
        [Trait("Regression", "both-stacking-ignores-max-stacks")]
        public void Both_stacking_respects_max_stacks_on_first_application()
        {
            CardRuntime runtime = NewRuntime(@"
status ""Charge""
  stacking both
  max_stacks 3
");
            Entity player = runtime.Player!;

            runtime.ApplyStatus("Charge", player, 5);

            Assert.Equal(3, player.StacksOf("Charge"));
        }

        /// <summary>
        /// Stack changes on an existing status inside <c>until</c> are never recorded for undo,
        /// because ChangeStat skips undo for resources and <c>stacks</c> is a built-in resource.
        /// </summary>
        [Fact]
        [Trait("Regression", "until-status-undo-is-per-instance")]
        public void Until_reverts_stacks_added_to_an_existing_status()
        {
            CardRuntime runtime = NewRuntime(FlexContent);
            Entity player = runtime.Player!;
            runtime.ApplyStatus("Strength", player, 1);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Play(runtime.AddCard("Flex", Zones.Hand));
            Assert.Equal(3, player.StacksOf("Strength"));

            runtime.EndTurn();

            Assert.Equal(1, player.StacksOf("Strength"));
        }

        /// <summary>
        /// The reverse case: when <c>until</c> created the status, reverting removes the whole
        /// instance, including stacks that were added permanently by something else later on.
        /// </summary>
        [Fact]
        [Trait("Regression", "until-status-undo-is-per-instance")]
        public void Until_keeps_stacks_added_permanently_after_it()
        {
            CardRuntime runtime = NewRuntime(FlexContent);
            Entity player = runtime.Player!;
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Play(runtime.AddCard("Flex", Zones.Hand));
            runtime.ApplyStatus("Strength", player, 3);
            Assert.Equal(5, player.StacksOf("Strength"));

            runtime.EndTurn();

            Assert.Equal(3, player.StacksOf("Strength"));
        }

        /// <summary>
        /// <c>decay N on &lt;event&gt;</c> accepts any event, but ProcessDecay is only ever called
        /// for turn_start and turn_end, so every other trigger silently never decays.
        /// </summary>
        [Fact]
        [Trait("Regression", "decay-only-on-turn-events")]
        public void Decay_on_a_custom_event_decays()
        {
            CardRuntime runtime = NewRuntime(@"
status ""Momentum""
  stacking intensity
  decay 1 on card_played

card ""Jab""
  cost 0
  effect:
    log ""jab""
");
            Entity player = runtime.Player!;
            runtime.ApplyStatus("Momentum", player, 3);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Play(runtime.AddCard("Jab", Zones.Hand));

            Assert.Equal(2, player.StacksOf("Momentum"));
        }

        /// <summary>
        /// For duration statuses, reading <c>enemy.Vulnerable</c> returns stacks (always 1) while
        /// assigning to it changes duration, so even a self-assignment shortens the status.
        /// </summary>
        [Fact]
        [Trait("Regression", "status-member-read-write-asymmetry")]
        public void Self_assignment_of_a_duration_status_changes_nothing()
        {
            CardRuntime runtime = NewRuntime(@"
status ""Vulnerable""
  tags debuff
  stacking duration
");
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Vulnerable", enemy, 3);
            Entity vulnerable = enemy.FindAttached("Vulnerable")!;
            Assert.Equal(3, vulnerable.GetInt("duration"));

            runtime.Execute("enemy.Vulnerable = enemy.Vulnerable");

            Assert.Equal(3, vulnerable.GetInt("duration"));
        }

        /// <summary>
        /// Hot reload replaces definition objects, but live statuses keep the old one and stacking
        /// compares definitions by reference, so re-applying after a reload adds a second instance.
        /// </summary>
        [Fact]
        [Trait("Regression", "hot-reload-duplicates-statuses")]
        public void Reapplying_a_status_after_hot_reload_stacks_onto_the_live_instance()
        {
            const string Text = "status \"Poison\"\n  stacking intensity\n";
            var content = new ContentLibrary();
            content.LoadText(Text, "statuses.ge");
            var runtime = new CardRuntime(content);
            runtime.CreatePlayer();
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Poison", enemy, 3);

            content.LoadText(Text, "statuses.ge");
            runtime.ApplyStatus("Poison", enemy, 2);

            Assert.Equal(5, enemy.StacksOf("Poison"));
            Assert.Single(enemy.Attached, a => !a.IsRemoved && a.Name == "Poison");
        }

        /// <summary>
        /// A definition filter matches when "something from that definition is involved", but the
        /// status entity only joins the event data after the action ran, so before and instead
        /// listeners filtered by status name never fire.
        /// </summary>
        [Fact]
        [Trait("Regression", "before-status-applied-definition-filter")]
        public void Before_status_applied_filtered_by_status_can_cancel_it()
        {
            CardRuntime runtime = NewRuntime(@"
status ""Poison""
  tags debuff
  stacking intensity

relic ""Antidote""
  on before_status_applied(Poison):
    cancel
");
            Entity player = runtime.Player!;
            runtime.AddRelic("Antidote");

            runtime.ApplyStatus("Poison", player, 3);

            Assert.Equal(0, player.StacksOf("Poison"));
        }
    }
}
