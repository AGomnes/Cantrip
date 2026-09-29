#nullable enable
using System;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Snapshots
{
    /// <summary>
    /// A tick is a length of time only while something says how many of them a second is, so the
    /// rate is part of the save. Restoring across a change of rate would re-time every cooldown,
    /// every <c>for 3s</c> and every <c>on every 2s</c> in the game, with nothing to show for it.
    /// </summary>
    public sealed class ClockRateSaveTests
    {
        private const string Content = """
            ruleset
              clock ticks

            ability "Bolt"
              cooldown 10s
              effect:
                deal 5 to enemies

            enemy "Dummy"
              hp 200
            """;

        private static CardRuntime Start(IGameClock clock)
        {
            var library = new ContentLibrary();
            library.LoadText(Content, "content.cantrip");
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 3, Clock = clock });
            Entity player = runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            runtime.GrantAbility("Bolt", player);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            return runtime;
        }

        [Fact]
        public void A_save_records_the_rate_its_clock_counts_at()
        {
            using CardRuntime fast = Start(new TickClock(60));

            Assert.Equal(60, fast.Capture().ClockUnitsPerSecond);
        }

        [Fact]
        public void A_turn_clock_records_no_rate_and_is_not_checked_against_one()
        {
            var library = new ContentLibrary();
            library.LoadText("enemy \"Dummy\"\n  hp 10\n", "content.cantrip");
            using var turns = new CardRuntime(library, new RuntimeOptions { Seed = 3 });
            turns.CreatePlayer();
            turns.SpawnEnemy("Dummy");
            turns.StartBattle(shuffle: false, drawOpeningHand: false);

            GameSnapshot snapshot = turns.Capture();
            Assert.Equal(0, snapshot.ClockUnitsPerSecond);

            // And it restores, because nothing recorded a rate to disagree about.
            turns.Restore(snapshot);
        }

        [Fact]
        public void A_save_from_a_faster_clock_is_refused_rather_than_re_timed()
        {
            using CardRuntime fast = Start(new TickClock(60));
            Entity bolt = fast.AbilitiesOf(fast.Player)[0];
            Assert.Equal(ActionResult.Played, fast.UseAbility(bolt, null));
            GameSnapshot snapshot = fast.Capture();

            // 600 ticks of cooldown at 60/s is ten seconds. At 30/s the same number is twenty.
            Assert.Equal(600, fast.ReadyIn(bolt) + 0);

            using CardRuntime slow = Start(new TickClock(30));
            Entity slowBolt = slow.AbilitiesOf(slow.Player)[0];
            long before = slow.ReadyIn(slowBolt);

            var refused = Assert.Throws<InvalidOperationException>(() => slow.Restore(snapshot));
            Assert.Contains("60 units a second", refused.Message);
            Assert.Contains("30", refused.Message);

            // Refused before anything was torn down: the game is exactly as it was.
            Assert.Equal(before, slow.ReadyIn(slowBolt));
        }

        [Fact]
        public void A_real_time_save_is_refused_by_a_turn_based_game()
        {
            using CardRuntime fast = Start(new TickClock(60));
            GameSnapshot snapshot = fast.Capture();

            var library = new ContentLibrary();
            library.LoadText("enemy \"Dummy\"\n  hp 10\n", "content.cantrip");
            using var turns = new CardRuntime(library, new RuntimeOptions { Seed = 3 });
            turns.CreatePlayer();

            var refused = Assert.Throws<InvalidOperationException>(() => turns.Restore(snapshot));
            Assert.Contains("does not measure seconds", refused.Message);
        }

        [Fact]
        public void A_save_written_before_the_rate_was_recorded_still_loads()
        {
            using CardRuntime fast = Start(new TickClock(60));
            GameSnapshot snapshot = fast.Capture();

            // What a save from format 1, 2 or an unpublished format 3 carries: no rate at all.
            snapshot.ClockUnitsPerSecond = 0;

            using CardRuntime other = Start(new TickClock(30));
            other.Restore(snapshot);
            Assert.Equal(snapshot.ClockNow, other.State.Clock.Now);
        }
    }
}
