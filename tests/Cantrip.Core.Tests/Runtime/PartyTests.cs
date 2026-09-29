using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;
using static Cantrip.Tests.Runtime.RuntimeTestKit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// The party: more than one actor on the player's side that the game asks for input. The
    /// promise every test here is really about is that a party of one — which is every game written
    /// before this existed — behaves exactly as it did, so each new rule is checked against both
    /// shapes rather than only the new one.
    /// </summary>
    public sealed class PartyTests
    {
        private const string Heroes = """
            hero "Crusader"
              hp 30
              abilities Smite

            hero "Vestal"
              hp 20
              abilities Mend

            ability "Smite"
              target enemy
              effect:
                deal 9 to target

            ability "Mend"
              cooldown 1 turns
              target ally
              effect:
                heal 7 to target

            status "Taunt"
              stacking none
              modify targetable of allies where source:enemies, not it.has(Taunt): set 0

            card "Jab"
              cost 1
              target enemy
              tags attack
              effect:
                deal 4 to target

            enemy "Brigand"
              hp 40
              move "Cutthroat" at lowest hp enemies:
                deal 8 to target
              pattern cycle Cutthroat
            """;

        // A party of one ------------------------------------------------------------------------

        [Fact]
        public void A_game_that_declares_no_hero_has_a_party_of_one()
        {
            CardRuntime runtime = Create(Heroes);
            Enemy(runtime);
            Start(runtime);

            Entity member = Assert.Single(runtime.Party);
            Assert.Same(runtime.Player, member);
            Assert.True(runtime.Player!.IsPartyMember);
        }

        /// <summary>
        /// <c>Pass</c> for the only member is <c>EndTurn</c>, to the turn number and the hash. That
        /// is what lets a game adopt the party API one call at a time without any of its results
        /// moving.
        /// </summary>
        [Fact]
        public void Passing_the_only_member_is_ending_the_turn()
        {
            CardRuntime passed = Create(Heroes);
            Enemy(passed);
            Start(passed);
            passed.Pass(passed.Player!);

            CardRuntime ended = Create(Heroes);
            Enemy(ended);
            Start(ended);
            ended.EndTurn();

            Assert.Equal(ended.State.Turn, passed.State.Turn);
            Assert.Equal(ended.State.ComputeHash(), passed.State.ComputeHash());
        }

        [Fact]
        public void The_only_member_may_act_until_it_passes()
        {
            CardRuntime runtime = Create(Heroes);
            Enemy(runtime);
            Start(runtime);

            Assert.True(runtime.CanAct(runtime.Player!));
            runtime.Pass(runtime.Player!);

            // Passing ended the turn, so a new one has begun and the member may act again.
            Assert.True(runtime.CanAct(runtime.Player!));
            Assert.Equal(2, runtime.State.Turn);
        }

        // Members --------------------------------------------------------------------------------

        [Fact]
        public void A_hero_joins_the_party_with_the_abilities_its_declaration_lists()
        {
            CardRuntime runtime = Create(Heroes);
            Entity vestal = runtime.AddHero("Vestal");

            Assert.True(vestal.IsPartyMember);
            Assert.Equal(Team.Player, vestal.Team);
            Assert.Equal(Zones.Board, vestal.Zone);
            Assert.Equal(20, vestal.GetInt("hp"));
            Assert.NotNull(vestal.FindAttached("Mend"));
            Assert.Equal(new[] { "Player", "Vestal" }, runtime.Party.Select(m => m.Name));
        }

        [Fact]
        public void A_summoned_actor_is_not_a_party_member()
        {
            CardRuntime runtime = Create("""
                actor "Shiv Golem"
                  hp 5

                card "Summon"
                  cost 0
                  effect:
                    create "Shiv Golem"
                """);
            Enemy(runtime);
            Start(runtime);
            runtime.Play(runtime.AddCard("Summon", Zones.Hand));

            Entity golem = runtime.State.Actors(Team.Player).Single(a => a.Name == "Shiv Golem");
            Assert.False(golem.IsPartyMember);
            Assert.Single(runtime.Party);
        }

        [Fact]
        public void Passing_for_somebody_who_is_not_a_member_is_refused_by_name()
        {
            CardRuntime runtime = Create(Heroes);
            Entity enemy = Enemy(runtime);
            Start(runtime);

            var error = Assert.Throws<System.ArgumentException>(() => runtime.Pass(enemy));
            Assert.Contains("not a party member", error.Message);
        }

        [Fact]
        public void The_party_turn_ends_when_the_last_member_has_passed()
        {
            CardRuntime runtime = Create(Heroes);
            runtime.AddHero("Crusader");
            Enemy(runtime);
            Start(runtime);

            runtime.Pass(runtime.Player!);
            Assert.Equal(1, runtime.State.Turn);
            Assert.False(runtime.CanAct(runtime.Player!));
            Assert.True(runtime.CanAct(runtime.Party.Single(m => m.Name == "Crusader")));

            runtime.Pass(runtime.Party.Single(m => m.Name == "Crusader"));
            Assert.Equal(2, runtime.State.Turn);
        }

        // Death ----------------------------------------------------------------------------------

        [Fact]
        public void The_battle_is_lost_only_when_no_member_is_alive()
        {
            CardRuntime runtime = Create(Heroes);
            Entity vestal = runtime.AddHero("Vestal");
            Enemy(runtime);
            Start(runtime);

            runtime.Execute("kill player");
            Assert.Null(runtime.Won);
            Assert.True(runtime.State.InBattle);
            Assert.Equal(new[] { "Vestal" }, runtime.Party.Select(m => m.Name));

            runtime.Execute("kill target", target: vestal);
            Assert.False(runtime.Won);
            Assert.False(runtime.State.InBattle);
        }

        /// <summary>
        /// A living summon is not a reason to keep fighting. It is the Darkest Dungeon rule and the
        /// one players expect; it is also why the check reads the party rather than the side.
        /// </summary>
        [Fact]
        public void A_surviving_summon_does_not_keep_the_battle_going()
        {
            CardRuntime runtime = Create("""
                actor "Shiv Golem"
                  hp 5

                card "Summon"
                  cost 0
                  effect:
                    create "Shiv Golem"
                """);
            Enemy(runtime);
            Start(runtime);
            runtime.Play(runtime.AddCard("Summon", Zones.Hand));

            runtime.Execute("kill player");
            Assert.False(runtime.Won);
            Assert.False(runtime.State.InBattle);
        }

        [Fact]
        public void Revive_brings_a_member_back_where_a_heal_refuses()
        {
            CardRuntime runtime = Create(Heroes);
            Entity vestal = runtime.AddHero("Vestal");
            Enemy(runtime);
            Start(runtime);

            runtime.Execute("kill target", target: vestal);
            Assert.True(vestal.IsDead);

            runtime.Execute("heal 10 to target", target: vestal);
            Assert.True(vestal.IsDead);

            Assert.True(runtime.Revive(vestal, 12));
            Assert.True(vestal.IsAlive);
            Assert.Equal(12, vestal.GetInt("hp"));
            Assert.Equal(Zones.Board, vestal.Zone);

            // Somebody who was never dead is not revived, so a card that says "bring back a fallen
            // ally" cannot quietly become a heal.
            Assert.False(runtime.Revive(vestal, 30));
            Assert.Equal(12, vestal.GetInt("hp"));
        }

        // Cards ----------------------------------------------------------------------------------

        [Fact]
        public void A_named_performer_is_the_plays_source_and_the_controller_still_pays()
        {
            CardRuntime runtime = Create(Heroes);
            Entity vestal = runtime.AddHero("Vestal");
            Entity enemy = Enemy(runtime);
            Start(runtime);

            Entity jab = runtime.AddCard("Jab", Zones.Hand);
            Assert.Equal(ActionResult.Played, runtime.Play(jab, enemy, vestal));

            Assert.Equal(46, enemy.GetInt("hp"));
            Assert.Equal(2, runtime.Player!.GetInt("energy"));
            Assert.Equal(0, vestal.GetInt("energy"));
            Assert.Equal(1, runtime.State.History("cards_played", vestal).ToInt());
        }

        [Fact]
        public void A_play_with_no_performer_is_the_controllers_play()
        {
            CardRuntime runtime = Create(Heroes);
            Entity enemy = Enemy(runtime);
            Start(runtime);

            Entity jab = runtime.AddCard("Jab", Zones.Hand);
            Assert.Equal(ActionResult.Played, runtime.Play(jab, enemy));
            Assert.Equal(1, runtime.State.History("cards_played", runtime.Player).ToInt());
        }

        /// <summary>
        /// Nobody draws here, so what is left in the hand afterwards is only what the turn’s end did
        /// with it. With a hand size the Vestal would draw its own discarded card straight back and
        /// the test would pass whether or not the hand had ever been emptied.
        /// </summary>
        [Fact]
        public void Every_members_hand_is_discarded_at_the_end_of_the_turn()
        {
            Ruleset rules = Ruleset.CreateDefault();
            rules.HandSize = 0;

            CardRuntime runtime = Create(Heroes, new RuntimeOptions { Rules = rules });
            Entity vestal = runtime.AddHero("Vestal");
            Enemy(runtime);
            Start(runtime);

            runtime.AddCard("Jab", Zones.Hand, vestal);
            Assert.Single(runtime.State.ZoneOf(vestal, Zones.Hand));

            runtime.EndTurn();

            // Before this the hand was never discarded at all and grew by a full draw every round.
            Assert.Empty(runtime.State.ZoneOf(vestal, Zones.Hand));
            Assert.Single(runtime.State.ZoneOf(vestal, Zones.Discard));
        }

        [Fact]
        public void Every_member_is_tidied_up_at_the_end_of_a_battle()
        {
            CardRuntime runtime = Create(Heroes);
            Entity vestal = runtime.AddHero("Vestal");
            Entity enemy = Enemy(runtime, hp: 1);
            Start(runtime);

            runtime.ApplyStatus("Taunt", vestal);
            runtime.AddCard("Jab", Zones.Hand, vestal);
            Assert.NotNull(vestal.FindAttached("Taunt"));

            runtime.Execute("kill enemy", target: enemy);
            Assert.True(runtime.Won);

            // Before this, only the leader was cleaned, so a hero carried its statuses and its whole
            // hand into the next battle.
            Assert.Null(vestal.FindAttached("Taunt"));
            Assert.Empty(runtime.State.ZoneOf(vestal, Zones.Hand));
            Assert.Single(runtime.State.ZoneOf(vestal, Zones.Draw));
        }

        // Abilities ------------------------------------------------------------------------------

        [Fact]
        public void CanUse_answers_for_an_ability_the_way_CanPlay_does_for_a_card()
        {
            CardRuntime runtime = Create(Heroes);
            Entity crusader = runtime.AddHero("Crusader");
            Entity enemy = Enemy(runtime, hp: 1);
            Start(runtime);

            Entity smite = crusader.FindAttached("Smite")!;
            Assert.True(runtime.CanUse(smite));

            // With nothing to aim at, an ability that needs somebody is refused rather than run at
            // nobody — which is also what UseAbility answers.
            runtime.Execute("kill enemy", target: enemy);
            Assert.False(runtime.CanUse(smite));
        }

        [Fact]
        public void A_cooldown_is_the_members_own()
        {
            CardRuntime runtime = Create(Heroes);
            Entity one = runtime.AddHero("Vestal");
            Entity two = runtime.AddHero("Vestal");
            Enemy(runtime);
            Start(runtime);

            Assert.Equal(ActionResult.Played, runtime.UseAbility(one.FindAttached("Mend")!, one));
            Assert.False(runtime.CanUse(one.FindAttached("Mend")!));
            Assert.True(runtime.CanUse(two.FindAttached("Mend")!));
        }

        // Intents --------------------------------------------------------------------------------

        [Fact]
        public void An_enemy_telegraphs_the_member_its_move_names()
        {
            CardRuntime runtime = Create(Heroes);
            runtime.AddHero("Crusader");
            Entity vestal = runtime.AddHero("Vestal");
            Entity brigand = runtime.SpawnEnemy("Brigand");
            Start(runtime);

            Assert.Equal("Cutthroat", brigand.Intent);
            Assert.Same(vestal, runtime.IntentTargetOf(brigand));
        }

        /// <summary>
        /// The stored target is a telegraph, not a decision: asking again re-reads it against the
        /// rules as they stand. That is what lets a taunt applied mid-turn change what a UI shows
        /// with no event for the UI to have missed.
        /// </summary>
        [Fact]
        public void A_taunt_re_aims_the_telegraph_with_no_second_roll()
        {
            CardRuntime runtime = Create(Heroes);
            Entity crusader = runtime.AddHero("Crusader");
            Entity vestal = runtime.AddHero("Vestal");
            Entity brigand = runtime.SpawnEnemy("Brigand");
            Start(runtime);

            Assert.Same(vestal, runtime.IntentTargetOf(brigand));

            ulong before = runtime.State.Rng.GetState()[0];
            runtime.ApplyStatus("Taunt", crusader);

            Assert.Same(crusader, runtime.IntentTargetOf(brigand));
            Assert.Same(vestal, brigand.IntentTarget);
            Assert.Equal(before, runtime.State.Rng.GetState()[0]);
        }

        [Fact]
        public void With_one_member_the_telegraph_is_that_member_and_nothing_is_rolled()
        {
            CardRuntime runtime = Create(Heroes);
            Entity brigand = runtime.SpawnEnemy("Brigand");

            ulong[] before = runtime.State.Rng.GetState();
            Start(runtime);

            Assert.Same(runtime.Player, runtime.IntentTargetOf(brigand));
            Assert.Equal(before, runtime.State.Rng.GetState());
        }

        // Saves ----------------------------------------------------------------------------------

        [Fact]
        public void A_save_carries_the_party_and_who_has_acted()
        {
            CardRuntime runtime = Create(Heroes);
            Entity vestal = runtime.AddHero("Vestal");
            Entity brigand = runtime.SpawnEnemy("Brigand");
            Start(runtime);
            runtime.Pass(runtime.Player!);

            GameSnapshot save = runtime.Capture();
            Assert.Equal(new[] { runtime.Player!.Id, vestal.Id }, save.PartyIds);
            Assert.Equal(new[] { runtime.Player.Id }, save.ActedIds);
            Assert.Equal(vestal.Id, save.Entities.Single(e => e.Id == brigand.Id).IntentTargetId);

            CardRuntime loaded = Create(Heroes);
            loaded.Restore(save);
            Assert.Equal(new[] { "Player", "Vestal" }, loaded.Party.Select(m => m.Name));
            Assert.False(loaded.CanAct(loaded.Player!));
            Assert.True(loaded.CanAct(loaded.Party.Single(m => m.Name == "Vestal")));
            Assert.Equal(runtime.State.ComputeHash(), loaded.State.ComputeHash());
        }

        /// <summary>
        /// Format 3 is not published, so the party's fields were added to it rather than starting a
        /// format 4. A save that lists no party is therefore not an old format to migrate — it is a
        /// party of one, which is what it was.
        /// </summary>
        [Fact]
        public void A_save_that_names_no_party_restores_as_a_party_of_one()
        {
            CardRuntime runtime = Create(Heroes);
            Enemy(runtime);
            Start(runtime);

            GameSnapshot save = runtime.Capture();
            save.PartyIds.Clear();
            save.ActedIds.Clear();
            save.ActiveMemberId = 0;
            foreach (EntitySnapshot record in save.Entities) record.IsPartyMember = false;

            CardRuntime loaded = Create(Heroes);
            loaded.Restore(save);

            Entity member = Assert.Single(loaded.Party);
            Assert.Same(loaded.Player, member);
            Assert.Equal(runtime.State.ComputeHash(), loaded.State.ComputeHash());
        }
    }
}
