using System;
using System.Collections.Generic;
using System.Linq;
using GameplayEffects.Content;
using GameplayEffects.Diagnostics;
using GameplayEffects.Runtime;
using GameplayEffects.Syntax;

namespace GameplayEffects.Testing
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
    /// Runs <c>test</c> blocks (section 5, item 9). Each test gets a fresh runtime with a player
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
        public static IReadOnlyCollection<string> TestVerbs { get; } = Session.VerbTable.Select(v => v.Name).ToArray();

        /// <summary>Record the causality trace for every test (slower; attached to failures).</summary>
        public bool Trace { get; set; }

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
                runtime = new CardRuntime(_content, options);
            }
            catch (Exception e)
            {
                return new DslTestResult(test, false, "could not create runtime: " + e.Message, test.Syntax.Span, null);
            }

            var session = new Session(runtime, chooser);
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
            catch (TestFailure failure)
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
                // The runtime API's own complaints ("No card named Strke. Did you mean Strike?")
                // fail this test only, instead of aborting the whole run.
                return new DslTestResult(test, false, error.Message, at, Trace ? runtime.State.Trace.FormatTree() : null);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                return new DslTestResult(test, false, $"internal error ({error.GetType().Name}): {error.Message}", at, Trace ? runtime.State.Trace.FormatTree() : null);
            }
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

        private sealed class TestFailure : Exception
        {
            public TestFailure(string message, SourceSpan span) : base(message) => Span = span;

            public SourceSpan Span { get; }
        }

        /// <summary>One running test: its runtime, its variables, and the test-only verbs.</summary>
        private sealed class Session
        {
            private readonly CardRuntime _runtime;
            private readonly ScriptedChooser _chooser;
            private readonly EvalContext _context;
            private int _enemies;

            public Session(CardRuntime runtime, ScriptedChooser chooser)
            {
                _runtime = runtime;
                _chooser = chooser;

                Entity player = runtime.CreatePlayer();
                _context = new EvalContext(player) { Source = player };
                RegisterVerbs();
            }

            public bool Started { get; private set; }

            private Interpreter Interpreter => _runtime.Interpreter;
            private GameState State => _runtime.State;

            public void Start()
            {
                Started = true;
                _runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            }

            public void Run(StatementNode statement)
            {
                Interpreter.ResetSteps();
                _context.Chain = Interpreter.NewChain();
                Interpreter.Execute(new BlockNode(new[] { statement }, statement.Span), _context);
                Interpreter.Drain();
                if (State.InBattle) _runtime.CheckBattleOver();
            }

            /// <summary>
            /// Every test-only verb. The single source of truth: it drives registration and
            /// <see cref="DslTestRunner.TestVerbs"/>, which the linter reads.
            /// </summary>
            internal static readonly (string Name, Func<Session, VerbHandler> Bind)[] VerbTable =
            {
                ("enemy", s => s.Enemy),
                ("player", s => call => s.SetStats(s.State.Player!, call, 0)),
                ("hand", s => call => s.AddCards(call, Zones.Hand)),
                ("deck", s => call => s.AddCards(call, Zones.Draw)),
                ("discard_pile", s => call => s.AddCards(call, Zones.Discard)),
                ("relic", s => call => { foreach (string name in Names(call)) s._runtime.AddRelic(name); }),
                ("seed", s => call => s.State.Rng.Reseed((ulong)call.Number(0, Num.One).ToInt())),
                ("answer", s => call => { foreach (string name in Names(call)) s._chooser.Enqueue(name); }),
                ("realtime", s => _ => { }),
                ("play", s => s.Play),
                ("end", s => s.EndTurn),
                ("end_turn", s => s.EndTurn),
                ("expect", s => s.Expect),
                ("tick", s => call => s._runtime.Tick(call.Number(0, Num.One).ToInt())),
                ("grant", s => call => { foreach (string name in Names(call)) s._runtime.GrantAbility(name, s.State.Player!); }),
                ("cast", s => s.Cast),
            };

            private void RegisterVerbs()
            {
                foreach (var (name, bind) in VerbTable) Interpreter.RegisterVerb(name, bind(this));
            }

            /// <summary><c>enemy hp 6</c>, <c>enemy "Jaw Worm"</c>, <c>enemy Slime hp 12 Poison 3</c>.</summary>
            private void Enemy(VerbCall call)
            {
                IReadOnlyList<ExprNode> nodes = call.Node.Arguments;
                int index = 0;
                Entity enemy;

                string? named = nodes.Count > 0 ? nodes[0] switch { NameExpr n => n.Name, StringExpr s => s.Value, _ => null } : null;
                if (named != null && _runtime.Content.Find(named, "enemy") != null)
                {
                    enemy = _runtime.SpawnEnemy(named);
                    index = 1;
                }
                else
                {
                    string name = nodes.Count > 0 && nodes[0] is StringExpr label ? label.Value : "Enemy";
                    if (nodes.Count > 0 && nodes[0] is StringExpr) index = 1;
                    enemy = State.Spawn(name, EntityKind.Actor, null, Team.Enemy, Zones.Board);
                    enemy.SetBase("max_hp", 10);
                    enemy.SetBase("hp", 10);
                    enemy.SetBase("block", 0);
                }

                // SpawnEnemy already rolled an intent if the battle is running; rolling again here
                // would make a cycling enemy skip its opening move.
                SetStats(enemy, call, index);

                _enemies++;
                if (_enemies == 1) _context.SetLocal("enemy", Value.FromEntity(enemy));
                _context.SetLocal("enemy" + _enemies, Value.FromEntity(enemy));
            }

            /// <summary>Reads <c>stat value</c> pairs. Status names apply that many stacks.</summary>
            private void SetStats(Entity actor, VerbCall call, int start)
            {
                IReadOnlyList<ExprNode> nodes = call.Node.Arguments;
                for (int i = start; i < nodes.Count; i += 2)
                {
                    string stat = nodes[i] is NameExpr name ? name.Name : throw Fail($"expected a stat name, found `{AstPrinter.Print(nodes[i])}`.", call.Span);
                    if (i + 1 >= nodes.Count) throw Fail($"`{stat}` needs a value.", call.Span);
                    Num value = Interpreter.EvaluateNumber(nodes[i + 1], call.Context);

                    if (_runtime.Content.FindAny(stat, "status", "keyword") != null)
                    {
                        _runtime.ApplyStatus(stat, actor, value.ToInt());
                        continue;
                    }

                    actor.SetBase(stat, value);
                    if (string.Equals(stat, "hp", StringComparison.OrdinalIgnoreCase) && actor.GetBase("max_hp") < value)
                        actor.SetBase("max_hp", value);
                    if (string.Equals(stat, "energy", StringComparison.OrdinalIgnoreCase) && actor.GetBase("max_energy") < value)
                        actor.SetBase("max_energy", value);
                }
            }

            private void AddCards(VerbCall call, string zone)
            {
                foreach (string name in Names(call)) _runtime.AddCard(name, zone);
            }

            /// <summary>Names written as <c>A B</c>, <c>A, B</c> or <c>"Twin Strike", Defend</c>.</summary>
            private static IEnumerable<string> Names(VerbCall call)
            {
                foreach (ExprNode node in call.Node.Arguments)
                {
                    if (node is NameExpr n) yield return n.Name;
                    else if (node is StringExpr s) yield return s.Value;
                }
                foreach (ClauseNode clause in call.Node.Clauses)
                {
                    if (clause.Value == null) yield return clause.Keyword;
                    else if (clause.Value is StringExpr s) yield return s.Value;
                    else if (clause.Value is NameExpr n) yield return n.Name;
                }
            }

            /// <summary><c>play Strike on enemy</c>. Cards not already in hand are put there first.</summary>
            private void Play(VerbCall call)
            {
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
                        ?? _runtime.AddCard(name, Zones.Hand);
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
                Entity ability = State.Player!.FindAttached(name) ?? throw Fail($"the player has no ability `{name}`; use `grant` first.", call.Span);
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

            private static TestFailure Fail(string message, SourceSpan span) => new TestFailure(message, span);
        }
    }
}
