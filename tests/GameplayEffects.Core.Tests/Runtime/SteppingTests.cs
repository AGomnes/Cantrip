using GameplayEffects.Diagnostics;
using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Runtime.RuntimeTestKit;

namespace GameplayEffects.Tests.Runtime
{
    /// <summary>
    /// Resolving queued triggers one at a time, which is what lets a debugger walk a game instead of
    /// only watching what it already did.
    /// </summary>
    public sealed class SteppingTests
    {
        private const string Content =
            "relic \"Tracker\"\n" +
            "  on damaged:\n" +
            "    gain 1 gold\n";

        private static CardRuntime Ready(out Entity enemy)
        {
            CardRuntime runtime = Create(Content);
            runtime.AddRelic("Tracker");
            enemy = Enemy(runtime);
            return runtime;
        }

        [Fact]
        public void A_game_that_never_pauses_resolves_everything_as_before()
        {
            CardRuntime runtime = Ready(out Entity _);

            runtime.Execute("deal 1 to enemy");

            Assert.Equal(1, runtime.Player!.GetInt("gold"));
            Assert.Equal(0, runtime.Interpreter.PendingTriggers);
            Assert.False(runtime.Interpreter.Paused);
        }

        [Fact]
        public void Pausing_holds_queued_triggers_until_they_are_stepped()
        {
            CardRuntime runtime = Ready(out Entity _);
            runtime.Interpreter.Pause();

            runtime.Execute("deal 1 to enemy");

            // The action itself finished; only what it queued is waiting.
            Assert.Equal(0, runtime.Player!.GetInt("gold"));
            Assert.Equal(1, runtime.Interpreter.PendingTriggers);

            Assert.True(runtime.Interpreter.TryDrainStep());
            Assert.Equal(1, runtime.Player.GetInt("gold"));
            Assert.Equal(0, runtime.Interpreter.PendingTriggers);

            Assert.False(runtime.Interpreter.TryDrainStep());
        }

        [Fact]
        public void Next_says_what_is_about_to_run_and_where_it_was_written()
        {
            CardRuntime runtime = Ready(out Entity _);
            runtime.Interpreter.Pause();
            runtime.Execute("deal 1 to enemy");

            PendingTrigger next = runtime.Interpreter.Next!.Value;

            Assert.Equal("damaged", next.EventName);
            Assert.Contains("damaged", next.Description);
            Assert.Equal(2, next.Span.Line);           // the `on damaged:` line
            Assert.True(next.ListenerId > 0);

            runtime.Interpreter.TryDrainStep();
            Assert.Null(runtime.Interpreter.Next);
        }

        [Fact]
        public void Each_step_resolves_exactly_one_trigger()
        {
            CardRuntime runtime = Create(
                "relic \"One\"\n" +
                "  on damaged:\n" +
                "    gain 1 gold\n" +
                "\n" +
                "relic \"Two\"\n" +
                "  on damaged:\n" +
                "    gain 1 gold\n");
            runtime.AddRelic("One");
            runtime.AddRelic("Two");
            Enemy(runtime);
            runtime.Interpreter.Pause();

            runtime.Execute("deal 1 to enemy");
            Assert.Equal(2, runtime.Interpreter.PendingTriggers);

            Assert.True(runtime.Interpreter.TryDrainStep());
            Assert.Equal(1, runtime.Player!.GetInt("gold"));
            Assert.Equal(1, runtime.Interpreter.PendingTriggers);

            Assert.True(runtime.Interpreter.TryDrainStep());
            Assert.Equal(2, runtime.Player.GetInt("gold"));
        }

        [Fact]
        public void Resuming_lets_the_rest_resolve_on_the_next_drain()
        {
            CardRuntime runtime = Ready(out Entity _);
            runtime.Interpreter.Pause();
            runtime.Execute("deal 1 to enemy");
            Assert.Equal(1, runtime.Interpreter.PendingTriggers);

            runtime.Interpreter.Resume();
            runtime.Interpreter.Drain();

            Assert.Equal(1, runtime.Player!.GetInt("gold"));
            Assert.Equal(0, runtime.Interpreter.PendingTriggers);
        }

        [Fact]
        public void A_breakpoint_on_an_event_stops_the_drain_before_the_trigger()
        {
            CardRuntime runtime = Ready(out Entity _);
            runtime.Interpreter.Breakpoints.AddEvent("damaged");

            runtime.Execute("deal 1 to enemy");

            Assert.True(runtime.Interpreter.Paused);
            Assert.Equal(0, runtime.Player!.GetInt("gold"));
            Assert.Equal(1, runtime.Interpreter.PendingTriggers);

            // Stepping past a breakpoint is how a debugger gets on with it.
            Assert.True(runtime.Interpreter.TryDrainStep());
            Assert.Equal(1, runtime.Player.GetInt("gold"));
        }

        [Fact]
        public void A_breakpoint_on_a_line_stops_only_that_trigger()
        {
            CardRuntime runtime = Ready(out Entity _);

            // Look at the trigger once to learn where it is written.
            runtime.Interpreter.Pause();
            runtime.Execute("deal 1 to enemy");
            SourceSpan where = runtime.Interpreter.Next!.Value.Span;
            runtime.Interpreter.Resume();
            runtime.Interpreter.Drain();

            runtime.Interpreter.Breakpoints.Add(where.File!, where.Line);
            runtime.Execute("deal 1 to enemy");
            Assert.True(runtime.Interpreter.Paused);
            Assert.Equal(1, runtime.Interpreter.PendingTriggers);

            runtime.Interpreter.Breakpoints.Clear();
            runtime.Interpreter.Resume();
            runtime.Interpreter.Drain();

            // A line nothing is written on stops nothing.
            runtime.Interpreter.Breakpoints.Add(where.File!, where.Line + 100);
            runtime.Execute("deal 1 to enemy");
            Assert.False(runtime.Interpreter.Paused);
            Assert.Equal(0, runtime.Interpreter.PendingTriggers);
        }

        [Fact]
        public void Breakpoints_are_tooling_and_keep_out_of_the_rules()
        {
            var breakpoints = new TriggerBreakpoints();
            Assert.False(breakpoints.Any);

            breakpoints.Add("res://a.ge", 3);
            breakpoints.AddEvent("Damaged");
            Assert.Equal(2, breakpoints.Count);

            // Matched the way content is written, without case ceremony.
            Assert.True(breakpoints.RemoveEvent("damaged"));
            Assert.True(breakpoints.Remove("RES://A.GE", 3));
            Assert.False(breakpoints.Any);
        }

        [Fact]
        public void A_paused_game_is_not_a_saveable_one()
        {
            CardRuntime runtime = Ready(out Entity _);
            runtime.Interpreter.Pause();
            runtime.Execute("deal 1 to enemy");

            // The queue holds live closures over listeners, events and chains that no snapshot can
            // represent, so the existing refusal is the right answer rather than a silent half-save.
            Assert.False(runtime.CanCapture);

            runtime.Interpreter.TryDrainStep();
            Assert.True(runtime.CanCapture);
        }

        [Fact]
        public void Stepping_through_a_game_reaches_the_same_state_as_letting_it_run()
        {
            CardRuntime straight = Ready(out Entity _);
            straight.Execute("deal 1 to enemy");

            CardRuntime stepped = Ready(out Entity _);
            stepped.Interpreter.Pause();
            stepped.Execute("deal 1 to enemy");
            while (stepped.Interpreter.TryDrainStep())
            {
            }

            // Stepping must be a way of watching a game, not a different game.
            Assert.Equal(straight.State.ComputeHash(), stepped.State.ComputeHash());
        }

        [Fact]
        public void A_trigger_that_fails_while_stepping_drops_the_rest()
        {
            CardRuntime runtime = Create(
                "relic \"Breaker\"\n" +
                "  on damaged:\n" +
                "    explode\n" +
                "\n" +
                "relic \"Later\"\n" +
                "  on damaged:\n" +
                "    gain 1 gold\n");
            runtime.AddRelic("Breaker");
            runtime.AddRelic("Later");
            Enemy(runtime);
            runtime.Interpreter.Pause();

            runtime.Execute("deal 1 to enemy");
            Assert.Equal(2, runtime.Interpreter.PendingTriggers);

            Assert.Throws<RuntimeError>(() => runtime.Interpreter.TryDrainStep());

            // Later triggers would run against half-resolved state, so they go with it.
            Assert.Equal(0, runtime.Interpreter.PendingTriggers);
            Assert.Equal(0, runtime.Player!.GetInt("gold"));
        }
    }
}
