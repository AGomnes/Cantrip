using System;

namespace GameplayEffects.Runtime
{
    /// <summary>
    /// Abstract game time (section 4.2). The core only ever sees whole units: a
    /// <see cref="TurnClock"/> advances one unit per turn and a <see cref="TickClock"/> one unit per
    /// fixed-timestep tick, so durations, cooldowns and <c>every</c> triggers share one code path.
    /// </summary>
    public interface IGameClock
    {
        long Now { get; }

        event Action<long>? Advanced;

        /// <summary>
        /// Converts a content-authored duration such as <c>3 turns</c> or <c>1.5s</c> to clock units.
        /// Returns false when the unit makes no sense for this clock (seconds on a turn clock).
        /// </summary>
        bool TryConvert(Num amount, string? unit, out long units);

        /// <summary>Restores time from a snapshot without raising <see cref="Advanced"/>.</summary>
        void Restore(long now);
    }

    /// <summary>One unit per turn. The default for turn-based games.</summary>
    public sealed class TurnClock : IGameClock
    {
        public long Now { get; private set; }

        public event Action<long>? Advanced;

        public void AdvanceTurn()
        {
            Now++;
            Advanced?.Invoke(Now);
        }

        public bool TryConvert(Num amount, string? unit, out long units)
        {
            switch (unit)
            {
                case null:
                case "":
                case "turn":
                case "turns":
                case "t":
                    units = Math.Max(0, amount.Ceiling().ToInt());
                    return true;
                default:
                    units = 0;
                    return false;
            }
        }

        public void Restore(long now) => Now = now;
    }

    /// <summary>
    /// Fixed-timestep clock for real-time games. Advance it from the engine's physics step, never
    /// from the render frame, so that simulation results do not depend on frame rate.
    /// </summary>
    public sealed class TickClock : IGameClock
    {
        public TickClock(int ticksPerSecond = 60)
        {
            if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
            TicksPerSecond = ticksPerSecond;
        }

        public int TicksPerSecond { get; }

        public long Now { get; private set; }

        public event Action<long>? Advanced;

        public void Tick(int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                Now++;
                Advanced?.Invoke(Now);
            }
        }

        public bool TryConvert(Num amount, string? unit, out long units)
        {
            switch (unit)
            {
                case "s":
                case "sec":
                case "secs":
                case "second":
                case "seconds":
                    units = Math.Max(0, (amount * TicksPerSecond).Ceiling().ToInt());
                    return true;
                case "ms":
                    units = Math.Max(0, (amount * TicksPerSecond / 1000).Ceiling().ToInt());
                    return true;
                case null:
                case "":
                case "tick":
                case "ticks":
                    units = Math.Max(0, amount.Ceiling().ToInt());
                    return true;
                default:
                    units = 0;
                    return false;
            }
        }

        public void Restore(long now) => Now = now;
    }
}
