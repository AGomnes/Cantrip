using System;
using Cantrip.Runtime;
using Xunit;
using static Cantrip.Tests.Runtime.RuntimeTestKit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>The modifier pipeline: layers, scopes, filters, caching and breakdowns.</summary>
    public sealed class ModifierTests
    {
        private const string Layers = """
            status "Plus"
              stacking intensity
              modify damage: +stacks

            status "Times"
              modify damage: x2

            status "Cap"
              modify damage: clamp 0..7

            status "Floor"
              modify damage: clamp 3..10

            status "Ceiling"
              modify damage: clamp 4

            status "Fixed4"
              modify damage: =4

            status "Fixed9"
              modify damage: =9

            status "Half"
              modify damage: x50%

            status "Minus"
              modify damage: -10
            """;

        private static int DamageDealt(CardRuntime runtime, Entity enemy, int amount)
        {
            int before = Hp(enemy);
            runtime.Execute($"deal {amount} to enemy");
            return before - Hp(enemy);
        }

        // Layers ----------------------------------------------------------------------------

        [Fact]
        public void Add_layer_adds_the_evaluated_amount()
        {
            CardRuntime runtime = Create(Layers);
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Plus", runtime.Player!, 2);

            Assert.Equal(7, DamageDealt(runtime, enemy, 5));
        }

        [Fact]
        public void Add_runs_before_multiply_by_default()
        {
            CardRuntime runtime = Create(Layers);
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Times", runtime.Player!);
            runtime.ApplyStatus("Plus", runtime.Player!, 2);

            Assert.Equal(14, DamageDealt(runtime, enemy, 5));
        }

        [Fact]
        public void Clamp_runs_after_multiply_and_override_runs_last()
        {
            CardRuntime runtime = Create(Layers);
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Plus", runtime.Player!, 2);
            runtime.ApplyStatus("Times", runtime.Player!);
            runtime.ApplyStatus("Cap", runtime.Player!);
            Assert.Equal(7, DamageDealt(runtime, enemy, 5));

            runtime.ApplyStatus("Fixed4", runtime.Player!);
            Assert.Equal(4, DamageDealt(runtime, enemy, 5));
        }

        [Fact]
        public void Ruleset_modifier_layers_reorder_the_pipeline()
        {
            CardRuntime runtime = Create(Layers + """

                ruleset
                  modifier_layers: multiply, add, clamp, override
                """);
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Plus", runtime.Player!, 2);
            runtime.ApplyStatus("Times", runtime.Player!);

            Assert.Equal(12, DamageDealt(runtime, enemy, 5));
        }

        [Fact]
        public void Ruleset_can_put_override_before_add()
        {
            CardRuntime runtime = Create(Layers + """

                ruleset
                  modifier_layers: override, add
                """);
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Fixed4", runtime.Player!);
            runtime.ApplyStatus("Plus", runtime.Player!, 2);

            Assert.Equal(6, DamageDealt(runtime, enemy, 5));
        }

        [Fact]
        public void Clamp_range_raises_values_below_the_lower_bound()
        {
            CardRuntime runtime = Create(Layers);
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Floor", runtime.Player!);

            Assert.Equal(3, DamageDealt(runtime, enemy, 1));
            Assert.Equal(10, DamageDealt(runtime, enemy, 25));
            Assert.Equal(6, DamageDealt(runtime, enemy, 6));
        }

        [Fact]
        public void Clamp_with_a_bare_number_is_a_ceiling_only()
        {
            CardRuntime runtime = Create(Layers);
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Ceiling", runtime.Player!);

            Assert.Equal(4, DamageDealt(runtime, enemy, 9));
            Assert.Equal(1, DamageDealt(runtime, enemy, 1));
        }

        [Fact]
        public void Override_uses_the_most_recently_registered_modifier()
        {
            CardRuntime later9 = Create(Layers);
            Entity enemy = Enemy(later9);
            later9.ApplyStatus("Fixed4", later9.Player!);
            later9.ApplyStatus("Fixed9", later9.Player!);
            Assert.Equal(9, DamageDealt(later9, enemy, 1));

            CardRuntime later4 = Create(Layers);
            Entity other = Enemy(later4);
            later4.ApplyStatus("Fixed9", later4.Player!);
            later4.ApplyStatus("Fixed4", later4.Player!);
            Assert.Equal(4, DamageDealt(later4, other, 1));
        }

        [Fact]
        public void Percent_multipliers_are_fractions()
        {
            CardRuntime runtime = Create(Layers);
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Half", runtime.Player!);

            Assert.Equal(5, DamageDealt(runtime, enemy, 10));
        }

        [Fact]
        public void Damage_is_floored_and_never_negative()
        {
            CardRuntime runtime = Create(Layers);
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Half", runtime.Player!);
            Assert.Equal(3, DamageDealt(runtime, enemy, 7));

            runtime.ApplyStatus("Minus", runtime.Player!);
            Assert.Equal(0, DamageDealt(runtime, enemy, 7));
        }

        [Fact]
        public void Damage_is_floored_only_after_both_damage_and_damage_taken()
        {
            CardRuntime runtime = Create("""
                status "Weak"
                  modify damage: x0.75
                status "Vulnerable"
                  modify damage_taken: x1.5
                """);
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Weak", runtime.Player!);
            runtime.ApplyStatus("Vulnerable", enemy);

            // 5 x 0.75 x 1.5 = 5.625. Flooring in between would give floor(3.75) x 1.5 = 4.5 -> 4.
            Assert.Equal(5, DamageDealt(runtime, enemy, 5));
        }

        // Default scopes --------------------------------------------------------------------

        private const string Scopes = """
            status "Strength"
              stacking intensity
              modify damage: +stacks

            status "Vulnerable"
              modify damage_taken: x2

            status "Sturdy"
              modify block_taken: +3

            status "Sick"
              modify heal_taken: -3

            status "Armored"
              stacking intensity
              modify armor: +stacks

            relic "Whetstone"
              modify damage: +3

            relic "Plating"
              modify damage_taken: -2

            relic "Gauntlet"
              modify block: +2

            relic "Salve"
              modify heal: +4

            relic "Discount"
              modify cost: -1

            relic "Heart"
              modify max_hp: +10

            relic "Battery"
              modify max_energy: +1

            card "Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 6 to target

            card "Heavy"
              cost 1
              target enemy
              modify damage: +4
              effect:
                deal 6 to target

            card "Cheap"
              cost 2
              modify cost: -1
            """;

        [Fact]
        public void A_status_modifier_applies_to_its_host_only()
        {
            CardRuntime runtime = Create(Scopes);
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Strength", enemy, 3);

            runtime.Execute("deal 5 to player", self: enemy);
            Assert.Equal(72, Hp(runtime.Player!));

            Assert.Equal(5, DamageDealt(runtime, enemy, 5));
        }

        [Fact]
        public void A_relic_modifier_applies_to_its_holder_including_their_cards()
        {
            CardRuntime runtime = Create(Scopes);
            Entity enemy = Enemy(runtime);
            runtime.AddRelic("Whetstone");

            Assert.Equal(8, DamageDealt(runtime, enemy, 5));

            runtime.AddCard("Strike", Zones.Hand);
            Assert.Equal(PlayResult.Played, runtime.Play("Strike", enemy));
            Assert.Equal(50 - 8 - 9, Hp(enemy));

            runtime.Execute("deal 5 to player", self: enemy);
            Assert.Equal(75, Hp(runtime.Player!));
        }

        [Fact]
        public void A_card_modifier_only_boosts_that_card()
        {
            CardRuntime runtime = Create(Scopes);
            Entity enemy = Enemy(runtime);
            runtime.AddCard("Heavy", Zones.Hand);
            runtime.AddCard("Strike", Zones.Hand);

            runtime.Play("Strike", enemy);
            Assert.Equal(44, Hp(enemy));

            runtime.Play("Heavy", enemy);
            Assert.Equal(34, Hp(enemy));
        }

        [Fact]
        public void Damage_taken_applies_to_damage_dealt_to_the_anchor()
        {
            CardRuntime runtime = Create(Scopes);
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Vulnerable", enemy);

            Assert.Equal(10, DamageDealt(runtime, enemy, 5));

            runtime.Execute("deal 5 to player", self: enemy);
            Assert.Equal(75, Hp(runtime.Player!));
        }

        [Fact]
        public void A_relic_damage_taken_modifier_protects_its_holder()
        {
            CardRuntime runtime = Create(Scopes);
            Entity enemy = Enemy(runtime);
            runtime.AddRelic("Plating");

            runtime.Execute("deal 5 to player", self: enemy);
            Assert.Equal(77, Hp(runtime.Player!));
            Assert.Equal(5, DamageDealt(runtime, enemy, 5));
        }

        [Fact]
        public void Block_and_block_taken_modify_block_gained()
        {
            CardRuntime runtime = Create(Scopes);
            runtime.AddRelic("Gauntlet");
            runtime.ApplyStatus("Sturdy", runtime.Player!);

            runtime.Execute("block 5");

            Assert.Equal(10, runtime.Player!.GetInt("block"));
        }

        [Fact]
        public void Heal_and_heal_taken_modify_healing()
        {
            CardRuntime runtime = Create(Scopes);
            runtime.Player!.SetBase("hp", 50);
            runtime.AddRelic("Salve");
            runtime.ApplyStatus("Sick", runtime.Player!);

            runtime.Execute("heal 5");

            Assert.Equal(56, Hp(runtime.Player!));
        }

        [Fact]
        public void Heal_taken_on_an_enemy_does_not_touch_the_players_healing()
        {
            CardRuntime runtime = Create(Scopes);
            Entity enemy = Enemy(runtime);
            runtime.Player!.SetBase("hp", 50);
            runtime.ApplyStatus("Sick", enemy);

            runtime.Execute("heal 5");

            Assert.Equal(55, Hp(runtime.Player!));
        }

        [Fact]
        public void A_relic_cost_modifier_discounts_every_card_its_holder_owns()
        {
            CardRuntime runtime = Create(Scopes);
            Entity strike = runtime.AddCard("Strike", Zones.Hand);
            runtime.AddRelic("Discount");

            Assert.Equal(0, runtime.CostOf(strike));
        }

        [Fact]
        public void Cost_never_goes_below_zero()
        {
            CardRuntime runtime = Create(Scopes);
            Entity strike = runtime.AddCard("Strike", Zones.Hand);
            runtime.AddRelic("Discount");
            runtime.AddRelic("Discount");

            Assert.Equal(0, runtime.CostOf(strike));
        }

        [Fact]
        [Trait("Regression", "cost-double-modified")]
        public void A_cost_modifier_is_applied_once()
        {
            CardRuntime runtime = Create(Scopes + """

                card "Big"
                  cost 3
                """);
            Entity big = runtime.AddCard("Big", Zones.Hand);
            runtime.AddRelic("Discount");

            // Regression: CostOf once ran the cost channel again on top of Entity.Get("cost").
            Assert.Equal(2, runtime.CostOf(big));
        }

        [Fact]
        [Trait("Regression", "cost-double-modified")]
        public void A_card_anchored_cost_modifier_discounts_only_that_card_once()
        {
            CardRuntime runtime = Create(Scopes);
            Entity cheap = runtime.AddCard("Cheap", Zones.Hand);
            Entity strike = runtime.AddCard("Strike", Zones.Hand);

            Assert.Equal(1, runtime.CostOf(strike));
            Assert.Equal(1, runtime.CostOf(cheap));
        }

        [Fact]
        public void Card_modifiers_are_only_active_while_the_card_is_in_hand()
        {
            CardRuntime runtime = Create(Scopes);
            Entity cheap = runtime.AddCard("Cheap", Zones.Draw);

            Assert.Equal(2, runtime.CostOf(cheap));
            Assert.Equal(2, EvalInt(runtime, "c.cost", locals: new System.Collections.Generic.Dictionary<string, Entity> { ["c"] = cheap }));
        }

        [Fact]
        public void Other_channels_modify_that_stat_on_the_anchor()
        {
            CardRuntime runtime = Create(Scopes);
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Armored", runtime.Player!, 2);

            Assert.Equal(2, runtime.Player!.GetInt("armor"));
            Assert.Equal(0, enemy.GetInt("armor"));
            Assert.Equal(2, EvalInt(runtime, "player.armor"));
        }

        [Fact]
        public void A_relic_stat_modifier_raises_resource_bounds_and_resets()
        {
            CardRuntime runtime = Create(Scopes);
            Enemy(runtime);
            runtime.AddRelic("Heart");
            runtime.AddRelic("Battery");
            Start(runtime);

            runtime.Execute("heal 20");
            Assert.Equal(90, Hp(runtime.Player!));

            runtime.EndTurn();
            Assert.Equal(4, runtime.Player!.GetInt("energy"));
        }

        [Fact]
        public void Stat_cache_is_invalidated_when_state_changes()
        {
            CardRuntime runtime = Create(Scopes);
            Entity player = runtime.Player!;
            runtime.ApplyStatus("Armored", player, 2);
            Assert.Equal(2, player.GetInt("armor"));

            long hits = runtime.State.Modifiers.CacheHits;
            Assert.Equal(2, player.GetInt("armor"));
            Assert.Equal(hits + 1, runtime.State.Modifiers.CacheHits);

            runtime.Execute("apply Armored 3 to player");
            Assert.Equal(5, player.GetInt("armor"));

            runtime.Execute("player.Armored -5");
            Assert.Equal(0, player.GetInt("armor"));

            player.SetBase("armor", 10);
            Assert.Equal(10, player.GetInt("armor"));
        }

        // Explicit scopes and filters -------------------------------------------------------

        private const string Filtered = """
            relic "Kindler"
              modify damage where tag:fire: +3

            relic "Codex"
              modify damage where tag:fire, source:self: x2

            relic "Spite"
              modify damage_taken of everyone where source:self: +1

            relic "Curse"
              modify damage_taken of enemies: +2

            relic "Hoard"
              modify armor of enemies where hp > 20: +1

            relic "Pyre"
              modify cost of cards where tag:fire: -1

            status "Burn"
              tags fire, dot
              on turn_end:
                deal stacks to owner, ignore block

            card "Flame"
              cost 1
              target enemy
              tags attack, fire
              effect:
                deal 3 to target

            card "Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 3 to target
            """;

        [Fact]
        public void Where_filters_test_the_tags_of_the_value_being_computed()
        {
            CardRuntime runtime = Create(Filtered);
            Entity enemy = Enemy(runtime);
            runtime.AddRelic("Kindler");

            Assert.Equal(5, DamageDealt(runtime, enemy, 5));

            runtime.Execute("deal 5 to enemy as fire");
            Assert.Equal(50 - 5 - 8, Hp(enemy));
        }

        [Fact]
        public void Where_filters_see_the_tags_of_the_card_being_played()
        {
            CardRuntime runtime = Create(Filtered);
            Entity enemy = Enemy(runtime);
            runtime.AddRelic("Kindler");
            runtime.AddCard("Flame", Zones.Hand);
            runtime.AddCard("Strike", Zones.Hand);

            runtime.Play("Flame", enemy);
            runtime.Play("Strike", enemy);

            Assert.Equal(50 - 6 - 3, Hp(enemy));
        }

        [Fact]
        public void Source_self_matches_damage_from_the_holders_cards()
        {
            CardRuntime runtime = Create(Filtered);
            Entity enemy = Enemy(runtime);
            runtime.AddRelic("Codex");
            runtime.AddCard("Flame", Zones.Hand);

            runtime.Play("Flame", enemy);

            Assert.Equal(44, Hp(enemy));
        }

        [Fact]
        public void A_status_damage_belongs_to_its_host_not_to_whoever_applied_it()
        {
            CardRuntime runtime = Create(Filtered);
            Entity enemy = Enemy(runtime);
            runtime.AddRelic("Codex");
            runtime.AddRelic("Kindler");
            Start(runtime);

            runtime.Execute("apply Burn 4 to enemy");
            runtime.EndTurn();

            // Burn is fire, but its damage comes from the enemy's side, so neither relic applies.
            Assert.Equal(46, Hp(enemy));
        }

        [Fact]
        public void Of_scope_replaces_the_default_scope()
        {
            CardRuntime runtime = Create(Filtered);
            Entity enemy = Enemy(runtime);
            runtime.AddRelic("Spite");

            Assert.Equal(6, DamageDealt(runtime, enemy, 5));

            runtime.Execute("deal 5 to player", self: enemy);
            Assert.Equal(75, Hp(runtime.Player!));
        }

        [Fact]
        public void Of_scope_with_a_where_clause_filters_the_subjects()
        {
            CardRuntime runtime = Create(Filtered);
            Entity big = Enemy(runtime, 30, "Big");
            Entity small = Enemy(runtime, 10, "Small");
            runtime.AddRelic("Hoard");

            Assert.Equal(1, big.GetInt("armor"));
            Assert.Equal(0, small.GetInt("armor"));
            Assert.Equal(0, runtime.Player!.GetInt("armor"));

            big.SetBase("hp", 15);
            Assert.Equal(0, big.GetInt("armor"));
        }

        [Fact]
        public void Of_scope_on_cost_discounts_only_matching_cards()
        {
            CardRuntime runtime = Create(Filtered);
            Entity flame = runtime.AddCard("Flame", Zones.Hand);
            Entity strike = runtime.AddCard("Strike", Zones.Hand);
            runtime.AddRelic("Pyre");

            Assert.Equal(0, runtime.CostOf(flame));
            Assert.Equal(1, runtime.CostOf(strike));
        }

        [Fact]
        public void Of_scope_enemies_boosts_damage_to_enemies()
        {
            CardRuntime runtime = Create(Filtered);
            Entity enemy = Enemy(runtime);
            runtime.AddRelic("Curse");

            Assert.Equal(7, DamageDealt(runtime, enemy, 5));
        }

        [Fact]
        [Trait("Regression", "of-scope-attacker-perspective")]
        public void Of_scope_enemies_is_relative_to_the_modifier_owner_not_the_attacker()
        {
            CardRuntime runtime = Create(Filtered);
            Entity enemy = Enemy(runtime);
            runtime.AddRelic("Curse");

            // "Enemies take 2 more damage" on the player's relic must not hurt the player. Regression:
            // the scope was once read from the attacker's side, which flipped what `enemies` meant.
            runtime.Execute("deal 5 to player", self: enemy);

            Assert.Equal(75, Hp(runtime.Player!));
        }

        // Explain ---------------------------------------------------------------------------

        [Fact]
        public void Explain_lists_every_step_in_layer_order()
        {
            CardRuntime runtime = Create("""
                status "Strength"
                  stacking intensity
                  modify damage: +stacks

                relic "Codex"
                  modify damage where tag:fire: x1.5

                relic "Lead"
                  modify damage: -1
                """);
            Entity enemy = Enemy(runtime);
            runtime.AddRelic("Codex");
            runtime.ApplyStatus("Strength", runtime.Player!, 3);
            runtime.AddRelic("Lead");

            var query = new ModifierQuery("damage") { Source = runtime.Player, Subject = enemy, Tags = new[] { "fire" } };
            ModifierResult result = runtime.State.Modifiers.Explain(query, Num.FromInt(6));

            Assert.Equal(Num.FromInt(6), result.Base);
            Assert.Equal(Num.Parse("12"), result.Final);
            Assert.Equal(3, result.Steps.Count);
            Assert.Equal("base 6 → +3 Strength → -1 Lead → ×1.5 Codex → 12", result.ToString());
        }

        [Fact]
        public void Explain_shows_clamp_and_override_steps_and_matches_compute()
        {
            CardRuntime runtime = Create(Layers);
            Entity enemy = Enemy(runtime);
            runtime.ApplyStatus("Cap", runtime.Player!);
            runtime.ApplyStatus("Fixed4", runtime.Player!);

            var query = new ModifierQuery("damage") { Source = runtime.Player, Subject = enemy };
            ModifierResult result = runtime.State.Modifiers.Explain(query, Num.FromInt(20));

            Assert.Equal("base 20 → clamp Cap → =4 Fixed4 → 4", result.ToString());
            Assert.Equal(runtime.State.Modifiers.Compute(query, Num.FromInt(20)), result.Final);
        }

        [Fact]
        public void Explain_with_no_applicable_modifiers_is_just_the_base()
        {
            CardRuntime runtime = Create(Layers);
            Entity enemy = Enemy(runtime);

            ModifierResult result = runtime.State.Modifiers.Explain(new ModifierQuery("damage") { Source = runtime.Player, Subject = enemy }, Num.FromInt(6));

            Assert.Empty(result.Steps);
            Assert.Equal("base 6 → 6", result.ToString());
        }

        [Fact]
        public void Modifiers_stop_when_their_owner_is_removed()
        {
            CardRuntime runtime = Create(Scopes);
            Entity enemy = Enemy(runtime);
            Entity relic = runtime.AddRelic("Whetstone");
            int before = runtime.State.Modifiers.Count;

            runtime.Execute("destroy target", target: relic);

            Assert.Equal(before - 1, runtime.State.Modifiers.Count);
            Assert.Equal(5, DamageDealt(runtime, enemy, 5));
        }

        [Fact]
        public void A_modifier_that_reads_its_own_stat_sees_the_base_value()
        {
            CardRuntime runtime = Create("""
                status "Echo"
                  modify armor: +armor
                """);
            Entity player = runtime.Player!;
            player.SetBase("armor", 3);
            runtime.ApplyStatus("Echo", player);

            Assert.Equal(6, player.GetInt("armor"));
        }

        [Fact]
        public void Unrelated_actors_never_see_a_status_modifier()
        {
            CardRuntime runtime = Create(Scopes);
            Entity a = Enemy(runtime, name: "A");
            Entity b = Enemy(runtime, name: "B");
            runtime.ApplyStatus("Strength", a, 4);

            runtime.Execute("deal 5 to player", self: b);

            Assert.Equal(75, Hp(runtime.Player!));
            Assert.Throws<ArgumentNullException>(() => new CardRuntime(null!));
        }
    }
}
