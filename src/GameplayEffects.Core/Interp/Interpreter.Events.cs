using System;
using System.Collections.Generic;
using System.Linq;
using GameplayEffects.Syntax;

namespace GameplayEffects.Runtime
{
    // Event dispatch, loop protection, limits and the trigger queue.
    public sealed partial class Interpreter
    {
        /// <summary>Lifecycle events that a listener on an attached entity only hears for its own controller.</summary>
        private static readonly HashSet<string> PersonalEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "turn_start", "turn_end",
        };

        private readonly Queue<PendingTrigger> _queue = new Queue<PendingTrigger>();
        private bool _draining;
        private long _nextChainRoot = 1;

        private sealed class PendingTrigger
        {
            public PendingTrigger(Listener listener, GameEvent gameEvent, Chain chain)
            {
                Listener = listener;
                Event = gameEvent;
                Chain = chain;
            }

            public Listener Listener { get; }
            public GameEvent Event { get; }
            public Chain Chain { get; }
        }

        /// <summary>After-phase triggers waiting to resolve.</summary>
        public int PendingTriggers => _queue.Count;

        internal Chain NewChain() => Chain.NewRoot(_nextChainRoot++);

        /// <summary>
        /// Raises an event through its three phases around <paramref name="action"/>. Before
        /// listeners run inline and may cancel or change <see cref="GameEvent.Amount"/>; if any
        /// instead listener fires, the action is skipped; after listeners are queued. Returns
        /// true when the default action actually ran.
        /// </summary>
        /// <param name="committed">
        /// Runs once the before phase has passed without cancelling, ahead of the instead phase.
        /// Card play uses it to pay the cost: a replaced effect still costs energy, a cancelled
        /// play does not.
        /// </param>
        public bool Raise(GameEvent gameEvent, EvalContext context, Action? action = null, Action? committed = null)
        {
            gameEvent.TraceId = State.Trace.Record(
                State.Clock.Now,
                "event",
                gameEvent.Name,
                gameEvent.Source?.ToString(),
                values: State.Trace.Enabled ? DescribeEvent(gameEvent) : null);

            using (State.Trace.Scope(gameEvent.TraceId))
            {
                if (Rules.BeforeEvents)
                {
                    Dispatch(gameEvent, EventPhase.Before, context);
                    if (gameEvent.Cancelled)
                    {
                        State.Trace.Record(State.Clock.Now, "cancelled", gameEvent.Name);
                        return false;
                    }
                }

                committed?.Invoke();

                bool replaced = Rules.InsteadEvents && Dispatch(gameEvent, EventPhase.Instead, context) > 0;
                if (replaced) gameEvent.Replaced = true;
                else action?.Invoke();

                if (gameEvent.Cancelled) return false;

                if (Rules.AfterEvents) Dispatch(gameEvent, EventPhase.After, context);
                ProcessDeadlines(gameEvent);
                Host.OnEvent(gameEvent);
                return !replaced;
            }
        }

        private static Dictionary<string, object> DescribeEvent(GameEvent gameEvent)
        {
            var values = new Dictionary<string, object>();
            if (gameEvent.Source != null) values["source"] = gameEvent.Source.ToString();
            if (gameEvent.Target != null) values["target"] = gameEvent.Target.ToString();
            if (gameEvent.Card != null) values["card"] = gameEvent.Card.ToString();
            if (!gameEvent.Amount.IsZero) values["amount"] = gameEvent.Amount.ToString();
            if (gameEvent.Tags.Count > 0) values["tags"] = string.Join(",", gameEvent.Tags.OrderBy(t => t, StringComparer.Ordinal));
            return values;
        }

        /// <summary>Runs or queues every matching listener for one phase. Returns how many fired.</summary>
        private int Dispatch(GameEvent gameEvent, EventPhase phase, EvalContext context)
        {
            if (!State.Events.HasListeners(gameEvent.Name, phase)) return 0;

            gameEvent.Phase = phase;
            int fired = 0;

            foreach (Listener listener in State.Events.Candidates(gameEvent.Name, phase, Rules, State.ActiveTeam))
            {
                // An earlier listener in this same dispatch may have removed or moved this one's owner.
                if (listener.Owner.IsRemoved || !State.IsActive(listener.Owner)) continue;
                if (!Matches(listener, gameEvent, context.Chain)) continue;

                if (Rules.Loops == LoopProtection.OncePerChain && context.Chain.Contains(listener.Id))
                {
                    State.Trace.Record(State.Clock.Now, "loop", $"skipped {listener}: already in this chain", span: listener.Syntax.Span);
                    continue;
                }

                if (context.Chain.Depth >= Rules.MaxDepth)
                {
                    State.Trace.Record(State.Clock.Now, "warning", $"skipped {listener}: chain depth {Rules.MaxDepth} reached", span: listener.Syntax.Span);
                    continue;
                }

                if (!ConsumeLimit(listener, context.Chain)) continue;

                fired++;
                Chain chain = context.Chain.Extend(listener.Id);

                if (phase == EventPhase.After && Rules.Triggers == TriggerResolution.Queued)
                    _queue.Enqueue(new PendingTrigger(listener, gameEvent, chain));
                else
                    RunListener(listener, gameEvent, chain);
            }

            return fired;
        }

        private bool Matches(Listener listener, GameEvent gameEvent, Chain chain)
        {
            if (listener.Scope != null)
            {
                if (listener.Scope != "any")
                {
                    Entity? scoped = ResolveScope(listener);
                    if (scoped == null || gameEvent.Target != scoped) return false;
                }
            }
            else if (PersonalEvents.Contains(gameEvent.Name) && listener.Owner.Kind != EntityKind.Global && gameEvent.Target != null)
            {
                // "At the end of your turn": a status or relic hears only its own controller's turn.
                if (gameEvent.Target != listener.Owner.Controller) return false;
            }

            ExprNode? filter = listener.Syntax.Filter;
            if (filter == null) return true;

            EvalContext context = ListenerContext(listener, gameEvent, chain);
            context.It = gameEvent.Target;
            Value value = Evaluate(filter, context);

            switch (value.Kind)
            {
                // `on card_played(self)` or `on died(enemies)`: the filter names who must be involved.
                case ValueKind.Entity:
                case ValueKind.List:
                {
                    IReadOnlyList<Entity> wanted = value.AsEntities();
                    return Involved(gameEvent).Any(wanted.Contains);
                }

                // `on status_applied(Poison)`: something from that definition must be involved.
                case ValueKind.Definition:
                    return Involved(gameEvent).Any(e => e.Definition == value.Definition);

                default:
                    return IsTrue(value, context);
            }
        }

        private static IEnumerable<Entity> Involved(GameEvent gameEvent)
        {
            if (gameEvent.Target != null) yield return gameEvent.Target;
            if (gameEvent.Source != null) yield return gameEvent.Source;
            if (gameEvent.Card != null) yield return gameEvent.Card;
            foreach (Value data in gameEvent.Data.Values)
            {
                if (data.Kind == ValueKind.Entity) yield return data.Entity!;
            }
        }

        /// <summary>Resolves the <c>owner</c> in <c>on owner.damaged</c>, relative to the listening entity.</summary>
        private Entity? ResolveScope(Listener listener)
        {
            Entity owner = listener.Owner;
            switch (listener.Scope)
            {
                case "self": return owner;
                case "owner": return owner.Owner ?? owner;
                case "controller": return owner.Controller;
                case "player": return State.Player;
                default:
                {
                    Value value = ResolveName(listener.Scope!, listener.Syntax.Span, new EvalContext(owner) { Source = owner });
                    return value.Entity;
                }
            }
        }

        private bool ConsumeLimit(Listener listener, Chain chain)
        {
            long window;
            switch (listener.Syntax.Limit)
            {
                case LimitScope.None: return true;
                case LimitScope.Turn: window = State.Turn; break;
                case LimitScope.Battle: window = State.BattleNumber; break;
                case LimitScope.Run: window = 0; break;
                case LimitScope.Chain: window = chain.RootId; break;
                default: return true;
            }

            if (listener.LimitWindow == window) return false;
            listener.LimitWindow = window;
            return true;
        }

        private static EvalContext ListenerContext(Listener listener, GameEvent gameEvent, Chain chain) =>
            new EvalContext(listener.Owner)
            {
                // The listening entity is the source of whatever it does. Its controller is found
                // through ownership, so "damage you deal" modifiers see relic damage as yours but
                // a status's damage belongs to the actor it sits on.
                Source = listener.Owner,
                Target = gameEvent.Target,
                // Deliberately not the event's card: a trigger is not part of the card that caused
                // it, so fire damage from a card must not make a status's retaliation "fire" too.
                // The card is still reachable as `event.card`.
                Event = gameEvent,
                Chain = chain,
            };

        private void RunListener(Listener listener, GameEvent gameEvent, Chain chain)
        {
            // Removing a relic during its own trigger is legal; its queued work quietly stops.
            if (listener.Owner.IsRemoved) return;

            long traceId = State.Trace.Record(
                State.Clock.Now,
                "listener",
                listener.ToString(),
                listener.Owner.ToString(),
                listener.ToString(),
                listener.Syntax.Span,
                parentOverride: gameEvent.TraceId == 0 ? (long?)null : gameEvent.TraceId);

            using (State.Trace.Scope(traceId))
                Execute(listener.Syntax.Body, ListenerContext(listener, gameEvent, chain));
        }

        /// <summary>
        /// Resolves queued triggers until none remain. Triggers raised while draining join the back
        /// of the queue, so resolution is breadth-first and fully deterministic.
        /// </summary>
        public void Drain()
        {
            if (_draining) return;
            _draining = true;
            try
            {
                while (_queue.Count > 0)
                {
                    PendingTrigger trigger = _queue.Dequeue();
                    RunListener(trigger.Listener, trigger.Event, trigger.Chain);
                }
            }
            catch
            {
                // A failed trigger leaves later ones meaningless; drop them rather than running
                // them against half-resolved state.
                _queue.Clear();
                throw;
            }
            finally
            {
                _draining = false;
            }
        }
    }
}
