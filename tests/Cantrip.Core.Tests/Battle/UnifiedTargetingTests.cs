#nullable enable
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Battle
{
    /// <summary>
    /// One function decides what an action may be aimed at, and every site that aims at somebody
    /// comes through it: a card, an ability, an enemy's move, the <c>attack</c> verb, the automatic
    /// play out of a pile, and the roll-back-and-replay a deferred chooser does. These are the
    /// rules that used to be written five times, and the three places where they used to be wrong.
    /// </summary>
    public sealed class UnifiedTargetingTests
    {
        private const string Content = """
            card "Strike"
              cost 0
              target enemy
              effect:
                deal 5 to target

            card "Finisher"
              cost 0
              target enemy where it.hp <= 10
              effect:
                deal 5 to target

            card "Mend"
              cost 0
              target ally
              effect:
                heal 4 to target

            card "Field Medic"
              cost 0
              target ally where it.hp < it.max_hp
              effect:
                heal 4 to target

            card "Cascade"
              cost 0
              effect:
                play draw.first, free

            card "Poke and Pick"
              cost 0
              target enemy
              effect:
                deal 5 to target
                choose 1 from hand as picked
                exhaust picked

            card "Swing"
              cost 0
              target enemy
              effect:
                attack target

            ability "Smite"
              cooldown 1 turns
              target enemy
              effect:
                deal 8 to target

            ability "Mercy"
              cooldown 1 turns
              target enemy where it.hp <= 10
              effect:
                deal 8 to target

            ability "Brace"
              cooldown 1 turns
              effect:
                block 6

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
              move Swipe:
                deal 4 to target

            actor "Squire"
              hp 20
            """;

        private static CardRuntime Setup(out Entity player, IChoiceProvider? chooser = null)
        {
            CardRuntime runtime = BattleKit.Create(Content, chooser: chooser);
            player = runtime.CreatePlayer();
            player.SetBase("attack", 3);
            return runtime;
        }

        /// <summary>An ally that is not the leader: what makes <c>target ally</c> a choice at all.</summary>
        private static Entity Squire(CardRuntime runtime) =>
            runtime.State.Instantiate(runtime.Content.Find("Squire", "actor")!, null, Team.Player, Zones.Board);

        // An ability honours its `target` line ---------------------------------------------------

        [Fact]
        [Trait("Regression", "ability-ignored-its-target-line")]
        public void An_ability_with_no_target_given_settles_one_the_way_a_card_does()
        {
            CardRuntime runtime = Setup(out Entity player);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity smite = runtime.GrantAbility("Smite", player);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            // `cast Smite` with nothing to aim it at used to run the effect at nobody, in silence.
            Assert.Equal(ActionResult.Played, runtime.UseAbility(smite));
            Assert.Equal(22, alpha.GetInt("hp"));
        }

        [Fact]
        [Trait("Regression", "ability-ignored-its-target-line")]
        public void An_ability_with_nothing_to_aim_at_is_refused_rather_than_running_at_nobody()
        {
            CardRuntime runtime = Setup(out Entity player);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity smite = runtime.GrantAbility("Smite", player);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Stealth", alpha);

            Assert.Equal(ActionResult.InvalidTarget, runtime.UseAbility(smite));
            Assert.Equal(30, alpha.GetInt("hp"));

            // A refused cast is not a use, so it does not start the cooldown either.
            Assert.True(runtime.IsReady(smite));
        }

        [Fact]
        [Trait("Regression", "ability-ignored-its-target-line")]
        public void An_ability_offers_the_chooser_only_legal_candidates_and_a_taunt_narrows_them()
        {
            var chooser = new BattleRecordingChooser();
            CardRuntime runtime = Setup(out Entity player, chooser);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            Entity smite = runtime.GrantAbility("Smite", player);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Taunt", beta);

            Assert.Equal(ActionResult.Played, runtime.UseAbility(smite));

            Assert.Empty(chooser.Requests);   // one candidate is not a choice
            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(22, beta.GetInt("hp"));
        }

        [Fact]
        [Trait("Regression", "ability-ignored-its-target-line")]
        public void An_ability_asks_when_it_has_several_enemies_to_choose_between()
        {
            var chooser = new BattleRecordingChooser((request, _) => new[] { request.Options[1] });
            CardRuntime runtime = Setup(out Entity player, chooser);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            Entity smite = runtime.GrantAbility("Smite", player);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(ActionResult.Played, runtime.UseAbility(smite));

            ChoiceRequest asked = Assert.Single(chooser.Requests);
            Assert.Equal(new[] { alpha, beta }, asked.Options);
            Assert.Equal(player, asked.Chooser);
            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(22, beta.GetInt("hp"));
        }

        [Fact]
        public void An_ability_with_no_target_line_still_needs_nobody()
        {
            CardRuntime runtime = Setup(out Entity player);
            runtime.SpawnEnemy("Alpha");
            Entity brace = runtime.GrantAbility("Brace", player);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(ActionResult.Played, runtime.UseAbility(brace));
            Assert.Equal(6, player.GetInt("block"));
        }

        [Fact]
        public void An_abilitys_legal_targets_can_be_asked_for_in_advance()
        {
            CardRuntime runtime = Setup(out Entity player);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            Entity smite = runtime.GrantAbility("Smite", player);
            Entity brace = runtime.GrantAbility("Brace", player);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal("enemy", runtime.TargetMode(smite));
            Assert.Equal(new[] { alpha, beta }, runtime.LegalTargets(smite));
            Assert.Equal("none", runtime.TargetMode(brace));
            Assert.Empty(runtime.LegalTargets(brace));
        }

        // `target ally` asks when there is a choice ----------------------------------------------

        [Fact]
        [Trait("Regression", "target-ally-never-asked")]
        public void Target_ally_asks_when_there_is_more_than_one_living_ally()
        {
            var chooser = new BattleRecordingChooser((request, _) => new[] { request.Options[1] });
            CardRuntime runtime = Setup(out Entity player, chooser);
            runtime.SpawnEnemy("Alpha");
            Entity squire = Squire(runtime);
            runtime.AddCard("Mend", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.Execute("deal 10 to target", target: player);
            runtime.Execute("deal 10 to target", target: squire);

            Assert.Equal(ActionResult.Played, runtime.Play("Mend"));

            ChoiceRequest asked = Assert.Single(chooser.Requests);
            Assert.Equal(new[] { player, squire }, asked.Options);

            // It healed the one that was chosen, not the leader by default.
            Assert.Equal(70, player.GetInt("hp"));
            Assert.Equal(14, squire.GetInt("hp"));
        }

        [Fact]
        [Trait("Regression", "target-ally-never-asked")]
        public void A_party_of_one_is_never_asked_and_still_heals_the_leader()
        {
            var chooser = new BattleRecordingChooser();
            CardRuntime runtime = Setup(out Entity player, chooser);
            runtime.SpawnEnemy("Alpha");
            runtime.AddCard("Mend", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.Execute("deal 10 to target", target: player);

            Assert.Equal(ActionResult.Played, runtime.Play("Mend"));

            Assert.Empty(chooser.Requests);
            Assert.Equal(74, player.GetInt("hp"));
        }

        [Fact]
        [Trait("Regression", "target-ally-never-asked")]
        public void Target_ally_never_offers_an_ally_a_rule_has_taken_off_the_table()
        {
            var chooser = new BattleRecordingChooser();
            CardRuntime runtime = Setup(out Entity player, chooser);
            runtime.SpawnEnemy("Alpha");
            Entity squire = Squire(runtime);
            runtime.AddCard("Mend", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.Execute("deal 10 to target", target: squire);
            runtime.ApplyStatus("Stealth", player);

            // The leader is hidden, so the squire is the only candidate and nothing is asked.
            Assert.Equal(ActionResult.Played, runtime.Play("Mend"));
            Assert.Empty(chooser.Requests);
            Assert.Equal(14, squire.GetInt("hp"));
        }

        // `target … where` on a card, a move and an ability ---------------------------------------

        [Fact]
        [Trait("Regression", "target-where-filter-ignored")]
        public void A_cards_own_where_filter_decides_who_it_may_be_pointed_at()
        {
            CardRuntime runtime = Setup(out _);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            Entity finisher = runtime.AddCard("Finisher", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.Execute("deal 25 to target", target: beta);

            Assert.Equal("enemy", runtime.TargetMode(finisher));
            Assert.Equal(new[] { beta }, runtime.LegalTargets(finisher));
            Assert.Equal(ActionResult.InvalidTarget, runtime.Play(finisher, alpha));

            Assert.Equal(ActionResult.Played, runtime.Play(finisher, beta));
            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(0, beta.GetInt("hp"));
        }

        [Fact]
        [Trait("Regression", "target-where-filter-ignored")]
        public void A_card_whose_filter_matches_nobody_cannot_be_played_at_all()
        {
            CardRuntime runtime = Setup(out _);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity finisher = runtime.AddCard("Finisher", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Empty(runtime.LegalTargets(finisher));
            Assert.False(runtime.CanPlay(finisher));
            Assert.Equal(ActionResult.InvalidTarget, runtime.Play(finisher));
            Assert.Equal(30, alpha.GetInt("hp"));
        }

        [Fact]
        [Trait("Regression", "target-where-filter-ignored")]
        public void An_abilitys_own_where_filter_applies_the_same_way()
        {
            CardRuntime runtime = Setup(out Entity player);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            Entity mercy = runtime.GrantAbility("Mercy", player);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Empty(runtime.LegalTargets(mercy));
            Assert.Equal(ActionResult.InvalidTarget, runtime.UseAbility(mercy));

            runtime.Execute("deal 25 to target", target: beta);
            Assert.Equal(new[] { beta }, runtime.LegalTargets(mercy));
            Assert.Equal(ActionResult.Played, runtime.UseAbility(mercy));
            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(0, beta.GetInt("hp"));
        }

        [Fact]
        [Trait("Regression", "target-where-filter-ignored")]
        public void An_ally_filter_narrows_the_party_before_anybody_is_asked()
        {
            var chooser = new BattleRecordingChooser();
            CardRuntime runtime = Setup(out Entity player, chooser);
            runtime.SpawnEnemy("Alpha");
            Entity squire = Squire(runtime);
            runtime.AddCard("Field Medic", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.Execute("deal 10 to target", target: squire);

            // Only the squire is hurt, so "whoever is below full" is one candidate, not a question.
            Assert.Equal(ActionResult.Played, runtime.Play("Field Medic"));
            Assert.Empty(chooser.Requests);
            Assert.Equal(80, player.GetInt("hp"));
            Assert.Equal(14, squire.GetInt("hp"));
        }

        [Fact]
        [Trait("Regression", "target-where-filter-ignored")]
        public void A_filter_runs_before_the_targetable_channel_and_both_must_pass()
        {
            CardRuntime runtime = Setup(out _);
            runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            Entity finisher = runtime.AddCard("Finisher", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.Execute("deal 25 to target", target: beta);
            runtime.ApplyStatus("Stealth", beta);

            // Beta passes the card's own filter and is then hidden by the channel.
            Assert.Empty(runtime.LegalTargets(finisher));
            Assert.Equal(ActionResult.InvalidTarget, runtime.Play(finisher, beta));
        }

        // The automatic path: `play` out of a pile ------------------------------------------------

        [Fact]
        [Trait("Regression", "target-where-filter-ignored")]
        public void The_play_verb_rolls_its_target_from_the_candidates_the_filter_left()
        {
            CardRuntime runtime = Setup(out _);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            runtime.AddCard("Finisher", Zones.Draw);
            runtime.AddCard("Cascade", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.Execute("deal 25 to target", target: beta);

            // Nobody is choosing, so the target is rolled — but only from those the card may reach.
            Assert.Equal(ActionResult.Played, runtime.Play("Cascade"));
            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(0, beta.GetInt("hp"));
        }

        [Fact]
        public void The_play_verb_never_asks_even_when_a_chooser_is_installed()
        {
            var chooser = new BattleRecordingChooser();
            CardRuntime runtime = Setup(out _, chooser);
            runtime.SpawnEnemy("Alpha");
            runtime.SpawnEnemy("Beta");
            runtime.AddCard("Strike", Zones.Draw);
            runtime.AddCard("Cascade", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(ActionResult.Played, runtime.Play("Cascade"));
            Assert.Empty(chooser.Requests);
        }

        [Fact]
        public void The_play_verb_rolls_the_same_target_from_the_same_seed()
        {
            static int Struck(ulong seed)
            {
                CardRuntime runtime = BattleKit.Create(Content, seed: seed);
                runtime.CreatePlayer();
                Entity alpha = runtime.SpawnEnemy("Alpha");
                runtime.SpawnEnemy("Beta");
                runtime.AddCard("Strike", Zones.Draw);
                runtime.AddCard("Cascade", Zones.Hand);
                runtime.StartBattle(shuffle: false, drawOpeningHand: false);
                runtime.Play("Cascade");
                return alpha.GetInt("hp");
            }

            for (ulong seed = 1; seed <= 8; seed++) Assert.Equal(Struck(seed), Struck(seed));
        }

        // Rolling back and replaying ---------------------------------------------------------------

        [Fact]
        public void A_target_chosen_through_a_deferred_chooser_replays_in_the_same_place()
        {
            CardRuntime runtime = Setup(out _, new DeferredChooser());
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            Entity poke = runtime.AddCard("Poke and Pick", Zones.Hand);
            Entity spare = runtime.AddCard("Strike", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            // Question one is the target. The action rolls back, so nothing has happened yet.
            Assert.Equal(ActionResult.ChoicePending, runtime.Play(poke));
            Assert.Equal(new[] { alpha, beta }, runtime.Pending!.Options);
            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(30, beta.GetInt("hp"));

            // Answering replays the play from the start; the target answer is used again where it
            // was asked, and the card gets as far as its second question.
            Assert.Equal(ActionResult.ChoicePending, runtime.Answer(beta.Id));
            Assert.Equal(new[] { spare }, runtime.Pending!.Options);
            Assert.Equal(30, beta.GetInt("hp"));

            Assert.Equal(ActionResult.Played, runtime.Answer(spare.Id));
            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(25, beta.GetInt("hp"));
            Assert.Equal(Zones.Exhaust, spare.Zone);
        }

        [Fact]
        public void An_ability_that_stops_to_ask_for_a_target_rolls_back_and_replays_too()
        {
            CardRuntime runtime = Setup(out Entity player, new DeferredChooser());
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            Entity smite = runtime.GrantAbility("Smite", player);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(ActionResult.ChoicePending, runtime.UseAbility(smite));
            Assert.Equal(new[] { alpha, beta }, runtime.Pending!.Options);
            Assert.Equal(30, beta.GetInt("hp"));

            // The cooldown is part of the rolled-back action, so it only starts once.
            Assert.True(runtime.IsReady(smite));

            Assert.Equal(ActionResult.Played, runtime.Answer(beta.Id));
            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(22, beta.GetInt("hp"));
            Assert.False(runtime.IsReady(smite));
        }

        // An enemy's move --------------------------------------------------------------------------

        [Fact]
        public void An_enemy_move_keeps_its_target_while_it_is_legal_and_otherwise_takes_the_first()
        {
            CardRuntime runtime = Setup(out Entity player);
            Entity beta = runtime.SpawnEnemy("Beta");
            Entity squire = Squire(runtime);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.EndTurn();
            Assert.Equal(76, player.GetInt("hp"));
            Assert.Equal(20, squire.GetInt("hp"));

            // A taunt takes the leader off the table, so the move goes to whoever is left.
            runtime.ApplyStatus("Taunt", squire);
            runtime.EndTurn();
            Assert.Equal(76, player.GetInt("hp"));
            Assert.Equal(16, squire.GetInt("hp"));

        }

        // `attack` ----------------------------------------------------------------------------------

        [Fact]
        public void An_attack_filtered_to_nothing_lands_nowhere_and_into_binds_zero()
        {
            CardRuntime runtime = Setup(out _);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Stealth", alpha);

            runtime.Execute("attack enemies into landed\nblock landed", target: alpha);

            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(0, runtime.Player!.GetInt("block"));
        }

        [Fact]
        public void An_attack_still_lands_on_whoever_is_left()
        {
            CardRuntime runtime = Setup(out Entity player);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity beta = runtime.SpawnEnemy("Beta");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Stealth", alpha);

            runtime.Execute("attack enemies into landed\nblock landed");

            Assert.Equal(30, alpha.GetInt("hp"));
            Assert.Equal(27, beta.GetInt("hp"));
            Assert.Equal(3, player.GetInt("block"));
        }

        // What the rest of the language does not ask ------------------------------------------------

        [Fact]
        public void An_area_effect_still_reaches_a_hidden_minion()
        {
            CardRuntime runtime = Setup(out _);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Stealth", alpha);

            runtime.Execute("deal 3 to enemies");

            Assert.Equal(27, alpha.GetInt("hp"));
        }

        [Fact]
        public void A_targets_list_is_read_once_per_question_and_the_filter_sees_live_numbers()
        {
            CardRuntime runtime = Setup(out _);
            Entity alpha = runtime.SpawnEnemy("Alpha");
            Entity finisher = runtime.AddCard("Finisher", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Empty(runtime.LegalTargets(finisher));
            runtime.Execute("deal 25 to target", target: alpha);
            Assert.Equal(new[] { alpha }, runtime.LegalTargets(finisher));
        }
    }
}
