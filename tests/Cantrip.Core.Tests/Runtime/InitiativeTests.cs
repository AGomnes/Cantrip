using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Runtime;
using Xunit;
using static Cantrip.Tests.Runtime.RuntimeTestKit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// <c>turns: initiative</c>: one order over both sides, each combatant's <c>turn_start</c> and
    /// <c>turn_end</c> at its own step, and the round, which is still one <c>turn</c>, ending when
    /// every living combatant has taken one.
    /// </summary>
    /// <remarks>
    /// The load-bearing promise is the last one here: with one member and one enemy a round of
    /// <c>initiative</c> is a turn of <c>sides</c>, so <c>once per turn</c>, <c>on every N turns</c>,
    /// every history counter and every saved turn number mean what they always meant.
    /// </remarks>
    public sealed class InitiativeTests
    {
        private const string Order = """
            ruleset
              clock turns
              turns: initiative

            relic "Bell"
              on any.turn_start:
                log event.source.name "up"
              on any.turn_end:
                log event.source.name "down"

            hero "Swift"
              hp 30
              speed 9

            hero "Slow"
              hp 30
              speed 1

            enemy "Middling"
              hp 60
              speed 5
              move "Poke":
                deal 1 to target
              pattern cycle Poke

            enemy "Sluggard"
              hp 60
              speed 0
              move "Poke":
                deal 1 to target
              pattern cycle Poke
            """;

        private static CardRuntime Fight(string order = "", string? content = null)
        {
            string text = content ?? Order;
            CardRuntime runtime = Create(order.Length == 0 ? text : text.Replace("  turns: initiative", "  turns: initiative\n  order: " + order));
            runtime.Player!.SetBase("speed", 4);
            return runtime;
        }

        // The order -------------------------------------------------------------------------------

        /// <summary>
        /// A hero acts, then the enemy faster than the other hero, then that hero, then the
        /// slowest enemy: the two sides interleave, which is the whole of what the mode is for.
        /// </summary>
        [Fact]
        public void Speed_interleaves_the_two_sides()
        {
            CardRuntime runtime = Fight("speed");
            runtime.AddHero("Swift");
            runtime.AddHero("Slow");
            runtime.SpawnEnemy("Middling");
            runtime.SpawnEnemy("Sluggard");
            Start(runtime);

            Assert.Equal(
                new[] { "Swift", "Middling", "Player", "Slow", "Sluggard" },
                runtime.State.TurnOrder.Select(e => e.Name).ToArray());
        }

        /// <summary>
        /// Under <c>order: position</c> the whole party comes first and then the enemies, which is
        /// the order <c>turns: sides</c> runs them in, so the only thing initiative changes there
        /// is where each combatant's own turn events fall.
        /// </summary>
        [Fact]
        public void Position_order_puts_the_party_first()
        {
            CardRuntime runtime = Fight();
            runtime.AddHero("Swift");
            runtime.AddHero("Slow");
            runtime.SpawnEnemy("Middling");
            runtime.SpawnEnemy("Sluggard");
            Start(runtime);

            Assert.Equal(
                new[] { "Player", "Swift", "Slow", "Middling", "Sluggard" },
                runtime.State.TurnOrder.Select(e => e.Name).ToArray());
        }

        /// <summary>
        /// <c>order: speed</c> is a party order in <c>sides</c> too: it says which member the engine
        /// offers first, and so the order they draw in.
        /// </summary>
        [Fact]
        public void Speed_orders_the_party_under_sides_as_well()
        {
            CardRuntime runtime = Create(Order.Replace("  turns: initiative", "  turns: sides\n  order: speed"));
            runtime.Player!.SetBase("speed", 4);
            runtime.AddHero("Slow");
            runtime.AddHero("Swift");
            Enemy(runtime);
            Start(runtime);

            Assert.Equal(new[] { "Swift", "Player", "Slow" }, runtime.Party.Select(e => e.Name).ToArray());
        }

        // Whose step it is ------------------------------------------------------------------------

        [Fact]
        public void Only_the_member_whose_step_it_is_may_act()
        {
            CardRuntime runtime = Fight();
            Entity swift = runtime.AddHero("Swift");
            Entity slow = runtime.AddHero("Slow");
            runtime.SpawnEnemy("Middling");
            Start(runtime);

            // Position order, so the leader is up first and the two heroes wait their places.
            Assert.Same(runtime.Player, runtime.ActiveMember);
            Assert.True(runtime.CanAct(runtime.Player!));
            Assert.False(runtime.CanAct(swift));
            Assert.False(runtime.CanAct(slow));

            runtime.Pass(runtime.Player!);
            Assert.Same(swift, runtime.ActiveMember);
            Assert.False(runtime.CanAct(runtime.Player!));
            Assert.True(runtime.CanAct(swift));
        }

        [Fact]
        public void Passing_out_of_turn_is_refused()
        {
            CardRuntime runtime = Fight();
            runtime.AddHero("Swift");
            Entity slow = runtime.AddHero("Slow");
            runtime.SpawnEnemy("Middling");
            Start(runtime);

            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => runtime.Pass(slow));
            Assert.Contains("not Slow#", refused.Message);
        }

        /// <summary>
        /// The reason the mode exists: "at the start of your turn" happens when you are up, not when
        /// your side is. Under <c>sides</c> both heroes' turn starts fire before either acts.
        /// </summary>
        [Fact]
        public void Each_combatant_starts_its_turn_at_its_own_step()
        {
            CardRuntime runtime = Fight();
            Entity swift = runtime.AddHero("Swift");
            runtime.AddHero("Slow");
            runtime.SpawnEnemy("Middling");
            runtime.AddRelic("Bell");
            List<string> log = CaptureLog(runtime);
            Start(runtime);

            // Only the leader is up: nobody else has started a turn yet.
            Assert.Equal(new[] { "Player up" }, log.ToArray());

            runtime.Pass(runtime.Player!);
            Assert.Equal(new[] { "Player up", "Player down", "Swift up" }, log.ToArray());

            runtime.Pass(swift);
            Assert.Equal(new[] { "Player up", "Player down", "Swift up", "Swift down", "Slow up" }, log.ToArray());
        }

        // The round -------------------------------------------------------------------------------

        /// <summary>
        /// The round is one turn. Four combatants take four steps and the turn number moves once,
        /// because everything the language counts in turns keys off it.
        /// </summary>
        [Fact]
        public void The_turn_number_and_the_clock_move_once_a_round()
        {
            CardRuntime runtime = Fight();
            Entity swift = runtime.AddHero("Swift");
            Entity slow = runtime.AddHero("Slow");
            runtime.SpawnEnemy("Middling");
            Start(runtime);

            Assert.Equal(1, runtime.State.Turn);
            long clock = runtime.State.Clock.Now;

            runtime.Pass(runtime.Player!);
            Assert.Equal(1, runtime.State.Turn);
            Assert.Equal(clock, runtime.State.Clock.Now);

            runtime.Pass(swift);
            Assert.Equal(1, runtime.State.Turn);
            Assert.Equal(clock, runtime.State.Clock.Now);

            // The last of ours: the enemy takes its step and the round rolls over.
            runtime.Pass(slow);
            Assert.Equal(2, runtime.State.Turn);
            Assert.Equal(clock + 1, runtime.State.Clock.Now);

            // And again through the second round, where a clock that moved per step would have
            // reached clock + 4 rather than clock + 2 by the end of it. The first round proves
            // nothing on its own, because the clock does not move on the turn a battle starts.
            runtime.Pass(runtime.Player!);
            Assert.Equal(clock + 1, runtime.State.Clock.Now);
            runtime.Pass(swift);
            Assert.Equal(clock + 1, runtime.State.Clock.Now);
            runtime.Pass(slow);
            Assert.Equal(3, runtime.State.Turn);
            Assert.Equal(clock + 2, runtime.State.Clock.Now);
        }

        /// <summary>
        /// <c>EndTurn</c> is <c>Pass</c> for everyone here too: whoever of ours has not acted gives
        /// their step up, and the round still runs to its end around them: the enemies take theirs.
        /// </summary>
        [Fact]
        public void Ending_the_turn_passes_everyone_who_has_not_acted()
        {
            CardRuntime runtime = Fight();
            runtime.AddHero("Swift");
            runtime.AddHero("Slow");
            runtime.SpawnEnemy("Middling");
            Start(runtime);

            int before = runtime.Party.Sum(Hp);
            runtime.EndTurn();

            Assert.Equal(2, runtime.State.Turn);

            // The enemy still took its step: giving our steps up ends the round, not the fight.
            Assert.Equal(before - 1, runtime.Party.Sum(Hp));
            Assert.Same(runtime.Player, runtime.ActiveMember);
        }

        /// <summary>
        /// The trap the whole "one round is one turn" rule exists to avoid: a listener that may fire
        /// once per turn fires once a round however many combatants take a step in it, and
        /// <c>on every 2 turns</c> counts rounds rather than steps.
        /// </summary>
        [Fact]
        public void Once_per_turn_and_every_n_turns_count_rounds_not_steps()
        {
            const string Counting = """
                relic "Tally"
                  on any.turn_start once per turn:
                    log "once"
                  on every 2 turns:
                    log "twice"
                """;

            CardRuntime runtime = Fight(content: Order + "\n" + Counting);
            Entity swift = runtime.AddHero("Swift");
            Entity slow = runtime.AddHero("Slow");
            runtime.SpawnEnemy("Middling");
            runtime.AddRelic("Tally");
            List<string> log = CaptureLog(runtime);
            Start(runtime);

            for (int round = 0; round < 4; round++)
            {
                runtime.Pass(runtime.Player!);
                runtime.Pass(swift);
                runtime.Pass(slow);
            }

            // Five rounds have begun over twenty steps: once each, not once per step. `on every 2
            // turns` fired on rounds three and five, which is the clock moving once a round.
            Assert.Equal(5, log.Count(line => line == "once"));
            Assert.Equal(2, log.Count(line => line == "twice"));
        }

        // The edges -------------------------------------------------------------------------------

        /// <summary>A member killed after acting is not waited for, and does not act twice.</summary>
        [Fact]
        public void A_member_killed_after_acting_does_not_hold_up_the_round()
        {
            CardRuntime runtime = Fight();
            Entity swift = runtime.AddHero("Swift");
            Entity slow = runtime.AddHero("Slow");
            runtime.SpawnEnemy("Middling");
            Start(runtime);

            runtime.Pass(runtime.Player!);
            runtime.Pass(swift);
            runtime.Execute("kill target", runtime.Player, swift);

            Assert.Same(slow, runtime.ActiveMember);
            runtime.Pass(slow);
            Assert.Equal(2, runtime.State.Turn);
        }

        /// <summary>
        /// A member revived after its step does not take a second one: the round remembers who has
        /// acted by id, which is what makes a death and a revival cost nothing to bookkeep.
        /// </summary>
        [Fact]
        public void A_member_revived_after_acting_does_not_act_twice()
        {
            CardRuntime runtime = Fight();
            Entity swift = runtime.AddHero("Swift");
            Entity slow = runtime.AddHero("Slow");
            runtime.SpawnEnemy("Middling");
            Start(runtime);

            runtime.Pass(runtime.Player!);
            runtime.Pass(swift);
            runtime.Execute("kill target", runtime.Player, swift);
            Assert.True(runtime.Revive(swift, 5));

            Assert.Same(slow, runtime.ActiveMember);
            Assert.False(runtime.CanAct(swift));
        }

        /// <summary>
        /// A member revived before its place comes round takes the step it never took, which is the
        /// answer that needs no bookkeeping at all: the order is read off whoever is living.
        /// </summary>
        [Fact]
        public void A_member_revived_before_its_place_still_takes_its_step()
        {
            CardRuntime runtime = Fight();
            Entity swift = runtime.AddHero("Swift");
            runtime.SpawnEnemy("Middling");
            Start(runtime);

            runtime.Execute("kill target", runtime.Player, swift);
            Assert.True(runtime.Revive(swift, 5));

            runtime.Pass(runtime.Player!);
            Assert.Same(swift, runtime.ActiveMember);
            Assert.True(runtime.CanAct(swift));
        }

        /// <summary>
        /// A hero summoned mid-round joins the order it is read off, so it acts this round when its
        /// place is still ahead. The round cannot loop on it: every step but a member's marks
        /// somebody as having acted.
        /// </summary>
        [Fact]
        public void A_member_created_mid_round_takes_the_place_it_arrives_in()
        {
            CardRuntime runtime = Fight();
            runtime.SpawnEnemy("Middling");
            Start(runtime);

            runtime.Execute("create Swift", runtime.Player);
            runtime.Pass(runtime.Player!);

            Assert.Equal("Swift", runtime.ActiveMember!.Name);
            Assert.Equal(1, runtime.State.Turn);
        }

        /// <summary>
        /// A summoned minion is an ally and not a member, so nobody is asked what it does, but it
        /// still takes a step of its own, which is how a minion that attacks from its own
        /// <c>turn_end</c> attacks. Before this the round stopped on it and waited forever for a
        /// pass that only a member can give.
        /// </summary>
        [Fact]
        public void A_summon_on_our_side_takes_its_step_without_being_asked()
        {
            const string Minion = """
                actor "Shiv"
                  hp 6
                  on turn_end:
                    deal 3 to enemies.first
                """;

            CardRuntime runtime = Fight(content: Order + "\n" + Minion);
            runtime.AddHero("Slow");
            Entity enemy = runtime.SpawnEnemy("Middling");
            Start(runtime);
            runtime.Execute("create Shiv", runtime.Player);

            int before = Hp(enemy);

            // Position order, so the Shiv stands behind both members: leader, Slow, Shiv, enemy.
            runtime.Pass(runtime.Player!);
            Assert.Equal("Slow", runtime.ActiveMember!.Name);
            Assert.Equal(before, Hp(enemy));

            // Passing the last member runs the rest of the round: the Shiv's step, where nobody is
            // asked anything and its turn_end still lands, and then the enemy's.
            runtime.Pass(runtime.ActiveMember!);
            Assert.Equal(before - 3, Hp(enemy));
            Assert.Equal(2, runtime.State.Turn);
            Assert.Same(runtime.Player, runtime.ActiveMember);
        }

        // A party of one --------------------------------------------------------------------------

        /// <summary>
        /// The invariant the whole mode is checked against: with one member and one enemy, a round
        /// of <c>initiative</c> is a turn of <c>sides</c>: same turn number, same clock, same hp
        /// on both sides, over four rounds.
        /// </summary>
        [Fact]
        public void A_party_of_one_plays_the_same_round_in_either_mode()
        {
            const string OneOnOne = """
                ruleset
                  clock turns
                {MODE}

                enemy "Biter"
                  hp 40
                  move "Bite":
                    deal 4 to target
                  pattern cycle Bite
                """;

            CardRuntime sides = Create(OneOnOne.Replace("{MODE}", "  turns: sides"));
            CardRuntime order = Create(OneOnOne.Replace("{MODE}", "  turns: initiative"));

            foreach (CardRuntime runtime in new[] { sides, order })
            {
                runtime.SpawnEnemy("Biter");
                Start(runtime);
                for (int round = 0; round < 4; round++) runtime.EndTurn();
            }

            Assert.Equal(5, sides.State.Turn);
            Assert.Equal(sides.State.Turn, order.State.Turn);
            Assert.Equal(sides.State.Clock.Now, order.State.Clock.Now);
            Assert.Equal(Hp(sides.Player!), Hp(order.Player!));
            Assert.Equal(
                Hp(sides.State.Actors(Team.Enemy)[0]),
                Hp(order.State.Actors(Team.Enemy)[0]));
        }

        /// <summary>A save taken mid-round comes back with the same combatant up.</summary>
        [Fact]
        public void A_round_survives_a_save()
        {
            CardRuntime runtime = Fight();
            Entity swift = runtime.AddHero("Swift");
            runtime.AddHero("Slow");
            runtime.SpawnEnemy("Middling");
            Start(runtime);
            runtime.Pass(runtime.Player!);

            GameSnapshot snapshot = runtime.Capture();
            CardRuntime loaded = Create(Order);
            loaded.Restore(snapshot);

            Assert.Equal(swift.Id, loaded.ActiveMember!.Id);
            Assert.Equal(runtime.State.ComputeHash(), loaded.State.ComputeHash());
        }
    }
}
