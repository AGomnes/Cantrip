using System;
using System.Collections.Generic;
using System.Linq;
using GameplayEffects.Content;
using GameplayEffects.Diagnostics;
using GameplayEffects.Syntax;

namespace GameplayEffects.Runtime
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

        private static EvalContext SystemContext(Entity? self) => new EvalContext(self) { Source = self };

        // Stats and resources ---------------------------------------------------------------

        /// <summary>
        /// The core <c>change</c> verb. Applies resource bounds, raises <c>&lt;stat&gt;_changed</c>,
        /// removes statuses whose counter runs out and kills actors whose hp reaches zero.
        /// Returns the change actually applied.
        /// </summary>
        public Num ChangeStat(Entity entity, string stat, AssignOperator op, Num amount, EvalContext context, SourceSpan span = default)
        {
            if (entity.IsRemoved) return Num.Zero;

            stat = stat.ToLowerInvariant();
            Num current = entity.GetBase(stat);
            Num desired = Clamp(entity, stat, Combine(current, op, amount));
            if (desired == current && entity.HasStat(stat)) return Num.Zero;

            Num applied = Num.Zero;
            string eventName = stat + "_changed";

            void Apply()
            {
                Num before = entity.GetBase(stat);
                Num after = Clamp(entity, stat, before + (desired - current));
                entity.SetBase(stat, after);
                applied = after - before;
            }

            if (HasAnyListeners(eventName))
            {
                var gameEvent = new GameEvent(eventName)
                {
                    Source = context.Source,
                    Target = entity,
                    Amount = desired - current,
                };
                gameEvent.Data["stat"] = Value.FromText(stat);
                gameEvent.Data["old"] = Value.FromNumber(current);
                gameEvent.Data["new"] = Value.FromNumber(desired);
                Raise(gameEvent, context, Apply);
            }
            else
            {
                Apply();
            }

            if (applied.IsZero) return applied;

            // Changes made inside `until` are undone at the deadline. Pools such as hp are
            // excluded: damage taken during a temporary buff is not refunded when it ends.
            if (context.UndoScope != null && Content.Resource(stat) == null)
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

            EvalContext context = SystemContext(entity);
            if (rule.Min != null && BoundApplies(rule.Min, entity)) value = Num.Max(value, EvaluateNumber(rule.Min, context));
            if (rule.Max != null && BoundApplies(rule.Max, entity)) value = Num.Min(value, EvaluateNumber(rule.Max, context));
            return value;
        }

        private static bool BoundApplies(ExprNode bound, Entity entity) =>
            !(bound is NameExpr name) || entity.HasStat(name.Name);

        /// <summary>Restores resources that reset on a lifecycle event, such as energy and block at turn start.</summary>
        public void ResetResources(Entity actor, string trigger)
        {
            foreach (ResourceRule rule in Content.Resources.Values)
            {
                if (rule.ResetTo == null || !string.Equals(rule.ResetOn, trigger, StringComparison.OrdinalIgnoreCase)) continue;
                if (!actor.HasStat(rule.Stat)) continue;
                if (rule.ResetTo is NameExpr bound && !actor.HasStat(bound.Name)) continue;

                EvalContext context = SystemContext(actor);
                Num value = EvaluateNumber(rule.ResetTo, context);
                ChangeStat(actor, rule.Stat, AssignOperator.Set, value, context);
            }
        }

        // Statuses --------------------------------------------------------------------------

        /// <summary>
        /// Applies a status following its stacking mode (section 3.9). <paramref name="durationUnits"/>
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
                    if (attached.IsRemoved || attached.Definition != definition) continue;
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
                        status.SetBase("stacks", amount);
                        status.SetBase("duration", Num.FromInt(durationUnits.HasValue ? (int)durationUnits.Value : amount.ToInt()));
                        break;
                    default:
                        status.SetBase("stacks", Clamp(status, "stacks", amount));
                        break;
                }

                if (durationUnits.HasValue) status.SetBase("expires_at", Num.FromInt(State.Clock.Now + durationUnits.Value));
                context.UndoScope?.Undo.Add(new TemporaryChange(host, attached: status));
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
        /// Adjusts the stacks of a named status on a host, applying or removing it as needed. This is
        /// what <c>target.Poison -1</c> and <c>gain 2 Strength</c> do.
        /// </summary>
        public void AdjustStatusStacks(Entity host, string statusName, AssignOperator op, Num amount, EvalContext context, SourceSpan span = default)
        {
            Entity? existing = host.FindAttached(statusName);
            if (existing != null)
            {
                string counter = existing.Definition?.Stacking is StackingMode.Duration or StackingMode.Refresh ? "duration" : "stacks";
                ChangeStat(existing, counter, op, amount, context, span);
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
                    foreach (Entity entity in what.AsEntities())
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

        /// <summary>Applies <c>decay</c> to every status on an actor that decays on this trigger.</summary>
        public void ProcessDecay(Entity actor, string trigger)
        {
            foreach (Entity status in actor.Attached.ToArray())
            {
                EntityDefinition? definition = status.Definition;
                if (status.IsRemoved || definition == null || definition.DecayAmount <= Num.Zero) continue;
                if (!string.Equals(definition.DecayOn, trigger, StringComparison.OrdinalIgnoreCase)) continue;

                string counter = definition.Stacking is StackingMode.Duration or StackingMode.Refresh or StackingMode.Both ? "duration" : "stacks";
                ChangeStat(status, counter, AssignOperator.Subtract, definition.DecayAmount, SystemContext(status));
            }
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

        /// <summary>Draws from the top of the draw pile, reshuffling the discard pile when it runs out.</summary>
        public IReadOnlyList<Entity> Draw(Entity actor, int count, EvalContext context)
        {
            var drawn = new List<Entity>();
            for (int i = 0; i < count; i++)
            {
                if (State.ZoneOf(actor, Zones.Hand).Count >= Rules.MaxHandSize) break;

                if (State.ZoneOf(actor, Zones.Draw).Count == 0)
                {
                    if (State.ZoneOf(actor, Zones.Discard).Count == 0) break;
                    ShuffleDiscardIntoDraw(actor, context);
                    if (State.ZoneOf(actor, Zones.Draw).Count == 0) break;
                }

                Entity card = State.ZoneOf(actor, Zones.Draw)[0];
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
                        Team team = context.Controller?.Team == Team.Enemy ? Team.Enemy : Team.Enemy;
                        if (context.Controller?.Team == Team.Player && definition.KindName == "actor") team = Team.Player;
                        created = State.Instantiate(definition, null, team, Zones.Board);
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

        // Temporary effects -----------------------------------------------------------------

        /// <summary>Reverts <c>until</c> blocks whose deadline event has just resolved.</summary>
        private void ProcessDeadlines(GameEvent gameEvent)
        {
            if (State.Scheduled.Count == 0) return;

            foreach (ScheduledAction action in State.Scheduled.ToArray())
            {
                if (action.Timing != ScheduleTiming.Until) continue;
                if (!string.Equals(action.Deadline, gameEvent.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (gameEvent.Target != null && gameEvent.Target.Kind == EntityKind.Actor && gameEvent.Target != action.Owner) continue;

                State.Unschedule(action);
                Revert(action);
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
                    change.Entity.SetBase(change.Stat, change.Entity.GetBase(change.Stat) - change.Delta);
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
