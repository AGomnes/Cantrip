using System;
using System.Collections.Generic;
using Cantrip.Syntax;

namespace Cantrip.Runtime
{
    /// <summary>
    /// Something that happened, or is about to. Every verb raises one of these in each of the three
    /// phases, and any content can listen with <c>on &lt;event&gt;(filter)</c>.
    /// </summary>
    public sealed class GameEvent
    {
        /// <summary>
        /// An event a verb or a game is about to raise. Building one does nothing; the interpreter
        /// raises it. A game raising an event of its own names it something the engine does not use, or
        /// it is forging a built-in one, which is CT322.
        /// </summary>
        /// <param name="name">The event name as content listens for it, without a phase prefix.</param>
        public GameEvent(string name)
        {
            Name = name;
            Data = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
            Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The event name without a phase prefix: <c>damaged</c>, not <c>before_damaged</c>. The phase
        /// is <see cref="Phase"/>, and content writes it as the prefix.
        /// </summary>
        public string Name { get; }

        /// <summary>The phase currently being dispatched.</summary>
        public EventPhase Phase { get; internal set; }

        /// <summary>Who or what caused it: the attacker, the healer, the card's player.</summary>
        public Entity? Source { get; set; }

        /// <summary>Who or what it happened to.</summary>
        public Entity? Target { get; set; }

        /// <summary>
        /// The action involved: the card for card flow events and for damage dealt by a card, and
        /// whatever else an effect was running as. Content reads it as <c>event.card</c>, which keeps
        /// its word.
        /// </summary>
        /// <remarks>
        /// Named <c>Card</c> until 1.0, beside an <see cref="EvalContext"/> and a
        /// <see cref="ModifierQuery"/> whose same-named member already carried abilities. One word
        /// across the three is worth more than three spellings of it, and <c>Action</c> is the word an
        /// ability needs here.
        /// </remarks>
        public Entity? Action { get; set; }

        /// <summary>The quantity involved. Before-phase listeners may change it.</summary>
        public Num Amount { get; set; }

        /// <summary>Type tags, such as <c>fire</c> on fire damage.</summary>
        public HashSet<string> Tags { get; }

        /// <summary>Anything else a verb wants to expose as <c>event.&lt;name&gt;</c>.</summary>
        public Dictionary<string, Value> Data { get; }

        /// <summary>Set by <c>cancel</c> in a before listener. The action and its after phase are skipped.</summary>
        public bool Cancelled { get; set; }

        /// <summary>Set when an instead listener ran in place of the default action.</summary>
        public bool Replaced { get; internal set; }

        /// <summary>Trace entry for this event, so listeners can be recorded as its children.</summary>
        public long TraceId { get; internal set; }

        /// <summary>
        /// The registration order the event bus had reached when this event was raised. A listener
        /// whose <see cref="Listener.Order"/> is at least this came into play during the event, and
        /// is the one a <c>new_listeners: miss_the_event</c> ruleset leaves out.
        /// </summary>
        internal long ListenersAtRaise { get; set; } = long.MaxValue;

        /// <summary>The phase and the name, as in <c>before damaged</c>. For a log, not for matching.</summary>
        public override string ToString() => $"{Phase.ToString().ToLowerInvariant()} {Name}";
    }

    /// <summary>A registered <c>on ...:</c> block, bound to the entity that declared it.</summary>
    public sealed class Listener
    {
        internal Listener(int id, Entity owner, ListenerNode syntax, long order)
        {
            Id = id;
            Owner = owner;
            Syntax = syntax;
            Order = order;

            // `on owner.damaged` listens to `damaged` scoped to whatever `owner` resolves to.
            string name = syntax.EventName;
            int dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                Scope = name.Substring(0, dot).ToLowerInvariant();
                EventName = name.Substring(dot + 1).ToLowerInvariant();
            }
            else
            {
                EventName = name.ToLowerInvariant();
            }
        }

        /// <summary>A number unique within this game, stable while the listener is registered. It is not saved, so it does not survive a restore.</summary>
        public int Id { get; }

        /// <summary>
        /// The entity whose declaration this came from: the relic, the status, the card. Inside the
        /// listener's body, that is what <c>self</c> and a bare <c>source</c> mean;
        /// <c>event.source</c> is who caused the event, which is the other thing entirely.
        /// </summary>
        public Entity Owner { get; }

        /// <summary>The parsed <c>on ...:</c> block, for a tool that needs the filter, the body or the span.</summary>
        public ListenerNode Syntax { get; }

        /// <summary>Event name without phase prefix or scope, e.g. <c>damaged</c>.</summary>
        public string EventName { get; }

        /// <summary>The dotted prefix, e.g. <c>owner</c> in <c>owner.damaged</c>. Null when unscoped.</summary>
        public string? Scope { get; }

        /// <summary>Which of the three phases it listens in, from the prefix content wrote.</summary>
        public EventPhase Phase => Syntax.Phase;

        /// <summary>
        /// The <c>priority</c> written on the listener, 0 unless stated. Higher runs first, and only
        /// while the ruleset's ordering puts priority first, which the default does.
        /// </summary>
        public int Priority => Syntax.Priority;

        /// <summary>Registration order, the "play order" tie-break.</summary>
        public long Order { get; }

        // Limit bookkeeping for `once per turn` and friends.
        internal long LimitWindow { get; set; } = long.MinValue;

        /// <summary>
        /// Interval of an <c>on every ...:</c> listener, in clock units. Zero for every other
        /// listener, which is what makes a periodic one recognisable at all.
        /// </summary>
        public long IntervalUnits { get; internal set; }

        /// <summary>
        /// Clock time this listener is next due to fire. Saved with the game, because when the next
        /// tick lands is part of the state a replay has to agree about.
        /// </summary>
        public long NextDueAt { get; internal set; }

        /// <summary>The owner and the event, for a log or an inspector.</summary>
        public override string ToString() => $"{Owner.Name}: on {Syntax.EventName}";
    }

    /// <summary>
    /// Holds every active listener and answers "who hears this event, in what order". It does not
    /// execute anything; the interpreter does, which keeps dispatch policy in one place.
    /// </summary>
    public sealed class EventBus
    {
        private readonly Dictionary<string, List<Listener>> _byEvent =
            new Dictionary<string, List<Listener>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<int, List<Listener>> _byOwner = new Dictionary<int, List<Listener>>();

        /// <summary>
        /// Periodic listeners, in registration order. Held separately because they are pumped by the
        /// clock rather than found by an event name: only the one whose time has come may run.
        /// </summary>
        private readonly List<Listener> _periodic = new List<Listener>();

        private int _nextId = 1;
        private long _nextOrder = 1;

        /// <summary>
        /// How many listeners are registered. It counts every phase and every owner, so it moves as
        /// statuses come and go rather than being a property of the content.
        /// </summary>
        public int Count { get; private set; }

        /// <summary>
        /// The order the next listener registered will be given. An event stamps this as it is
        /// raised, so anything that arrives afterwards can be told apart from what was already here.
        /// </summary>
        internal long NextOrder => _nextOrder;

        internal Listener Register(Entity owner, ListenerNode syntax, long intervalUnits = 0)
        {
            var listener = new Listener(_nextId++, owner, syntax, _nextOrder++);

            if (intervalUnits > 0)
            {
                listener.IntervalUnits = intervalUnits;
                _periodic.Add(listener);
            }

            if (!_byEvent.TryGetValue(listener.EventName, out List<Listener>? list))
            {
                list = new List<Listener>();
                _byEvent[listener.EventName] = list;
            }
            list.Add(listener);

            if (!_byOwner.TryGetValue(owner.Id, out List<Listener>? owned))
            {
                owned = new List<Listener>();
                _byOwner[owner.Id] = owned;
            }
            owned.Add(listener);

            Count++;
            return listener;
        }

        internal void UnregisterAll(Entity owner)
        {
            if (!_byOwner.TryGetValue(owner.Id, out List<Listener>? owned)) return;

            foreach (Listener listener in owned)
            {
                if (_byEvent.TryGetValue(listener.EventName, out List<Listener>? list))
                {
                    list.Remove(listener);
                    Count--;
                }

                if (listener.IntervalUnits > 0) _periodic.Remove(listener);
            }

            _byOwner.Remove(owner.Id);
        }

        /// <summary>Every periodic listener, for the clock to pump. Empty in a game with none.</summary>
        public IReadOnlyList<Listener> Periodic => _periodic;

        /// <summary>
        /// Every listener one entity declared, in registration order. With
        /// <c>ModifierPipeline.OwnedBy(Entity)</c> it is what an inspector needs to say what a single
        /// thing is doing to a game. The list is live: it changes as the entity gains or loses listeners.
        /// </summary>
        public IReadOnlyList<Listener> OwnedBy(Entity owner) =>
            _byOwner.TryGetValue(owner.Id, out List<Listener>? owned) ? owned : (IReadOnlyList<Listener>)Array.Empty<Listener>();

        /// <summary>
        /// Cheap pre-check so verbs can skip building events nobody listens to.
        /// </summary>
        public bool HasListeners(string eventName, EventPhase phase)
        {
            if (!_byEvent.TryGetValue(eventName, out List<Listener>? list)) return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Phase == phase) return true;
            }
            return false;
        }

        /// <summary>Listeners for an event and phase, in the ruleset's deterministic order.</summary>
        public List<Listener> Candidates(string eventName, EventPhase phase, Ruleset rules, Team activeTeam)
        {
            var result = new List<Listener>();
            if (!_byEvent.TryGetValue(eventName, out List<Listener>? list)) return result;

            for (int i = 0; i < list.Count; i++)
            {
                Listener listener = list[i];
                if (listener.Phase == phase && !listener.Owner.IsRemoved) result.Add(listener);
            }

            if (result.Count > 1)
            {
                // A stable sort with an explicit final tie-break on id: the order never depends on
                // dictionary or hash ordering, so a replay or a restored save always agrees.
                result.Sort((a, b) => Compare(a, b, rules.Ordering, activeTeam));
            }

            return result;
        }

        private static int Compare(Listener a, Listener b, IReadOnlyList<ListenerOrdering> ordering, Team activeTeam)
        {
            foreach (ListenerOrdering rule in ordering)
            {
                int cmp;
                switch (rule)
                {
                    case ListenerOrdering.Priority:
                        cmp = b.Priority.CompareTo(a.Priority); // higher priority first
                        break;
                    case ListenerOrdering.PlayOrder:
                        cmp = a.Owner.Sequence.CompareTo(b.Owner.Sequence);
                        if (cmp == 0) cmp = a.Order.CompareTo(b.Order);
                        break;
                    case ListenerOrdering.ActivePlayer:
                        bool aActive = a.Owner.Team == activeTeam;
                        bool bActive = b.Owner.Team == activeTeam;
                        cmp = bActive.CompareTo(aActive); // active side first
                        break;
                    default:
                        cmp = 0;
                        break;
                }
                if (cmp != 0) return cmp;
            }
            return a.Id.CompareTo(b.Id);
        }
    }
}
