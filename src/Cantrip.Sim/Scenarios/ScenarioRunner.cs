using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Cantrip.Syntax;
using Cantrip.Testing;

namespace Cantrip.Sim.Scenarios
{
    /// <summary>How the runner plays a scenario. The command line sets these; nothing else does.</summary>
    public sealed class ScenarioOptions
    {
        /// <summary>The first run's seed. Run <c>n</c> uses this plus <c>n</c>, so runs never share dice.</summary>
        public ulong FirstSeed { get; set; } = 1;

        /// <summary>Overrides every scenario's <c>runs</c> line. Null leaves each one as written.</summary>
        public int? Runs { get; set; }

        /// <summary>Turns one battle may take before the run is called a stall.</summary>
        public int TurnLimit { get; set; } = ScenarioRunner.DefaultTurnLimit;

        /// <summary>Makes the bot for a run from its seed. Replacing this is how a new bot arrives.</summary>
        public Func<ulong, IBot> MakeBot { get; set; } = seed => new FirstPlayableBot(seed);
    }

    /// <summary>
    /// Plays a <c>scenario</c>: its statements in the order they are written, a real battle at each
    /// <c>battle</c> line, and everything between them run as written. Each run is a game of its
    /// own, seeded from the first seed and the run's number, so the same command plays the same
    /// runs on any machine. A run that throws is recorded and the next one starts; a battle that
    /// reaches the turn limit ends its run as a stall.
    /// </summary>
    /// <example>
    /// <code>
    /// var runner = new ScenarioRunner(content, new ScenarioOptions { Runs = 100 });
    /// ScenarioResult result = runner.Run(content.Scenarios[0]);
    /// </code>
    /// </example>
    public sealed class ScenarioRunner
    {
        /// <summary>Runs for a scenario that does not say, which is the fewest the linter is happy with.</summary>
        public const int DefaultRuns = 100;

        /// <summary>Turns one battle may take. Past this it is a rules loop, not a fight.</summary>
        public const int DefaultTurnLimit = 50;

        private readonly ContentLibrary _content;
        private readonly ScenarioOptions _options;
        private readonly Dictionary<string, string[]?> _sources = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);

        public ScenarioRunner(ContentLibrary content, ScenarioOptions options)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// How many times this scenario is played: what <c>--runs</c> says, else the last
        /// <c>runs</c> line in the body, else <see cref="DefaultRuns"/>.
        /// </summary>
        public int RunCount(ScenarioDefinition scenario)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            if (_options.Runs is int forced) return forced;

            int? written = null;
            foreach (CommandNode command in Commands(scenario.Syntax.Body))
            {
                if (!Is(command, "runs")) continue;
                if (command.Arguments.FirstOrDefault() is NumberExpr { Unit: null } count && count.Value >= Num.One)
                    written = count.Value.ToInt();
            }
            return written ?? DefaultRuns;
        }

        /// <summary>Plays the scenario <see cref="RunCount"/> times and measures what happened.</summary>
        public ScenarioResult Run(ScenarioDefinition scenario)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));

            var result = new ScenarioResult(scenario, _options.MakeBot(_options.FirstSeed), _options.TurnLimit);
            foreach (CommandNode command in Commands(scenario.Syntax.Body))
            {
                if (Is(command, "battle")) result.BattleLines++;
                else if (!Is(command, "runs") && !Is(command, "expect")) result.OtherLines++;
            }

            int runs = RunCount(scenario);
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < runs; i++)
                result.Runs.Add(new OneRun(this, scenario, _options.FirstSeed + (ulong)i, result.Facts, null).Play());
            clock.Stop();
            result.Elapsed = clock.Elapsed;

            foreach (ExpectResult expectation in Measure(scenario, result)) result.Expectations.Add(expectation);
            return result;
        }

        /// <summary>
        /// Plays one run and tells <paramref name="log"/> every turn, play and statement, for
        /// <c>--watch</c>. It is the same run the report counted: the seed decides everything.
        /// </summary>
        public RunResult Replay(ScenarioDefinition scenario, ulong seed, Action<string> log)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            if (log == null) throw new ArgumentNullException(nameof(log));

            return new OneRun(this, scenario, seed, new ContentFacts(), log).Play();
        }

        // Expectations -----------------------------------------------------------------------

        /// <summary>
        /// Reads each <c>expect</c> line and answers it from the runs. <c>stalls</c> and
        /// <c>errors</c> are facts about the content and are checked. <c>wins</c>, <c>hp_left</c>
        /// and <c>turns</c> measure what the bot did, and this release's bot is a placeholder, so
        /// they are reported unchecked rather than answered with a number nobody should quote.
        /// </summary>
        private static IEnumerable<ExpectResult> Measure(ScenarioDefinition scenario, ScenarioResult result)
        {
            foreach (CommandNode command in Commands(scenario.Syntax.Body))
            {
                if (!Is(command, "expect")) continue;

                string text = "expect " + string.Join(" ", command.Arguments.Select(AstPrinter.Print));
                var answer = new ExpectResult(text, command.Span);
                IReadOnlyList<ExprNode> arguments = command.Arguments;

                string? metric = null;
                BinaryOperator op = BinaryOperator.Equal;
                Num against = Num.FromInt(0);

                if (arguments.Count == 2 && arguments[0] is NameExpr no && Same(no.Name, "no") && arguments[1] is NameExpr named)
                {
                    metric = named.Name;
                }
                else if (arguments.Count == 1 && arguments[0] is BinaryExpr comparison && AstPrinter.IsComparison(comparison.Operator)
                         && comparison.Left is NameExpr left && comparison.Right is NumberExpr number)
                {
                    metric = left.Name;
                    op = comparison.Operator;
                    against = number.Value;
                }

                if (metric == null)
                {
                    answer.Detail = "not checked: this is not a measurement over runs (error CT319 says so, and was suppressed)";
                }
                else if (Same(metric, "errors"))
                {
                    Hold(answer, result.Errors, op, against, result.Errors == 1 ? "1 run threw" : $"{result.Errors} runs threw");
                }
                else if (Same(metric, "stalls"))
                {
                    Hold(answer, result.Stalls, op, against, result.Stalls == 1 ? "1 battle reached the turn limit" : $"{result.Stalls} battles reached the turn limit");
                }
                else
                {
                    answer.Detail = $"not checked: `{metric.ToLowerInvariant()}` measures what the bot did, and the bot in this release is a placeholder";
                }

                yield return answer;
            }
        }

        private static void Hold(ExpectResult answer, int measured, BinaryOperator op, Num against, string detail)
        {
            Num value = Num.FromInt(measured);
            answer.Held = op switch
            {
                BinaryOperator.Equal => value == against,
                BinaryOperator.NotEqual => value != against,
                BinaryOperator.Less => value < against,
                BinaryOperator.LessOrEqual => value <= against,
                BinaryOperator.Greater => value > against,
                _ => value >= against,
            };
            answer.Detail = detail;
        }

        // Helpers ----------------------------------------------------------------------------

        /// <summary>Every command in a body, including the ones inside blocks such as <c>setup:</c>.</summary>
        private static IEnumerable<CommandNode> Commands(BlockNode body)
        {
            foreach (StatementNode statement in body.Statements)
            {
                if (statement is CommandNode command) yield return command;
                else if (statement is LabeledBlockNode labeled)
                {
                    foreach (CommandNode inner in Commands(labeled.Body)) yield return inner;
                }
            }
        }

        private static bool Is(CommandNode command, string verb) => Same(command.Verb, verb);

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A statement as it was written, for a report that has to say what ran: the line from the
        /// file where there is one, and the parsed statement where there is not, as for content
        /// loaded from a string.
        /// </summary>
        private string Written(StatementNode statement) => FromSource(statement.Span) ?? Printed(statement);

        /// <summary>The line itself, less any trailing comment, or null when the file cannot be read.</summary>
        private string? FromSource(SourceSpan span)
        {
            if (!_sources.TryGetValue(span.File, out string[]? lines))
            {
                try
                {
                    lines = File.ReadAllLines(span.File);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                                           || error is ArgumentException || error is NotSupportedException)
                {
                    lines = null;
                }
                _sources[span.File] = lines;
            }

            if (lines == null || span.Line < 1 || span.Line > lines.Length) return null;

            string written = WithoutComment(lines[span.Line - 1]).Trim();
            if (written.Length == 0) return null;
            return written.Length <= 72 ? written : written.Substring(0, 69) + "...";
        }

        /// <summary>Drops a trailing <c>#</c> comment, which is not part of what ran.</summary>
        private static string WithoutComment(string line)
        {
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') quoted = !quoted;
                else if (line[i] == '#' && !quoted) return line.Substring(0, i);
            }
            return line;
        }

        /// <summary>
        /// The statement printed back. The parser puts what follows a comma into a clause rather
        /// than an argument, so printing the node in its own order would rearrange
        /// <c>deck 4 Zap, 4 Ward</c>; only the spans still know how the line was written, and with
        /// a count before a name the order is the meaning.
        /// </summary>
        private static string Printed(StatementNode statement)
        {
            if (!(statement is CommandNode command))
                return statement is LabeledBlockNode labeled ? labeled.Label + ":" : "the statement";

            var pieces = new List<(SourceSpan At, string Text)>();
            foreach (ExprNode argument in command.Arguments) pieces.Add((argument.Span, AstPrinter.Print(argument)));
            foreach (ClauseNode clause in command.Clauses)
            {
                // `and` is what the parser calls a value that simply followed a comma, and the
                // scenario never wrote the word.
                if (clause.Value == null) pieces.Add((clause.Span, clause.Keyword));
                else if (Same(clause.Keyword, "and")) pieces.Add((clause.Value.Span, AstPrinter.Print(clause.Value)));
                else pieces.Add((clause.Value.Span, clause.Keyword + " " + AstPrinter.Print(clause.Value)));
            }

            string written = string.Join(" ", pieces.OrderBy(p => p.At.Line).ThenBy(p => p.At.Column).Select(p => p.Text));
            return written.Length == 0 ? command.Verb : command.Verb + " " + written;
        }

        /// <summary>
        /// One run: its own runtime, its own bot, its own dice. Everything a scenario's verbs need
        /// lives here, so nothing carries from one run to the next but the facts being collected.
        /// </summary>
        private sealed class OneRun
        {
            private readonly ScenarioRunner _owner;
            private readonly ScenarioDefinition _scenario;
            private readonly ContentFacts _facts;
            private readonly Action<string>? _log;
            private readonly RunResult _result;
            private readonly CardRuntime _runtime;
            private readonly SetupSession _setup;
            private readonly IBot _bot;
            private int _battles;
            private bool _over;

            public OneRun(ScenarioRunner owner, ScenarioDefinition scenario, ulong seed, ContentFacts facts, Action<string>? log)
            {
                _owner = owner;
                _scenario = scenario;
                _facts = facts;
                _log = log;
                _result = new RunResult(seed);
                _bot = owner._options.MakeBot(seed);

                var answers = new ScriptedChooser();
                var options = new RuntimeOptions
                {
                    Seed = seed,
                    Chooser = new ScenarioChooser(answers, _bot.Chooser),
                    Host = new FactHost(facts),
                };
                _runtime = new CardRuntime(owner._content, options);
                _setup = new SetupSession(_runtime, answers);

                // Beside the setup verbs: what a scenario has instead of a test's `play` and
                // `end turn`. `runs` and `expect` are read from the body before a run starts, so
                // here they do nothing; registering them keeps them from being unknown verbs.
                _runtime.RegisterVerb("battle", Battle);
                _runtime.RegisterVerb("runs", _ => { });
                _runtime.RegisterVerb("expect", _ => { });
                _runtime.RegisterVerb("enemy", call => throw Refuse(
                    "A scenario says what it fights with `battle \"Name\"`, and a bot fights it. `enemy` belongs in a `test`.", call));
                _runtime.RegisterVerb("realtime", call => throw Refuse(
                    "`cantrip sim` plays turn-based battles. When to act in continuous time is the game's own frame loop, so there is nothing for a bot to decide.", call));
            }

            public RunResult Play()
            {
                _log?.Invoke($"{_scenario.Name}, seed {_result.Seed}");
                StatementNode? at = null;

                try
                {
                    foreach (StatementNode statement in _scenario.Syntax.Body.Statements)
                    {
                        at = statement;
                        _result.At = statement.Span;
                        if (_log != null && Announce(statement)) _log("  " + _owner.Written(statement));
                        _setup.Run(statement);
                        if (_over) break;
                    }

                    _result.Won = !_over && _battles > 0;
                }
                catch (Exception error) when (!(error is OutOfMemoryException))
                {
                    _result.Error = Plain(error);
                    _result.Statement = at == null ? null : _owner.Written(at);
                    _log?.Invoke($"  threw at {_result.At}: {_result.Error}");
                }

                _result.HpLeft = _runtime.Player?.GetInt("hp") ?? 0;
                foreach (Entity card in _runtime.State.Entities.Where(e => e.Kind == EntityKind.Card && !e.IsRemoved))
                    _facts.CardsOwned.Add(card.Name);

                _log?.Invoke($"  ended with {_result.HpLeft} hp, {_result.Battles.Count(b => b.Won == true)} of {_result.Battles.Count} battle(s) won");
                return _result;
            }

            /// <summary><c>battle Archmage</c>, <c>battle "Frost Wisp", "Cinder Imp"</c>.</summary>
            private void Battle(VerbCall call)
            {
                // The run is over: the player is dead, or a battle before this one never ended.
                if (_over) return;

                IReadOnlyList<string> names = SetupSession.Names(call);
                if (names.Count == 0) throw Refuse("`battle` names no enemy, so there is nothing to fight.", call);

                var battle = new BattleResult(++_battles, string.Join(" + ", names));
                Entity player = _runtime.Player ?? throw Refuse("the scenario has no player to fight with.", call);
                int hpBefore = player.GetInt("hp");

                foreach (string name in names)
                {
                    Entity enemy = _runtime.SpawnEnemy(_setup.Defined(name, call.Span, "enemy", "enemy"));
                    foreach (MoveDefinition move in enemy.Definition?.Moves ?? Array.Empty<MoveDefinition>())
                        _facts.MovesDefined.Add((enemy.Name, move.Name));
                }

                _runtime.StartBattle();
                _log?.Invoke($"  battle {battle.Label}, at {hpBefore} hp");

                Action<string>? log = _log;
                while (_runtime.Won == null && battle.Turns < _owner._options.TurnLimit)
                {
                    battle.Turns++;
                    Observe();
                    log?.Invoke($"    turn {battle.Turns}  {Watch.Board(_runtime)}");
                    if (log != null && Watch.Hand(_runtime) is string hand) log("      " + hand);
                    _bot.PlayTurn(_runtime, log == null ? null : new Action<string>(line => log("      " + line)));
                    if (_runtime.Won != null) break;
                    _runtime.EndTurn();
                }

                battle.Won = _runtime.Won;
                battle.HpLost = Math.Max(0, hpBefore - player.GetInt("hp"));
                _result.Battles.Add(battle);
                _log?.Invoke($"    {(battle.Won == true ? "won" : battle.Won == false ? "lost" : "turn limit")} after {battle.Turns} turn(s), {player.GetInt("hp")} hp left");

                // Losing ends the run, and so does a battle that never ended: there is no honest
                // way to carry on from either.
                if (battle.Won != true) _over = true;
            }

            /// <summary>
            /// What the content allowed at the start of this turn, before the bot chose anything:
            /// which cards were in hand, and which of them could have been played at all.
            /// </summary>
            private void Observe()
            {
                foreach (Entity card in _runtime.State.ZoneOf(_runtime.Player, Zones.Hand)) _facts.CardsHeld.Add(card.Name);
                foreach (string name in Options.CardNames(_runtime, Options.Legal(_runtime))) _facts.CardsPlayable.Add(name);
            }

            private static InvalidOperationException Refuse(string message, VerbCall call) =>
                new InvalidOperationException($"`{call.Verb}`: {message}");

            /// <summary>
            /// Whether <c>--watch</c> says a statement is running. <c>battle</c> writes a fuller
            /// line of its own; <c>runs</c> and <c>expect</c> are read before the first run and do
            /// nothing during one, so announcing them would suggest they had.
            /// </summary>
            private static bool Announce(StatementNode statement) =>
                !(statement is CommandNode command && (Is(command, "battle") || Is(command, "runs") || Is(command, "expect")));

            /// <summary>
            /// An exception's message without the <c>(Parameter 'name')</c> that .NET adds to an
            /// <see cref="ArgumentException"/>, which means nothing to someone writing content.
            /// </summary>
            private static string Plain(Exception error)
            {
                string message = error.Message;
                if (error is ArgumentException argument && argument.ParamName != null)
                {
                    string suffix = $" (Parameter '{argument.ParamName}')";
                    if (message.EndsWith(suffix, StringComparison.Ordinal)) message = message.Substring(0, message.Length - suffix.Length);
                }
                return message;
            }
        }

        /// <summary>
        /// Writes what the engine raised into the facts. Only <c>move</c> matters yet; the damage
        /// meter that comes next hangs off the same call, which fires after every event.
        /// </summary>
        /// <remarks>
        /// Every turn the placeholder bot plays is played for real, so everything this hears
        /// happened. A bot that tries a play through <c>Capture</c> and <c>Restore</c> before
        /// choosing must be able to stop this recording while it does, or a move it merely tried
        /// is counted as one that fired. That switch belongs with the first bot that needs it.
        /// </remarks>
        private sealed class FactHost : EffectHostBase
        {
            private readonly ContentFacts _facts;

            public FactHost(ContentFacts facts) => _facts = facts;

            public override void OnEvent(GameEvent gameEvent)
            {
                if (gameEvent.Source == null || !string.Equals(gameEvent.Name, "move", StringComparison.Ordinal)) return;
                if (gameEvent.Data.TryGetValue("move", out Value move) && move.Text != null)
                    _facts.MovesFired.Add((gameEvent.Source.Name, move.Text));
            }
        }

        /// <summary>
        /// Answers what a scenario's <c>answer</c> lines queued, and leaves the rest to the bot, so
        /// a choice content asks for mid-effect is made by whatever is playing.
        /// </summary>
        private sealed class ScenarioChooser : IChoiceProvider, IDefinitionChooser
        {
            private readonly ScriptedChooser _answers;
            private readonly IChoiceProvider _bot;

            public ScenarioChooser(ScriptedChooser answers, IChoiceProvider bot)
            {
                _answers = answers;
                _bot = bot;
            }

            public IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state) =>
                _answers.Remaining > 0 ? _answers.Choose(request, state) : _bot.Choose(request, state);

            public EntityDefinition? ChooseDefinition(DefinitionChoice request, GameState state)
            {
                if (_answers.Remaining > 0) return _answers.ChooseDefinition(request, state);
                return _bot is IDefinitionChooser chooser
                    ? chooser.ChooseDefinition(request, state)
                    : request.Options.FirstOrDefault();
            }
        }
    }
}
