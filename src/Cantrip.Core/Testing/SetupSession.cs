using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Cantrip.Syntax;

namespace Cantrip.Testing
{
    /// <summary>A line of a <c>test</c> or a <c>scenario</c> that did not hold.</summary>
    internal sealed class SetupFailure : Exception
    {
        public SetupFailure(string message, SourceSpan span) : base(message) => Span = span;

        public SourceSpan Span { get; }
    }

    /// <summary>
    /// The half of a <c>test</c> or a <c>scenario</c> that builds the game up: the verbs
    /// <c>enemy</c>, <c>player</c>, <c>hand</c>, <c>deck</c>, <c>discard_pile</c>, <c>relic</c>,
    /// <c>seed</c>, <c>answer</c>, <c>realtime</c> and <c>grant</c>, registered on a runtime, and
    /// the statement loop that runs a line. A test adds <c>play</c>, <c>end turn</c>, <c>cast</c>,
    /// <c>tick</c> and <c>expect</c> on top; what plays a scenario adds <c>battle</c> instead. Both
    /// get the same setup, so a line cannot mean one thing in a test and another in a scenario.
    /// </summary>
    /// <example>
    /// <code>
    /// var session = new SetupSession(runtime, new ScriptedChooser());
    /// foreach (StatementNode statement in scenario.Syntax.Body.Statements) session.Run(statement);
    /// </code>
    /// </example>
    public sealed class SetupSession
    {
        /// <summary>More cards than any hand-written line means, so a typo cannot hang the run.</summary>
        private const int MaxRepeat = 1000;

        private readonly CardRuntime _runtime;
        private readonly ScriptedChooser _chooser;
        private readonly EvalContext _context;
        private int _enemies;

        /// <param name="runtime">The runtime to set up. Its player is made here if it has none.</param>
        /// <param name="chooser">Where <c>answer</c> queues its answers. Give the runtime the same one.</param>
        public SetupSession(CardRuntime runtime, ScriptedChooser chooser)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _chooser = chooser ?? throw new ArgumentNullException(nameof(chooser));

            // The caller's ConfigureRuntime may have made the player already.
            Entity player = runtime.Player ?? runtime.CreatePlayer();
            _context = new EvalContext(player) { Source = player };
            foreach (var (name, bind) in SetupVerbTable) Interpreter.RegisterVerb(name, bind(this));
        }

        private Interpreter Interpreter => _runtime.Interpreter;
        private GameState State => _runtime.State;

        /// <summary>Runs one statement and drains the triggers it raised, as a test line does.</summary>
        public void Run(StatementNode statement)
        {
            if (statement == null) throw new ArgumentNullException(nameof(statement));

            Interpreter.ResetSteps();
            _context.Chain = Interpreter.NewChain();
            Interpreter.Execute(new BlockNode(new[] { statement }, statement.Span), _context);
            Interpreter.Drain();
            if (State.InBattle) _runtime.CheckBattleOver();
        }

        /// <summary>
        /// The setup verbs. The single source of truth: it drives registration here, and
        /// <see cref="DslTestRunner.TestVerbs"/>, which the linter reads.
        /// </summary>
        internal static readonly (string Name, Func<SetupSession, VerbHandler> Bind)[] SetupVerbTable =
        {
            ("enemy", s => s.Enemy),
            ("player", s => call => s.SetStats(s.State.Player!, call, 0)),
            ("hand", s => call => s.AddCards(call, Zones.Hand)),
            ("deck", s => call => s.AddCards(call, Zones.Draw)),
            ("discard_pile", s => call => s.AddCards(call, Zones.Discard)),
            ("relic", s => call => { foreach (string name in Names(call)) s._runtime.AddRelic(s.Defined(name, call.Span, "relic or item", "relic", "item")); }),
            ("seed", s => call => s.State.Rng.Reseed((ulong)call.Number(0, Num.One).ToInt())),
            ("answer", s => call => { foreach (string name in Names(call)) s._chooser.Enqueue(name); }),
            ("realtime", s => _ => { }),
            ("grant", s => call => { foreach (string name in Names(call)) s._runtime.GrantAbility(s.Defined(name, call.Span, "ability", "ability"), s.State.Player!); }),
        };

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
                // `enemy Ghoul` with no Ghoul defined. Stats come in pairs, so an odd count means
                // one word is unpaired; it is a name only when it comes first with no value of its
                // own after it and is not a status. `enemy hp 20 block` is a missing value instead.
                if (nodes.Count % 2 == 1 && nodes[0] is NameExpr missing
                    && (nodes.Count == 1 || nodes[1] is NameExpr)
                    && _runtime.Content.FindAny(missing.Name, "status", "keyword") == null)
                {
                    EntityDefinition? other = _runtime.Content.Find(missing.Name);
                    if (other != null)
                    {
                        throw Fail(
                            $"`{missing.Name}` is {A(other.KindName)}, not an enemy. Write `enemy \"{missing.Name}\" hp 20` for a plain enemy with that label.",
                            call.Span);
                    }

                    string? close = Suggest.Closest(missing.Name, _runtime.Content.Pool("enemy").Select(d => d.Name));
                    throw Fail(
                        $"No enemy named `{missing.Name}` is defined." + (close == null ? string.Empty : $" Did you mean `{close}`?") +
                        $" Define it with `enemy {missing.Name}`, or write `enemy \"{missing.Name}\" hp 20` for a plain enemy with that label.",
                        call.Span);
                }

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
            foreach (string name in Names(call)) _runtime.AddCard(Defined(name, call.Span, "card", "card"), zone);
        }

        /// <summary>
        /// Fails the line, naming the nearest definition, when <paramref name="name"/> names
        /// nothing of the <paramref name="kinds"/> it needs, as in <c>hand Strik</c>.
        /// </summary>
        /// <param name="name">The name as written.</param>
        /// <param name="span">Where it was written, for the message.</param>
        /// <param name="what">What the line needed, as a noun: "card", "relic or item", "ability".</param>
        /// <param name="kinds">The kinds of definition that would do.</param>
        /// <returns><paramref name="name"/>, so the call reads as a lookup.</returns>
        public string Defined(string name, SourceSpan span, string what, params string[] kinds)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (kinds == null) throw new ArgumentNullException(nameof(kinds));

            ContentLibrary content = _runtime.Content;
            if (content.FindAny(name, kinds) != null) return name;

            EntityDefinition? other = content.Find(name);
            if (other != null) throw Fail($"`{name}` is {A(other.KindName)}, not {A(what)}.", span);

            string? close = Suggest.Closest(name, kinds.SelectMany(kind => content.Pool(kind)).Select(d => d.Name));
            throw Fail($"No {what} named `{name}` is defined." + (close == null ? string.Empty : $" Did you mean `{close}`?"), span);
        }

        /// <summary>"a card", "an ability".</summary>
        private static string A(string noun) => ("aeiou".IndexOf(char.ToLowerInvariant(noun[0])) >= 0 ? "an " : "a ") + noun;

        /// <summary>
        /// The names a line lists, written as <c>A B</c>, <c>A, B</c> or <c>"Twin Strike", Defend</c>.
        /// A whole number before a name repeats it, so <c>deck 4 Zap, 4 Ward</c> is eight cards.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="call"/> is null.</exception>
        public static IReadOnlyList<string> Names(VerbCall call)
        {
            if (call == null) throw new ArgumentNullException(nameof(call));

            var names = new List<string>();
            int? repeat = null;

            foreach (ExprNode node in AsWritten(call.Node))
            {
                if (node is NumberExpr number)
                {
                    if (repeat != null || number.Value < Num.One || number.Value != number.Value.Floor() || number.Value > Num.FromInt(MaxRepeat))
                    {
                        throw Fail(
                            $"`{AstPrinter.Print(number)}` is not a count. A whole number from 1 to {MaxRepeat} before a name repeats it, as in `{call.Verb} 4 Zap`.",
                            call.Span);
                    }

                    repeat = number.Value.ToInt();
                    continue;
                }

                string? name = node switch { NameExpr n => n.Name, StringExpr s => s.Value, _ => null };
                if (name == null) continue;

                for (int i = 0; i < (repeat ?? 1); i++) names.Add(name);
                repeat = null;
            }

            if (repeat != null) throw Fail($"`{repeat}` has no name after it to repeat, as in `{call.Verb} {repeat} Zap`.", call.Span);
            return names;
        }

        /// <summary>
        /// A line's arguments and its bare clauses in the order they were written. The parser puts
        /// what follows a comma in a clause rather than an argument, so <c>deck 4 Zap, 4 Ward</c>
        /// arrives as three arguments and one clause; only the spans still know the order, and the
        /// order is what a count in front of a name means.
        /// </summary>
        private static IEnumerable<ExprNode> AsWritten(CommandNode node)
        {
            var pieces = new List<(SourceSpan At, ExprNode Node)>();
            foreach (ExprNode argument in node.Arguments) pieces.Add((argument.Span, argument));
            foreach (ClauseNode clause in node.Clauses)
            {
                // A clause's own span is the token after it, so a valued clause is placed by its value.
                if (clause.Value != null) pieces.Add((clause.Value.Span, clause.Value));
                else pieces.Add((clause.Span, new NameExpr(clause.Keyword, clause.Span)));
            }

            return pieces.OrderBy(p => p.At.Line).ThenBy(p => p.At.Column).Select(p => p.Node);
        }

        internal static SetupFailure Fail(string message, SourceSpan span) => new SetupFailure(message, span);
    }
}
