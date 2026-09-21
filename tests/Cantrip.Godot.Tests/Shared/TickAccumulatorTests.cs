using System;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// Physics frames to simulation ticks. The engine's step is fixed, so this is an integer ratio
    /// and the only interesting questions are whether it drifts and what it does after a stall.
    /// </summary>
    public sealed class TickAccumulatorTests
    {
        [Fact]
        public void A_clock_that_matches_the_step_runs_one_tick_a_frame()
        {
            var accumulator = new TickAccumulator(ticksPerSecond: 60, framesPerSecond: 60);

            for (int frame = 0; frame < 10; frame++) Assert.Equal(1, accumulator.Advance());

            Assert.Equal(10, accumulator.TotalTicks);
            Assert.Equal(0, accumulator.Remainder);
        }

        [Fact]
        public void A_slower_clock_spreads_its_ticks_across_the_frames()
        {
            var accumulator = new TickAccumulator(ticksPerSecond: 30, framesPerSecond: 60);

            Assert.Equal(new[] { 0, 1, 0, 1, 0, 1 }, Run(accumulator, 6));
            Assert.Equal(3, accumulator.TotalTicks);
        }

        [Fact]
        public void A_faster_clock_runs_several_ticks_a_frame()
        {
            var accumulator = new TickAccumulator(ticksPerSecond: 120, framesPerSecond: 60);

            Assert.Equal(new[] { 2, 2, 2 }, Run(accumulator, 3));
        }

        [Theory]
        [InlineData(50, 60)]
        [InlineData(144, 60)]
        [InlineData(7, 60)]
        [InlineData(60, 144)]
        public void A_rate_that_does_not_divide_the_step_still_never_drifts(int ticksPerSecond, int framesPerSecond)
        {
            var accumulator = new TickAccumulator(ticksPerSecond, framesPerSecond) { MaxCatchUp = 0 };

            // A hundred seconds of frames must be exactly a hundred seconds of ticks.
            for (int frame = 0; frame < framesPerSecond * 100; frame++) accumulator.Advance();

            Assert.Equal(ticksPerSecond * 100L, accumulator.TotalTicks);
            Assert.Equal(0, accumulator.Dropped);
        }

        [Fact]
        public void Catching_up_is_capped_and_the_skipped_ticks_are_counted()
        {
            var accumulator = new TickAccumulator(ticksPerSecond: 60, framesPerSecond: 60, maxCatchUp: 8);

            // The game was suspended for a thousand frames. Replaying them all would only put the
            // next frame further behind, so the time is lost on purpose.
            Assert.Equal(8, accumulator.Advance(1000));
            Assert.Equal(992, accumulator.Dropped);
            Assert.Equal(8, accumulator.TotalTicks);

            // And it carries on normally afterwards rather than owing a debt.
            Assert.Equal(1, accumulator.Advance());
        }

        [Fact]
        public void Several_frames_at_once_come_to_the_same_as_one_at_a_time()
        {
            var together = new TickAccumulator(50, 60) { MaxCatchUp = 0 };
            var separately = new TickAccumulator(50, 60) { MaxCatchUp = 0 };

            int batched = together.Advance(7);
            int singly = 0;
            for (int frame = 0; frame < 7; frame++) singly += separately.Advance();

            Assert.Equal(singly, batched);
            Assert.Equal(separately.Remainder, together.Remainder);
        }

        [Fact]
        public void Changing_the_ratio_starts_the_remainder_again()
        {
            var accumulator = new TickAccumulator(50, 60);
            accumulator.Advance();
            Assert.Equal(50, accumulator.Remainder);

            // The carried fraction measured the old ratio and means nothing in the new one.
            accumulator.Configure(30, 60);
            Assert.Equal(0, accumulator.Remainder);
            Assert.Equal(30, accumulator.TicksPerSecond);
        }

        [Fact]
        public void Configuring_the_same_ratio_changes_nothing()
        {
            var accumulator = new TickAccumulator(50, 60);
            accumulator.Advance();

            // The driver calls this every physics frame, so a repeat must not throw the count away.
            accumulator.Configure(50, 60);

            Assert.Equal(50, accumulator.Remainder);
        }

        [Fact]
        public void Resetting_forgets_the_remainder_and_the_counters()
        {
            var accumulator = new TickAccumulator(50, 60, maxCatchUp: 1);
            accumulator.Advance(600);

            accumulator.Reset();

            Assert.Equal(0, accumulator.TotalTicks);
            Assert.Equal(0, accumulator.Dropped);
            Assert.Equal(0, accumulator.Remainder);
        }

        [Fact]
        public void A_frame_count_of_nothing_owes_nothing()
        {
            var accumulator = new TickAccumulator(60, 60);

            Assert.Equal(0, accumulator.Advance(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => accumulator.Advance(-1));
        }

        [Fact]
        public void A_rate_must_be_a_rate()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickAccumulator(0, 60));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickAccumulator(60, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickAccumulator(60, 60).Configure(-1, 60));
        }

        private static int[] Run(TickAccumulator accumulator, int frames)
        {
            var ticks = new int[frames];
            for (int frame = 0; frame < frames; frame++) ticks[frame] = accumulator.Advance();
            return ticks;
        }
    }
}
