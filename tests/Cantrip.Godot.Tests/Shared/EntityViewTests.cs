using System.Linq;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// The entity mapping: what a game is told about an actor, and whether the numbers in it are
    /// the ones the rules would use.
    /// </summary>
    public sealed class EntityViewTests
    {
        [Fact]
        public void An_actor_is_described_by_id_kind_side_and_place()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity enemy);

            EntityView view = EntityView.Of(enemy);

            Assert.Equal(enemy.Id, view.Id);
            Assert.Equal("Jaw Worm", view.Name);
            Assert.Equal("actor", view.Kind);
            Assert.Equal("enemy", view.Team);
            Assert.Equal(Zones.Board, view.Zone);
            Assert.True(view.Alive);
            Assert.False(view.Dead);
            Assert.False(view.Removed);
            Assert.Equal(0, view.Owner);

            // The player is on the same board but the other side of it.
            Assert.Equal("player", EntityView.Of(runtime.Player!).Team);
        }

        [Fact]
        public void Stats_are_read_through_the_modifier_pipeline()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity enemy);
            runtime.ApplyStatus("Strength", enemy, 3);

            EntityView view = EntityView.Of(enemy);

            Assert.Equal(40, view.Stats["hp"]);
            Assert.Equal(40, view.Stats["max_hp"]);
            Assert.Equal(0, view.Stats["block"]);

            // Strength modifies damage, not a stat, so nothing here moves; what matters is that
            // the values come from Get and not from the printed definition.
            enemy.SetBase("hp", 31);
            Assert.Equal(31, EntityView.Of(enemy).Stats["hp"]);
        }

        [Fact]
        public void A_game_can_ask_for_only_the_stats_it_draws()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity enemy);

            EntityView view = EntityView.Of(enemy, new[] { "hp", "shield" });

            Assert.Equal(new[] { "hp" }, view.Stats.Keys);
            Assert.Equal(40, view.Stats["hp"]);

            // A stat the entity has not got is left out rather than reported as zero.
            Assert.False(view.Stats.ContainsKey("shield"));
        }

        [Fact]
        public void Tags_come_out_in_a_fixed_order()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity _);
            Entity fireball = runtime.AddCard("Fireball", Zones.Hand);

            Assert.Equal(new[] { "attack", "fire" }, EntityView.Of(fireball).Tags);
            Assert.Equal("card", EntityView.Of(fireball).Kind);
            Assert.Equal(Zones.Hand, EntityView.Of(fireball).Zone);
            Assert.Equal(runtime.Player!.Id, EntityView.Of(fireball).Owner);
        }

        // Statuses -----------------------------------------------------------------------------

        [Fact]
        public void A_duration_status_counts_by_its_duration_not_its_stacks()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity enemy);
            runtime.ApplyStatus("Vulnerable", enemy, 2);

            StatusView status = Assert.Single(EntityView.Of(enemy).Statuses);

            Assert.Equal("Vulnerable", status.Name);
            Assert.Equal(2, status.Counter);
            Assert.Equal(2, status.Duration);
            Assert.Equal(1, status.Stacks);

            // Which is exactly what the core says when asked each question separately.
            Assert.Equal(enemy.CounterOf("Vulnerable"), status.Counter);
            Assert.Equal(enemy.StacksOf("Vulnerable"), status.Stacks);
        }

        [Fact]
        public void An_intensity_status_counts_by_its_stacks()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity enemy);
            runtime.ApplyStatus("Burn", enemy, 4);

            StatusView status = Assert.Single(EntityView.Of(enemy).Statuses);

            Assert.Equal(4, status.Counter);
            Assert.Equal(4, status.Stacks);
            Assert.Equal(enemy.CounterOf("Burn"), status.Counter);
            Assert.Equal(enemy.StacksOf("Burn"), status.Stacks);
            Assert.Equal(new[] { "debuff", "dot", "fire" }, status.Tags);
        }

        [Fact]
        public void A_hidden_status_is_listed_and_flagged_rather_than_left_out()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity enemy);
            runtime.ApplyStatus("Marked", enemy, 1);
            runtime.ApplyStatus("Burn", enemy, 2);

            StatusView[] statuses = EntityView.Of(enemy).Statuses.ToArray();

            // A status bar filters on the flag; a debug view shows both. The mapping decides neither.
            Assert.True(statuses.Single(s => s.Name == "Marked").Hidden);
            Assert.False(statuses.Single(s => s.Name == "Burn").Hidden);
        }

        [Fact]
        public void Statuses_come_in_application_order_and_removed_ones_are_gone()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity enemy);
            runtime.ApplyStatus("Burn", enemy, 2);
            runtime.ApplyStatus("Vulnerable", enemy, 2);

            Assert.Equal(new[] { "Burn", "Vulnerable" }, EntityView.Of(enemy).Statuses.Select(s => s.Name));

            runtime.Execute("remove Burn from enemy");

            Assert.Equal(new[] { "Vulnerable" }, EntityView.Of(enemy).Statuses.Select(s => s.Name));
        }

        [Fact]
        public void An_intent_is_empty_until_it_has_been_rolled()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity enemy);

            Assert.Equal(string.Empty, EntityView.Of(enemy).Intent);

            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal("Chomp", EntityView.Of(enemy).Intent);
            Assert.Equal(enemy.Intent, EntityView.Of(enemy).Intent);
        }

        [Fact]
        public void A_dead_actor_is_neither_alive_nor_removed()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity enemy);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Execute("kill enemy");

            EntityView view = EntityView.Of(enemy);
            Assert.True(view.Dead);
            Assert.False(view.Alive);
        }

        [Fact]
        public void An_ability_is_listed_by_id_so_a_real_time_game_can_fire_it()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity _);
            Entity player = runtime.Player!;
            runtime.ApplyStatus("Burn", player, 1);

            EntityView view = EntityView.Of(player);

            // Nothing was granted, so the list is empty rather than absent, and the status that was
            // applied is not mistaken for one.
            Assert.Empty(view.Abilities);
            Assert.Single(view.Statuses);
        }

        [Fact]
        public void Reading_nothing_is_a_mistake_worth_hearing_about()
        {
            Assert.Throws<System.ArgumentNullException>(() => EntityView.Of(null!));
            Assert.Throws<System.ArgumentNullException>(() => StatusView.Of(null!));
        }
    }
}
