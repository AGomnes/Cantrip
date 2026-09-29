using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Runtime;
using Xunit;
using static Cantrip.Tests.Runtime.RuntimeTestKit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// What a tick runtime does and refuses to do. Real time was a second clock bolted to a turn
    /// engine: every turn-shaped call was still there, still callable, and still did turn things.
    /// </summary>
    /// <remarks>
    /// <c>EndTurn()</c> ran a whole turn cycle, every enemy's telegraphed move included, in a game
    /// with no turns — so a front end that left its <b>End turn</b> button wired up, which is the
    /// default in every sample and every doc snippet, gave the player a button that fired every
    /// enemy's move at once for free. <c>turn_start</c> fired at <c>StartBattle</c> while the
    /// diagnostic whose whole job is to explain how real time differs from turns told authors it
    /// never fired at all. These are the round that made the engine and the documentation agree.
    /// </remarks>
    public sealed class RealTimeTests
    {
        private const string Content =
            "enemy \"Hollow\"\n" +
            "  hp 16\n" +
            "  on every 2s:\n" +
            "    deal 5 to player\n";

        private static CardRuntime RealTime(string content = Content, int rate = 10) =>
            Create(content, new RuntimeOptions { Seed = 1, Clock = new TickClock(rate) });

        // Every turn-shaped call refuses -----------------------------------------------------

        [Fact]
        [Trait("Regression", "endturn-runs-a-turn-on-a-tick-runtime")]
        public void EndTurn_refuses_on_a_tick_runtime()
        {
            CardRuntime runtime = RealTime();
            Enemy(runtime);
            Start(runtime);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => runtime.EndTurn());

            Assert.Contains("EndTurn needs turns", error.Message);
            Assert.Contains("Tick()", error.Message);
        }

        [Fact]
        [Trait("Regression", "endturn-runs-a-turn-on-a-tick-runtime")]
        public void Pass_CanAct_and_ActiveMember_refuse_too()
        {
            CardRuntime runtime = RealTime();
            Enemy(runtime);
            Start(runtime);

            Assert.Throws<InvalidOperationException>(() => runtime.Pass(runtime.Player));
            Assert.Throws<InvalidOperationException>(() => runtime.CanAct(runtime.Player));
            Assert.Throws<InvalidOperationException>(() => _ = runtime.ActiveMember);
        }

        /// <summary>The mirror of it: a turn runtime still refuses <c>Tick</c>, as it always did.</summary>
        [Fact]
        public void And_a_turn_runtime_still_refuses_Tick()
        {
            CardRuntime runtime = Create(Content);
            Enemy(runtime);
            Start(runtime);

            Assert.Throws<InvalidOperationException>(() => runtime.Tick());
            Assert.Equal(ActionResult.Played, runtime.EndTurn());
        }

        // No turn is taken, so none is counted and none is announced --------------------------

        [Fact]
        [Trait("Regression", "turn-start-fires-at-startbattle-on-a-tick-clock")]
        public void StartBattle_raises_no_turn_event_on_a_tick_clock()
        {
            var host = new Heard();
            CardRuntime runtime = Create(Content, new RuntimeOptions { Seed = 1, Clock = new TickClock(10), Host = host });
            Enemy(runtime);
            Start(runtime);

            Assert.Contains("battle_start", host.Names);
            Assert.DoesNotContain("turn_start", host.Names);
            Assert.DoesNotContain("turn_end", host.Names);
        }

        [Fact]
        [Trait("Regression", "turn-start-fires-at-startbattle-on-a-tick-clock")]
        public void And_the_turn_number_stays_at_zero_for_the_whole_fight()
        {
            CardRuntime runtime = RealTime();
            Enemy(runtime);
            Start(runtime);

            Assert.Equal(0, runtime.State.Turn);
            runtime.Tick(100);
            Assert.Equal(0, runtime.State.Turn);
        }

        /// <summary>
        /// A hand is not a turn: a real-time deckbuilder still gets its opening hand, because that
        /// happens at <c>StartBattle</c> and not at a turn start.
        /// </summary>
        [Fact]
        public void But_the_opening_hand_is_still_dealt()
        {
            CardRuntime runtime = RealTime(
                "card \"Zap\"\n" +
                "  cost 1\n" +
                "  effect:\n" +
                "    deal 3 to target\n");
            runtime.AddDeck("Zap", "Zap", "Zap", "Zap", "Zap", "Zap");
            Enemy(runtime);
            runtime.StartBattle(shuffle: false);

            Assert.Equal(5, runtime.State.ZoneOf(runtime.Player, Zones.Hand).Count);
        }

        // A fight the game ends itself ---------------------------------------------------------

        [Fact]
        [Trait("Regression", "a-wave-game-needs-a-decoy-to-exist")]
        public void With_ends_called_an_empty_board_does_not_win_the_fight()
        {
            CardRuntime runtime = RealTime(
                "ruleset\n" +
                "  ends: called\n");
            Entity hollow = Enemy(runtime, hp: 10);
            Start(runtime);

            runtime.Execute("deal 99 to enemies");

            Assert.True(hollow.IsDead);
            Assert.Empty(runtime.State.Actors(Team.Enemy));
            Assert.True(runtime.State.InBattle);
            Assert.Null(runtime.Won);
        }

        [Fact]
        [Trait("Regression", "a-wave-game-needs-a-decoy-to-exist")]
        public void Until_the_game_says_so()
        {
            var host = new Heard();
            CardRuntime runtime = Create(
                "ruleset\n  ends: called\n",
                new RuntimeOptions { Seed = 1, Clock = new TickClock(10), Host = host });
            Enemy(runtime, hp: 10);
            Start(runtime);
            runtime.Execute("deal 99 to enemies");
            host.Names.Clear();

            Assert.Equal(ActionResult.Played, runtime.EndBattle(won: true));

            Assert.True(runtime.Won);
            Assert.False(runtime.State.InBattle);
            Assert.Contains("battle_end", host.Names);
            Assert.Equal(ActionResult.Unplayable, runtime.EndBattle(won: true));
        }

        /// <summary>The default is untouched, which is what makes this setting safe to add.</summary>
        [Fact]
        public void And_without_the_setting_the_last_enemy_still_wins_it()
        {
            CardRuntime runtime = RealTime();
            Enemy(runtime, hp: 10);
            Start(runtime);

            runtime.Execute("deal 99 to enemies");

            Assert.True(runtime.Won);
            Assert.False(runtime.State.InBattle);
        }

        // A spawn says so, and a game can place it ----------------------------------------------

        [Fact]
        [Trait("Regression", "spawnenemy-raises-no-event")]
        public void SpawnEnemy_raises_created()
        {
            var host = new Heard();
            CardRuntime runtime = Create(Content, new RuntimeOptions { Seed = 1, Clock = new TickClock(10), Host = host });

            Entity hollow = runtime.SpawnEnemy("Hollow");

            Assert.Equal(new[] { "created" }, host.Names);
            Assert.Same(hollow, host.Events[0].Target);
        }

        [Fact]
        [Trait("Regression", "spawnenemy-raises-no-event")]
        public void So_content_can_meet_an_arrival()
        {
            CardRuntime runtime = RealTime(
                "board \"Line\"\n" +
                "  lanes 3\n" +
                "  ranks 4\n" +
                "  facing\n\n" +
                "enemy \"Hollow\"\n" +
                "  hp 16\n" +
                "  on created(target:self):\n" +
                "    self.rank = 3\n");
            Start(runtime);

            Entity hollow = runtime.SpawnEnemy("Hollow");

            Assert.Equal(3, hollow.Rank);
        }

        [Fact]
        [Trait("Regression", "no-documented-call-places-an-actor")]
        public void Place_stands_an_actor_where_the_game_says()
        {
            CardRuntime runtime = RealTime(
                "board \"Line\"\n" +
                "  lanes 3\n" +
                "  ranks 4\n" +
                "  facing\n\n" + Content);
            Start(runtime);
            Entity hollow = runtime.SpawnEnemy("Hollow");

            Assert.True(runtime.Place(hollow, lane: 2, rank: 3));
            Assert.Equal((2, 3), hollow.Slot);

            // Already there is not a refusal.
            Assert.True(runtime.Place(hollow, lane: 2, rank: 3));
        }

        [Fact]
        [Trait("Regression", "no-documented-call-places-an-actor")]
        public void And_refuses_a_slot_the_board_does_not_have_rather_than_clamping()
        {
            CardRuntime runtime = RealTime(
                "board \"Line\"\n" +
                "  lanes 3\n" +
                "  ranks 4\n" +
                "  facing\n\n" + Content);
            Start(runtime);
            Entity hollow = runtime.SpawnEnemy("Hollow");

            ArgumentException error = Assert.Throws<ArgumentException>(() => runtime.Place(hollow, lane: 0, rank: 9));

            Assert.Contains("Line", error.Message);
            Assert.Contains("rank 9", error.Message);
        }

        /// <summary>A place is a rule, so content may refuse one, and <c>Place</c> honours that.</summary>
        [Fact]
        public void A_before_moved_listener_can_refuse_a_placement()
        {
            CardRuntime runtime = RealTime(
                "board \"Line\"\n" +
                "  lanes 3\n" +
                "  ranks 4\n" +
                "  facing\n\n" +
                "enemy \"Hollow\"\n" +
                "  hp 16\n" +
                "  on before_moved(target:self):\n" +
                "    cancel\n");
            Start(runtime);
            Entity hollow = runtime.SpawnEnemy("Hollow");
            (int Lane, int Rank) was = hollow.Slot;

            Assert.False(runtime.Place(hollow, lane: 2, rank: 3));
            Assert.Equal(was, hollow.Slot);
        }

        // The two numbers a real-time interface reads --------------------------------------------

        [Fact]
        [Trait("Regression", "a-real-time-ui-needs-two-undocumented-numbers")]
        public void AbilitiesOf_lists_what_an_actor_is_carrying()
        {
            CardRuntime runtime = RealTime(Abilities);
            runtime.GrantAbility("Bolt", runtime.Player);
            runtime.GrantAbility("Ward", runtime.Player);
            Enemy(runtime);
            Start(runtime);

            Assert.Equal(new[] { "Bolt", "Ward" }, runtime.AbilitiesOf(runtime.Player).Select(a => a.Name));
        }

        [Fact]
        [Trait("Regression", "a-real-time-ui-needs-two-undocumented-numbers")]
        public void ReadyIn_is_the_sweep_a_cooldown_button_draws()
        {
            CardRuntime runtime = RealTime(Abilities);
            Entity bolt = runtime.GrantAbility("Bolt", runtime.Player);
            Entity hollow = Enemy(runtime);
            Start(runtime);

            // Before it has ever been used there is no `ready_at` stat at all, and "absent" has to
            // read as ready rather than as zero seconds left.
            Assert.True(runtime.IsReady(bolt));
            Assert.Equal(0, runtime.ReadyIn(bolt));

            Assert.Equal(ActionResult.Played, runtime.UseAbility(bolt, hollow));
            Assert.Equal(20, runtime.ReadyIn(bolt));       // 2s at ten ticks a second

            runtime.Tick(15);
            Assert.Equal(5, runtime.ReadyIn(bolt));
            runtime.Tick(5);
            Assert.Equal(0, runtime.ReadyIn(bolt));
            Assert.True(runtime.IsReady(bolt));
        }

        private const string Abilities =
            "ability \"Bolt\"\n" +
            "  cooldown 2s\n" +
            "  target enemy\n" +
            "  effect:\n" +
            "    deal 5 to target\n\n" +
            "ability \"Ward\"\n" +
            "  cooldown 3s\n" +
            "  effect:\n" +
            "    block 4\n";

        /// <summary>Everything the runtime told its host, in order.</summary>
        private sealed class Heard : EffectHostBase
        {
            public List<GameEvent> Events { get; } = new List<GameEvent>();
            public List<string> Names { get; } = new List<string>();

            public override void OnEvent(GameEvent gameEvent)
            {
                Events.Add(gameEvent);
                Names.Add(gameEvent.Name);
            }
        }
    }
}
