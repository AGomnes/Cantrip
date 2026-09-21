using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// What the editor can do to a paused game over the channel: hold it, walk it one trigger at a
    /// time, and stop it before a trigger it names.
    /// </summary>
    public sealed class SteppingChannelTests
    {
        private const string File = "res://stepping.cantrip";

        private const string Content =
            "relic \"Tracker\"\n" +
            "  on damaged:\n" +
            "    gain 1 gold\n" +
            "\n" +
            "enemy \"Slime\"\n" +
            "  hp 30\n" +
            "  move \"Poke\":\n" +
            "    deal 1 to player\n" +
            "  pattern cycle Poke\n";

        private static CantripDebugService Started()
        {
            ContentLibrary library = ContentLibrary.FromText(Content, File);
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 1 });
            runtime.CreatePlayer();
            runtime.AddRelic("Tracker");
            runtime.SpawnEnemy("Slime");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            return new CantripDebugService(runtime);
        }

        [Fact]
        public void A_running_game_reports_itself_as_running()
        {
            CantripDebugService service = Started();

            CantripStepState state = service.Stepping();

            Assert.False(state.Paused);
            Assert.True(state.Steppable);
            Assert.Equal(0, state.Pending);
            Assert.Equal(string.Empty, state.Next);
            Assert.Equal(string.Empty, state.Message);
        }

        [Fact]
        public void Pausing_holds_a_trigger_and_says_what_it_is()
        {
            CantripDebugService service = Started();
            Assert.True(service.Pause().Paused);

            service.Runtime.Execute("deal 1 to enemy");
            CantripStepState held = service.Stepping();

            Assert.True(held.Paused);
            Assert.Equal(1, held.Pending);
            Assert.Equal("damaged", held.Event);
            Assert.Contains("damaged", held.Next);
            Assert.Equal(File, held.Span.File);
            Assert.Equal(2, held.Span.Line);
            Assert.Equal(0, service.Runtime.Player!.GetInt("gold"));
        }

        [Fact]
        public void Stepping_resolves_one_trigger_and_then_says_there_is_nothing_left()
        {
            CantripDebugService service = Started();
            service.Pause();
            service.Runtime.Execute("deal 1 to enemy");

            CantripStepState after = service.Step();

            Assert.Equal(1, service.Runtime.Player!.GetInt("gold"));
            Assert.Equal(0, after.Pending);
            Assert.Equal(string.Empty, after.Message);

            // A button that silently does nothing is worse than one that says why.
            CantripStepState again = service.Step();
            Assert.Equal("Nothing is queued.", again.Message);
        }

        [Fact]
        public void Resuming_lets_the_rest_go()
        {
            CantripDebugService service = Started();
            service.Pause();
            service.Runtime.Execute("deal 1 to enemy");

            Assert.False(service.Resume().Paused);
            service.Runtime.Interpreter.Drain();

            Assert.Equal(1, service.Runtime.Player!.GetInt("gold"));
            Assert.Equal(0, service.Stepping().Pending);
        }

        [Fact]
        public void A_breakpoint_on_an_event_stops_the_game_where_it_was_written()
        {
            CantripDebugService service = Started();
            Assert.Equal(1, service.BreakOnEvent("damaged", true).Breakpoints);

            service.Runtime.Execute("deal 1 to enemy");
            CantripStepState stopped = service.Stepping();

            Assert.True(stopped.Paused);
            Assert.Equal(1, stopped.Pending);
            Assert.Equal(File, stopped.Span.File);
            Assert.Equal(0, service.Runtime.Player!.GetInt("gold"));
        }

        [Fact]
        public void A_breakpoint_on_a_line_can_be_set_and_taken_away()
        {
            CantripDebugService service = Started();

            Assert.Equal(1, service.Break(File, 2, true).Breakpoints);
            Assert.Equal(0, service.Break(File, 2, false).Breakpoints);

            service.Break(File, 2, true);
            service.BreakOnEvent("damaged", true);
            Assert.Equal(0, service.ClearBreakpoints().Breakpoints);

            service.Runtime.Execute("deal 1 to enemy");
            Assert.False(service.Stepping().Paused);
            Assert.Equal(1, service.Runtime.Player!.GetInt("gold"));
        }

        [Fact]
        public void A_trigger_that_fails_while_stepping_is_reported_rather_than_thrown()
        {
            ContentLibrary library = ContentLibrary.FromText(
                "relic \"Breaker\"\n" +
                "  on damaged:\n" +
                "    explode\n" +
                "\n" +
                "enemy \"Slime\"\n" +
                "  hp 30\n",
                File);
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 1 });
            runtime.CreatePlayer();
            runtime.AddRelic("Breaker");
            runtime.SpawnEnemy("Slime");
            var service = new CantripDebugService(runtime);

            service.Pause();
            runtime.Execute("deal 1 to enemy");

            CantripStepState failed = service.Step();

            // The point of a debugger is to walk through things that might not work.
            Assert.NotEqual(string.Empty, failed.Message);
            Assert.Equal(0, failed.Pending);
        }
    }
}
