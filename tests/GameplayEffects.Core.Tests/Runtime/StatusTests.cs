using System.Linq;
using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Runtime.RuntimeTestKit;

namespace GameplayEffects.Tests.Runtime
{
    /// <summary>Stacking modes, caps, decay, removal and immunity (section 3.9).</summary>
    public sealed class StatusTests
    {
        private const string Statuses = """
            status "Poison"
              tags dot, poison, debuff
              stacking intensity

            status "Vuln"
              tags debuff
              stacking duration

            status "Both"
              stacking both
              decay 1

            status "Once"
              stacking none

            status "Fresh"
              stacking refresh

            status "Split"
              stacking separate

            status "Capped"
              stacking intensity
              max_stacks 5

            status "Ticking"
              stacking intensity
              decay 1 on turn_start

            status "Mark"
              flags unique

            status "Antidote"
              immune tag:poison

            status "Shield"
              tags buff
              immune Vuln

            enemy "Golem"
              hp 40
              immune tag:poison

            relic "Watcher"
              on status_resisted(tag:poison):
                gain 1 gold
              on status_removed(tag:poison):
                gain 10 gold
            """;

        [Fact]
        public void Intensity_adds_stacks_to_a_single_instance()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);

            runtime.Execute("apply Poison 3 to enemy");
            runtime.Execute("apply Poison 2 to enemy");

            Assert.Equal(5, Stacks(enemy, "Poison"));
            Assert.Single(enemy.Attached, a => a.Name == "Poison");
        }

        [Fact]
        public void Duration_extends_the_timer_and_keeps_one_stack()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);

            runtime.Execute("apply Vuln 2 to enemy");
            runtime.Execute("apply Vuln 3 to enemy");

            Assert.Equal(1, Stacks(enemy, "Vuln"));
            Assert.Equal(5, Duration(enemy, "Vuln"));
        }

        [Fact]
        public void Both_accumulates_stacks_and_duration()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);

            runtime.Execute("apply Both 2 to enemy");
            runtime.Execute("apply Both 3 to enemy");

            Assert.Equal(5, Stacks(enemy, "Both"));
            Assert.Equal(5, Duration(enemy, "Both"));
        }

        [Fact]
        public void Both_decays_its_duration_not_its_stacks()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);
            Start(runtime);

            runtime.Execute("apply Both 3 to enemy");
            runtime.EndTurn();

            Assert.Equal(3, Stacks(enemy, "Both"));
            Assert.Equal(2, Duration(enemy, "Both"));
        }

        [Fact]
        public void None_ignores_reapplication_while_present()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);

            runtime.Execute("apply Once 2 to enemy");
            runtime.Execute("apply Once 5 to enemy");

            Assert.Equal(2, Stacks(enemy, "Once"));
            Assert.Single(enemy.Attached);
        }

        [Fact]
        public void Refresh_resets_the_timer_to_the_larger_value_instead_of_extending_it()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);

            runtime.Execute("apply Fresh 2 to enemy");
            runtime.Execute("apply Fresh 3 to enemy");
            Assert.Equal(3, Duration(enemy, "Fresh"));

            runtime.Execute("apply Fresh 1 to enemy");
            Assert.Equal(3, Duration(enemy, "Fresh"));
            Assert.Equal(1, Stacks(enemy, "Fresh"));
        }

        [Fact]
        public void Separate_tracks_each_application_as_its_own_instance()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);

            runtime.Execute("apply Split 2 to enemy");
            runtime.Execute("apply Split 3 to enemy");

            Entity[] instances = enemy.Attached.Where(a => a.Name == "Split").ToArray();
            Assert.Equal(2, instances.Length);
            Assert.Equal(new[] { 2, 3 }, instances.Select(i => i.GetInt("stacks")));
            Assert.Equal(5, EvalInt(runtime, "enemy.Split"));
        }

        [Fact]
        public void Max_stacks_caps_both_the_first_application_and_later_ones()
        {
            CardRuntime runtime = Create(Statuses);
            Entity first = Enemy(runtime, name: "A");
            Entity second = Enemy(runtime, name: "B");

            runtime.Execute("apply Capped 9 to target", target: first);
            runtime.Execute("apply Capped 3 to target", target: second);
            runtime.Execute("apply Capped 4 to target", target: second);

            Assert.Equal(5, Stacks(first, "Capped"));
            Assert.Equal(5, Stacks(second, "Capped"));
        }

        [Fact]
        public void Duration_statuses_decay_by_one_at_their_hosts_turn_end_by_default()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);
            Start(runtime);

            runtime.Execute("apply Vuln 2 to enemy");
            runtime.Execute("apply Vuln 2 to player");
            runtime.EndTurn();

            Assert.Equal(1, Duration(enemy, "Vuln"));
            Assert.Equal(1, Duration(runtime.Player!, "Vuln"));

            runtime.EndTurn();
            Assert.Null(enemy.FindAttached("Vuln"));
            Assert.Null(runtime.Player!.FindAttached("Vuln"));
        }

        [Fact]
        public void Intensity_statuses_do_not_decay_unless_told_to()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);
            Start(runtime);

            runtime.Execute("apply Poison 3 to enemy");
            runtime.EndTurn();
            runtime.EndTurn();

            Assert.Equal(3, Stacks(enemy, "Poison"));
        }

        [Fact]
        public void Decay_on_turn_start_ticks_at_the_start_of_the_hosts_turn()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);
            Start(runtime);

            runtime.Execute("apply Ticking 3 to enemy");
            runtime.Execute("apply Ticking 3 to player");

            runtime.EndTurn();

            // The enemy's turn started once and the player's next turn started once.
            Assert.Equal(2, Stacks(enemy, "Ticking"));
            Assert.Equal(2, Stacks(runtime.Player!, "Ticking"));
        }

        [Fact]
        public void A_status_is_removed_when_its_stacks_reach_zero()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);

            runtime.Execute("apply Poison 3 to enemy");
            runtime.Execute("enemy.Poison -2");
            Assert.Equal(1, Stacks(enemy, "Poison"));

            runtime.Execute("enemy.Poison -5");
            Assert.Null(enemy.FindAttached("Poison"));
            Assert.Empty(enemy.Attached);
        }

        [Fact]
        public void Removal_at_zero_raises_status_removed_with_the_status_tags()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);
            runtime.AddRelic("Watcher");

            runtime.Execute("apply Poison 1 to enemy");
            runtime.Execute("enemy.Poison -1");

            Assert.Equal(10, runtime.Player!.GetInt("gold"));
        }

        [Fact]
        public void A_duration_status_is_removed_when_its_duration_reaches_zero()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);

            runtime.Execute("apply Vuln 2 to enemy");
            runtime.Execute("enemy.Vuln -2");

            Assert.Null(enemy.FindAttached("Vuln"));
        }

        [Fact]
        public void Applying_zero_stacks_does_nothing()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);

            runtime.Execute("apply Poison 0 to enemy");

            Assert.Empty(enemy.Attached);
        }

        [Fact]
        public void Unique_statuses_keep_one_instance_per_source()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);
            Entity other = Enemy(runtime, name: "Other");

            runtime.ApplyStatus("Mark", enemy, 1);
            runtime.ApplyStatus("Mark", enemy, 1, source: other);
            runtime.ApplyStatus("Mark", enemy, 2);

            Entity[] marks = enemy.Attached.Where(a => a.Name == "Mark").ToArray();
            Assert.Equal(2, marks.Length);
            Assert.Equal(3, marks.Single(m => m.Source == runtime.Player).GetInt("stacks"));
            Assert.Equal(1, marks.Single(m => m.Source == other).GetInt("stacks"));
        }

        [Fact]
        public void Statuses_without_the_unique_flag_merge_regardless_of_source()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);
            Entity other = Enemy(runtime, name: "Other");

            runtime.ApplyStatus("Poison", enemy, 1);
            runtime.ApplyStatus("Poison", enemy, 1, source: other);

            Assert.Single(enemy.Attached);
            Assert.Equal(2, Stacks(enemy, "Poison"));
        }

        [Fact]
        public void Immune_tag_on_the_host_definition_blocks_matching_statuses()
        {
            CardRuntime runtime = Create(Statuses);
            Entity golem = runtime.SpawnEnemy("Golem");

            Assert.Null(runtime.ApplyStatus("Poison", golem, 3));
            runtime.Execute("apply Vuln 2 to enemy");

            Assert.Null(golem.FindAttached("Poison"));
            Assert.NotNull(golem.FindAttached("Vuln"));
        }

        [Fact]
        public void Immune_on_an_attached_status_blocks_by_tag_or_by_name()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);

            runtime.Execute("apply Antidote 1 to enemy");
            runtime.Execute("apply Shield 1 to enemy");
            runtime.Execute("apply Poison 3 to enemy");
            runtime.Execute("apply Vuln 3 to enemy");
            runtime.Execute("apply Capped 1 to enemy");

            Assert.Null(enemy.FindAttached("Poison"));
            Assert.Null(enemy.FindAttached("Vuln"));
            Assert.NotNull(enemy.FindAttached("Capped"));
        }

        [Fact]
        public void Resisted_statuses_raise_status_resisted_with_the_status_tags()
        {
            CardRuntime runtime = Create(Statuses);
            runtime.SpawnEnemy("Golem");
            runtime.AddRelic("Watcher");

            runtime.Execute("apply Poison 3 to enemy");
            runtime.Execute("apply Vuln 3 to enemy");

            Assert.Equal(1, runtime.Player!.GetInt("gold"));
        }

        [Fact]
        public void Status_entities_are_attached_to_their_host_and_remember_who_applied_them()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);

            runtime.Execute("apply Poison 2 to enemy");

            Entity poison = enemy.FindAttached("Poison")!;
            Assert.Equal(EntityKind.Status, poison.Kind);
            Assert.Same(enemy, poison.Owner);
            Assert.Same(enemy, poison.Controller);
            Assert.Same(runtime.Player, poison.Source);
            Assert.Equal(Zones.Attached, poison.Zone);
            Assert.True(poison.HasTag("dot"));
        }

        [Fact]
        public void Removing_by_tag_removes_every_status_carrying_it()
        {
            CardRuntime runtime = Create(Statuses);
            Entity enemy = Enemy(runtime);

            runtime.Execute("apply Poison 2 to enemy");
            runtime.Execute("apply Vuln 2 to enemy");
            runtime.Execute("apply Capped 2 to enemy");
            runtime.Execute("remove tag:debuff from enemy");

            Assert.Equal(new[] { "Capped" }, enemy.Attached.Select(a => a.Name));
        }

        [Fact]
        public void Timed_statuses_expire_on_the_tick_clock()
        {
            CardRuntime runtime = Create(Statuses, new RuntimeOptions { Clock = new TickClock(10) });
            Entity enemy = Enemy(runtime);

            runtime.Execute("apply Poison 40% for 2s to enemy");
            Assert.Equal(40, Stacks(enemy, "Poison"));

            runtime.Tick(19);
            Assert.Equal(40, Stacks(enemy, "Poison"));

            runtime.Tick(1);
            Assert.Null(enemy.FindAttached("Poison"));
        }
    }
}
