#nullable enable
using System;
using System.Collections.Generic;
using Cantrip.Runtime;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// Paces resolved events for presentation: one <c>Present</c> at a time, and the next only when
    /// the game says the last one has finished.
    /// </summary>
    /// <remarks>
    /// The rules have already run by the time anything reaches here, so this node decides nothing
    /// about the game; it decides when the player is shown what has happened. Each record carries
    /// its own stat snapshot, so a card that hits twice animates two different hp numbers even
    /// though the entity has long since settled on the second.
    /// <para>
    /// <see cref="IsBusy"/> is what input should be gated on. The game state is final either way, so
    /// a player who does not want to watch can <see cref="SkipAll"/>: the queue is dropped and
    /// nothing further is presented for it.
    /// </para>
    /// </remarks>
    [GlobalClass]
    public partial class BattlePresenter : Node
    {
        /// <summary>One resolved event, in the dictionary shape the addon uses across the boundary.</summary>
        [Signal]
        public delegate void PresentEventHandler(Godot.Collections.Dictionary effect_event);

        /// <summary>Everything queued has been presented; the game is idle again.</summary>
        [Signal]
        public delegate void SettledEventHandler();

        private readonly Queue<EventRecord> _queue = new Queue<EventRecord>();
        private readonly Pacing _pacing;
        private EventRecord? _current;
        private bool _pumping;
        private bool _active;

        /// <summary>
        /// Godot builds this; a scene adds the node and the runtime node is pointed at it. Nothing is
        /// queued until the runtime hands it events.
        /// </summary>
        public BattlePresenter() => _pacing = new Pacing(this);

        /// <summary>
        /// Turns a record into the dictionary <c>Present</c> carries. The runtime node sets this so
        /// the whole addon shares one mapping; left unset, <see cref="DefaultFormat"/> is used.
        /// </summary>
        public Func<EventRecord, Godot.Collections.Dictionary>? Formatter { get; set; }

        /// <summary>True while an event is being presented and its <c>Done</c> has not arrived.</summary>
        public bool IsBusy() => _current != null;

        /// <summary>True when nothing is being presented and nothing is waiting.</summary>
        public bool IsSettled() => _current == null && _queue.Count == 0;

        /// <summary>Events waiting behind the one being presented.</summary>
        public int PendingCount() => _queue.Count;

        /// <summary>The sequence number of the event being presented, or 0 when idle.</summary>
        public long CurrentSequence() => _current?.Sequence ?? 0;

        /// <summary>
        /// Takes everything the host has recorded and starts presenting it. Records are collected
        /// before the first signal goes out, so no handler runs while the buffer is mid-drain.
        /// </summary>
        public int Drain(EventBuffer buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            int taken = 0;
            buffer.Drain(record =>
            {
                _queue.Enqueue(record);
                _active = true;
                taken++;
            });

            _pacing.Pump();
            return taken;
        }

        /// <summary>Queues one record, for a game that drains the buffer itself.</summary>
        public void Enqueue(EventRecord record)
        {
            if (record == null) return;

            _queue.Enqueue(record);
            _active = true;
            _pacing.Pump();
        }

        /// <summary>
        /// Called by the game when it has finished showing the current event. Calling it when
        /// nothing is being presented is a mistake worth hearing about: it would otherwise eat the
        /// next event's animation.
        /// </summary>
        public void Done()
        {
            if (_current == null)
            {
                GD.PushError("BattlePresenter.Done() was called while nothing is being presented. Call it once per Present.");
                return;
            }

            _current = null;
            _pacing.Pump();
        }

        /// <summary>
        /// Drops everything still to present and settles at once. The rules already resolved, so
        /// skipping costs the player nothing but the show.
        /// </summary>
        public void SkipAll()
        {
            _queue.Clear();
            _current = null;

            if (!_active) return;
            _active = false;
            EmitSignal(SignalName.Settled);
        }

        /// <summary>The dictionary shape of an event, per the addon's contract.</summary>
        /// <remarks>
        /// One line, because it used to be all thirteen keys written out a second time. The runtime
        /// node always installs <see cref="VariantMap.Event"/> as the <see cref="Formatter"/>, so
        /// the copy only ran for a presenter used on its own: exactly where a key that had drifted
        /// apart from the real one would go unnoticed.
        /// </remarks>
        public static Godot.Collections.Dictionary DefaultFormat(EventRecord record) => VariantMap.Event(record);

        /// <summary>
        /// The presenter's own loop, off the node.
        /// </summary>
        /// <remarks>
        /// Godot's source generator publishes every ordinary method of a <c>[GlobalClass]</c> to
        /// script whatever its C# accessibility says, so as a member of the presenter this was
        /// callable from GDScript and, at 1.0, promised. A script calling it could emit a second
        /// <c>Settled</c> for one batch, or start the next event over the top of the one playing.
        /// </remarks>
        private sealed class Pacing
        {
            private readonly BattlePresenter _presenter;

            public Pacing(BattlePresenter presenter) => _presenter = presenter;

            /// <summary>
            /// Presents until something is in flight or the queue runs dry. The loop is iterative
            /// on purpose: a game with no animation calls <c>Done</c> straight out of the handler,
            /// and recursing there would put the whole battle on the stack.
            /// </summary>
            public void Pump()
            {
                BattlePresenter presenter = _presenter;
                if (presenter._pumping) return;

                presenter._pumping = true;
                try
                {
                    while (presenter._current == null && presenter._queue.Count > 0)
                    {
                        presenter._current = presenter._queue.Dequeue();
                        presenter.EmitSignal(SignalName.Present, presenter.Format(presenter._current));
                    }
                }
                finally
                {
                    presenter._pumping = false;
                }

                if (!presenter._active || presenter._current != null || presenter._queue.Count > 0) return;
                presenter._active = false;
                presenter.EmitSignal(SignalName.Settled);
            }
        }

        private Godot.Collections.Dictionary Format(EventRecord record)
        {
            Func<EventRecord, Godot.Collections.Dictionary>? formatter = Formatter;
            return formatter != null ? formatter(record) : DefaultFormat(record);
        }
    }
}
