using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Syntax;

namespace Cantrip.Runtime
{
    /// <summary>Implementation of a built-in or host-registered verb.</summary>
    public delegate void VerbHandler(VerbCall call);

    /// <summary>
    /// A verb invocation as seen by its implementation: lazy access to the positional arguments
    /// and named clauses written in content, plus the context it runs in.
    /// </summary>
    public sealed class VerbCall
    {
        internal VerbCall(Interpreter interpreter, CommandNode node, EvalContext context)
        {
            Interpreter = interpreter;
            Node = node;
            Context = context;
        }

        public Interpreter Interpreter { get; }
        public CommandNode Node { get; }
        public EvalContext Context { get; }
        public GameState State => Interpreter.State;
        public SourceSpan Span => Node.Span;
        public string Verb => Node.Verb;

        public int ArgumentCount => Node.Arguments.Count;

        public ExprNode? ArgumentNode(int index) => index < Node.Arguments.Count ? Node.Arguments[index] : null;

        public Value Argument(int index)
        {
            ExprNode? node = ArgumentNode(index);
            return node == null ? Value.None : Interpreter.Evaluate(node, Context);
        }

        public Num Number(int index, Num fallback)
        {
            ExprNode? node = ArgumentNode(index);
            return node == null ? fallback : Interpreter.EvaluateNumber(node, Context);
        }

        public bool HasClause(string keyword) => Node.HasFlag(keyword);

        public Value Clause(string keyword)
        {
            ExprNode? node = Node.Clause(keyword);
            return node == null ? Value.None : Interpreter.Evaluate(node, Context);
        }

        /// <summary>True for trailing flags such as <c>, ignore block</c> (stored as <c>ignore_block</c>).</summary>
        public bool Flag(string name) => Node.HasFlag(name);

        /// <summary>
        /// The entities a verb acts on: the given clause (usually <c>to</c>), or else the effect's
        /// target, or else <paramref name="fallback"/>.
        /// </summary>
        public IReadOnlyList<Entity> Targets(string clause = "to", Entity? fallback = null)
        {
            if (Node.Clause(clause) != null) return Clause(clause).AsEntities();
            if (Context.Target != null) return new[] { Context.Target };
            if (fallback != null) return new[] { fallback };
            return Array.Empty<Entity>();
        }

        public RuntimeError Error(string message) => new RuntimeError($"`{Verb}`: {message}", Span);
    }

    /// <summary>
    /// The tree-walking interpreter (section 4.5). It evaluates expressions, executes statements,
    /// dispatches events and resolves the trigger queue. All randomness goes through
    /// <see cref="GameState.Rng"/> and all arithmetic through <see cref="Num"/>, so a run is fully
    /// determined by its seed and its inputs.
    /// </summary>
    public sealed partial class Interpreter : IModifierEvaluator
    {
        private readonly Dictionary<string, VerbHandler> _verbs = new Dictionary<string, VerbHandler>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _builtinVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _steps;
        private int _callDepth;

        public Interpreter(GameState state, IEffectHost? host = null)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            Host = host ?? new EffectHostBase();
            State.Modifiers.Evaluator = this;
            RegisterBuiltinVerbs();
            _builtinVerbs.UnionWith(_verbs.Keys);
        }

        public GameState State { get; }
        public ContentLibrary Content => State.Content;
        public Ruleset Rules => State.Rules;
        public IEffectHost Host { get; }

        public IChoiceProvider Chooser { get; set; } = new FirstOptionChooser();

        /// <summary>Adds or replaces a verb implemented in C#, per <c>runtime.RegisterVerb</c> in section 4.7.</summary>
        public void RegisterVerb(string name, VerbHandler handler)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Verb name is required.", nameof(name));
            _verbs[name] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public bool IsVerb(string name) => _verbs.ContainsKey(name) || Content.FindVerb(name) != null;

        public IEnumerable<string> VerbNames => _verbs.Keys.Concat(Content.Verbs.Select(v => v.Name)).Distinct(StringComparer.OrdinalIgnoreCase);

        public bool IsBuiltinVerb(string name) => _builtinVerbs.Contains(name);

        /// <summary>Resets the sandbox step counter. Called at the start of every top-level action.</summary>
        internal void ResetSteps() => _steps = 0;

        private void Step(SourceSpan span)
        {
            if (++_steps > Rules.MaxStepsPerAction)
                throw new RuntimeError($"Step limit of {Rules.MaxStepsPerAction} exceeded; is there an unbounded loop?", span);
        }

        // Statements ------------------------------------------------------------------------

        public void Execute(BlockNode block, EvalContext context)
        {
            foreach (StatementNode statement in block.Statements) ExecuteStatement(statement, context);
        }

        private void ExecuteStatement(StatementNode statement, EvalContext context)
        {
            Step(statement.Span);

            switch (statement)
            {
                // A local lives in the context that bound it, which is a fresh one per listener run,
                // per card play and per move, so nothing leaks between invocations. It deliberately
                // outlives the `if` that bound it: blocks do not derive a scope of their own, and a
                // name that disappeared halfway down a body would be the surprising rule.
                case LetNode let:
                    context.SetLocal(let.Name, Evaluate(let.Value, context));
                    break;

                case CommandNode command:
                    ExecuteCommand(command, context);
                    break;

                case IfNode branch:
                    if (EvaluateCondition(branch.Condition, context)) Execute(branch.Then, context);
                    else if (branch.Else != null) Execute(branch.Else, context);
                    break;

                case RepeatNode repeat:
                {
                    int count = EvaluateNumber(repeat.Count, context).ToInt();
                    for (int i = 0; i < count; i++)
                    {
                        EvalContext iteration = context.Derive();
                        iteration.SetLocal("index", Value.FromNumber(Num.FromInt(i)));
                        Execute(repeat.Body, iteration);
                    }
                    break;
                }

                case ForEachNode loop:
                {
                    // Iterate over a snapshot, so effects that move or kill things mid-loop
                    // cannot skip or repeat elements.
                    Value source = Evaluate(loop.Source, context);
                    foreach (Entity item in source.AsEntities().ToArray())
                    {
                        if (item.IsRemoved) continue;
                        EvalContext iteration = context.Derive();
                        iteration.SetLocal(loop.Variable, Value.FromEntity(item));
                        Execute(loop.Body, iteration);
                    }
                    break;
                }

                case ChanceNode chance:
                {
                    Num percent = EvaluateNumber(chance.Probability, context);
                    if (State.Rng.Chance(percent)) Execute(chance.Body, context);
                    else if (chance.Else != null) Execute(chance.Else, context);
                    break;
                }

                case ScheduleNode schedule:
                    ExecuteSchedule(schedule, context);
                    break;

                case AssignNode assign:
                    ExecuteAssign(assign, context);
                    break;

                case LabeledBlockNode labeled:
                    Execute(labeled.Body, context);
                    break;

                default:
                    throw new RuntimeError($"Cannot execute {statement.GetType().Name}.", statement.Span);
            }
        }

        private void ExecuteCommand(CommandNode command, EvalContext context)
        {
            // Content verbs win, so a mod can redefine a macro such as `deal` for its own game.
            VerbDefinition? contentVerb = Content.FindVerb(command.Verb);
            if (contentVerb != null)
            {
                CallContentVerb(contentVerb, command, context);
                return;
            }

            if (_verbs.TryGetValue(command.Verb, out VerbHandler? handler))
            {
                if (State.Trace.Enabled)
                {
                    long id = State.Trace.Record(State.Clock.Now, "verb", command.Verb, context.Self?.ToString(), span: command.Span);
                    using (State.Trace.Scope(id)) handler(new VerbCall(this, command, context));
                }
                else
                {
                    handler(new VerbCall(this, command, context));
                }
                return;
            }

            throw new RuntimeError($"Unknown verb `{command.Verb}`." + SuggestionText(command.Verb, VerbNames), command.Span);
        }

        private void CallContentVerb(VerbDefinition verb, CommandNode command, EvalContext context)
        {
            EvalContext call = context.Derive();

            var values = command.Arguments.Select(a => Evaluate(a, context)).ToList();

            // `shatter to enemy` and `shatter enemy` both bind the first parameter.
            ExprNode? to = command.Clause("to");
            if (to != null && values.Count < verb.Parameters.Count) values.Add(Evaluate(to, context));

            for (int i = 0; i < verb.Parameters.Count; i++)
                call.SetLocal(verb.Parameters[i], i < values.Count ? values[i] : Value.None);

            if (_callDepth >= Rules.MaxCallDepth)
                throw new RuntimeError($"`{verb.Name}` is nested more than {Rules.MaxCallDepth} calls deep; is it calling itself forever?", command.Span);

            long traceId = State.Trace.Record(State.Clock.Now, "verb", verb.Name, context.Self?.ToString(), span: command.Span);
            _callDepth++;
            try
            {
                using (State.Trace.Scope(traceId)) Execute(verb.Body, call);
            }
            finally
            {
                _callDepth--;
            }
        }

        private void ExecuteAssign(AssignNode assign, EvalContext context)
        {
            Value raw = Evaluate(assign.Value, context);
            Num amount = ToNumber(raw, assign.Span);

            switch (assign.Target)
            {
                case NameExpr name when context.TryGetLocal(name.Name, out Value current):
                    context.SetLocal(name.Name, Value.FromNumber(Combine(ToNumber(current, assign.Span), assign.Operator, amount)));
                    return;

                case NameExpr name:
                {
                    Entity holder = StatHolder(name.Name, context)
                        ?? throw new RuntimeError($"Nothing here has a `{name.Name}` to change.", assign.Span);
                    ChangeStat(holder, name.Name, assign.Operator, amount, context, assign.Span);
                    return;
                }

                case MemberExpr member when member.Target is NameExpr { Name: var root }
                                         && string.Equals(root, "event", StringComparison.OrdinalIgnoreCase):
                {
                    GameEvent gameEvent = context.Event
                        ?? throw new RuntimeError("`event` is only available inside an `on ...:` listener.", assign.Span);
                    if (!string.Equals(member.Member, "amount", StringComparison.OrdinalIgnoreCase))
                        throw new RuntimeError("Only `event.amount` can be changed.", assign.Span);
                    gameEvent.Amount = Combine(gameEvent.Amount, assign.Operator, amount);
                    return;
                }

                case MemberExpr member:
                {
                    foreach (Entity entity in Evaluate(member.Target, context).AsEntities())
                    {
                        if (IsStatusName(member.Member, entity))
                            AdjustStatusStacks(entity, member.Member, assign.Operator, amount, context, assign.Span);
                        else
                            ChangeStat(entity, member.Member, assign.Operator, amount, context, assign.Span);
                    }
                    return;
                }

                default:
                    throw new RuntimeError("Only names and properties can be assigned to.", assign.Span);
            }
        }

        private bool IsStatusName(string name, Entity host) =>
            host.FindAttached(name) != null || Content.Find(name, "status") != null || Content.Find(name, "keyword") != null;

        internal static Num Combine(Num current, AssignOperator op, Num amount) => op switch
        {
            AssignOperator.Set => amount,
            AssignOperator.Add => current + amount,
            AssignOperator.Subtract => current - amount,
            AssignOperator.Multiply => current * amount,
            _ => amount,
        };

        /// <summary>
        /// Who a bare stat name refers to in an assignment: the nearest entity up the ownership
        /// chain that has it, else the controller. <c>stacks -1</c> in a status changes the status;
        /// <c>energy +1</c> in a card changes the player; <c>block +6</c> in a move changes the enemy.
        /// </summary>
        private static Entity? StatHolder(string stat, EvalContext context)
        {
            for (Entity? current = context.Self; current != null; current = current.Owner)
            {
                if (current.HasStat(stat)) return current;
                if (current.Kind == EntityKind.Actor) break;
            }
            return context.Controller ?? context.Self;
        }

        private void ExecuteSchedule(ScheduleNode schedule, EvalContext context)
        {
            Entity owner = context.Controller ?? context.Self
                ?? throw new RuntimeError("Scheduled blocks need an owner.", schedule.Span);

            switch (schedule.Kind)
            {
                case ScheduleKind.NextTurn:
                {
                    ScheduledAction action = State.Schedule(ScheduleTiming.NextTurn, owner, schedule.Body);
                    Capture(action, context);
                    break;
                }

                case ScheduleKind.After:
                {
                    Value delay = Evaluate(schedule.Delay!, context);
                    if (!State.Clock.TryConvert(ToNumber(delay, schedule.Span), delay.Unit, out long units))
                        throw new RuntimeError($"`{delay}` is not a duration this game's clock understands.", schedule.Span);

                    ScheduledAction action = State.Schedule(ScheduleTiming.AtTime, owner, schedule.Body);
                    action.DueAt = State.Clock.Now + Math.Max(1, units);
                    Capture(action, context);
                    break;
                }

                case ScheduleKind.Until:
                {
                    ScheduledAction action = State.Schedule(ScheduleTiming.Until, owner, null);
                    action.Deadline = schedule.Deadline ?? "turn_end";
                    EvalContext scoped = context.Derive();
                    scoped.UndoScope = action;
                    Execute(schedule.Body, scoped);
                    break;
                }
            }
        }

        private static void Capture(ScheduledAction action, EvalContext context)
        {
            action.Bindings["self"] = Value.FromEntity(context.Self);
            action.Bindings["source"] = Value.FromEntity(context.Source);
            action.Bindings["target"] = Value.FromEntity(context.Target);
            action.Bindings["card"] = Value.FromEntity(context.Card);
        }

        /// <summary>Runs a scheduled block with the actors it captured when it was scheduled.</summary>
        internal void RunScheduled(ScheduledAction action)
        {
            if (action.Body == null) return;

            var context = new EvalContext(action.Bindings["self"].Entity ?? action.Owner)
            {
                Source = action.Bindings["source"].Entity,
                Target = action.Bindings["target"].Entity,
                Card = action.Bindings["card"].Entity,
                Chain = NewChain(),
            };

            long traceId = State.Trace.Record(State.Clock.Now, "scheduled", "scheduled block", action.Owner.ToString(), span: action.Body.Span);
            using (State.Trace.Scope(traceId)) Execute(action.Body, context);
        }

        // Modifier evaluation ---------------------------------------------------------------

        public bool Applies(Modifier modifier, ModifierQuery query)
        {
            EvalContext context = ModifierContext(modifier, query);

            if (modifier.Syntax.Scope != null)
            {
                if (query.Subject == null || !InScope(modifier, modifier.Syntax.Scope, query, context)) return false;
            }
            else if (!InDefaultScope(modifier, query))
            {
                return false;
            }

            return modifier.Syntax.Filter == null || EvaluateCondition(modifier.Syntax.Filter, context);
        }

        /// <summary>
        /// Whether a query's subject belongs to an <c>of ...</c> scope. The group is read from the
        /// modifier owner's side, so <c>of enemies</c> on the player's relic means the player's
        /// enemies whoever is attacking. A <c>where</c> on the scope reads stats from the candidate
        /// but tests qualifiers such as <c>source:self</c> against the value being computed, the same
        /// way a <c>where</c> on the modifier itself does.
        /// </summary>
        private bool InScope(Modifier modifier, ExprNode scope, ModifierQuery query, EvalContext context)
        {
            if (scope is WhereExpr where)
            {
                if (!InScope(modifier, where.Source, query, context)) return false;

                EvalContext probe = context.Derive();
                probe.It = query.Subject;
                probe.ItIsFocus = false;
                probe.Focus = query;
                return EvaluateCondition(where.Predicate, probe);
            }

            var groupContext = new EvalContext(modifier.Owner) { Source = modifier.Owner };
            return Evaluate(scope, groupContext).AsEntities().Contains(query.Subject!);
        }

        public Value Amount(Modifier modifier, ModifierQuery query)
        {
            EvalContext context = ModifierContext(modifier, query);
            context.It = null;
            ExprNode amount = modifier.Syntax.Amount;
            // Ranges are only meaningful to the clamp layer, which reads them unrolled.
            if (amount is RangeExpr range)
                return Value.FromRange(EvaluateNumber(range.Low, context), EvaluateNumber(range.High, context));
            return Evaluate(amount, context);
        }

        private static EvalContext ModifierContext(Modifier modifier, ModifierQuery query) =>
            new EvalContext(modifier.Owner)
            {
                Source = query.Source,
                Target = query.Subject,
                Card = query.Card,
                It = query.Subject,
                Focus = query,
            };

        /// <summary>
        /// Where a modifier applies when content does not say, chosen to match what an author means:
        /// a status affects its host, a relic its holder, and <c>damage</c> means damage dealt by
        /// them while <c>damage_taken</c> means damage dealt to them.
        /// </summary>
        private static bool InDefaultScope(Modifier modifier, ModifierQuery query)
        {
            Entity owner = modifier.Owner;
            Entity anchor = owner.Kind switch
            {
                EntityKind.Status or EntityKind.Keyword or EntityKind.Ability => owner.Owner ?? owner,
                EntityKind.Relic or EntityKind.Item => owner.Controller,
                _ => owner,
            };

            if (owner.Kind == EntityKind.Global) return true;

            switch (query.Channel.ToLowerInvariant())
            {
                case "damage":
                case "block":
                case "heal":
                case "draw":
                    if (anchor.Kind == EntityKind.Card) return query.Card == anchor;
                    return query.Source != null && query.Source.Controller == anchor.Controller;

                case "damage_taken":
                case "heal_taken":
                case "block_taken":
                    return query.Subject != null && query.Subject.Controller == anchor.Controller;

                case "cost":
                    if (anchor.Kind == EntityKind.Card) return query.Subject == anchor;
                    return query.Subject != null && query.Subject.Controller == anchor.Controller;

                case "cooldown":
                    // The same division as `cost`: written on the ability it governs that ability,
                    // written on a relic or a status it governs every ability the holder has. Compared
                    // against the owner rather than the anchor, because an ability's anchor is its host.
                    if (owner.Kind == EntityKind.Ability) return query.Subject == owner;
                    return query.Subject != null && query.Subject.Controller == anchor.Controller;

                default:
                    return query.Subject == anchor;
            }
        }
    }
}
