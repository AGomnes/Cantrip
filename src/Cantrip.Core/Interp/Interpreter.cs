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

        /// <summary>
        /// The interpreter running this verb, which is where a C# verb reaches the primitives
        /// (<c>ChangeStat</c>, <see cref="Interpreter.Heal"/>, <see cref="Interpreter.Raise"/>) that do
        /// the same bookkeeping content's own verbs do.
        /// </summary>
        public Interpreter Interpreter { get; }

        /// <summary>The parsed call, for a verb that needs more than the arguments and clauses this class offers.</summary>
        public CommandNode Node { get; }

        /// <summary>
        /// Who is acting and on whom. Pass it on to any primitive called from here, or the effects this
        /// verb causes will be attributed to nobody.
        /// </summary>
        public EvalContext Context { get; }

        /// <summary>The game this is running in, for a verb that needs to look something up.</summary>
        public GameState State => Interpreter.State;

        /// <summary>Where this call is written, for a diagnostic or a trace entry. <see cref="Error"/> attaches it for you.</summary>
        public SourceSpan Span => Node.Span;

        /// <summary>The name this was called by, as content wrote it. A handler registered for two names can tell which one was used.</summary>
        public string Verb => Node.Verb;

        /// <summary>How many positional arguments were written. Nothing checks it, so a verb that needs one has to say so itself.</summary>
        public int ArgumentCount => Node.Arguments.Count;

        /// <summary>
        /// The unevaluated argument, or null when it was not written. It is what to print in a message
        /// about an argument, since the value alone does not say how it was spelled.
        /// </summary>
        public ExprNode? ArgumentNode(int index) => index < Node.Arguments.Count ? Node.Arguments[index] : null;

        /// <summary>
        /// Evaluates a positional argument, or <see cref="Value.None"/> when it was not written.
        /// Arguments are evaluated on demand, so reading one twice runs it twice, including any roll in it.
        /// </summary>
        public Value Argument(int index)
        {
            ExprNode? node = ArgumentNode(index);
            return node == null ? Value.None : Interpreter.Evaluate(node, Context);
        }

        /// <summary>
        /// A numeric argument, or <paramref name="fallback"/> when it was not written. It accepts a bare
        /// percentage; <see cref="Amount"/> is the one that refuses one, and is what the built-in verbs
        /// use for counts.
        /// </summary>
        public Num Number(int index, Num fallback)
        {
            ExprNode? node = ArgumentNode(index);
            return node == null ? fallback : Interpreter.EvaluateNumber(node, Context);
        }

        /// <summary>
        /// A number argument that is not a percentage: stacks, cards, damage, block. The built-in
        /// verbs read their numbers through this.
        /// </summary>
        /// <remarks>
        /// A bare percentage means nothing to any of them, and the unit used to be dropped: <c>apply
        /// Slow 40%</c> applied forty stacks, and the card's generated text said "Apply 40% Slow",
        /// a percentage stated to the player that the engine does not implement. A percentage that
        /// is part of a sum is already a fraction by the time it arrives (<c>target.max_hp * 50%</c>
        /// is a number with no unit), so only one written on its own is refused.
        /// </remarks>
        public Num Amount(int index, Num fallback)
        {
            ExprNode? node = ArgumentNode(index);
            if (node == null) return fallback;

            Value value = Interpreter.Evaluate(node, Context);
            if (value.Kind == ValueKind.Number && value.Unit == "%")
                throw Error(PercentageMessage(Verb, AstPrinter.Print(node), value.Number));

            return Interpreter.ToNumber(value, node.Span);
        }

        /// <summary>Why a bare percentage is refused, worded once for the linter and the runtime.</summary>
        internal static string PercentageMessage(string verb, string written, Num percent) =>
            $"`{written}` is a percentage, and `{verb.ToLowerInvariant()}` counts in whole things, not in per cent. " +
            $"It read as {percent}, while the generated text said `{written}`: a percentage stated to the player that nothing implements. " +
            $"Write the number (`{percent}`), or a share of something: `target.max_hp * {written}`.";

        /// <summary>
        /// Whether a named clause was written, without evaluating it. It also answers true for a bare
        /// flag of that name, so it is "was this word written", not "was a value given for it".
        /// </summary>
        public bool HasClause(string keyword) => Node.HasFlag(keyword);

        /// <summary>
        /// Evaluates a named clause such as <c>to</c> or <c>from</c>, or <see cref="Value.None"/> when it
        /// was not written. A clause a verb never reads is silently dropped, which is what CT323 reports.
        /// </summary>
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

        /// <summary>
        /// Builds the error to throw from a verb, with the verb's name and this call's location already
        /// on it. Throw it: returning one does nothing.
        /// </summary>
        public RuntimeError Error(string message) => new RuntimeError($"`{Verb}`: {message}", Span);
    }

    /// <summary>
    /// The tree-walking interpreter. It evaluates expressions, executes statements,
    /// dispatches events and resolves the trigger queue. All randomness goes through
    /// <see cref="GameState.Rng"/> and all arithmetic through <see cref="Num"/>, so a run is fully
    /// determined by its seed and its inputs.
    /// </summary>
    public sealed partial class Interpreter : IModifierEvaluator
    {
        private readonly Dictionary<string, VerbHandler> _verbs = new Dictionary<string, VerbHandler>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _builtinVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The handler each built-in verb started with, so the clause check can tell the engine's own
        /// <c>deal</c> from a game's replacement for it by identity rather than by name.
        /// </summary>
        private readonly Dictionary<string, VerbHandler> _builtinHandlers = new Dictionary<string, VerbHandler>(StringComparer.OrdinalIgnoreCase);

        private int _steps;
        private int _callDepth;

        /// <summary>
        /// Builds the interpreter for a game and installs itself as the state's modifier evaluator, so
        /// a state without one computes every value unmodified. <c>CardRuntime</c> does this; a game
        /// builds one directly only when it drives the rules itself.
        /// </summary>
        /// <param name="state">The game to run. It is left as it is; nothing starts here.</param>
        /// <param name="host">The game's side of the integration. Null installs one that answers nothing, which is right for a simulation.</param>
        public Interpreter(GameState state, IEffectHost? host = null)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            Host = host ?? new EffectHostBase();
            State.Modifiers.Evaluator = this;
            RegisterBuiltinVerbs();
            _builtinVerbs.UnionWith(_verbs.Keys);
            foreach (KeyValuePair<string, VerbHandler> verb in _verbs) _builtinHandlers[verb.Key] = verb.Value;
        }

        /// <summary>The game this interpreter runs.</summary>
        public GameState State { get; }

        /// <summary>The library being played, which is the state's. Loading into it does not rebind live entities on its own.</summary>
        public ContentLibrary Content => State.Content;

        /// <summary>The rules this game started with, which is the state's.</summary>
        public Ruleset Rules => State.Rules;

        /// <summary>
        /// The game's side of the integration. Never null: a runtime given none gets one that answers
        /// nothing, so a call here needs no guard.
        /// </summary>
        public IEffectHost Host { get; }

        private IChoiceProvider _chooser = new FirstOptionChooser();

        /// <summary>Who answers a choice. Never null; setting it to null throws.</summary>
        public IChoiceProvider Chooser
        {
            get => _chooser;
            set => _chooser = value ?? throw new ArgumentNullException(nameof(value), "An interpreter always has a chooser; pass a FirstOptionChooser to keep the default.");
        }

        /// <summary>Adds or replaces a verb implemented in C#, as games do through <c>runtime.RegisterVerb</c>.</summary>
        public void RegisterVerb(string name, VerbHandler handler)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Verb name is required.", nameof(name));
            _verbs[name] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>
        /// Whether anything answers to this name: a built-in, a verb the game registered, or one content
        /// declares. It is the question the linter cannot answer on its own, which is why host verbs have
        /// to be named in <c>LintOptions.HostVerbs</c>.
        /// </summary>
        public bool IsVerb(string name) => _verbs.ContainsKey(name) || Content.FindVerb(name) != null;

        /// <summary>
        /// The handler currently registered for a verb, so a later registration can keep the earlier
        /// one and delegate to it. The test runner's <c>play</c> does exactly that: a <c>play</c> in a
        /// test's own body is the test's, and every other one is the rules'.
        /// </summary>
        internal bool TryGetVerb(string name, out VerbHandler handler) => _verbs.TryGetValue(name, out handler!);

        /// <summary>
        /// Registers a verb the runtime layer owns (<c>play</c>, <c>replay</c>, <c>use</c>) as one
        /// of the built-in ones, so that its clauses are checked like any other built-in verb's and
        /// a game that registers its own over the top is left alone.
        /// </summary>
        internal void RegisterRuntimeVerb(string name, VerbHandler handler)
        {
            RegisterVerb(name, handler);
            _builtinVerbs.Add(name);
            _builtinHandlers[name] = handler;
        }

        /// <summary>
        /// Every verb that can be called: built-ins, host verbs and content verbs, deduplicated without
        /// case. It is what a "did you mean?" and an editor's completion list are built from.
        /// </summary>
        public IEnumerable<string> VerbNames => _verbs.Keys.Concat(Content.Verbs.Select(v => v.Name)).Distinct(StringComparer.OrdinalIgnoreCase);

        internal bool IsBuiltinVerb(string name) => _builtinVerbs.Contains(name);

        /// <summary>The channel content adds its own target rules to: a taunt, a stealth, a reach limit.</summary>
        public const string TargetableChannel = "targetable";

        /// <summary>
        /// Whether one entity may be aimed at, over a base of 1: zero or less means "not this one".
        /// A card's target asks this, and so do an enemy's move and the <c>attack</c> verb, so that
        /// a taunt or a stealth means one thing wherever something is pointed at somebody.
        /// </summary>
        /// <remarks>
        /// Area and random effects still resolve through the interpreter's own selectors, and do not
        /// ask: a taunt constrains what something may be pointed at, not what a blast reaches. The
        /// query carries the action when there is one, so a <c>where</c> on the group must say
        /// <c>it.</c> to mean the candidate; a bare <c>tag:</c> tests the action.
        /// </remarks>
        /// <param name="candidate">Who is being pointed at.</param>
        /// <param name="source">Who is pointing, or null.</param>
        /// <param name="action">
        /// The card or ability being aimed, or null. Not <c>card</c>: an ability is aimed through
        /// here too, which is why <see cref="CardRuntime.LegalTargets(Entity)"/> is named as it is.
        /// </param>
        public bool IsTargetable(Entity candidate, Entity? source, Entity? action)
        {
            if (candidate == null) return false;
            if (!State.Modifiers.HasChannel(TargetableChannel)) return true;

            var query = new ModifierQuery(TargetableChannel)
            {
                Subject = candidate,
                Source = source,
                Action = action,
                Tags = action == null ? Array.Empty<string>() : action.Tags.ToArray(),
            };
            return State.Modifiers.Compute(query, Num.One) > Num.Zero;
        }

        /// <summary>Those of a group that may be aimed at, in the group's own order.</summary>
        /// <param name="candidates">Who is being pointed at.</param>
        /// <param name="source">Who is pointing, or null.</param>
        /// <param name="action">The card or ability being aimed, or null; see <see cref="IsTargetable"/>.</param>
        public IReadOnlyList<Entity> Targetable(IReadOnlyList<Entity> candidates, Entity? source, Entity? action)
        {
            if (candidates == null || candidates.Count == 0) return Array.Empty<Entity>();
            if (!State.Modifiers.HasChannel(TargetableChannel)) return candidates;

            var allowed = new List<Entity>();
            foreach (Entity candidate in candidates)
            {
                if (IsTargetable(candidate, source, action)) allowed.Add(candidate);
            }
            return allowed;
        }

        /// <summary>Resets the sandbox step counter. Called at the start of every top-level action.</summary>
        internal void ResetSteps() => _steps = 0;

        private void Step(SourceSpan span)
        {
            if (++_steps > Rules.MaxStepsPerAction)
                throw new RuntimeError($"Step limit of {Rules.MaxStepsPerAction} exceeded; is there an unbounded loop?", span);
        }

        // Statements ------------------------------------------------------------------------

        /// <summary>
        /// Runs a block of statements. It is how a game runs a block of its own that it read out of
        /// <c>EntityDefinition.Blocks</c>. Such a block has to be named in
        /// <c>LintOptions.HostBlocks</c>, or the linter reports it as a line that never runs (CT313).
        /// </summary>
        /// <remarks>
        /// It runs the statements and nothing else: the trigger queue is drained by whoever started the
        /// action, which is why content run this way from inside a host callback resolves at a different
        /// moment than content run through <c>CardRuntime.Execute(string, Entity, Entity)</c>.
        /// </remarks>
        /// <exception cref="RuntimeError">A statement failed, with the line it failed on.</exception>
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
                // A clause a built-in verb does not read used to be dropped in silence, so
                // `block 8 for 2 turns` gave ordinary block and `deal 5 against enemy2` hit whatever
                // the card was aimed at. Only the engine's own handler is held to the table: a game
                // that registers `deal` of its own reads what it likes.
                if (command.Clauses.Count != 0
                    && _builtinHandlers.TryGetValue(command.Verb, out VerbHandler? builtin)
                    && ReferenceEquals(builtin, handler))
                {
                    foreach (ClauseNode clause in BuiltinClauses.Unread(command))
                        throw new RuntimeError(BuiltinClauses.NotRead(command.Verb, clause.Keyword), clause.Span);
                }

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

                // Movement, which is a member write and not a verb: `target.rank = 0`,
                // `self.lane += 1`, `a.rank = b.rank`. It is checked before statuses and stats so
                // that a place always means a place, whatever a game has named a status.
                case MemberExpr member when IsPlaceAxis(member.Member):
                {
                    if (string.Equals(member.Member, "position", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new RuntimeError(
                            "`position` is the older name for `rank` and reads the same number, but it cannot be written: " +
                            "it names one axis of a place that now has two. Write `rank`.",
                            assign.Span);
                    }

                    bool lane = string.Equals(member.Member, "lane", StringComparison.OrdinalIgnoreCase);
                    foreach (Entity entity in Evaluate(member.Target, context).AsEntities().ToArray())
                        MoveOnBoard(entity, lane, assign.Operator, amount, context, assign.Span);
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

        /// <summary>Whether a member name is one of the two axes of a place on the board.</summary>
        private static bool IsPlaceAxis(string member) =>
            string.Equals(member, "lane", StringComparison.OrdinalIgnoreCase)
            || string.Equals(member, "rank", StringComparison.OrdinalIgnoreCase)
            || string.Equals(member, "position", StringComparison.OrdinalIgnoreCase);

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
            action.Bindings["card"] = Value.FromEntity(context.Action);
        }

        /// <summary>Runs a scheduled block with the actors it captured when it was scheduled.</summary>
        internal void RunScheduled(ScheduledAction action)
        {
            if (action.Body == null) return;

            var context = new EvalContext(action.Bindings["self"].Entity ?? action.Owner)
            {
                Source = action.Bindings["source"].Entity,
                Target = action.Bindings["target"].Entity,
                Action = action.Bindings["card"].Entity,
                Chain = NewChain(),
            };

            long traceId = State.Trace.Record(State.Clock.Now, "scheduled", "scheduled block", action.Owner.ToString(), span: action.Body.Span);
            using (State.Trace.Scope(traceId)) Execute(action.Body, context);
        }

        // Modifier evaluation ---------------------------------------------------------------
        //
        // Implemented explicitly: the pipeline calls these through IModifierEvaluator, and they are
        // no use to anyone else. Calling one from game code would evaluate a modifier outside the
        // pipeline that caches and orders it.

        bool IModifierEvaluator.Applies(Modifier modifier, ModifierQuery query)
        {
            EvalContext context = ModifierContext(modifier, query);

            if (modifier.Syntax.Scope != null)
            {
                if (InScope(modifier, modifier.Syntax.Scope, query, context) == null) return false;
            }
            else if (!InDefaultScope(modifier, query))
            {
                return false;
            }

            return modifier.Syntax.Filter == null || EvaluateCondition(modifier.Syntax.Filter, context);
        }

        /// <summary>
        /// The one in an <c>of ...</c> scope the value being computed belongs to, or null when the
        /// scope does not cover this value at all. The group is read from the modifier owner's side,
        /// so <c>of enemies</c> on the player's relic means the player's enemies whoever is acting.
        /// A <c>where</c> on the scope reads stats from the one that matched but tests qualifiers
        /// such as <c>source:self</c> against the value being computed, the same way a <c>where</c>
        /// on the modifier itself does.
        /// </summary>
        /// <remarks>
        /// Which end of an action the group is matched against is <see cref="ValueOwner"/>'s answer,
        /// and it is the end the bare form uses, so <c>of party</c> is a widening of
        /// <c>modify damage</c> rather than its opposite. It used to be the query's subject on every
        /// channel, which on <c>damage</c> is the one being hit: <c>modify damage of party: +2</c> on
        /// a relic then matched nothing the party did and said nothing about it.
        /// </remarks>
        private Entity? InScope(Modifier modifier, ExprNode scope, ModifierQuery query, EvalContext context)
        {
            if (scope is WhereExpr where)
            {
                Entity? matched = InScope(modifier, where.Source, query, context);
                if (matched == null) return null;

                EvalContext probe = context.Derive();
                probe.It = matched;
                probe.ItIsFocus = false;
                probe.Focus = query;
                return EvaluateCondition(where.Predicate, probe) ? matched : null;
            }

            Entity? owner = ValueOwner(query);
            Entity? printed = PrintedOn(query);
            if (owner == null && printed == null) return null;

            var groupContext = new EvalContext(modifier.Owner) { Source = modifier.Owner };
            IReadOnlyList<Entity> group = Evaluate(scope, groupContext).AsEntities().ToList();

            // The printed thing first, so `modify cost of cards where tag:fire` keeps naming the
            // cards rather than the actor holding them.
            if (printed != null && group.Contains(printed)) return printed;
            return owner != null && group.Contains(owner) ? owner : null;
        }

        /// <summary>
        /// The actor a value on this channel belongs to: who deals the damage, who takes it, whose
        /// card is being priced, whose ability is recharging, who is reaching. This is the end an
        /// <c>of</c> group is matched against, and the same end <see cref="InDefaultScope"/> uses
        /// when content names no group.
        /// </summary>
        internal static Entity? ValueOwner(ModifierQuery query) => query.Channel.ToLowerInvariant() switch
        {
            "damage" or "block" or "heal" or "draw" => query.Source,
            "cost" or "cooldown" or RangeChannel => query.Source,
            _ => query.Subject,
        };

        /// <summary>
        /// The card or ability a value is printed on, where that is something other than the actor
        /// it belongs to. An <c>of</c> group may name either, because a card is never an actor and
        /// so the two can never be confused for one another.
        /// </summary>
        private static Entity? PrintedOn(ModifierQuery query) => query.Channel.ToLowerInvariant() switch
        {
            "cost" or "cooldown" or RangeChannel => query.Subject,
            _ => null,
        };

        Value IModifierEvaluator.Amount(Modifier modifier, ModifierQuery query)
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
                Action = query.Action,
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
                    if (anchor.Kind == EntityKind.Card) return query.Action == anchor;
                    return query.Source != null && query.Source.Controller == anchor.Controller;

                case "damage_taken":
                case "heal_taken":
                case "block_taken":
                    return query.Subject != null && query.Subject.Controller == anchor.Controller;

                case "cost":
                    if (anchor.Kind == EntityKind.Card) return query.Subject == anchor;
                    return query.Subject != null && query.Subject.Controller == anchor.Controller;

                case RangeChannel:
                    // Reach belongs to whoever is reaching. Written on a card or an ability it
                    // governs that one; written on a status or a relic it governs everything its
                    // holder points at, which is the whole of "everything you do is melee now".
                    if (owner.Kind == EntityKind.Card || owner.Kind == EntityKind.Ability) return query.Subject == owner;
                    return query.Source != null && query.Source.Controller == anchor.Controller;

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
