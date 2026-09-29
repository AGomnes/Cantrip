using System;

namespace Cantrip.Runtime
{
    /// <summary>
    /// Abstract game time. The core only ever sees whole units: a
    /// <see cref="TurnClock"/> advances one unit per turn and a <see cref="TickClock"/> one unit per
    /// fixed-timestep tick, so durations, cooldowns and <c>every</c> triggers share one code path.
    /// </summary>
    /// <remarks>
    /// <see cref="TryConvert"/> has a default, so a clock that measures only its own unit needs no
    /// body for it, and a member added in a later release will have one too. <see cref="Now"/>,
    /// <see cref="Advanced"/> and <see cref="Restore"/> deliberately have none: a default for them
    /// could only be a clock stuck at zero, an event that never fires, and a restore that quietly
    /// keeps the wrong time — the kind of silent wrong answer an interface should not offer. See
    /// <see href="https://github.com/AGomnes/Cantrip/blob/main/docs/stability.md">Stability</see>.
    /// </remarks>
    public interface IGameClock
    {
        /// <summary>
        /// The current time in this clock's own whole units: turns elapsed, or ticks elapsed. It starts
        /// at 0 and only ever goes up, so a duration is stored as the absolute time it ends at.
        /// </summary>
        long Now { get; }

        /// <summary>
        /// Raised once per unit after the clock has moved, carrying the new <see cref="Now"/>. A runtime
        /// subscribes from the moment it is built — that is what runs scheduled work, periodic triggers
        /// and timed statuses — so a game that shares one clock between two runtimes drives both.
        /// <see cref="Restore"/> deliberately does not raise it.
        /// </summary>
        event Action<long>? Advanced;

        /// <summary>
        /// Converts a content-authored duration such as <c>3 turns</c> or <c>1.5s</c> to clock units.
        /// Returns false when the unit makes no sense for this clock (seconds on a turn clock).
        /// </summary>
        /// <remarks>
        /// The default accepts a bare number as this clock's own unit and refuses every named one,
        /// which is true of any clock; a clock that knows what a second or a turn is says so itself.
        /// </remarks>
        bool TryConvert(Num amount, string? unit, out long units)
        {
            if (string.IsNullOrEmpty(unit))
            {
                units = Math.Max(0, amount.Ceiling().ToInt());
                return true;
            }

            units = 0;
            return false;
        }

        /// <summary>Restores time from a snapshot without raising <see cref="Advanced"/>.</summary>
        void Restore(long now);

        /// <summary>
        /// How many of this clock's units make one second of game time, or 0 for a clock whose unit
        /// is not a length of real time at all — which is every turn clock, and the default here.
        /// </summary>
        /// <remarks>
        /// It is what turns <c>3s</c> in content into a number of units, so it is saved with the
        /// game: <see cref="GameSnapshot.ClockUnitsPerSecond"/> records it and a restore refuses a
        /// save written at another rate, rather than silently reinterpreting every cooldown, every
        /// <c>for 3s</c> and every <c>on every 2s</c> in it. A clock that answers 0 both writes and
        /// accepts 0, so nothing changes for a turn-based game.
        /// </remarks>
        int UnitsPerSecond => 0;
    }

    /// <summary>One unit per turn. The default for turn-based games.</summary>
    public sealed class TurnClock : IGameClock
    {
        /// <summary>Turns elapsed since the game began. It is not the battle's turn number, which is <c>GameState.Turn</c> and restarts each battle.</summary>
        public long Now { get; private set; }

        /// <summary>Raised once per turn, after <see cref="Now"/> has moved. <see cref="Restore"/> does not raise it.</summary>
        public event Action<long>? Advanced;

        /// <summary>Moves one turn on and raises <see cref="Advanced"/>. The runtime calls it; a game that calls it itself moves time without ending a turn.</summary>
        public void AdvanceTurn()
        {
            Now++;
            Advanced?.Invoke(Now);
        }

        /// <summary>
        /// Accepts a bare number, <c>turn</c>, <c>turns</c> and <c>t</c>, rounding up; refuses seconds
        /// and milliseconds, which is what CT325 reports before the game ever runs. A negative length
        /// converts to 0 rather than failing.
        /// </summary>
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

        /// <summary>Sets the time from a snapshot without raising <see cref="Advanced"/>, so restoring a save does not re-run everything that was due.</summary>
        public void Restore(long now) => Now = now;
    }

    /// <summary>
    /// Fixed-timestep clock for real-time games. Advance it from the engine's physics step, never
    /// from the render frame, so that simulation results do not depend on frame rate.
    /// </summary>
    public sealed class TickClock : IGameClock
    {
        /// <summary>
        /// A tick clock at a fixed rate. The rate is what turns <c>3s</c> in content into a number of
        /// ticks, so it has to match the fixed timestep the game calls <c>CardRuntime.Tick(int)</c> from,
        /// or every duration in the content is wrong by that ratio.
        /// </summary>
        /// <param name="ticksPerSecond">
        /// Ticks in one second of game time. It cannot be changed afterwards, and it is written into
        /// a save as <see cref="GameSnapshot.ClockUnitsPerSecond"/>: a restore into a clock running
        /// at another rate is refused rather than reinterpreting every cooldown and every timed
        /// status in it.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">Zero or fewer ticks per second.</exception>
        public TickClock(int ticksPerSecond = 60)
        {
            if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
            TicksPerSecond = ticksPerSecond;
        }

        /// <summary>
        /// How many ticks a second is. A UI dividing <c>ready_at - Now</c> by this gets seconds, which
        /// is the one conversion the core cannot do for it.
        /// </summary>
        public int TicksPerSecond { get; }

        /// <summary>
        /// <see cref="TicksPerSecond"/>, under the name a save and a restore compare it by. See
        /// <see cref="IGameClock.UnitsPerSecond"/>.
        /// </summary>
        public int UnitsPerSecond => TicksPerSecond;

        /// <summary>Ticks elapsed since the game began. It does not reset between battles, so a cooldown across a battle boundary still expires when it should.</summary>
        public long Now { get; private set; }

        /// <summary>
        /// Raised once per tick, after <see cref="Now"/> has moved — so a <see cref="Tick"/> of four
        /// raises it four times, and nothing that was due in between is skipped.
        /// <see cref="Restore"/> does not raise it.
        /// </summary>
        public event Action<long>? Advanced;

        /// <summary>
        /// Moves time on, raising <see cref="Advanced"/> once per tick rather than once per call — so a
        /// game that catches up four ticks at once resolves each of them in order, and nothing that was
        /// due in between is skipped.
        /// </summary>
        public void Tick(int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                Now++;
                Advanced?.Invoke(Now);
            }
        }

        /// <summary>
        /// Accepts seconds, milliseconds, ticks and a bare number, always rounding up, so a length
        /// shorter than one tick becomes one tick rather than none. Refuses <c>turns</c>, which is what
        /// CT325 reports before the game ever runs.
        /// </summary>
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

        /// <summary>Sets the time from a snapshot without raising <see cref="Advanced"/>, so restoring a save does not replay every tick that had passed.</summary>
        public void Restore(long now) => Now = now;
    }
}
