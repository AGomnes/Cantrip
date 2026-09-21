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
        public delegate void PresentEventHandler(Godot.Collections.Dictionary evt);

        /// <summary>Everything queued has been presented; the game is idle again.</summary>
        [Signal]
        public delegate void SettledEventHandler();

        private readonly Queue<EventRecord> _queue = new Queue<EventRecord>();
        private EventRecord? _current;
        private bool _pumping;
        private bool _active;

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

            Pump();
            return taken;
        }

        /// <summary>Queues one record, for a game that drains the buffer itself.</summary>
        public void Enqueue(EventRecord record)
        {
            if (record == null) return;

            _queue.Enqueue(record);
            _active = true;
            Pump();
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
            Pump();
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
        public static Godot.Collections.Dictionary DefaultFormat(EventRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            var map = new Godot.Collections.Dictionary();
            map["seq"] = record.Sequence;
            map["name"] = record.Name;
            map["phase"] = record.PhaseName;
            map["time"] = record.Time;
            map["source"] = record.Source;
            map["target"] = record.Target;
            map["card"] = record.Card;
            map["amount"] = record.AmountInt;
            map["amount_raw"] = record.AmountRaw;
            map["replaced"] = record.Replaced;

            var tags = new Godot.Collections.Array();
            foreach (string tag in record.Tags) tags.Add(tag);
            map["tags"] = tags;

            var values = new Godot.Collections.Dictionary();
            foreach (KeyValuePair<string, Value> value in record.Values)
            {
                values[value.Key] = GodotEffectHost.DefaultMarshal.ToVariant(value.Value);
            }
            map["values"] = values;

            var after = new Godot.Collections.Dictionary();
            foreach (KeyValuePair<int, IReadOnlyDictionary<string, int>> entity in record.After)
            {
                var stats = new Godot.Collections.Dictionary();
                foreach (KeyValuePair<string, int> stat in entity.Value) stats[stat.Key] = stat.Value;
                after[entity.Key] = stats;
            }
            map["after"] = after;

            return map;
        }

        /// <summary>
        /// Presents until something is in flight or the queue runs dry. The loop is iterative on
        /// purpose: a game with no animation calls <c>Done</c> straight out of the handler, and
        /// recursing there would put the whole battle on the stack.
        /// </summary>
        private void Pump()
        {
            if (_pumping) return;

            _pumping = true;
            try
            {
                while (_current == null && _queue.Count > 0)
                {
                    _current = _queue.Dequeue();
                    EmitSignal(SignalName.Present, Format(_current));
                }
            }
            finally
            {
                _pumping = false;
            }

            if (!_active || _current != null || _queue.Count > 0) return;
            _active = false;
            EmitSignal(SignalName.Settled);
        }

        private Godot.Collections.Dictionary Format(EventRecord record)
        {
            Func<EventRecord, Godot.Collections.Dictionary>? formatter = Formatter;
            return formatter != null ? formatter(record) : DefaultFormat(record);
        }
    }
}
