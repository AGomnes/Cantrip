using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Cantrip.Syntax;

namespace Cantrip.Testing
{
    public sealed class DslTestResult
    {
        internal DslTestResult(TestDefinition test, bool passed, string? failure, SourceSpan failureSpan, string? trace)
        {
            Test = test;
            Passed = passed;
            Failure = failure;
            FailureSpan = failureSpan;
            Trace = trace;
        }

        public TestDefinition Test { get; }
        public string Name => Test.Name;
        public bool Passed { get; }
        public string? Failure { get; }
        public SourceSpan FailureSpan { get; }

        /// <summary>The causality tree, when the runner was asked to trace.</summary>
        public string? Trace { get; }

        public override string ToString() => Passed ? $"PASS {Name}" : $"FAIL {Name}: {Failure} ({FailureSpan})";
    }

    /// <summary>
    /// Runs <c>test</c> blocks. Each test gets a fresh runtime with a player
    /// (80 hp, 3 energy) and seed 1. Setup statements run first; the battle starts, without
    /// shuffling or drawing, at the first statement that is not setup.
    /// </summary>
    /// <example>
    /// <code>
    /// test "Fireball kills a 6 HP enemy"
    ///   setup: enemy hp 6
    ///   play Fireball on enemy
    ///   expect enemy.dead
    /// </code>
    /// </example>
    public sealed class DslTestRunner
    {
        private static readonly HashSet<string> SetupVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "enemy", "player", "hand", "deck", "discard_pile", "relic", "seed", "answer", "realtime", "grant",
        };

        private readonly ContentLibrary _content;

        public DslTestRunner(ContentLibrary content) => _content = content ?? throw new ArgumentNullException(nameof(content));

        /// <summary>Verbs that only exist inside <c>test</c> blocks.</summary>
        public static IReadOnlyCollection<string> TestVerbs { get; } =
            SetupSession.SetupVerbTable.Select(v => v.Name).Concat(Session.VerbTable.Select(v => v.Name)).ToArray();

        /// <summary>
        /// Record the causality trace for every test (slower; attached to failures). The trace also
        /// holds what <c>log</c> statements wrote, which a test otherwise shows nowhere.
        /// </summary>
        public bool Trace { get; set; }

        /// <summary>
        /// Makes the host for each test's runtime, for content that uses names or functions the game
        /// answers in C#: <c>runner.CreateHost = () => new GameHost();</c>. It is called once per
        /// test, so each test starts with a host of its own. Null, the default, gives each test a
        /// host that adds nothing.
        /// </summary>
        public Func<IEffectHost>? CreateHost { get; set; }

        /// <summary>
        /// Runs on each test's new runtime before the test's first line, for the verbs the game
        /// registers in C#:
        /// <c>runner.ConfigureRuntime = runtime => runtime.RegisterVerb("corrupt", Corrupt);</c>.
        /// The runtime has no player yet. If this creates one, the test uses it; otherwise the test
        /// creates one with 80 hp and 3 energy. The test verbs are registered afterwards, so they win
        /// over a verb of the same name. <c>play</c> is the exception, because it is a rule verb too:
        /// the test's wins only for the <c>play</c> statements in the test's own body, and every other
        /// one goes to whatever was registered before — the rules', or the game's. An exception from
        /// this fails that test.
        /// </summary>
        public Action<CardRuntime>? ConfigureRuntime { get; set; }

        public IReadOnlyList<DslTestResult> RunAll(string? nameFilter = null) =>
            _content.Tests
                .Where(t => nameFilter == null || t.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(Run)
                .ToList();

        public DslTestResult Run(TestDefinition test)
        {
            BlockNode body = test.Syntax.Body;
            var chooser = new ScriptedChooser();
            var options = new RuntimeOptions
            {
                Seed = 1,
                Trace = Trace,
                Chooser = chooser,
                Clock = FindRealtime(body) is int ticks ? new TickClock(ticks) : null,
            };

            CardRuntime runtime;
            try
            {
                options.Host = CreateHost?.Invoke();
                runtime = new CardRuntime(_content, options);
            }
            catch (Exception e)
            {
                return new DslTestResult(test, false, "could not create runtime: " + Plain(e), test.Syntax.Span, null);
            }

            try
            {
                ConfigureRuntime?.Invoke(runtime);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                return new DslTestResult(test, false, "could not configure the runtime: " + Plain(e), test.Syntax.Span, null);
            }

            // `log` writes to an event nothing in a test listens to. Traced, its message goes into the
            // trace as a `[log]` line under the `log` statement that wrote it.
            if (Trace)
                runtime.Interpreter.Logged += message => runtime.State.Trace.Record(runtime.State.Clock.Now, "log", message);

            var session = new Session(runtime, chooser, body);
            SourceSpan at = test.Syntax.Span;

            try
            {
                foreach (StatementNode statement in body.Statements)
                {
                    at = statement.Span;
                    if (!session.Started && !IsSetup(statement)) session.Start();
                    session.Run(statement);
                }
                return new DslTestResult(test, true, null, SourceSpan.None, Trace ? runtime.State.Trace.FormatTree() : null);
            }
            catch (SetupFailure failure)
            {
                return new DslTestResult(test, false, failure.Message, failure.Span, Trace ? runtime.State.Trace.FormatTree() : null);
            }
            catch (RuntimeError error)
            {
                return new DslTestResult(test, false, "runtime error: " + error.Detail, error.Span, Trace ? runtime.State.Trace.FormatTree() : null);
            }
            catch (DslException error)
            {
                return new DslTestResult(test, false, error.Message, error.Diagnostics.FirstOrDefault()?.Span ?? test.Syntax.Span, null);
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
            {
                // The runtime API's own complaints, and a game verb's, fail this test only, instead
                // of aborting the whole run.
                return new DslTestResult(test, false, Plain(error), at, Trace ? runtime.State.Trace.FormatTree() : null);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                return new DslTestResult(test, false, $"internal error ({error.GetType().Name}): {Plain(error)}", at, Trace ? runtime.State.Trace.FormatTree() : null);
            }
        }

        /// <summary>
        /// An exception's message without the <c>(Parameter 'name')</c> that .NET adds to an
        /// <see cref="ArgumentException"/>, which means nothing to someone writing content.
        /// </summary>
        private static string Plain(Exception error)
        {
            string message = error.Message;
            if (error is ArgumentException { ParamName: string parameter })
            {
                string suffix = $" (Parameter '{parameter}')";
                if (message.EndsWith(suffix, StringComparison.Ordinal)) message = message.Substring(0, message.Length - suffix.Length);
            }
            return message;
        }

        private static bool IsSetup(StatementNode statement) => statement switch
        {
            LabeledBlockNode labeled => labeled.Label == "setup",
            CommandNode command => SetupVerbs.Contains(command.Verb),
            _ => false,
        };

        private static int? FindRealtime(BlockNode body)
        {
            foreach (StatementNode statement in body.Statements)
            {
                IEnumerable<StatementNode> candidates = statement is LabeledBlockNode labeled ? labeled.Body.Statements : new[] { statement };
                foreach (StatementNode candidate in candidates)
                {
                    if (candidate is CommandNode { Verb: var verb } command && string.Equals(verb, "realtime", StringComparison.OrdinalIgnoreCase))
                        return command.Arguments.FirstOrDefault() is NumberExpr number ? number.Value.ToInt() : 60;
                }
            }
            return null;
        }

        /// <summary>
        /// Every <c>play</c> statement written in a test's own body. Collected once per test, so the
        /// test's verb and the rules' can be told apart by <em>where the line is written</em>.
        /// </summary>
        private sealed class OwnPlays : AstWalker
        {
            public HashSet<CommandNode> Statements { get; } = new HashSet<CommandNode>();

            public override void VisitStatement(StatementNode statement)
            {
                if (statement is CommandNode command && string.Equals(command.Verb, "play", StringComparison.OrdinalIgnoreCase))
                    Statements.Add(command);
                base.VisitStatement(statement);
            }
        }

        /// <summary>One running test: its setup, and the verbs only a test has.</summary>
        private sealed class Session
        {
            private readonly CardRuntime _runtime;
            private readonly SetupSession _setup;
            private readonly HashSet<CommandNode> _ownPlays;
            private readonly VerbHandler? _rulePlay;

            public Session(CardRuntime runtime, ScriptedChooser chooser, BlockNode body)
            {
                _runtime = runtime;
                _setup = new SetupSession(runtime, chooser);

                // `play` is a rule verb as well as a test verb, and one word cannot mean two things
                // by accident. It is decided lexically: the `play` statements in this test's own body
                // are the test's, and a `play` written anywhere else — in a card's effect, or in a
                // content verb this test calls — is the rules', even while the test is what set it
                // going. A content verb takes its context from its caller, so no check made while the
                // verb runs could get that right.
                var own = new OwnPlays();
                own.VisitBlock(body);
                _ownPlays = own.Statements;
                _rulePlay = Interpreter.TryGetVerb("play", out VerbHandler rules) ? rules : null;

                foreach (var (name, bind) in VerbTable) Interpreter.RegisterVerb(name, bind(this));
            }

            public bool Started { get; private set; }

            private Interpreter Interpreter => _runtime.Interpreter;
            private GameState State => _runtime.State;

            public void Start()
            {
                Started = true;
                _runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            }

            public void Run(StatementNode statement) => _setup.Run(statement);

            /// <summary>
            /// The verbs a test has beyond setup. With <see cref="SetupSession.SetupVerbTable"/> it
            /// is the single source of truth: the two drive registration and
            /// <see cref="DslTestRunner.TestVerbs"/>, which the linter reads.
            /// </summary>
            internal static readonly (string Name, Func<Session, VerbHandler> Bind)[] VerbTable =
            {
                ("play", s => s.Play),
                ("end", s => s.EndTurn),
                ("end_turn", s => s.EndTurn),
                ("expect", s => s.Expect),
                ("tick", s => call => s._runtime.Tick(call.Number(0, Num.One).ToInt())),
                ("cast", s => s.Cast),
            };

            private string Defined(string name, VerbCall call, string what, params string[] kinds) =>
                _setup.Defined(name, call.Span, what, kinds);

            /// <summary><c>play Strike on enemy</c>. Cards not already in hand are put there first.</summary>
            private void Play(VerbCall call)
            {
                // Written somewhere other than this test's own body, so it is the rules' verb.
                if (!_ownPlays.Contains(call.Node) && _rulePlay != null)
                {
                    _rulePlay(call);
                    return;
                }

                ExprNode node = call.ArgumentNode(0) ?? throw Fail("`play` needs a card.", call.Span);
                Entity? target = null;
                if (node is BinaryExpr { Operator: BinaryOperator.On } on)
                {
                    node = on.Left;
                    target = Interpreter.Evaluate(on.Right, call.Context).AsEntities().FirstOrDefault()
                        ?? throw Fail($"`{AstPrinter.Print(on.Right)}` is not anything that can be targeted.", call.Span);
                }

                Entity card;
                if (node is NameExpr || node is StringExpr)
                {
                    string name = node is NameExpr n ? n.Name : ((StringExpr)node).Value;
                    card = State.ZoneOf(State.Player, Zones.Hand).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? _runtime.AddCard(Defined(name, call, "card", "card"), Zones.Hand);
                }
                else
                {
                    card = Interpreter.Evaluate(node, call.Context).Entity ?? throw Fail($"`{AstPrinter.Print(node)}` is not a card.", call.Span);
                }

                PlayResult result = _runtime.Play(card, target);
                if (result != PlayResult.Played) throw Fail($"could not play {card.Name}: {result}.", call.Span);
            }

            private void EndTurn(VerbCall call)
            {
                if (!Started) Start();
                if (!State.InBattle) throw Fail("the battle is already over, so there is no turn to end.", call.Span);
                _runtime.EndTurn();
            }

            private void Cast(VerbCall call)
            {
                ExprNode node = call.ArgumentNode(0) ?? throw Fail("`cast` needs an ability.", call.Span);
                Entity? target = null;
                if (node is BinaryExpr { Operator: BinaryOperator.On } on)
                {
                    node = on.Left;
                    target = Interpreter.Evaluate(on.Right, call.Context).AsEntities().FirstOrDefault();
                }

                string name = node is NameExpr n ? n.Name : node is StringExpr s ? s.Value : throw Fail("expected an ability name.", call.Span);
                Entity ability = State.Player!.FindAttached(name)
                    ?? throw Fail($"the player has no ability `{Defined(name, call, "ability", "ability")}`; use `grant` first.", call.Span);
                if (!_runtime.UseAbility(ability, target)) throw Fail($"{name} is not ready.", call.Span);
            }

            private void Expect(VerbCall call)
            {
                ExprNode condition = call.ArgumentNode(0) ?? throw Fail("`expect` needs a condition.", call.Span);
                if (Interpreter.EvaluateCondition(condition, call.Context)) return;

                string message = "expected " + AstPrinter.Print(condition);
                if (condition is BinaryExpr binary && AstPrinter.IsComparison(binary.Operator))
                {
                    Value left = Interpreter.Evaluate(binary.Left, call.Context);
                    message += $", but {AstPrinter.Print(binary.Left)} was {Show(left)}";
                    if (!(binary.Right is NumberExpr) && !(binary.Right is StringExpr))
                        message += $" and {AstPrinter.Print(binary.Right)} was {Show(Interpreter.Evaluate(binary.Right, call.Context))}";
                }
                throw Fail(message, call.Span);
            }

            private static string Show(Value value) => value.Kind == ValueKind.Text ? "\"" + value.Text + "\"" : value.ToString();

            private static Exception Fail(string message, SourceSpan span) => SetupSession.Fail(message, span);
        }
    }
}
