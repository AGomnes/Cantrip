#nullable enable
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// A runtime subscribes to its clock when it is built. Two runtimes given the same clock both
    /// hear every tick, so the one a game has finished with goes on expiring statuses and running
    /// scheduled work on a game nobody is playing, and the clock keeps it alive while it does.
    /// <see cref="CardRuntime.Dispose"/> is how a game lets go.
    /// </summary>
    public sealed class RuntimeDisposeTests
    {
        private const string Content = """
            status "Weak"
              tags debuff

            card "Hex"
              cost 0
              effect:
                apply Weak 1 to player for 1

            enemy "Dummy"
              hp 40
            """;

        private static CardRuntime OnClock(TickClock clock, out Entity player)
        {
            var runtime = CardRuntime.FromText(Content, new RuntimeOptions { Seed = 1, Clock = clock });
            player = runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            runtime.AddCard("Hex", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.Play("Hex");
            Assert.NotNull(player.FindAttached("Weak"));
            return runtime;
        }

        [Fact]
        public void A_disposed_runtime_stops_hearing_the_clock_it_was_given()
        {
            var clock = new TickClock();
            CardRuntime finished = OnClock(clock, out Entity left);
            CardRuntime playing = OnClock(clock, out Entity current);

            finished.Dispose();
            clock.Tick();

            // The one still in play kept its promise; the one let go of did not carry on without us.
            Assert.Null(current.FindAttached("Weak"));
            Assert.NotNull(left.FindAttached("Weak"));
        }

        [Fact]
        public void Before_disposing_both_runtimes_hear_the_same_clock()
        {
            var clock = new TickClock();
            CardRuntime first = OnClock(clock, out Entity a);
            CardRuntime second = OnClock(clock, out Entity b);

            clock.Tick();

            Assert.Null(a.FindAttached("Weak"));
            Assert.Null(b.FindAttached("Weak"));

            first.Dispose();
            second.Dispose();
        }

        [Fact]
        public void Disposing_twice_does_nothing_the_second_time_and_the_game_is_still_readable()
        {
            var clock = new TickClock();
            CardRuntime runtime = OnClock(clock, out Entity player);

            runtime.Dispose();
            runtime.Dispose();

            // Nothing was torn down: the state is an ordinary object and still answers.
            Assert.Equal(player, runtime.Player);
            Assert.True(runtime.State.InBattle);
            Assert.NotNull(player.FindAttached("Weak"));
        }
    }
}
