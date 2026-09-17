using System;

namespace GameplayEffects.GodotAdapter
{
    /// <summary>
    /// Turns physics frames into whole simulation ticks.
    /// </summary>
    /// <remarks>
    /// The core's <c>TickClock</c> only ever advances in whole units, and the engine's physics step
    /// is fixed, so the conversion is an integer ratio: each frame adds <see cref="TicksPerSecond"/>
    /// to a counter and a tick comes out every <see cref="FramesPerSecond"/>. Fifty ticks a second
    /// on a sixty hertz step is then exactly fifty ticks every sixty frames, for ever, with no
    /// floating-point remainder to drift.
    /// <para>
    /// The frame's delta is deliberately ignored. A fixed step is fixed by definition, and using the
    /// measured delta instead would make the simulation depend on how long the last frame happened
    /// to take, which is the one thing a deterministic engine must not do.
    /// </para>
    /// </remarks>
    public sealed class TickAccumulator
    {
        /// <summary>Ticks a single frame may run at most. Beyond this the simulation skips time.</summary>
        public const int DefaultMaxCatchUp = 8;

        private int _remainder;

        public TickAccumulator(int ticksPerSecond = 60, int framesPerSecond = 60, int maxCatchUp = DefaultMaxCatchUp)
        {
            Validate(ticksPerSecond, framesPerSecond);
            TicksPerSecond = ticksPerSecond;
            FramesPerSecond = framesPerSecond;
            MaxCatchUp = maxCatchUp;
        }

        public int TicksPerSecond { get; private set; }

        public int FramesPerSecond { get; private set; }

        /// <summary>
        /// The most ticks one <see cref="Advance()"/> may return, or 0 for no cap. After a stall the
        /// ticks beyond it are abandoned rather than owed: a game that tried to repay them would
        /// spend every later frame further behind.
        /// </summary>
        public int MaxCatchUp { get; set; }

        /// <summary>Ticks this accumulator has handed out.</summary>
        public long TotalTicks { get; private set; }

        /// <summary>Ticks skipped by <see cref="MaxCatchUp"/>, which is how much time the game lost.</summary>
        public long Dropped { get; private set; }

        /// <summary>The part of a tick carried to the next frame, in frame units.</summary>
        public int Remainder => _remainder;

        /// <summary>
        /// Changes the ratio, as when the engine's physics rate changes. The same values are a
        /// no-op, so this is safe to call every frame; a real change clears the remainder, which
        /// measured the old ratio and means nothing in the new one.
        /// </summary>
        public void Configure(int ticksPerSecond, int framesPerSecond)
        {
            if (ticksPerSecond == TicksPerSecond && framesPerSecond == FramesPerSecond) return;

            Validate(ticksPerSecond, framesPerSecond);
            TicksPerSecond = ticksPerSecond;
            FramesPerSecond = framesPerSecond;
            _remainder = 0;
        }

        /// <summary>Whole ticks owed for one physics frame.</summary>
        public int Advance() => Advance(1);

        /// <summary>Whole ticks owed for <paramref name="frames"/> physics frames.</summary>
        public int Advance(int frames)
        {
            if (frames < 0) throw new ArgumentOutOfRangeException(nameof(frames), "Time does not run backwards.");
            if (frames == 0) return 0;

            long owed = _remainder + (long)frames * TicksPerSecond;
            long whole = owed / FramesPerSecond;
            _remainder = (int)(owed - whole * FramesPerSecond);

            int ticks = whole > int.MaxValue ? int.MaxValue : (int)whole;
            if (MaxCatchUp > 0 && ticks > MaxCatchUp)
            {
                Dropped += ticks - MaxCatchUp;
                ticks = MaxCatchUp;
            }

            TotalTicks += ticks;
            return ticks;
        }

        /// <summary>Clears the remainder and the counters, for a new game or a restored save.</summary>
        public void Reset()
        {
            _remainder = 0;
            TotalTicks = 0;
            Dropped = 0;
        }

        private static void Validate(int ticksPerSecond, int framesPerSecond)
        {
            if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond), "A clock needs at least one tick per second.");
            if (framesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "A physics step needs at least one frame per second.");
        }
    }
}
