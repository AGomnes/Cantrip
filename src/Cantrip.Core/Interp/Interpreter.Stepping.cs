using System;
using System.Collections.Generic;
using System.Globalization;
using Cantrip.Diagnostics;

namespace Cantrip.Runtime
{
    /// <summary>
    /// One piece of queued work, described before it is allowed to run. This is what a debugger
    /// shows when it says what the next step will be.
    /// </summary>
    public readonly struct PendingTrigger
    {
        internal PendingTrigger(string description, string eventName, int listenerId, SourceSpan span)
        {
            Description = description ?? string.Empty;
            EventName = eventName ?? string.Empty;
            ListenerId = listenerId;
            Span = span;
        }

        /// <summary>How it reads to a person: <c>on damaged</c>, or what the engine is about to do.</summary>
        public string Description { get; }

        /// <summary>The event that queued it. Empty for the engine's own follow-up work.</summary>
        public string EventName { get; }

        /// <summary>The listener about to run, or 0 when this is not a listener at all.</summary>
        public int ListenerId { get; }

        /// <summary>The line of content behind it, when it has one.</summary>
        public SourceSpan Span { get; }

        public override string ToString() => Description;
    }

    /// <summary>
    /// Where a game should stop before resolving a queued trigger.
    /// </summary>
    /// <remarks>
    /// Breakpoints are tooling, not rules. They are never saved, never hashed and never consulted
    /// unless something has actually set one, so a game that has none behaves exactly as it did
    /// before they existed.
    /// </remarks>
    public sealed class TriggerBreakpoints
    {
        private readonly HashSet<string> _lines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _events = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int Count => _lines.Count + _events.Count;

        /// <summary>Whether anything is set at all, checked before a drain does any extra work.</summary>
        public bool Any => Count > 0;

        /// <summary>Stops before any trigger written on this line.</summary>
        public void Add(string file, int line)
        {
            if (!string.IsNullOrEmpty(file)) _lines.Add(Key(file, line));
        }

        public bool Remove(string file, int line) => !string.IsNullOrEmpty(file) && _lines.Remove(Key(file, line));

        /// <summary>
        /// Stops before anything queued by this event, wherever it was written. This is the one a
        /// designer reaches for: "stop whenever something reacts to damage".
        /// </summary>
        public void AddEvent(string eventName)
        {
            if (!string.IsNullOrWhiteSpace(eventName)) _events.Add(eventName.Trim());
        }

        public bool RemoveEvent(string eventName) =>
            !string.IsNullOrWhiteSpace(eventName) && _events.Remove(eventName.Trim());

        public void Clear()
        {
            _lines.Clear();
            _events.Clear();
        }

        internal bool Matches(in PendingTrigger trigger)
        {
            if (_events.Count > 0 && trigger.EventName.Length > 0 && _events.Contains(trigger.EventName)) return true;
            if (_lines.Count == 0 || string.IsNullOrEmpty(trigger.Span.File)) return false;

            return _lines.Contains(Key(trigger.Span.File!, trigger.Span.Line));
        }

        private static string Key(string file, int line) => file + ":" + line.ToString(CultureInfo.InvariantCulture);
    }

    // Stepping: resolving queued work one trigger at a time, so a debugger can walk a game.
    public sealed partial class Interpreter
    {
        private bool _paused;

        /// <summary>
        /// Where the game stops before resolving a trigger. Empty unless a debugger has asked for
        /// one, and checked only then.
        /// </summary>
        public TriggerBreakpoints Breakpoints { get; } = new TriggerBreakpoints();

        /// <summary>
        /// True while the queue is being held. <see cref="Drain"/> does nothing at all in this
        /// state, so the only way queued work resolves is one <see cref="TryDrainStep"/> at a time.
        /// </summary>
        public bool Paused => _paused;

        /// <summary>The work at the head of the queue, or null when nothing is waiting.</summary>
        public PendingTrigger? Next => _queue.Count == 0 ? (PendingTrigger?)null : _queue.Peek().About;

        /// <summary>
        /// Holds the queue from the next drain onwards.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Pausing does not interrupt anything: whatever is running finishes, and what it queued
        /// waits. That is the only granularity the interpreter can offer honestly — a queue entry is
        /// a self-contained closure, while a before or instead listener runs inline on the raising
        /// thread's stack, nested arbitrarily deep, and cannot be suspended without turning the
        /// whole interpreter into a state machine.
        /// </para>
        /// <para>
        /// A paused game is deliberately not a saveable one: <see cref="HasPendingWork"/> stays
        /// true, so capture and restore keep refusing, which is right — the queue holds live
        /// closures over listeners, events and chains that no snapshot can represent.
        /// </para>
        /// </remarks>
        public void Pause() => _paused = true;

        /// <summary>Lets the queue resolve normally again. The next drain runs what is left.</summary>
        public void Resume() => _paused = false;

        /// <summary>
        /// Resolves exactly one queued trigger, whether or not the game is paused, and returns
        /// whether one actually ran. What is left afterwards is <see cref="PendingTriggers"/>, and
        /// what would run next is <see cref="Next"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One step is one queue entry. A step may queue more work of its own, which joins the back
        /// of the queue exactly as it would during a full drain, so stepping through a game and
        /// letting it run reach the same state in the same order.
        /// </para>
        /// <para>
        /// Each step resets the sandbox step budget, because a paused drain is spread across host
        /// frames and cannot share a budget with the action that queued it. A stepped game is
        /// therefore not protected from an unbounded loop by the budget — but every step is a
        /// deliberate request from a debugger, so there is nothing running away.
        /// </para>
        /// <para>
        /// A trigger that throws drops the rest of the queue, exactly as it does in
        /// <see cref="Drain"/>: later triggers would run against half-resolved state.
        /// </para>
        /// </remarks>
        public bool TryDrainStep()
        {
            if (_draining || _queue.Count == 0) return false;

            _draining = true;
            ResetSteps();
            try
            {
                _queue.Dequeue().Work();
            }
            catch
            {
                _queue.Clear();
                throw;
            }
            finally
            {
                _draining = false;
            }

            return true;
        }

        /// <summary>
        /// One entry in the work queue: the work itself, and enough about it to say what is about to
        /// happen before it happens. Without the description a step is blind, and a debugger that
        /// cannot name the next trigger is not much use.
        /// </summary>
        private readonly struct QueuedWork
        {
            internal QueuedWork(Action work, PendingTrigger about)
            {
                Work = work;
                About = about;
            }

            internal Action Work { get; }

            internal PendingTrigger About { get; }
        }
    }
}
