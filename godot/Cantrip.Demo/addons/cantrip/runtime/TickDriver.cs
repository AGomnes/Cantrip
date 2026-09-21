#nullable enable
using System;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// Advances a real-time game from the engine's fixed step, in whole ticks.
    /// </summary>
    /// <remarks>
    /// Only <c>_PhysicsProcess</c> drives the simulation, never <c>_Process</c>: the rules engine
    /// promises that the same inputs produce the same game, and a render frame's length is not an
    /// input, it is whatever the machine managed this time. The conversion from frames to ticks is
    /// an exact integer ratio, so a clock rate that does not divide the physics rate still comes out
    /// right over a second.
    /// </remarks>
    [GlobalClass]
    public partial class TickDriver : Node
    {
        private readonly TickAccumulator _accumulator = new TickAccumulator();

        /// <summary>Whole ticks were run this frame.</summary>
        [Signal]
        public delegate void TickedEventHandler(int count);

        /// <summary>The clock rate the content was written against. Must match the runtime's TickClock.</summary>
        [Export]
        public int TicksPerSecond { get; set; } = 60;

        /// <summary>
        /// The most ticks one frame may run after a stall. The rest are abandoned: a game that tried
        /// to repay them would spend every later frame further behind.
        /// </summary>
        [Export]
        public int MaxCatchUp { get; set; } = TickAccumulator.DefaultMaxCatchUp;

        /// <summary>Set false to pause the simulation without taking the node out of the tree.</summary>
        [Export]
        public bool Running { get; set; } = true;

        /// <summary>
        /// What one or more ticks means: the runtime node sets this to advance the game and then
        /// drain its events to the presenter, in that order.
        /// </summary>
        public Action<int>? Drive { get; set; }

        /// <summary>The frames-to-ticks conversion, exposed for tests and for a game that saves it.</summary>
        public TickAccumulator Accumulator => _accumulator;

        public override void _Ready()
        {
            // Said out loud, because driving the rules from the render frame is the mistake this
            // node exists to prevent.
            SetProcess(false);
            SetPhysicsProcess(true);
        }

        public override void _PhysicsProcess(double delta)
        {
            if (!Running || Drive == null) return;

            // The physics rate can be changed at runtime; the accumulator ignores a repeat.
            _accumulator.Configure(TicksPerSecond, Engine.PhysicsTicksPerSecond);
            _accumulator.MaxCatchUp = MaxCatchUp;

            int ticks = _accumulator.Advance();
            if (ticks <= 0) return;

            try
            {
                Drive(ticks);
            }
            catch (Exception error)
            {
                // Without this the same failure repeats sixty times a second and buries the cause.
                Running = false;
                GD.PushError("Cantrip: ticking failed, so the driver has stopped. " + error.Message);
                return;
            }

            EmitSignal(SignalName.Ticked, ticks);
        }

        /// <summary>Clears the carried remainder and the counters, as after loading a save.</summary>
        public void Reset() => _accumulator.Reset();

        /// <summary>Ticks run since the last reset, for a HUD or a desync check.</summary>
        public long TotalTicks() => _accumulator.TotalTicks;

        /// <summary>Ticks abandoned to the catch-up cap: time the game skipped rather than replayed.</summary>
        public long DroppedTicks() => _accumulator.Dropped;
    }
}
