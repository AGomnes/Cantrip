using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Syntax;

namespace Cantrip.Runtime
{
    // The state-changing primitives every verb is built from. Each one raises its event through
    // all three phases, so any rule can be listened to, altered or replaced from content.
    public sealed partial class Interpreter
    {
        /// <summary>Raised by the <c>log</c> verb, for debugging content.</summary>
        public event Action<string>? Logged;

        internal void Log(string message) => Logged?.Invoke(message);

        private bool HasAnyListeners(string eventName) =>
            State.Events.HasListeners(eventName, EventPhase.Before)
            || State.Events.HasListeners(eventName, EventPhase.Instead)
            || State.Events.HasListeners(eventName, EventPhase.After);

        /// <summary>
        /// Context for work the engine does on its own behalf: decay, resets, expiry. Each gets a
        /// causal chain of its own, so <c>once per chain</c> treats separate turns as separate chains.
        /// </summary>
        private EvalContext SystemContext(Entity? self) => new EvalContext(self) { Source = self, Chain = NewChain() };

        /// <summary>
        /// Two definitions are the same content when their kind and name match. Comparing by
        /// reference would treat a hot-reloaded status as a different one from its live instances.
        /// </summary>
        internal static bool SameDefinition(EntityDefinition? a, EntityDefinition? b) =>
            a != null && b != null
            && (ReferenceEquals(a, b)
                || (string.Equals(a.KindName, b.KindName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)));

        // Stats and resources ---------------------------------------------------------------

        /// <summary>
        /// The core <c>change</c> verb. Applies resource bounds, raises <c>&lt;stat&gt;_changed</c>,
        /// removes statuses whose counter runs out and kills actors whose hp reaches zero.
        /// Returns the change actually applied.
        /// </summary>
        /// <param name="entity">The entity whose stat changes.</param>
        /// <param name="stat">The stat, or a status counter such as <c>stacks</c>.</param>
        /// <param name="op">How <paramref name="amount"/> combines with the current value.</param>
        /// <param name="amount">The operand, before resource bounds.</param>
        /// <param name="context">Who is acting, for the <c>&lt;stat&gt;_changed</c> event.</param>
        /// <param name="span">Where in content the change was written, for traces and errors.</param>
        /// <param name="fromReset">
        /// True when a <c>reset_on</c> rule is doing this, which puts <c>event.reset</c> on the
        /// <c>&lt;stat&gt;_changed</c> event. Content that wants to stop a reset can then say so
        /// exactly, instead of inferring it from the value — "block became 0" is also true of an
        /// effect that legitimately sets block to 0.
        /// </param>
        public Num ChangeStat(Entity entity, string stat, AssignOperator op, Num amount, EvalContext context, SourceSpan span = default, bool fromReset = false)
        {
            if (entity.IsRemoved) return Num.Zero;

            stat = stat.ToLowerInvariant();
            Num current = entity.GetBase(stat);
            Num desired = Clamp(entity, stat, Combine(current, op, amount));
            if (desired == current && entity.HasStat(stat)) return Num.Zero;

            Num applied = Num.Zero;
            string eventName = stat + "_changed";
            GameEvent? gameEvent = null;

            void Apply()
            {
                // Before listeners may have changed the amount: apply what the event says now.
                Num delta = gameEvent?.Amount ?? desired - current;
                Num before = entity.GetBase(stat);
                Num after = Clamp(entity, stat, before + delta);
                entity.SetBase(stat, after);
                applied = after - before;

                if (gameEvent != null)
                {
                    gameEvent.Amount = applied;
                    gameEvent.Data["new"] = Value.FromNumber(after);
                }
            }

            if (HasAnyListeners(eventName))
            {
                gameEvent = new GameEvent(eventName)
                {
                    Source = context.Source,
                    Target = entity,
                    Amount = desired - current,
                };
                gameEvent.Data["stat"] = Value.FromText(stat);
                gameEvent.Data["old"] = Value.FromNumber(current);
                gameEvent.Data["new"] = Value.FromNumber(desired);
                gameEvent.Data["reset"] = Value.FromBool(fromReset);
                Raise(gameEvent, context, Apply);
            }
            else
            {
                Apply();
            }

            if (applied.IsZero) return applied;

            // Changes made inside `until` are undone at the deadline. Pools such as hp are
            // excluded: damage taken during a temporary buff is not refunded when it ends. A
            // status's own counters are included, so `until: gain 2 Strength` gives back exactly
            // those two stacks and nothing added by anything else.
            bool statusCounter = entity.Kind == EntityKind.Status || entity.Kind == EntityKind.Keyword;
            if (context.UndoScope != null && (Content.Resource(stat) == null || statusCounter))
                context.UndoScope.Undo.Add(new TemporaryChange(entity, stat: stat, delta: applied));

            AfterStatChanged(entity, stat, context);
            return applied;
        }

        private void AfterStatChanged(Entity entity, string stat, EvalContext context)
        {
            if (entity.Kind == EntityKind.Status && entity.Definition != null && !entity.IsRemoved)
            {
                StackingMode mode = entity.Definition.Stacking;
                bool usesDuration = mode == StackingMode.Duration || mode == StackingMode.Refresh || mode == StackingMode.Both;
                bool exhausted = (stat == "stacks" && mode != StackingMode.Duration && mode != StackingMode.Refresh && entity.GetBase("stacks") <= Num.Zero)
                              || (stat == "duration" && usesDuration && entity.GetBase("duration") <= Num.Zero);
                if (exhausted) RemoveStatus(entity, context);
                return;
            }

            if (stat == "hp" && entity.Kind == EntityKind.Actor && entity.IsAlive && entity.GetBase("hp") <= Num.Zero)
                Kill(entity, context.Source, context);
        }

        /// <summary>Applies a resource rule's bounds. Unset bound stats (no <c>max_hp</c>) impose nothing.</summary>
        private Num Clamp(Entity entity, string stat, Num value)
        {
            if (entity.Kind == EntityKind.Status && stat == "stacks" && entity.Definition?.MaxStacks is int maxStacks)
                value = Num.Min(value, Num.FromInt(maxStacks));

            ResourceRule? rule = Content.Resource(stat);
            if (rule == null) return value;

            EvalContext context = new EvalContext(entity) { Source = entity };
            if (rule.Min != null && BoundApplies(rule.Min, entity)) value = Num.Max(value, EvaluateNumber(rule.Min, context));
            if (rule.Max != null && BoundApplies(rule.Max, entity)) value = Num.Min(value, EvaluateNumber(rule.Max, context));
            return value;
        }

        private static bool BoundApplies(ExprNode bound, Entity entity) =>
            !(bound is NameExpr name) || entity.HasStat(name.Name);

        /// <summary>Restores the resources an actor holds that reset on <paramref name="trigger"/>.</summary>
        public void ResetResources(Entity actor, string trigger)
        {
            // Sorted, so the order of the resulting `<stat>_changed` events never depends on load order.
            foreach (ResourceRule rule in Content.Resources.Values.OrderBy(r => r.Stat, StringComparer.Ordinal))
            {
                if (rule.ResetTo == null || !string.Equals(rule.ResetOn, trigger, StringComparison.OrdinalIgnoreCase)) continue;

                // The reset establishes the stat rather than skipping whoever lacks it. A resource
                // declared with `reset_to` is a per-turn allowance, and requiring something to have
                // granted it first made the declaration silently inert.
                //
                // A reset whose value names another stat is still skipped for an entity without that
                // stat, which is what keeps an enemy from acquiring `energy` from `reset_to
                // max_energy`.
                if (rule.ResetTo is NameExpr bound && !actor.HasStat(bound.Name)) continue;

                EvalContext context = SystemContext(actor);
                Num value = EvaluateNumber(rule.ResetTo, context);
                ChangeStat(actor, rule.Stat, AssignOperator.Set, value, context, fromReset: true);
            }
        }

        /// <summary>Runs <c>reset_on</c> rules for this event, for the entities it concerns.</summary>
        private void ApplyEventResets(GameEvent gameEvent)
        {
            bool any = false;
            foreach (ResourceRule rule in Content.Resources.Values)
            {
                if (rule.ResetTo != null && string.Equals(rule.ResetOn, gameEvent.Name, StringComparison.OrdinalIgnoreCase))
                {
                    any = true;
                    break;
                }
            }
            if (!any) return;

            foreach (Entity who in Participants(gameEvent).ToArray()) ResetResources(who, gameEvent.Name);
        }

        // Statuses --------------------------------------------------------------------------

        /// <summary>
        /// Applies a status following its stacking mode. <paramref name="durationUnits"/>
        /// comes from <c>for 3s</c> / <c>for 2 turns</c> and sets an expiry time.
        /// </summary>
        public Entity? ApplyStatus(EntityDefinition definition, Entity host, Num stacks, long? durationUnits, EvalContext context, SourceSpan span = default)
        {
            if (host.IsRemoved) return null;

            if (IsImmune(host, definition))
            {
                var resisted = new GameEvent("status_resisted") { Source = context.Source, Target = host, Amount = stacks };
                foreach (string tag in definition.Tags) resisted.Tags.Add(tag);
                resisted.Data["status"] = Value.FromDefinition(definition);
                Raise(resisted, context);
                return null;
            }

            var gameEvent = new GameEvent("status_applied") { Source = context.Source, Target = host, Amount = stacks };
            foreach (string tag in definition.Tags) gameEvent.Tags.Add(tag);
            gameEvent.Data["status_name"] = Value.FromText(definition.Name);

            // The status entity does not exist yet, so the definition stands in for it. That is
            // what lets `on before_status_applied(Poison)` match and cancel.
            gameEvent.Data["status"] = Value.FromDefinition(definition);

            Entity? result = null;
            Raise(gameEvent, context, () =>
            {
                result = ApplyStatusNow(definition, host, gameEvent.Amount, durationUnits, context);
                if (result != null) gameEvent.Data["status"] = Value.FromEntity(result);
            });
            return result;
        }

        private Entity? ApplyStatusNow(EntityDefinition definition, Entity host, Num amount, long? durationUnits, EvalContext context)
        {
            if (amount <= Num.Zero && definition.Stacking != StackingMode.None) return null;

            Entity? existing = null;
            if (definition.Stacking != StackingMode.Separate)
            {
                foreach (Entity attached in host.Attached)
                {
                    if (attached.IsRemoved || !SameDefinition(attached.Definition, definition)) continue;
                    if ((definition.Flags & StatusFlags.UniquePerSource) != 0 && attached.Source != context.Source) continue;
                    existing = attached;
                    break;
                }
            }

            if (existing == null)
            {
                Entity status = State.Instantiate(definition, host);
                status.Source = context.Source;
                State.Attach(host, status);

                switch (definition.Stacking)
                {
                    case StackingMode.Duration:
                    case StackingMode.Refresh:
                        status.SetBase("stacks", Num.One);
                        status.SetBase("duration", amount);
                        break;
                    case StackingMode.Both:
                        status.SetBase("stacks", Clamp(status, "stacks", amount));
                        status.SetBase("duration", Num.FromInt(durationUnits.HasValue ? (int)durationUnits.Value : amount.ToInt()));
                        break;
                    default:
                        status.SetBase("stacks", Clamp(status, "stacks", amount));
                        break;
                }

                if (durationUnits.HasValue) status.SetBase("expires_at", Num.FromInt(State.Clock.Now + durationUnits.Value));

                // Inside `until`, remember the counter this application added rather than the
                // whole instance, so stacks added permanently later on survive the revert.
                if (context.UndoScope != null)
                {
                    string counter = Entity.CounterStat(status);
                    context.UndoScope.Undo.Add(new TemporaryChange(status, stat: counter, delta: status.GetBase(counter)));
                }

                return status;
            }

            switch (definition.Stacking)
            {
                case StackingMode.Intensity:
                    ChangeStat(existing, "stacks", AssignOperator.Add, amount, context);
                    break;
                case StackingMode.Duration:
                    ChangeStat(existing, "duration", AssignOperator.Add, amount, context);
                    break;
                case StackingMode.Refresh:
                    ChangeStat(existing, "duration", AssignOperator.Set, Num.Max(existing.GetBase("duration"), amount), context);
                    break;
                case StackingMode.Both:
                    ChangeStat(existing, "stacks", AssignOperator.Add, amount, context);
                    ChangeStat(existing, "duration", AssignOperator.Add, Num.FromInt(durationUnits.HasValue ? (int)durationUnits.Value : amount.ToInt()), context);
                    break;
                case StackingMode.None:
                    break;
            }

            if (durationUnits.HasValue) existing.SetBase("expires_at", Num.FromInt(State.Clock.Now + durationUnits.Value));
            return existing;
        }

        /// <summary><c>immune tag:poison</c> or <c>immune Weak</c> on the host or anything attached to it.</summary>
        private bool IsImmune(Entity host, EntityDefinition status)
        {
            foreach (Entity source in new[] { host }.Concat(host.Attached.Where(a => !a.IsRemoved)))
            {
                PropertyNode? immune = source.Definition?.Property("immune");
                if (immune == null) continue;

                foreach (ExprNode value in immune.Values)
                {
                    switch (value)
                    {
                        case QualifiedExpr { Qualifier: "tag" } tag when status.HasTag(tag.Name):
                            return true;
                        case QualifiedExpr { Qualifier: "status" } named when string.Equals(named.Name, status.Name, StringComparison.OrdinalIgnoreCase):
                            return true;
                        case NameExpr named when string.Equals(named.Name, status.Name, StringComparison.OrdinalIgnoreCase):
                            return true;
                        case StringExpr named when string.Equals(named.Value, status.Name, StringComparison.OrdinalIgnoreCase):
                            return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Removes one status instance, raising <c>status_removed</c> with its tags.</summary>
        public void RemoveStatus(Entity status, EvalContext context)
        {
            if (status.IsRemoved) return;
            Entity? host = status.Owner;

            var gameEvent = new GameEvent("status_removed")
            {
                Source = context.Source,
                Target = host,
                Amount = status.GetBase("stacks"),
            };
            foreach (string tag in status.Tags) gameEvent.Tags.Add(tag);
            gameEvent.Data["status"] = Value.FromEntity(status);
            gameEvent.Data["status_name"] = Value.FromText(status.Name);

            Raise(gameEvent, context, () => State.Remove(status));
        }

        /// <summary>
        /// Adjusts the counter of a named status on a host (see <see cref="Entity.CounterOf"/>),
        /// applying the status when it is absent. This is what <c>target.Poison -1</c> and
        /// <c>gain 2 Strength</c> do.
        /// </summary>
        public void AdjustStatusStacks(Entity host, string statusName, AssignOperator op, Num amount, EvalContext context, SourceSpan span = default)
        {
            Entity? existing = host.FindAttached(statusName);
            if (existing != null)
            {
                ChangeStat(existing, Entity.CounterStat(existing), op, amount, context, span);
                return;
            }

            Num desired = Combine(Num.Zero, op, amount);
            if (desired <= Num.Zero) return;

            EntityDefinition definition = Content.FindAny(statusName, "status", "keyword")
                ?? throw new RuntimeError($"Unknown status `{statusName}`." + SuggestionText(statusName, Content.AllNames), span);
            ApplyStatus(definition, host, desired, null, context, span);
        }

        /// <summary>
        /// <c>remove X from Y</c>: a status by name, every status with a tag (plus the tag itself),
        /// or a specific entity. Returns how many things were removed.
        /// </summary>
        public int RemoveMatching(Entity host, Value what, EvalContext context, SourceSpan span = default)
        {
            int removed = 0;

            switch (what.Kind)
            {
                case ValueKind.Definition:
                case ValueKind.Text:
                {
                    string name = what.Definition?.Name ?? what.Text!;
                    foreach (Entity status in host.Attached.Where(a => !a.IsRemoved && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray())
                    {
                        RemoveStatus(status, context);
                        removed++;
                    }
                    if (host.HasTag(name))
                    {
                        host.RemoveTag(name);
                        removed++;
                    }
                    break;
                }

                case ValueKind.Qualified:
                {
                    QualifiedName q = what.Qualified!;
                    foreach (Entity status in host.Attached.Where(a => !a.IsRemoved && (q.Qualifier == "status" ? string.Equals(a.Name, q.Name, StringComparison.OrdinalIgnoreCase) : a.HasTag(q.Name))).ToArray())
                    {
                        RemoveStatus(status, context);
                        removed++;
                    }
                    if (q.Qualifier == "tag" && host.HasTag(q.Name))
                    {
                        host.RemoveTag(q.Name);
                        removed++;
                    }
                    break;
                }

                case ValueKind.Entity:
                case ValueKind.List:
                    // A zone name evaluates to the live zone list, which removal shrinks, so
                    // iterate over a copy.
                    foreach (Entity entity in what.AsEntities().ToArray())
                    {
                        if (entity.Kind == EntityKind.Status || entity.Kind == EntityKind.Keyword) RemoveStatus(entity, context);
                        else Destroy(entity, context);
                        removed++;
                    }
                    break;

                default:
                    throw new RuntimeError($"Don't know how to remove {what}.", span);
            }

            return removed;
        }

        /// <summary>Removes statuses whose <c>for</c> duration has run out on the clock.</summary>
        public void ExpireTimedStatuses()
        {
            foreach (Entity status in State.Entities.Where(e => !e.IsRemoved && e.HasStat("expires_at")).ToArray())
            {
                if (status.GetBase("expires_at") <= Num.FromInt(State.Clock.Now))
                    RemoveStatus(status, SystemContext(status));
            }
        }

        /// <summary>Applies <c>decay</c> to every status on a host that decays on this trigger.</summary>
        public void ProcessDecay(Entity host, string trigger)
        {
            foreach (Entity status in host.Attached.ToArray())
            {
                EntityDefinition? definition = status.Definition;
                if (status.IsRemoved || definition == null || definition.DecayAmount <= Num.Zero) continue;
                if (!string.Equals(definition.DecayOn, trigger, StringComparison.OrdinalIgnoreCase)) continue;

                string counter = definition.Stacking is StackingMode.Duration or StackingMode.Refresh or StackingMode.Both ? "duration" : "stacks";
                ChangeStat(status, counter, AssignOperator.Subtract, definition.DecayAmount, SystemContext(status));
            }
        }

        /// <summary>
        /// Decays the statuses on an event's participants whose <c>decay ... on</c> names it. Decay
        /// waits for the event's own listeners, so "at end of turn, deal damage equal to stacks"
        /// sees the stacks before they tick down.
        /// </summary>
        private void QueueDecay(GameEvent gameEvent)
        {
            List<Entity>? hosts = null;
            foreach (Entity host in Participants(gameEvent))
            {
                foreach (Entity status in host.Attached)
                {
                    EntityDefinition? definition = status.Definition;
                    if (status.IsRemoved || definition == null || definition.DecayAmount <= Num.Zero) continue;
                    if (!string.Equals(definition.DecayOn, gameEvent.Name, StringComparison.OrdinalIgnoreCase)) continue;

                    (hosts ??= new List<Entity>()).Add(host);
                    break;
                }
            }

            if (hosts == null) return;

            RunAfterListeners(
                () =>
                {
                    foreach (Entity host in hosts)
                    {
                        if (!host.IsRemoved) ProcessDecay(host, gameEvent.Name);
                    }
                },
                "status decay after " + gameEvent.Name);
        }

        // Combat ----------------------------------------------------------------------------

        /// <summary>
        /// Deals one hit of damage. Modifiers run first (<c>damage</c> for the source, then
        /// <c>damage_taken</c> for the target), then before listeners may adjust or cancel, block
        /// absorbs what it can, and the target dies if hp reaches zero. Returns hp actually lost.
        /// </summary>
        public Num DealDamage(Entity? source, Entity target, Num amount, IEnumerable<string> tags, bool ignoreBlock, EvalContext context, SourceSpan span = default)
        {
            if (!target.IsAlive) return Num.Zero;

            var tagList = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var query = new ModifierQuery("damage") { Source = source, Subject = target, Card = context.Card, Tags = tagList };
            Num modified = State.Modifiers.Compute(query, amount);
            modified = State.Modifiers.Compute(new ModifierQuery("damage_taken") { Source = source, Subject = target, Card = context.Card, Tags = tagList }, modified);
            modified = Num.Max(Num.Zero, modified.Floor());

            var gameEvent = new GameEvent("damaged") { Source = source, Target = target, Card = context.Card, Amount = modified };
            foreach (string tag in tagList) gameEvent.Tags.Add(tag);
            gameEvent.Data["base"] = Value.FromNumber(amount);

            Num lost = Num.Zero;
            Num blocked = Num.Zero;
            Num overkill = Num.Zero;

            Raise(gameEvent, context, () =>
            {
                Num hit = Num.Max(Num.Zero, gameEvent.Amount.Floor());
                gameEvent.Data["total"] = Value.FromNumber(hit);

                if (!ignoreBlock)
                {
                    blocked = Num.Min(Num.Max(Num.Zero, target.GetBase("block")), hit);
                    if (blocked > Num.Zero) target.SetBase("block", target.GetBase("block") - blocked);
                }

                Num through = hit - blocked;
                Num hp = target.GetBase("hp");
                lost = Num.Min(through, Num.Max(Num.Zero, hp));
                overkill = through - lost;
                target.SetBase("hp", hp - lost);

                gameEvent.Amount = lost;
                gameEvent.Data["blocked"] = Value.FromNumber(blocked);
                gameEvent.Data["overkill"] = Value.FromNumber(overkill);

                State.RecordHistory("damage_taken", target, lost);
                if (source != null) State.RecordHistory("damage_dealt", source.Controller, lost);
            });

            if (blocked > Num.Zero)
                Raise(new GameEvent("blocked") { Source = source, Target = target, Card = context.Card, Amount = blocked }, context);

            if (target.IsAlive && target.GetBase("hp") <= Num.Zero && target.Kind == EntityKind.Actor)
            {
                if (Kill(target, source, context) && overkill > Num.Zero)
                    Raise(new GameEvent("overkill") { Source = source, Target = target, Card = context.Card, Amount = overkill }, context);
            }
            else if (lost > Num.Zero)
            {
                RetelegraphIfPhaseChanged(target);
            }

            return lost;
        }

        /// <summary>
        /// Kills an actor. The instead phase of <c>died</c> is where "the first time you would die,
        /// heal to 50%" lives. Returns true if the actor is dead afterwards.
        /// </summary>
        public bool Kill(Entity target, Entity? source, EvalContext context)
        {
            if (!target.IsAlive) return target.IsDead;

            var died = new GameEvent("died") { Source = source, Target = target, Card = context.Card };
            Raise(died, context, () =>
            {
                State.MarkDead(target);
                State.RecordHistory("kills", source?.Controller, Num.One);
            });

            if (!target.IsDead) return false;

            Raise(new GameEvent("killed") { Source = source, Target = target, Card = context.Card }, context);

            // Only now does the actor leave the board. Its own `on died` and `on killed` listeners,
            // and those of its statuses, were queued while it was still listening.
            State.Bury(target);
            return true;
        }

        public Num Heal(Entity? source, Entity target, Num amount, EvalContext context, SourceSpan span = default)
        {
            if (!target.IsAlive) return Num.Zero;

            Num modified = State.Modifiers.Compute(new ModifierQuery("heal") { Source = source, Subject = target, Card = context.Card }, amount);
            modified = State.Modifiers.Compute(new ModifierQuery("heal_taken") { Source = source, Subject = target, Card = context.Card }, modified);
            modified = Num.Max(Num.Zero, modified.Floor());

            var gameEvent = new GameEvent("healed") { Source = source, Target = target, Card = context.Card, Amount = modified };
            Num healed = Num.Zero;
            Raise(gameEvent, context, () =>
            {
                Num before = target.GetBase("hp");
                Num after = Clamp(target, "hp", before + Num.Max(Num.Zero, gameEvent.Amount));
                target.SetBase("hp", after);
                healed = after - before;
                gameEvent.Amount = healed;
                State.RecordHistory("healed", target, healed);
            });
            return healed;
        }

        public Num GainBlock(Entity? source, Entity target, Num amount, EvalContext context, SourceSpan span = default)
        {
            if (!target.IsAlive) return Num.Zero;

            Num modified = State.Modifiers.Compute(new ModifierQuery("block") { Source = source, Subject = target, Card = context.Card }, amount);
            modified = State.Modifiers.Compute(new ModifierQuery("block_taken") { Source = source, Subject = target, Card = context.Card }, modified);
            modified = Num.Max(Num.Zero, modified.Floor());

            var gameEvent = new GameEvent("gained_block") { Source = source, Target = target, Card = context.Card, Amount = modified };
            Num gained = Num.Zero;
            Raise(gameEvent, context, () =>
            {
                Num before = target.GetBase("block");
                Num after = Clamp(target, "block", before + Num.Max(Num.Zero, gameEvent.Amount));
                target.SetBase("block", after);
                gained = after - before;
                gameEvent.Amount = gained;
            });
            return gained;
        }

        // Cards and zones -------------------------------------------------------------------

        /// <summary>
        /// Draws from the top of the draw pile, reshuffling the discard pile when it runs out. The
        /// count goes through the <c>draw</c> modifier channel. With a full hand a drawn card goes
        /// to the discard pile instead.
        /// </summary>
        public IReadOnlyList<Entity> Draw(Entity actor, int count, EvalContext context)
        {
            var query = new ModifierQuery("draw") { Source = actor, Subject = actor, Card = context.Card };
            count = Math.Max(0, State.Modifiers.Compute(query, Num.FromInt(count)).Floor().ToInt());

            var drawn = new List<Entity>();
            for (int i = 0; i < count; i++)
            {
                if (State.ZoneOf(actor, Zones.Draw).Count == 0)
                {
                    if (State.ZoneOf(actor, Zones.Discard).Count == 0) break;
                    ShuffleDiscardIntoDraw(actor, context);
                    if (State.ZoneOf(actor, Zones.Draw).Count == 0) break;
                }

                Entity card = State.ZoneOf(actor, Zones.Draw)[0];

                if (State.ZoneOf(actor, Zones.Hand).Count >= Rules.MaxHandSize)
                {
                    bool discarded = MoveCard(card, Zones.Discard, "discarded", context);
                    if (!discarded && card.Zone == Zones.Draw) break; // cancelled: stop rather than loop on the same card
                    continue;
                }

                var gameEvent = new GameEvent("drawn") { Source = actor, Target = card, Card = card };
                foreach (string tag in card.Tags) gameEvent.Tags.Add(tag);

                bool ran = Raise(gameEvent, context, () =>
                {
                    State.MoveTo(card, Zones.Hand);
                    State.RecordHistory("cards_drawn", actor, Num.One);
                });
                if (ran) drawn.Add(card);
                else if (card.Zone == Zones.Draw) break; // cancelled: stop rather than loop on the same card
            }
            return drawn;
        }

        public void ShuffleDiscardIntoDraw(Entity actor, EvalContext context)
        {
            var gameEvent = new GameEvent("shuffled") { Source = actor, Target = actor };
            Raise(gameEvent, context, () =>
            {
                foreach (Entity card in State.ZoneOf(actor, Zones.Discard).ToArray()) State.MoveTo(card, Zones.Draw);
                ShuffleZone(actor, Zones.Draw);
            });
        }

        public void ShuffleZone(Entity actor, string zone)
        {
            State.Rng.Shuffle(State.MutableZone(actor, zone));
            State.Touch();
        }

        /// <summary>Moves a card to a zone, raising a card flow event such as <c>discarded</c> or <c>exhausted</c>.</summary>
        public bool MoveCard(Entity card, string zone, string eventName, EvalContext context, bool toTop = false)
        {
            if (card.IsRemoved) return false;

            var gameEvent = new GameEvent(eventName) { Source = context.Source?.Controller, Target = card, Card = card };
            foreach (string tag in card.Tags) gameEvent.Tags.Add(tag);
            gameEvent.Data["from"] = Value.FromText(card.Zone);
            gameEvent.Data["to"] = Value.FromText(zone);

            return Raise(gameEvent, context, () =>
            {
                State.MoveTo(card, zone, toTop);
                if (eventName != "moved") State.RecordHistory("cards_" + eventName, card.Controller, Num.One);
            });
        }

        /// <summary>Creates cards, relics or actors from a definition, per the <c>create</c> verb.</summary>
        public Entity Create(EntityDefinition definition, Entity? owner, string? zone, EvalContext context)
        {
            Entity? created = null;
            var gameEvent = new GameEvent("created") { Source = context.Source };

            Raise(gameEvent, context, () =>
            {
                switch (definition.Kind)
                {
                    case EntityKind.Actor:
                    {
                        // An enemy always joins the enemy side; a generic actor joins whoever made it.
                        Team team = Team.Enemy;
                        if (definition.KindName != "enemy" && context.Controller != null && context.Controller.Team != Team.Neutral)
                            team = context.Controller.Team;

                        created = State.Instantiate(definition, null, team, Zones.Board);
                        if (!created.HasStat("block")) created.SetBase("block", Num.Zero);
                        break;
                    }
                    case EntityKind.Relic:
                    case EntityKind.Item:
                        created = State.Instantiate(definition, owner, Team.Neutral, zone ?? Zones.Relics);
                        break;
                    default:
                        created = State.Instantiate(definition, owner, Team.Neutral, zone ?? Zones.Hand);
                        break;
                }
                gameEvent.Target = created;
                if (created.Kind == EntityKind.Card) gameEvent.Card = created;
            });

            if (created == null)
                throw new RuntimeError($"Creating {definition} was replaced, so there is nothing to return.", context.Self?.Definition?.Syntax.Span ?? SourceSpan.None);

            // A summon or split mid-battle acts on the next enemy turn, like any other enemy.
            if (created.Kind == EntityKind.Actor && State.InBattle) RollIntent(created);
            return created;
        }

        /// <summary>Takes an entity out of the game entirely, raising <c>destroyed</c>.</summary>
        public void Destroy(Entity entity, EvalContext context)
        {
            if (entity.IsRemoved) return;
            if (entity.Kind == EntityKind.Status || entity.Kind == EntityKind.Keyword)
            {
                RemoveStatus(entity, context);
                return;
            }

            var gameEvent = new GameEvent("destroyed") { Source = context.Source, Target = entity, Card = entity.Kind == EntityKind.Card ? entity : null };
            foreach (string tag in entity.Tags) gameEvent.Tags.Add(tag);
            Raise(gameEvent, context, () => State.Remove(entity));
        }

        // Enemies ---------------------------------------------------------------------------

        /// <summary>Picks an enemy's next move from its pattern, so the UI can show intents in advance.</summary>
        public void RollIntent(Entity enemy)
        {
            EntityDefinition? definition = enemy.Definition;
            if (definition == null || definition.Moves.Count == 0)
            {
                enemy.Intent = null;
                return;
            }

            UpdatePhase(enemy, definition);

            List<string> names = definition.PatternMoves.Count > 0
                ? definition.PatternMoves.ToList()
                : definition.Moves.Select(m => m.Name).ToList();

            if (definition.Phases.Count > 0)
            {
                names.RemoveAll(n => !AvailableInPhase(definition, n, enemy.Phase));
                if (names.Count == 0)
                {
                    enemy.Intent = null;
                    State.Touch();
                    return;
                }
            }

            switch (definition.Pattern)
            {
                case EnemyPatternKind.Cycle:
                    enemy.Intent = names[enemy.PatternIndex % names.Count];
                    enemy.PatternIndex++;
                    break;

                case EnemyPatternKind.Random:
                case EnemyPatternKind.RandomNoRepeat:
                {
                    var candidates = names.ToList();
                    if (definition.Pattern == EnemyPatternKind.RandomNoRepeat && candidates.Count > 1 && enemy.LastMove != null)
                        candidates.RemoveAll(n => string.Equals(n, enemy.LastMove, StringComparison.OrdinalIgnoreCase));

                    var weights = candidates
                        .Select(n => definition.Moves.FirstOrDefault(m => string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase))?.Weight ?? Num.One)
                        .ToList();
                    int index = State.Rng.PickWeighted(weights);
                    enemy.Intent = index < 0 ? null : candidates[index];
                    break;
                }
            }

            State.Touch();
        }

        /// <summary>
        /// Settles which phase an enemy is in before its next move is chosen.
        /// </summary>
        /// <remarks>
        /// The last declared phase whose condition holds wins, so thresholds can be written in the
        /// order a designer thinks of them — three quarters, then half, then a quarter — and the
        /// deepest one that is true is the one that applies.
        /// </remarks>
        private bool UpdatePhase(Entity enemy, EntityDefinition definition)
        {
            if (definition.Phases.Count == 0) return false;

            EvalContext context = SystemContext(enemy);
            string? active = null;
            foreach (PhaseDefinition phase in definition.Phases)
            {
                if (EvaluateCondition(phase.Condition, context)) active = phase.Name;
            }

            if (string.Equals(active, enemy.Phase, StringComparison.OrdinalIgnoreCase)) return false;

            // A new phase starts its own sequence: the old index counted through a list of moves
            // that is no longer the same one.
            enemy.Phase = active;
            enemy.PatternIndex = 0;
            return true;
        }

        /// <summary>
        /// Re-rolls an enemy's intent when a hit has just moved it into a phase that asks to
        /// re-telegraph, so the move the phase unlocked is the one the player is shown.
        /// </summary>
        /// <remarks>
        /// Checked where damage lands rather than inside <see cref="RollIntent"/>, because a phase
        /// crossed during the player's turn has to be noticed before the next intent would be rolled
        /// — which is the whole point of re-telegraphing.
        /// </remarks>
        private void RetelegraphIfPhaseChanged(Entity enemy)
        {
            EntityDefinition? definition = enemy.Definition;
            if (definition == null || definition.Phases.Count == 0) return;
            if (!enemy.IsAlive || enemy.Kind != EntityKind.Actor || enemy.Team != Team.Enemy) return;

            string? before = enemy.Phase;
            if (!UpdatePhase(enemy, definition)) return;

            PhaseDefinition? entered = definition.Phases
                .FirstOrDefault(p => string.Equals(p.Name, enemy.Phase, StringComparison.OrdinalIgnoreCase));

            // Leaving a phase for no phase at all never re-telegraphs: there is nothing that asked.
            if (entered == null || !entered.Retelegraph) return;
            if (string.Equals(before, enemy.Phase, StringComparison.OrdinalIgnoreCase)) return;

            RollIntent(enemy);
        }

        /// <summary>A move with no phase is always available; one with a phase only during it.</summary>
        private static bool AvailableInPhase(EntityDefinition definition, string move, string? phase)
        {
            MoveDefinition? found = definition.Moves.FirstOrDefault(m => string.Equals(m.Name, move, StringComparison.OrdinalIgnoreCase));
            return found?.Phase == null || string.Equals(found.Phase, phase, StringComparison.OrdinalIgnoreCase);
        }

        // Temporary effects -----------------------------------------------------------------

        /// <summary>
        /// Ends <c>until</c> blocks whose deadline event has just been raised for their owner. The
        /// revert waits for the deadline's own listeners, so "at end of turn" effects still see
        /// the temporary change.
        /// </summary>
        private void ProcessDeadlines(GameEvent gameEvent)
        {
            if (State.Scheduled.Count == 0) return;

            foreach (ScheduledAction action in State.Scheduled.ToArray())
            {
                if (action.Timing != ScheduleTiming.Until) continue;
                if (!string.Equals(action.Deadline, gameEvent.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (gameEvent.Target != null && gameEvent.Target.Kind == EntityKind.Actor && gameEvent.Target != action.Owner) continue;

                State.Unschedule(action);
                RunAfterListeners(() => Revert(action), "end of `until " + action.Deadline + "` block", action.Body?.Span ?? default);
            }
        }

        internal void Revert(ScheduledAction action)
        {
            EvalContext context = SystemContext(action.Owner);
            for (int i = action.Undo.Count - 1; i >= 0; i--)
            {
                TemporaryChange change = action.Undo[i];
                if (change.Attached != null)
                {
                    if (!change.Attached.IsRemoved) RemoveStatus(change.Attached, context);
                }
                else if (change.Tag != null)
                {
                    change.Entity.RemoveTag(change.Tag);
                }
                else if (change.Stat != null && !change.Entity.IsRemoved)
                {
                    // Through ChangeStat, so a status whose temporary stacks were all it had is removed.
                    ChangeStat(change.Entity, change.Stat, AssignOperator.Subtract, change.Delta, context);
                }
            }
        }

        /// <summary>Adds a tag, recording it for undo inside <c>until</c> blocks.</summary>
        public void AddTag(Entity entity, string tag, EvalContext context)
        {
            if (entity.HasTag(tag)) return;
            entity.AddTag(tag);
            context.UndoScope?.Undo.Add(new TemporaryChange(entity, tag: tag.ToLowerInvariant()));
        }
    }
}
