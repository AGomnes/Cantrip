using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Syntax;

namespace Cantrip.Runtime
{
    // Expression evaluation: names, members, calls, operators, selectors and qualifier tests.
    public sealed partial class Interpreter
    {
        /// <summary>Stat names that read as zero when an entity has never had them set.</summary>
        internal static readonly string[] CommonStats =
        {
            "hp", "max_hp", "block", "energy", "max_energy", "strength", "dexterity", "cost", "stacks", "duration", "gold",
        };

        /// <summary>
        /// Names <see cref="ResolveName"/> gives a built-in meaning. The linter and "did you mean"
        /// suggestions read this list, so keep it in step with the switch below.
        /// </summary>
        internal static readonly string[] ReservedNames =
        {
            "self", "owner", "source", "target", "it", "card", "player", "controller", "true", "false", "none", "nothing",
            "turn", "now", "enemies", "allies", "everyone", "actors", "enemy", "hand", "draw", "draw_pile", "discard",
            "discard_pile", "exhaust", "exhaust_pile", "powers", "relics", "deck", "cards", "statuses", "stacks", "event",
        };

        public Value Evaluate(ExprNode node, EvalContext context)
        {
            switch (node)
            {
                case NumberExpr number:
                    return Value.FromNumber(number.Value, number.Unit);

                case StringExpr text:
                    return Value.FromText(text.Value);

                case NameExpr name:
                    return ResolveName(name.Name, name.Span, context);

                case QualifiedExpr qualified:
                    return Value.FromQualified(qualified.Qualifier, qualified.Name);

                case MemberExpr member:
                    return EvaluateMember(member, context);

                case CallExpr call:
                    return EvaluateCall(call, context);

                case UnaryExpr unary when unary.Operator == UnaryOperator.Negate:
                {
                    Value operand = Evaluate(unary.Operand, context);
                    return Value.FromNumber(-ToNumber(operand, unary.Span), operand.Unit);
                }

                case UnaryExpr unary:
                    return Value.FromBool(!EvaluateCondition(unary.Operand, context));

                case BinaryExpr binary:
                    return EvaluateBinary(binary, context);

                case RangeExpr range:
                    return Value.FromRange(EvaluateNumber(range.Low, context), EvaluateNumber(range.High, context));

                case WhereExpr where:
                    return Value.FromEntities(FilterWhere(Evaluate(where.Source, context).AsEntities(), where.Predicate, context));

                case SelectorExpr selector:
                    return EvaluateSelector(selector, context);

                default:
                    throw new RuntimeError($"Cannot evaluate {node.GetType().Name}.", node.Span);
            }
        }

        /// <summary>Evaluates to a number. Ranges roll, lists count, booleans are 1 or 0.</summary>
        public Num EvaluateNumber(ExprNode node, EvalContext context) => ToNumber(Evaluate(node, context), node.Span);

        /// <summary>Evaluates as a condition. Qualifiers such as <c>tag:fire</c> are tested against the focus.</summary>
        public bool EvaluateCondition(ExprNode node, EvalContext context) => IsTrue(Evaluate(node, context), context);

        public bool IsTrue(Value value, EvalContext context) =>
            value.Kind == ValueKind.Qualified ? TestQualified(value.Qualified!, context) : value.AsBool();

        internal Num ToNumber(Value value, SourceSpan span)
        {
            switch (value.Kind)
            {
                case ValueKind.Number:
                case ValueKind.Bool:
                    return value.Number;

                case ValueKind.Range:
                {
                    Num low = value.Number, high = value.RangeHigh;
                    // Whole-number ranges roll whole numbers: `deal 3..6` never deals 4.37.
                    if (low == low.Floor() && high == high.Floor())
                        return Num.FromInt(State.Rng.NextInt(low.ToInt(), high.ToInt()));
                    return State.Rng.NextNum(low, high);
                }

                case ValueKind.List:
                    return Num.FromInt(value.AsEntities().Count);

                case ValueKind.Entity:
                    return Num.One;

                case ValueKind.None:
                    return Num.Zero;

                case ValueKind.Text when Num.TryParse(value.Text!, out Num parsed):
                    return parsed;

                default:
                    throw new RuntimeError($"Expected a number but got {value}.", span);
            }
        }

        // Names -----------------------------------------------------------------------------

        private Value ResolveName(string name, SourceSpan span, EvalContext context)
        {
            if (context.TryGetLocal(name, out Value local)) return local;

            string key = name.ToLowerInvariant();
            Entity? controller = context.Controller;

            switch (key)
            {
                case "self": return Value.FromEntity(context.Self);
                case "owner": return Value.FromEntity(context.Self?.Owner ?? context.Self);
                case "source": return Value.FromEntity(context.Source);
                case "target": return Value.FromEntity(context.Target);
                case "it": return context.ItDefinition != null ? Value.FromDefinition(context.ItDefinition) : Value.FromEntity(context.It);
                case "card": return Value.FromEntity(context.Card ?? (context.Self?.Kind == EntityKind.Card ? context.Self : null));
                case "player": return Value.FromEntity(State.Player);
                case "controller": return Value.FromEntity(controller);
                case "true": return Value.True;
                case "false": return Value.False;
                case "none": case "nothing": return Value.None;
                case "turn": return Value.FromNumber(Num.FromInt(State.Turn));
                case "now": return Value.FromNumber(Num.FromInt(State.Clock.Now));

                case "enemies": return Value.FromEntities(State.Actors(Opposing(Perspective(context))));
                case "allies": return Value.FromEntities(State.Actors(Perspective(context)));
                case "everyone": case "actors": return Value.FromEntities(State.Actors());
                case "enemy":
                {
                    Team opposing = Opposing(Perspective(context));
                    if (context.Target != null && context.Target.Kind == EntityKind.Actor && context.Target.Team == opposing)
                        return Value.FromEntity(context.Target);
                    return Value.FromEntity(State.Actors(opposing).FirstOrDefault());
                }

                case "hand": return Value.FromEntities(State.ZoneOf(controller, Zones.Hand));
                case "draw": case "draw_pile": return Value.FromEntities(State.ZoneOf(controller, Zones.Draw));
                case "discard": case "discard_pile": return Value.FromEntities(State.ZoneOf(controller, Zones.Discard));
                case "exhaust": case "exhaust_pile": return Value.FromEntities(State.ZoneOf(controller, Zones.Exhaust));
                case "powers": return Value.FromEntities(State.ZoneOf(controller, Zones.Powers));
                case "relics": return Value.FromEntities(State.ZoneOf(controller, Zones.Relics));
                case "deck":
                    return Value.FromEntities(
                        State.ZoneOf(controller, Zones.Draw)
                            .Concat(State.ZoneOf(controller, Zones.Hand))
                            .Concat(State.ZoneOf(controller, Zones.Discard))
                            .ToList());
                case "cards":
                    return Value.FromEntities(State.Entities
                        .Where(e => e.Kind == EntityKind.Card && !e.IsRemoved && e.Controller == controller)
                        .ToList());
                case "statuses":
                    return Value.FromEntities((context.It ?? controller)?.Attached.Where(a => !a.IsRemoved).ToList() ?? new List<Entity>());

                case "stacks" when context.Self != null && context.Self.Kind == EntityKind.Status:
                    return Value.FromNumber(context.Self.Get("stacks"));
            }

            if (TryHistory(key, controller, out Value history)) return history;

            if (Host.TryResolveName(name, context, out Value hosted)) return hosted;

            // Inside `where`, bare stat names read from the candidate: `cards where cost > 1`.
            if (context.It != null && context.It.HasStat(name)) return Value.FromNumber(context.It.Get(name));
            if (context.Self != null && context.Self.HasStat(name)) return Value.FromNumber(context.Self.Get(name));

            // The same courtesy for a definition candidate: `discover 3 cards where cost <= 2`.
            if (context.ItDefinition != null && context.ItDefinition.Stats.TryGetValue(name, out Num printed))
                return Value.FromNumber(printed);

            // When a status and a card share a name (Burn, Wound), a bare name in an expression means
            // the status: `apply`, `has`, `on` and member access all want it. Verbs that want the
            // card (`create`, `shuffle`) resolve the name themselves.
            EntityDefinition? definition = Content.FindAny(name, "status", "keyword") ?? Content.Find(name);
            if (definition != null) return Value.FromDefinition(definition);

            if (context.Event != null && context.Event.Data.TryGetValue(name, out Value data)) return data;

            if (controller != null && controller.HasStat(name)) return Value.FromNumber(controller.Get(name));

            if (IsKnownStat(name)) return Value.FromNumber(Num.Zero);

            throw new RuntimeError(
                $"Unknown name `{name}`." + SuggestionText(name, NameCandidates(context)),
                span);
        }

        private bool IsKnownStat(string name)
        {
            if (CommonStats.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
            if (Content.Resource(name) != null) return true;
            return KnownStats.Contains(name);
        }

        private HashSet<string>? _knownStats;
        private int _knownStatsGeneration = -1;

        /// <summary>Every stat name any definition declares, so unset stats read as zero rather than erroring.</summary>
        private HashSet<string> KnownStats
        {
            get
            {
                if (_knownStats == null || _knownStatsGeneration != Content.Generation)
                {
                    _knownStats = new HashSet<string>(
                        Content.Definitions.SelectMany(d => d.Stats.Keys),
                        StringComparer.OrdinalIgnoreCase);
                    _knownStatsGeneration = Content.Generation;
                }
                return _knownStats;
            }
        }

        private IEnumerable<string> NameCandidates(EvalContext context) =>
            ReservedNames
            .Concat(context.LocalNames())
            .Concat(Content.AllNames)
            .Concat(CommonStats);

        private static string SuggestionText(string name, IEnumerable<string> candidates)
        {
            string? suggestion = Suggest.Closest(name, candidates);
            return suggestion == null ? string.Empty : $" Did you mean `{suggestion}`?";
        }

        /// <summary>
        /// History queries: <c>damage_taken_this_turn</c>, <c>cards_played_this_battle</c>.
        /// </summary>
        private bool TryHistory(string key, Entity? who, out Value value)
        {
            value = Value.None;
            if (key.EndsWith("_this_turn", StringComparison.Ordinal))
            {
                value = Value.FromNumber(State.History(key.Substring(0, key.Length - "_this_turn".Length), who));
                return true;
            }
            if (key.EndsWith("_this_battle", StringComparison.Ordinal))
            {
                value = Value.FromNumber(State.History(key.Substring(0, key.Length - "_this_battle".Length), who, battle: true));
                return true;
            }
            return false;
        }

        // Members ---------------------------------------------------------------------------

        private Value EvaluateMember(MemberExpr member, EvalContext context)
        {
            if (member.Target is NameExpr { Name: var root } && string.Equals(root, "event", StringComparison.OrdinalIgnoreCase)
                && !context.TryGetLocal("event", out _))
            {
                return EventMember(context.Event, member.Member, member.Span);
            }

            Value target = Evaluate(member.Target, context);

            switch (target.Kind)
            {
                case ValueKind.Entity:
                    return EntityMember(target.Entity!, member.Member);

                case ValueKind.List:
                    return ListMember(target.AsEntities(), member.Member);

                case ValueKind.None:
                    // `target.dead` with no target is simply false; content need not guard it.
                    return Value.None;

                case ValueKind.Definition:
                {
                    EntityDefinition definition = target.Definition!;
                    string key = member.Member.ToLowerInvariant();
                    if (key == "name") return Value.FromText(definition.Name);
                    if (key == "kind") return Value.FromText(definition.KindName);
                    return Value.FromNumber(definition.Stats.TryGetValue(member.Member, out Num stat) ? stat : Num.Zero);
                }

                default:
                    throw new RuntimeError($"`{target}` has no property `{member.Member}`.", member.Span);
            }
        }

        internal Value EntityMember(Entity entity, string member)
        {
            string key = member.ToLowerInvariant();
            switch (key)
            {
                case "dead": return Value.FromBool(entity.IsDead || entity.IsRemoved);
                case "alive": return Value.FromBool(entity.IsAlive);
                case "removed": return Value.FromBool(entity.IsRemoved);
                case "name": return Value.FromText(entity.Name);
                case "id": return Value.FromNumber(Num.FromInt(entity.Id));
                case "owner": return Value.FromEntity(entity.Owner);
                case "source": return Value.FromEntity(entity.Source);
                case "controller": return Value.FromEntity(entity.Controller);
                case "team": return Value.FromText(entity.Team.ToString().ToLowerInvariant());
                case "zone": return Value.FromText(entity.Zone);
                case "position": return Value.FromNumber(Num.FromInt(entity.Position));
                case "intent": return entity.Intent == null ? Value.None : Value.FromText(entity.Intent);
                case "phase": return entity.Phase == null ? Value.None : Value.FromText(entity.Phase);
                case "statuses": return Value.FromEntities(entity.Attached.Where(a => !a.IsRemoved).ToList());
                case "kind": return Value.FromText(entity.Definition?.KindName ?? entity.Kind.ToString().ToLowerInvariant());
            }

            if (TryHistory(key, entity, out Value history)) return history;

            // `target.Poison` reads an attached status's counter: stacks, or duration for duration statuses.
            if (entity.FindAttached(member) != null || Content.FindAny(member, "status", "keyword") != null)
                return Value.FromNumber(Num.FromInt(entity.CounterOf(member)));

            return Value.FromNumber(entity.Get(member));
        }

        private static Value ListMember(IReadOnlyList<Entity> list, string member)
        {
            switch (member.ToLowerInvariant())
            {
                case "count":
                case "size":
                case "length":
                    return Value.FromNumber(Num.FromInt(list.Count));
                case "first": return Value.FromEntity(list.Count > 0 ? list[0] : null);
                case "last": return Value.FromEntity(list.Count > 0 ? list[list.Count - 1] : null);
                case "empty": return Value.FromBool(list.Count == 0);
                case "any": return Value.FromBool(list.Count > 0);
            }

            // Anything else sums the stat across the group: `enemies.hp` is their total hp.
            Num total = Num.Zero;
            foreach (Entity entity in list) total += entity.Get(member);
            return Value.FromNumber(total);
        }

        private static Value EventMember(GameEvent? gameEvent, string member, SourceSpan span)
        {
            if (gameEvent == null)
                throw new RuntimeError("`event` is only available inside an `on ...:` listener.", span);

            switch (member.ToLowerInvariant())
            {
                case "source": return Value.FromEntity(gameEvent.Source);
                case "target": return Value.FromEntity(gameEvent.Target);
                case "card": return Value.FromEntity(gameEvent.Card);
                case "amount": return Value.FromNumber(gameEvent.Amount);
                case "name": return Value.FromText(gameEvent.Name);
                case "cancelled": return Value.FromBool(gameEvent.Cancelled);
            }

            return gameEvent.Data.TryGetValue(member, out Value value) ? value : Value.None;
        }

        // Calls -----------------------------------------------------------------------------

        private Value EvaluateCall(CallExpr call, EvalContext context)
        {
            if (call.Receiver != null)
            {
                Value receiver = Evaluate(call.Receiver, context);
                switch (call.Name.ToLowerInvariant())
                {
                    case "has":
                        RequireArguments(call, 1);
                        Value predicate = Evaluate(call.Arguments[0], context);
                        return Value.FromBool(receiver.AsEntities().Any(e => Has(e, predicate, context)));

                    case "stacks":
                        RequireArguments(call, 1);
                        string status = NameOf(Evaluate(call.Arguments[0], context), call.Span);
                        return Value.FromNumber(Num.FromInt(receiver.AsEntities().Sum(e => e.StacksOf(status))));

                    default:
                        throw new RuntimeError($"Unknown method `{call.Name}`." + SuggestionText(call.Name, new[] { "has", "stacks" }), call.Span);
                }
            }

            string name = call.Name.ToLowerInvariant();
            switch (name)
            {
                case "min":
                case "max":
                {
                    if (call.Arguments.Count == 0) throw new RuntimeError($"`{name}` needs at least one argument.", call.Span);
                    var values = call.Arguments.Select(a => EvaluateNumber(a, context)).ToList();
                    return Value.FromNumber(name == "min" ? values.Aggregate(Num.Min) : values.Aggregate(Num.Max));
                }

                case "abs": RequireArguments(call, 1); return Value.FromNumber(Num.Abs(EvaluateNumber(call.Arguments[0], context)));
                case "floor": RequireArguments(call, 1); return Value.FromNumber(EvaluateNumber(call.Arguments[0], context).Floor());
                case "ceil": RequireArguments(call, 1); return Value.FromNumber(EvaluateNumber(call.Arguments[0], context).Ceiling());
                case "round": RequireArguments(call, 1); return Value.FromNumber(EvaluateNumber(call.Arguments[0], context).Round());

                case "clamp":
                    RequireArguments(call, 3);
                    return Value.FromNumber(Num.Clamp(
                        EvaluateNumber(call.Arguments[0], context),
                        EvaluateNumber(call.Arguments[1], context),
                        EvaluateNumber(call.Arguments[2], context)));

                case "count":
                    RequireArguments(call, 1);
                    return Value.FromNumber(Num.FromInt(Evaluate(call.Arguments[0], context).AsEntities().Count));

                case "random":
                    RequireArguments(call, 2);
                    return Value.FromNumber(Num.FromInt(State.Rng.NextInt(
                        EvaluateNumber(call.Arguments[0], context).ToInt(),
                        EvaluateNumber(call.Arguments[1], context).ToInt())));

                case "adjacent":
                {
                    RequireArguments(call, 1);
                    var result = new List<Entity>();
                    foreach (Entity center in Evaluate(call.Arguments[0], context).AsEntities())
                    {
                        foreach (Entity actor in State.Actors(center.Team))
                        {
                            if (actor != center && Math.Abs(actor.Position - center.Position) == 1 && !result.Contains(actor))
                                result.Add(actor);
                        }
                    }
                    return Value.FromEntities(result);
                }

                case "has":
                {
                    RequireArguments(call, 2);
                    Value predicate = Evaluate(call.Arguments[1], context);
                    return Value.FromBool(Evaluate(call.Arguments[0], context).AsEntities().Any(e => Has(e, predicate, context)));
                }

                case "stacks":
                {
                    if (call.Arguments.Count < 1) throw new RuntimeError("`stacks` needs a status name.", call.Span);
                    string status = NameOf(Evaluate(call.Arguments[0], context), call.Span);
                    IReadOnlyList<Entity> on = call.Arguments.Count > 1
                        ? Evaluate(call.Arguments[1], context).AsEntities()
                        : new[] { context.Target ?? context.Controller }.Where(e => e != null).ToList()!;
                    return Value.FromNumber(Num.FromInt(on.Sum(e => e.StacksOf(status))));
                }
            }

            var arguments = call.Arguments.Select(a => Evaluate(a, context)).ToList();
            if (Host.TryCall(call.Name, arguments, context, out Value hosted)) return hosted;

            if (name == "within")
                throw new RuntimeError("Spatial selectors like `within` need a host that implements them (IEffectHost.TryCall).", call.Span);

            throw new RuntimeError(
                $"Unknown function `{call.Name}`." +
                SuggestionText(call.Name, new[] { "min", "max", "abs", "floor", "ceil", "round", "clamp", "count", "random", "adjacent", "has", "stacks" }),
                call.Span);
        }

        private static void RequireArguments(CallExpr call, int count)
        {
            if (call.Arguments.Count != count)
                throw new RuntimeError($"`{call.Name}` takes {count} argument{(count == 1 ? "" : "s")}, not {call.Arguments.Count}.", call.Span);
        }

        private static string NameOf(Value value, SourceSpan span) => value.Kind switch
        {
            ValueKind.Definition => value.Definition!.Name,
            ValueKind.Text => value.Text!,
            ValueKind.Entity => value.Entity!.Name,
            ValueKind.Qualified => value.Qualified!.Name,
            _ => throw new RuntimeError($"Expected a name but got {value}.", span),
        };

        /// <summary>True when an entity has a tag, a status, or a keyword, directly or via an attachment.</summary>
        internal bool Has(Entity entity, Value predicate, EvalContext context)
        {
            switch (predicate.Kind)
            {
                case ValueKind.Qualified:
                {
                    QualifiedName q = predicate.Qualified!;
                    if (q.Qualifier == "tag" || q.Qualifier == "keyword")
                        return HasTagDeep(entity, q.Name);
                    if (q.Qualifier == "status")
                        return entity.FindAttached(q.Name) != null;
                    EvalContext probe = context.Derive();
                    probe.It = entity;
                    probe.ItIsFocus = true;
                    return TestQualified(q, probe);
                }

                case ValueKind.Definition:
                    return entity.FindAttached(predicate.Definition!.Name) != null || entity.HasTag(predicate.Definition!.Name);

                case ValueKind.Text:
                    return HasTagDeep(entity, predicate.Text!) || entity.FindAttached(predicate.Text!) != null;

                case ValueKind.Entity:
                    return entity == predicate.Entity || entity.Attached.Contains(predicate.Entity!);

                default:
                    return false;
            }
        }

        private static bool HasTagDeep(Entity entity, string tag)
        {
            if (entity.HasTag(tag)) return true;
            foreach (Entity attached in entity.Attached)
            {
                if (!attached.IsRemoved && (attached.HasTag(tag) || string.Equals(attached.Name, tag, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        // Operators -------------------------------------------------------------------------

        private Value EvaluateBinary(BinaryExpr binary, EvalContext context)
        {
            switch (binary.Operator)
            {
                case BinaryOperator.And:
                    return Value.FromBool(EvaluateCondition(binary.Left, context) && EvaluateCondition(binary.Right, context));

                case BinaryOperator.Or:
                    return Value.FromBool(EvaluateCondition(binary.Left, context) || EvaluateCondition(binary.Right, context));

                case BinaryOperator.Equal:
                    return Value.FromBool(AreEqual(Evaluate(binary.Left, context), Evaluate(binary.Right, context), binary.Span));

                case BinaryOperator.NotEqual:
                    return Value.FromBool(!AreEqual(Evaluate(binary.Left, context), Evaluate(binary.Right, context), binary.Span));

                case BinaryOperator.Less:
                    return Value.FromBool(EvaluateNumber(binary.Left, context) < EvaluateNumber(binary.Right, context));
                case BinaryOperator.LessOrEqual:
                    return Value.FromBool(EvaluateNumber(binary.Left, context) <= EvaluateNumber(binary.Right, context));
                case BinaryOperator.Greater:
                    return Value.FromBool(EvaluateNumber(binary.Left, context) > EvaluateNumber(binary.Right, context));
                case BinaryOperator.GreaterOrEqual:
                    return Value.FromBool(EvaluateNumber(binary.Left, context) >= EvaluateNumber(binary.Right, context));

                case BinaryOperator.Add:
                {
                    Value left = Evaluate(binary.Left, context);
                    Value right = Evaluate(binary.Right, context);
                    if (left.Kind == ValueKind.Text || right.Kind == ValueKind.Text)
                        return Value.FromText(Display(left) + Display(right));
                    return Value.FromNumber(ToNumber(left, binary.Span) + ToNumber(right, binary.Span), left.Unit ?? right.Unit);
                }

                case BinaryOperator.Subtract:
                {
                    Value left = Evaluate(binary.Left, context);
                    return Value.FromNumber(ToNumber(left, binary.Span) - EvaluateNumber(binary.Right, context), left.Unit);
                }

                case BinaryOperator.Multiply:
                    return Value.FromNumber(Scalar(Evaluate(binary.Left, context), binary.Span) * Scalar(Evaluate(binary.Right, context), binary.Span));

                case BinaryOperator.Divide:
                    return Value.FromNumber(Scalar(Evaluate(binary.Left, context), binary.Span) / Scalar(Evaluate(binary.Right, context), binary.Span));

                case BinaryOperator.Modulo:
                {
                    Num left = EvaluateNumber(binary.Left, context);
                    Num right = EvaluateNumber(binary.Right, context);
                    return Value.FromNumber(right.IsZero ? Num.Zero : Num.FromRaw(left.Raw % right.Raw));
                }

                case BinaryOperator.Per:
                {
                    // `2 per Poison on target`: the right side is a count.
                    Value left = Evaluate(binary.Left, context);
                    return Value.FromNumber(ToNumber(left, binary.Span) * CountOf(Evaluate(binary.Right, context), context, binary.Span), left.Unit);
                }

                case BinaryOperator.On:
                    return Value.FromNumber(CountOn(Evaluate(binary.Left, context), Evaluate(binary.Right, context).AsEntities(), context, binary.Span));

                case BinaryOperator.In:
                {
                    Value left = Evaluate(binary.Left, context);
                    IReadOnlyList<Entity> right = Evaluate(binary.Right, context).AsEntities();
                    if (left.Kind == ValueKind.Entity) return Value.FromBool(right.Contains(left.Entity!));
                    // `cards in hand`: the intersection, in the right-hand zone's order.
                    var set = new HashSet<Entity>(left.AsEntities());
                    return Value.FromEntities(right.Where(set.Contains).ToList());
                }

                case BinaryOperator.Has:
                {
                    Value predicate = Evaluate(binary.Right, context);
                    return Value.FromBool(Evaluate(binary.Left, context).AsEntities().Any(e => Has(e, predicate, context)));
                }

                default:
                    throw new RuntimeError($"Unsupported operator {binary.Operator}.", binary.Span);
            }
        }

        /// <summary>Numbers for multiplication: percentages become fractions, so <c>10 * 50%</c> is 5.</summary>
        private Num Scalar(Value value, SourceSpan span)
        {
            Num number = ToNumber(value, span);
            return value.Kind == ValueKind.Number && value.Unit == "%" ? Num.Percent(number) : number;
        }

        private Num CountOf(Value value, EvalContext context, SourceSpan span) => value.Kind switch
        {
            ValueKind.List => Num.FromInt(value.AsEntities().Count),
            ValueKind.Entity => Num.One,
            ValueKind.Definition => Num.FromInt(StacksOnDefault(value.Definition!.Name, context)),
            _ => ToNumber(value, span),
        };

        private int StacksOnDefault(string status, EvalContext context)
        {
            Entity? subject = context.Target ?? context.Controller;
            return subject?.CounterOf(status) ?? 0;
        }

        /// <summary><c>Poison on target</c> is total stacks; <c>tag:x on targets</c> counts matches.</summary>
        private Num CountOn(Value left, IReadOnlyList<Entity> entities, EvalContext context, SourceSpan span)
        {
            switch (left.Kind)
            {
                case ValueKind.Definition:
                    return Num.FromInt(entities.Sum(e => e.CounterOf(left.Definition!.Name)));
                case ValueKind.Text:
                    return Num.FromInt(entities.Sum(e => e.CounterOf(left.Text!)));
                case ValueKind.Qualified:
                    return Num.FromInt(entities.Count(e => Has(e, left, context)));
                default:
                    throw new RuntimeError($"`on` expects a status or tag on its left, not {left}.", span);
            }
        }

        private bool AreEqual(Value left, Value right, SourceSpan span)
        {
            if (left.Kind == ValueKind.None || right.Kind == ValueKind.None)
                return left.Kind == right.Kind || (!left.AsBool() && !right.AsBool() && left.Kind != ValueKind.Number && right.Kind != ValueKind.Number);

            if (left.Kind == ValueKind.Entity && right.Kind == ValueKind.Entity) return left.Entity == right.Entity;
            if (left.Kind == ValueKind.Entity && right.Kind == ValueKind.Definition) return left.Entity!.Definition == right.Definition;
            if (left.Kind == ValueKind.Definition && right.Kind == ValueKind.Entity) return right.Entity!.Definition == left.Definition;
            if (left.Kind == ValueKind.Definition && right.Kind == ValueKind.Definition) return left.Definition == right.Definition;

            if (left.Kind == ValueKind.Text || right.Kind == ValueKind.Text)
                return string.Equals(Display(left), Display(right), StringComparison.OrdinalIgnoreCase);

            return ToNumber(left, span) == ToNumber(right, span);
        }

        private static string Display(Value value) => value.Kind switch
        {
            ValueKind.Text => value.Text!,
            ValueKind.Entity => value.Entity!.Name,
            ValueKind.Definition => value.Definition!.Name,
            _ => value.ToString(),
        };

        // Selectors -------------------------------------------------------------------------

        private Value EvaluateSelector(SelectorExpr selector, EvalContext context)
        {
            IReadOnlyList<Entity> source = Evaluate(selector.Source, context).AsEntities();

            switch (selector.Modifier)
            {
                case SelectorModifier.All:
                    return Value.FromEntities(source);

                case SelectorModifier.Random:
                {
                    int count = selector.Count == null ? 1 : EvaluateNumber(selector.Count, context).ToInt();
                    var pool = source.ToList();
                    State.Rng.Shuffle(pool);
                    return Value.FromEntities(pool.Take(Math.Max(0, count)).ToList());
                }

                case SelectorModifier.Lowest:
                case SelectorModifier.Highest:
                {
                    string key = selector.Key ?? "hp";
                    int count = selector.Count == null ? 1 : EvaluateNumber(selector.Count, context).ToInt();
                    // Ties break on position, then id, so the choice is always deterministic.
                    IOrderedEnumerable<Entity> ordered = selector.Modifier == SelectorModifier.Lowest
                        ? source.OrderBy(e => ReadSortKey(e, key).Raw)
                        : source.OrderByDescending(e => ReadSortKey(e, key).Raw);
                    return Value.FromEntities(ordered.ThenBy(e => e.Position).ThenBy(e => e.Id).Take(count).ToList());
                }

                case SelectorModifier.Other:
                {
                    Entity? excluded = context.Target != null && source.Contains(context.Target) ? context.Target : context.Self?.Controller;
                    return Value.FromEntities(source.Where(e => e != excluded && e != context.Self).ToList());
                }

                default:
                    return Value.FromEntities(source);
            }
        }

        private Num ReadSortKey(Entity entity, string key)
        {
            Value value = EntityMember(entity, key);
            return value.Kind == ValueKind.Number || value.Kind == ValueKind.Bool ? value.Number : Num.Zero;
        }

        private List<Entity> FilterWhere(IReadOnlyList<Entity> source, ExprNode predicate, EvalContext context)
        {
            var result = new List<Entity>();
            EvalContext probe = context.Derive();
            probe.ItIsFocus = true;
            probe.ItDefinition = null;

            foreach (Entity candidate in source)
            {
                probe.It = candidate;
                if (EvaluateCondition(predicate, probe)) result.Add(candidate);
            }
            return result;
        }

        /// <summary>
        /// The same filter over definitions rather than live entities, for <c>discover</c>. Clears
        /// <see cref="EvalContext.It"/> so a definition candidate can never be mistaken for one in
        /// play, which is what keeps the qualifier tests honest.
        /// </summary>
        internal List<EntityDefinition> FilterWhereDefinitions(IReadOnlyList<EntityDefinition> source, ExprNode predicate, EvalContext context)
        {
            var result = new List<EntityDefinition>();
            EvalContext probe = context.Derive();
            probe.It = null;
            probe.ItIsFocus = false;

            foreach (EntityDefinition candidate in source)
            {
                probe.ItDefinition = candidate;
                if (EvaluateCondition(predicate, probe)) result.Add(candidate);
            }
            return result;
        }

        // Qualifier tests -------------------------------------------------------------------

        /// <summary>
        /// Tests <c>tag:fire</c>, <c>source:self</c> and friends against whatever is in focus: the
        /// candidate inside <c>where</c>, else the value a modifier is computing, else the event.
        /// </summary>
        internal bool TestQualified(QualifiedName q, EvalContext context)
        {
            if (context.ItDefinition != null) return TestQualifiedDefinition(q, context.ItDefinition);

            switch (q.Qualifier)
            {
                case "tag":
                case "keyword":
                    return FocusTags(context).Any(tag => string.Equals(tag, q.Name, StringComparison.OrdinalIgnoreCase));

                case "status":
                {
                    Entity? subject = FocusSubject(context);
                    if (subject == null) return false;
                    return subject.FindAttached(q.Name) != null || (subject.Kind == EntityKind.Status && string.Equals(subject.Name, q.Name, StringComparison.OrdinalIgnoreCase));
                }

                case "source":
                    return MatchesRole(FocusSource(context), q.Name, context);

                case "target":
                    return MatchesRole(FocusSubject(context), q.Name, context);

                case "name":
                case "card":
                {
                    Entity? subject = q.Qualifier == "card" ? (context.Focus?.Card ?? context.Event?.Card ?? context.It) : FocusSubject(context);
                    return subject != null && string.Equals(subject.Name, q.Name, StringComparison.OrdinalIgnoreCase);
                }

                case "zone":
                    return FocusSubject(context)?.Zone.Equals(q.Name, StringComparison.OrdinalIgnoreCase) == true;

                case "team":
                    return string.Equals(FocusSubject(context)?.Team.ToString(), q.Name, StringComparison.OrdinalIgnoreCase);

                case "kind":
                case "type":
                {
                    Entity? subject = FocusSubject(context);
                    if (subject == null) return false;
                    string kind = subject.Definition?.KindName ?? subject.Kind.ToString();
                    return string.Equals(kind, q.Name, StringComparison.OrdinalIgnoreCase) || subject.HasTag(q.Name);
                }

                case "rarity":
                    return string.Equals(FocusSubject(context)?.Definition?.Word("rarity"), q.Name, StringComparison.OrdinalIgnoreCase);

                case "id":
                    return FocusSubject(context)?.Id.ToString() == q.Name;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Qualifiers against a definition, inside a <c>where</c> over content rather than over the
        /// board. Only what a definition can actually answer is honoured; anything about being in
        /// play is an error rather than a silent false, so content is told the difference instead of
        /// quietly matching nothing.
        /// </summary>
        private static bool TestQualifiedDefinition(QualifiedName q, EntityDefinition candidate)
        {
            switch (q.Qualifier)
            {
                case "tag":
                case "keyword":
                    return candidate.Tags.Any(tag => string.Equals(tag, q.Name, StringComparison.OrdinalIgnoreCase));

                case "kind":
                case "type":
                    return string.Equals(candidate.KindName, q.Name, StringComparison.OrdinalIgnoreCase)
                        || candidate.Tags.Any(tag => string.Equals(tag, q.Name, StringComparison.OrdinalIgnoreCase));

                case "name":
                    return string.Equals(candidate.Name, q.Name, StringComparison.OrdinalIgnoreCase);

                case "rarity":
                    return string.Equals(candidate.Word("rarity"), q.Name, StringComparison.OrdinalIgnoreCase);

                default:
                    throw new RuntimeError(
                        $"`{q.Qualifier}:` asks about something in play, and `{candidate.Name}` is content nothing has been made from yet. " +
                        "Filter a discovery by `tag:`, `kind:`, `name:`, `rarity:`, or a printed property such as `it.cost`.",
                        SourceSpan.None);
            }
        }

        private static IEnumerable<string> FocusTags(EvalContext context)
        {
            if (context.ItIsFocus && context.It != null)
            {
                foreach (string tag in context.It.Tags) yield return tag;
                foreach (Entity attached in context.It.Attached)
                {
                    if (attached.IsRemoved) continue;
                    yield return attached.Name;
                    foreach (string tag in attached.Tags) yield return tag;
                }
                yield break;
            }

            if (context.Focus != null)
            {
                foreach (string tag in context.Focus.Tags) yield return tag;
                if (context.Focus.Card != null) foreach (string tag in context.Focus.Card.Tags) yield return tag;
                if (context.Focus.Tags.Count == 0 && context.Focus.Card == null && context.Focus.Subject != null)
                    foreach (string tag in context.Focus.Subject.Tags) yield return tag;
                yield break;
            }

            if (context.Event != null)
            {
                foreach (string tag in context.Event.Tags) yield return tag;
                if (context.Event.Card != null) foreach (string tag in context.Event.Card.Tags) yield return tag;
                yield break;
            }

            Entity? fallback = context.It ?? context.Self;
            if (fallback != null) foreach (string tag in fallback.Tags) yield return tag;
        }

        private static Entity? FocusSubject(EvalContext context)
        {
            if (context.ItIsFocus && context.It != null) return context.It;
            if (context.Focus != null) return context.Focus.Subject;
            if (context.Event != null) return context.Event.Target;
            return context.It ?? context.Target;
        }

        private static Entity? FocusSource(EvalContext context)
        {
            if (context.ItIsFocus && context.It != null) return context.It.Source ?? context.It.Controller;
            if (context.Focus != null) return context.Focus.Source;
            if (context.Event != null) return context.Event.Source;
            return context.Source;
        }

        /// <summary>
        /// Does <paramref name="actual"/> fill the role named by a word such as <c>self</c>,
        /// <c>owner</c>, <c>player</c> or <c>enemy</c>? Roles compare by controller, so a card's
        /// damage counts as coming from the actor who played it.
        /// </summary>
        private bool MatchesRole(Entity? actual, string role, EvalContext context)
        {
            if (actual == null) return false;

            switch (role.ToLowerInvariant())
            {
                case "any":
                    return true;
                case "self":
                    return context.Self != null && (actual == context.Self || actual.Controller == context.Self.Controller);
                case "owner":
                {
                    Entity? owner = context.Self?.Owner ?? context.Self;
                    return owner != null && (actual == owner || actual.Controller == owner.Controller);
                }
                case "player":
                    return State.Player != null && actual.Controller == State.Player;
                case "enemy":
                case "enemies":
                    return actual.Controller.Team == Opposing(Perspective(context));
                case "ally":
                case "allies":
                    return actual.Controller.Team == Perspective(context);
                case "target":
                    return context.Target != null && actual == context.Target;
                default:
                    return string.Equals(actual.Name, role, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Teams -----------------------------------------------------------------------------

        /// <summary>
        /// The side an effect is running for, which is what <c>enemies</c>, <c>allies</c> and the
        /// matching roles are relative to. Inside a modifier that is the modifier owner's side, not
        /// the side of whoever is dealing the damage being modified: a relic that reduces damage
        /// taken <c>where source:enemies</c> must still mean the player's enemies when an enemy is
        /// the source. Neutral content is treated as the player's.
        /// </summary>
        internal static Team Perspective(EvalContext context)
        {
            Entity? anchor = context.Focus != null
                ? context.Self ?? context.Source
                : context.Source ?? context.Self;
            Team team = anchor?.Controller.Team ?? Team.Player;
            return team == Team.Neutral ? Team.Player : team;
        }

        internal static Team Opposing(Team team) => team == Team.Enemy ? Team.Player : Team.Enemy;
    }
}
