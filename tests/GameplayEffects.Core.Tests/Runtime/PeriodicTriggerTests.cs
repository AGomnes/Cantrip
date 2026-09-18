using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Runtime.RuntimeTestKit;

namespace GameplayEffects.Tests.Runtime
{
    /// <summary>
    /// `on every 1s:` — a trigger the clock pumps rather than an event anything raises.
    /// </summary>
    public sealed class PeriodicTriggerTests
    {
        private const string Content =
            "relic \"Ticker\"\n" +
            "  on every 1s:\n" +
            "    gain 1 gold\n";

        /// <summary>Ten ticks to the second, so `every 1s` is a round ten units.</summary>
        private static CardRuntime RealTime(string content = Content)
        {
            CardRuntime runtime = Create(content, new RuntimeOptions { Seed = 1, Clock = new TickClock(10) });
            runtime.AddRelic("Ticker");
            return runtime;
        }

        [Fact]
        public void An_every_listener_registers_itself_as_periodic()
        {
            CardRuntime runtime = RealTime();

            Listener listener = Assert.Single(runtime.State.Events.Periodic);

            Assert.Equal(10, listener.IntervalUnits);
            Assert.Equal(10, listener.NextDueAt);
            Assert.Equal("every", listener.EventName);
        }

        [Fact]
        public void A_periodic_trigger_fires_on_its_interval_and_not_before()
        {
            CardRuntime runtime = RealTime();

            runtime.Tick(9);
            Assert.Equal(0, runtime.Player!.GetInt("gold"));

            runtime.Tick(1);
            Assert.Equal(1, runtime.Player.GetInt("gold"));

            runtime.Tick(10);
            Assert.Equal(2, runtime.Player.GetInt("gold"));
        }

        [Fact]
        public void It_keeps_firing_at_a_steady_rate()
        {
            CardRuntime runtime = RealTime();

            runtime.Tick(30);

            Assert.Equal(3, runtime.Player!.GetInt("gold"));
        }

        [Fact]
        public void Seconds_mean_nothing_to_a_turn_clock_so_nothing_is_registered()
        {
            // The clock is the only thing that can convert a unit, and a turn clock refuses
            // seconds. Registering the listener as periodic anyway would make it half-work.
            CardRuntime runtime = Create(Content);
            runtime.AddRelic("Ticker");

            Assert.Empty(runtime.State.Events.Periodic);
        }

        [Fact]
        public void An_interval_in_turns_is_registered_on_a_turn_clock()
        {
            CardRuntime runtime = Create(
                "relic \"Slowpoke\"\n" +
                "  on every 2 turns:\n" +
                "    gain 1 gold\n");
            runtime.AddRelic("Slowpoke");

            Listener listener = Assert.Single(runtime.State.Events.Periodic);
            Assert.Equal(2, listener.IntervalUnits);
        }

        [Fact]
        public void When_the_next_firing_is_due_survives_a_save_and_load()
        {
            CardRuntime runtime = RealTime();
            runtime.Tick(5);

            GameSnapshot saved = runtime.Capture();

            runtime.Tick(5);
            Assert.Equal(1, runtime.Player!.GetInt("gold"));

            runtime.Restore(saved);
            Assert.Equal(0, runtime.Player.GetInt("gold"));

            // Five more ticks reach the same due time again, rather than firing at once or never.
            runtime.Tick(4);
            Assert.Equal(0, runtime.Player.GetInt("gold"));
            runtime.Tick(1);
            Assert.Equal(1, runtime.Player.GetInt("gold"));
        }

        [Fact]
        public void Two_games_that_differ_only_in_when_the_next_firing_lands_do_not_hash_the_same()
        {
            CardRuntime runtime = RealTime();
            runtime.Tick(5);

            ulong before = runtime.State.ComputeHash();
            runtime.State.Events.Periodic[0].NextDueAt += 1;

            // Different futures must not hash alike, or a lockstep peer would miss the desync.
            Assert.NotEqual(before, runtime.State.ComputeHash());
        }

        [Fact]
        public void A_periodic_trigger_stops_with_the_entity_that_declared_it()
        {
            CardRuntime runtime = Create(
                "enemy \"Dripper\"\n" +
                "  hp 10\n" +
                "  on every 1s:\n" +
                "    deal 1 to player\n",
                new RuntimeOptions { Seed = 1, Clock = new TickClock(10) });
            Entity dripper = runtime.SpawnEnemy("Dripper");

            runtime.Tick(10);
            Assert.Equal(79, Hp(runtime.Player!));

            runtime.Execute("deal 100 to enemy");
            runtime.Tick(30);

            // Nothing should go on ticking on behalf of something no longer in the game.
            Assert.True(dripper.IsDead);
            Assert.Equal(79, Hp(runtime.Player!));
        }
    }
}
