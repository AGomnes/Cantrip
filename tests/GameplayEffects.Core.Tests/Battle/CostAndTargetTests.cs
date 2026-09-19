#nullable enable
using System.Collections.Generic;
using GameplayEffects.Runtime;
using Xunit;

namespace GameplayEffects.Tests.Battle
{
    /// <summary>Cost modifiers, X-cost cards, and how each <c>target</c> kind resolves.</summary>
    public sealed class CostAndTargetTests
    {
        private const string Content = """
            card "Fire Bolt"
              cost 2
              target enemy
              tags attack, fire
              effect:
                deal 5 to target

            card "Plain Bolt"
              cost 2
              target enemy
              tags attack
              effect:
                deal 5 to target

            card "Freebie"
              cost 0
              effect:
                block 1

            card "Heavy Blade"
              cost 1
              target enemy
              tags attack
              modify cost: +1
              effect:
                deal 1 to target

            card "Whirlwind"
              cost x
              tags attack
              effect:
                repeat x:
                  deal 5 to enemies

            card "Jab"
              cost 0
              target enemy
              effect:
                deal 3 to target

            card "Mend"
              cost 0
              target ally
              effect:
                heal 4 to target

            card "Self Harm"
              cost 0
              target self
              effect:
                deal 3 to target

            card "Zap"
              cost 0
              target any
              effect:
                deal 2 to target

            card "Shout"
              cost 0
              effect:
                block 2

            status "Discount"
              stacking intensity
              modify cost: -1

            relic "Codex"
              modify cost of cards where tag:fire: -1

            status "Taunt"
              stacking none
              modify targetable of allies where source:enemies, not it.has(Taunt): set 0

            status "Stealth"
              stacking none
              modify targetable: set 0

            enemy "Alpha"
              hp 30

            enemy "Beta"
              hp 30

            enemy "Gamma"
              hp 30
            """;

        private static CardRuntime Setup(out Entity player, IChoiceProvider? chooser = null)
        {
            CardRuntime runtime = BattleKit.Create(Content, chooser: chooser);
            player = runtime.CreatePlayer();
            return runtime;
        }

        // Cost ----------------------------------------------------------------------------------

        [Fact]
        [Trait("Regression", "cost-modifiers-applied-twice")]
        public void A_relic_cost_modifier_discounts_matching_cards_once()
        {
            CardRuntime runtime = Setup(out Entity player);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            runtime.AddRelic("Codex");
            Entity fire = runtime.AddCard("Fire Bolt", Zones.Hand);
            Entity plain = runtime.AddCard("Plain Bolt", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(1, runtime.CostOf(fire));
            Assert.Equal(2, runtime.CostOf(plain));

            Assert.Equal(PlayResult.Played, runtime.Play(fire, alpha));
            Assert.Equal(2, player.GetInt("energy"));
        }

        [Fact]
        [Trait("Regression", "cost-modifiers-applied-twice")]
        public void A_status_cost_modifier_discounts_every_card_of_its_host_once()
        {
            CardRuntime runtime = Setup(out Entity player);
            runtime.SpawnEnemy("Alpha");
            Entity fire = runtime.AddCard("Fire Bolt", Zones.Hand);
            Entity plain = runtime.AddCard("Plain Bolt", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Discount", player);

            Assert.Equal(1, runtime.CostOf(fire));
            Assert.Equal(1, runtime.CostOf(plain));
        }

        [Fact]
        [Trait("Regression", "cost-modifiers-applied-twice")]
        public void A_cost_modifier_on_a_card_changes_only_that_card_while_it_is_active()
        {
            CardRuntime runtime = Setup(out _);
            runtime.SpawnEnemy("Alpha");
            Entity inHand = runtime.AddCard("Heavy Blade", Zones.Hand);
            Entity inDraw = runtime.AddCard("Heavy Blade", Zones.Draw);
            Entity other = runtime.AddCard("Plain Bolt", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(2, runtime.CostOf(inHand));
            Assert.Equal(1, runtime.CostOf(inDraw));
            Assert.Equal(2, runtime.CostOf(other));
        }

        [Fact]
        public void Cost_modifiers_never_take_a_cost_below_zero()
        {
            CardRuntime runtime = Setup(out Entity player);
            runtime.SpawnEnemy("Alpha");
            Entity freebie = runtime.AddCard("Freebie", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Discount", player, 3);

            Assert.Equal(0, runtime.CostOf(freebie));
            Assert.Equal(PlayResult.Played, runtime.Play(freebie));
            Assert.Equal(3, player.GetInt("energy"));
        }

        [Fact]
        public void X_cost_spends_all_energy_and_binds_it_to_x()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = BattleKit.Create(Content, host: recorder);
            Entity player = runtime.CreatePlayer();
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            Entity whirlwind = runtime.AddCard("Whirlwind", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.True(runtime.IsXCost(whirlwind));
            Assert.Equal(3, runtime.CostOf(whirlwind));
            Assert.Equal(PlayResult.Played, runtime.Play(whirlwind));

            Assert.Equal(0, player.GetInt("energy"));
            Assert.Equal(15, alpha.GetInt("hp"));
            Assert.Equal(15, beta.GetInt("hp"));
            Assert.Equal(3, Assert.Single(recorder.Named("card_played")).Amount);
        }

        [Fact]
        public void X_cost_is_playable_with_no_energy_and_does_nothing()
        {
            CardRuntime runtime = Setup(out Entity player);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity whirlwind = runtime.AddCard("Whirlwind", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            player.SetBase("energy", 0);

            Assert.Equal(PlayResult.Played, runtime.Play(whirlwind));
            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(Zones.Discard, whirlwind.Zone);
        }

        [Fact]
        public void X_cost_ignores_cost_modifiers()
        {
            CardRuntime runtime = Setup(out Entity player);
            runtime.SpawnEnemy("Alpha");
            Entity whirlwind = runtime.AddCard("Whirlwind", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Discount", player);

            Assert.Equal(3, runtime.CostOf(whirlwind));
        }

        // Targets -------------------------------------------------------------------------------

        [Fact]
        public void An_enemy_card_targets_the_only_enemy_without_asking()
        {
            var chooser = new BattleRecordingChooser();
            CardRuntime runtime = Setup(out _, chooser);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            runtime.AddCard("Jab", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play("Jab"));

            Assert.Equal(27, alpha.GetInt("hp"));
            Assert.Empty(chooser.Requests);
        }

        [Fact]
        public void An_enemy_card_asks_the_chooser_when_there_are_several_enemies()
        {
            var chooser = new BattleRecordingChooser(new ScriptedChooser("Beta"));
            CardRuntime runtime = Setup(out Entity player, chooser);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            runtime.AddCard("Jab", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play("Jab"));

            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(27, beta.GetInt("hp"));
            ChoiceRequest request = Assert.Single(chooser.Requests);
            Assert.Equal(new[] { alpha, beta }, request.Options);
            Assert.Equal(1, request.Min);
            Assert.Equal(1, request.Max);
            Assert.Same(player, request.Chooser);
        }

        [Fact]
        public void An_answer_outside_the_options_falls_back_to_the_first_enemy()
        {
            Entity? player = null;
            var chooser = new BattleRecordingChooser((request, state) => new[] { player! });
            CardRuntime runtime = Setup(out player, chooser);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            runtime.AddCard("Jab", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play("Jab"));

            Assert.Equal(27, alpha.GetInt("hp"));
            Assert.Equal(30, beta.GetInt("hp"));
            Assert.Equal(80, player.GetInt("hp"));
        }

        [Fact]
        public void An_explicit_target_skips_the_chooser()
        {
            var chooser = new BattleRecordingChooser();
            CardRuntime runtime = Setup(out _, chooser);
            runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            runtime.AddCard("Jab", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play("Jab", beta));

            Assert.Equal(27, beta.GetInt("hp"));
            Assert.Empty(chooser.Requests);
        }

        [Fact]
        public void An_ally_card_defaults_to_the_player_and_refuses_enemies()
        {
            CardRuntime runtime = Setup(out Entity player);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            runtime.AddCard("Mend", Zones.Hand);
            runtime.AddCard("Mend", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            player.SetBase("hp", 70);

            Assert.Equal(PlayResult.InvalidTarget, runtime.Play("Mend", alpha));
            Assert.Equal(PlayResult.Played, runtime.Play("Mend"));
            Assert.Equal(74, player.GetInt("hp"));
            Assert.Equal(PlayResult.Played, runtime.Play("Mend", player));
            Assert.Equal(78, player.GetInt("hp"));
        }

        // Target validity -----------------------------------------------------------------------

        [Fact]
        public void A_taunt_minion_makes_its_companions_unavailable()
        {
            CardRuntime runtime = Setup(out _);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            runtime.AddCard("Jab", Zones.Hand);
            runtime.AddCard("Jab", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Taunt", beta);

            Assert.Equal(PlayResult.InvalidTarget, runtime.Play("Jab", alpha));
            Assert.Equal(PlayResult.Played, runtime.Play("Jab", beta));

            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(27, beta.GetInt("hp"));
        }

        [Fact]
        public void Two_taunt_minions_are_both_available_and_nothing_else_is()
        {
            var chooser = new BattleRecordingChooser(new ScriptedChooser("Gamma"));
            CardRuntime runtime = Setup(out _, chooser);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            Entity gamma = runtime.SpawnEnemy("Gamma");
            runtime.AddCard("Jab", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Taunt", beta);
            runtime.ApplyStatus("Taunt", gamma);

            Assert.Equal(PlayResult.Played, runtime.Play("Jab"));

            // Each Taunt leaves the other in: the rule is "must have Taunt", not "must be me".
            ChoiceRequest request = Assert.Single(chooser.Requests);
            Assert.Equal(new[] { beta, gamma }, request.Options);
            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(27, gamma.GetInt("hp"));
        }

        [Fact]
        public void A_taunt_constrains_the_other_side_and_not_its_own()
        {
            CardRuntime runtime = Setup(out Entity player);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            runtime.AddCard("Mend", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            player.SetBase("hp", 70);
            runtime.ApplyStatus("Taunt", player);

            // `source:enemies` is what keeps a taunting entity from blocking its own side's cards.
            Assert.Equal(PlayResult.Played, runtime.Play("Mend", player));
            Assert.Equal(74, player.GetInt("hp"));
            Assert.Equal(30, alpha.GetInt("hp"));
        }

        [Fact]
        public void A_scopeless_targetable_modifier_hides_only_its_host()
        {
            CardRuntime runtime = Setup(out _);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            runtime.AddCard("Jab", Zones.Hand);
            runtime.AddCard("Whirlwind", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Stealth", alpha);

            Assert.Equal(PlayResult.InvalidTarget, runtime.Play("Jab", alpha));
            Assert.Equal(PlayResult.Played, runtime.Play("Jab", beta));

            // An area effect is not target selection, so it reaches the hidden one anyway.
            Assert.Equal(PlayResult.Played, runtime.Play("Whirlwind"));
            Assert.Equal(15, alpha.GetInt("hp"));
        }

        [Fact]
        public void Nothing_targetable_refuses_the_card_instead_of_choosing_anyway()
        {
            CardRuntime runtime = Setup(out Entity player);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            runtime.AddCard("Jab", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Stealth", alpha);

            Assert.Equal(PlayResult.InvalidTarget, runtime.Play("Jab"));
            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(3, player.GetInt("energy"));
        }

        [Fact]
        public void A_self_card_always_targets_the_player()
        {
            CardRuntime runtime = Setup(out Entity player);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            runtime.AddCard("Self Harm", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play("Self Harm", alpha));

            Assert.Equal(77, player.GetInt("hp"));
            Assert.Equal(30, alpha.GetInt("hp"));
        }

        [Fact]
        public void An_any_card_accepts_either_side_or_no_target_but_only_live_actors()
        {
            CardRuntime runtime = Setup(out Entity player);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            var zaps = new List<Entity>();
            for (int i = 0; i < 4; i++) zaps.Add(runtime.AddCard("Zap", Zones.Hand));
            Entity shout = runtime.AddCard("Shout", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.Execute("deal 100 to target", target: beta);

            Assert.Equal(PlayResult.Played, runtime.Play(zaps[0], alpha));
            Assert.Equal(PlayResult.Played, runtime.Play(zaps[1], player));
            Assert.Equal(PlayResult.Played, runtime.Play(zaps[2]));
            Assert.Equal(PlayResult.InvalidTarget, runtime.Play(zaps[3], beta));
            Assert.Equal(PlayResult.InvalidTarget, runtime.Play(zaps[3], shout));

            Assert.Equal(28, alpha.GetInt("hp"));
            Assert.Equal(78, player.GetInt("hp"));
            Assert.Equal(Zones.Hand, zaps[3].Zone);
        }

        [Fact]
        public void An_untargeted_card_plays_with_no_target()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = BattleKit.Create(Content, host: recorder);
            Entity player = runtime.CreatePlayer();
            runtime.SpawnEnemy("Alpha");
            runtime.AddCard("Shout", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play("Shout"));

            Assert.Equal(2, player.GetInt("block"));
            Assert.Null(Assert.Single(recorder.Named("card_played")).Target);
        }
    }
}
