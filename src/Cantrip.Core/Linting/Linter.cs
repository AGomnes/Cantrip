using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Descriptions;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Cantrip.Syntax;
using Cantrip.Testing;

namespace Cantrip.Linting
{
    /// <summary>Tunes a lint run for a particular game.</summary>
    public sealed class LintOptions
    {
        /// <summary>Diagnostic codes to leave out, such as <c>CT306</c>.</summary>
        public ISet<string> Suppressed { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Verbs the game registers from C# with <c>RegisterVerb</c>.</summary>
        public ISet<string> HostVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Events the game raises from C#.</summary>
        public ISet<string> HostEvents { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Names the game's <see cref="IEffectHost"/> resolves, and stats it only sets from C#.</summary>
        public ISet<string> HostNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Blocks the game runs itself, reading them from C# through <see cref="EntityDefinition.Blocks"/>,
        /// such as a custom <c>on_reveal:</c>. The engine runs only <c>effect:</c> and <c>move ...:</c>.
        /// </summary>
        public ISet<string> HostBlocks { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Static checks over loaded content: mistakes that would otherwise only
    /// show up when the effect runs, if ever. The linter never executes content, so it is safe to
    /// run in editors and CI on anything, including content that does not load cleanly.
    /// </summary>
    public sealed class Linter
    {
        /// <summary>
        /// Error: an unknown verb, or one written in the wrong kind of block: a test verb such as
        /// <c>cast</c> outside a test, <c>play</c> inside a scenario. A verb the game registers from C#
        /// looks unknown too until it is named in <see cref="LintOptions.HostVerbs"/>.
        /// </summary>
        public const string UnknownVerb = "CT301";

        /// <summary>
        /// An unknown name. An error where a definition has to be named (<c>apply Posion</c>,
        /// <c>card:Strke</c>, a <c>board</c> that is not declared); a warning elsewhere, since the game
        /// may be supplying the name at runtime.
        /// </summary>
        public const string UnknownName = "CT302";

        /// <summary>Warning: a <c>tag:</c> test for a tag no definition has, so it never matches.</summary>
        public const string UnknownTag = "CT303";

        /// <summary>
        /// Warning: a listener on an event nothing raises: not built in, not emitted by content, and
        /// not <c>&lt;stat&gt;_changed</c> for a stat this content has. Events the game raises from C#
        /// go in <see cref="LintOptions.HostEvents"/>.
        /// </summary>
        public const string UnknownEvent = "CT304";

        /// <summary>
        /// Note: an event content emits that no content listens for. Harmless when the game listens in
        /// its own code, which is why it is a note.
        /// </summary>
        public const string UnheardEvent = "CT305";

        /// <summary>
        /// Note: listeners that can set each other off. Loop protection already stops each after one
        /// pass, so this is a question rather than a fault.
        /// </summary>
        public const string EventCycle = "CT306";

        /// <summary>Error: <c>event</c> read outside an <c>on ...:</c> listener, where there is no event to read.</summary>
        public const string EventOutsideListener = "CT307";

        /// <summary>Error: <c>cancel</c> in a listener that runs after its event, which cannot undo what has already happened. Listen to <c>before_&lt;event&gt;</c>.</summary>
        public const string CancelAfterEvent = "CT308";

        /// <summary>Warning: a card asks the player to pick a target and then never reads <c>target</c>.</summary>
        public const string UnusedTarget = "CT309";

        /// <summary>Note: a verb declared in content that nothing calls.</summary>
        public const string UnusedVerb = "CT310";

        /// <summary>Warning: <c>stacks</c> read somewhere that is not a status, where it is a stat nothing ever sets. Read the status by name instead, as in <c>target.Poison</c>.</summary>
        public const string StacksOutsideStatus = "CT311";

        /// <summary>Error: <c>use</c> names a move the enemy does not have, or is written in something with no moves at all.</summary>
        public const string UnknownMove = "CT312";

        /// <summary>
        /// Warning: a line in a declaration ending in <c>:</c> that is not <c>effect:</c>, a
        /// <c>move ...:</c> or a listener. The shapes this catches are <c>when card_played:</c> and
        /// <c>once per battle</c> written before the <c>on</c>. Either loads as a label and never runs. A block the game runs itself goes in
        /// <see cref="LintOptions.HostBlocks"/>.
        /// </summary>
        public const string UnknownBlock = "CT313";

        /// <summary>
        /// Warning: <c>for N turns</c> on a status that already ticks down on turns, with N longer than
        /// the amount applied. <c>for</c> can only end such a status sooner, so the length has to be the
        /// amount: <c>apply Weak 2</c>.
        /// </summary>
        public const string ForOnDurationStatus = "CT314";

        /// <summary>Warning: a <c>duration</c> line on a status that takes its duration from whoever applies it, so the line does nothing.</summary>
        public const string IgnoredDuration = "CT315";

        /// <summary>
        /// Warning: a tag with behaviour of its own (<c>exhaust</c>, <c>retain</c>, <c>unplayable</c>
        /// and the rest) written on a line of its own instead of on the <c>tags</c> line, where it is a
        /// property nothing reads and the behaviour never happens.
        /// </summary>
        public const string TagWrittenAsProperty = "CT316";

        /// <summary>Warning: a scenario with no <c>battle</c> line, or a <c>battle</c> naming no enemy. There is nothing to play and nothing to measure.</summary>
        public const string NothingToFight = "CT317";

        /// <summary>A scenario's <c>runs</c> count: an error when it is not a whole number of one or more, and a note below 100, where the same content answers differently each time.</summary>
        public const string RunCount = "CT318";

        /// <summary>
        /// Error: an <c>expect</c> a scenario cannot check. A scenario plays hundreds of games and
        /// measures them in aggregate, so a condition about one game (<c>expect enemy.hp == 3</c>) has
        /// nothing to read.
        /// </summary>
        public const string UnknownMeasurement = "CT319";

        /// <summary>
        /// Error: a verb that acts on something already in the game, handed the name of a definition
        /// instead (<c>copy Strike</c>, <c>transform Strike into Wound</c>), or <c>create</c> given a
        /// status or ability, which belong to whoever has them rather than to a zone. Each of these is a
        /// runtime error too.
        /// </summary>
        public const string ContentWhereSomethingInPlayIsMeant = "CT320";

        /// <summary>Error: a <c>transform</c> inside an <c>until</c> block. <c>until</c> puts back what it did, and what a transform replaced is gone.</summary>
        public const string TransformInsideUntil = "CT321";

        /// <summary>
        /// Error: <c>emit</c> handed the name of a built-in event. Every listener of it would run while
        /// nothing had happened and no history counter had moved: the event forged and the record not.
        /// </summary>
        public const string EmitsBuiltinEvent = "CT322";

        /// <summary>
        /// Error: a named clause a built-in verb does not read, such as <c>block 8 for 2 turns</c>. The
        /// clause was dropped in silence, so the line read as one thing and did another. A flag after a
        /// comma is not a clause and is never reported.
        /// </summary>
        public const string ClauseNotRead = "CT323";

        /// <summary>
        /// Error: a bare percentage where a verb counts whole things. <c>apply Slow 40%</c> dropped the
        /// unit and applied forty stacks, while the generated rules text still said "40%".
        /// </summary>
        public const string PercentageWhereACountIsMeant = "CT324";

        /// <summary>
        /// Error: a length in units this game's clock cannot measure: <c>for 3s</c> under
        /// <c>clock turns</c>, or <c>2 turns</c> under <c>clock ticks</c>. Only content that states its
        /// clock is checked. See <see cref="TurnMachineryWithoutTurns"/> for the declarations, as
        /// opposed to the units.
        /// </summary>
        public const string WrongClock = "CT325";

        /// <summary>
        /// <c>player</c> written where a party member is meant. It is an error rather than a
        /// warning because the alternative is a party game whose every enemy move hits one hero
        /// forever, silently. A silent wrong answer is the one class of change this project
        /// has a written policy against.
        /// </summary>
        public const string PlayerWhereAMemberIsMeant = "CT326";

        /// <summary>
        /// Warning: a lane or rank no board this game declares can hold. Compared against, it is the
        /// same answer for every actor before the game even runs; moved to, the move stops at the edge
        /// of the board instead.
        /// </summary>
        public const string OffTheBoard = "CT327";

        /// <summary>Error: <c>position</c> assigned. It reads a rank and always will, but it names one axis of a place that has two, so a move written with it would have to guess which. Write <c>rank</c>.</summary>
        public const string PlaceAssigned = "CT328";

        /// <summary>Note: <c>position</c> read. It is the older name for <c>rank</c>, reads the same number and keeps working for the whole 1.x line.</summary>
        public const string PositionIsNowRank = "CT329";

        /// <summary>
        /// A <c>within</c> that will not be answered the way it reads: a plain number, which counts
        /// slots, in a game with no board (warning); or a length with a unit, which is a question about
        /// the world and goes to the game's <c>IEffectHost.TryCall</c> (note). The unit is what tells
        /// the two apart.
        /// </summary>
        public const string SpatialSelector = "CT330";

        /// <summary>Warning: <c>range</c> on something that points at nobody, so nothing ever reads it.</summary>
        public const string ReachWithoutATarget = "CT331";

        /// <summary>
        /// Warning: a <c>range</c> that decides nothing: as wide as the widest board declared, written
        /// backwards, or <c>range 0</c> at an enemy, which on a facing board is a slot no enemy stands
        /// on. <c>range 1</c> is what melee is written as.
        /// </summary>
        public const string ReachLimitsNothing = "CT332";

        /// <summary>Warning: <c>lane(...)</c> on a board one rank deep, or <c>rank(...)</c> on a board one lane wide, where the row is one actor and nobody else.</summary>
        public const string RowOfOne = "CT333";

        /// <summary>
        /// A turn order written into a game whose clock has no turns. CT334 was the `turns:` and
        /// `order:` values the first party release had not built yet; both are built, and a value
        /// nothing recognises is already CT0202, so the code was retired rather than re-used.
        /// </summary>
        public const string TurnOrderWithoutTurns = "CT335";

        /// <summary>
        /// An <c>of</c> group that can never hold the one the value belongs to: a pile of cards on a
        /// channel that belongs to an actor. A modifier that cannot match is worth saying out loud
        /// rather than leaving to a test that guessed the number.
        /// </summary>
        public const string ScopeCannotMatch = "CT336";

        /// <summary>
        /// Turn machinery written into a game that says <c>clock ticks</c>. CT325 checks the
        /// <em>units</em> a designer writes; this checks the <em>declarations</em>. A <c>move</c>,
        /// a <c>pattern</c>, a <c>phase</c>, a <c>stacking duration</c>, a
        /// <c>decay ... on turn_end</c>, an <c>until turn_end:</c>, a <c>next turn:</c>, an
        /// <c>once per turn</c>, a <c>reset_on turn_start</c> and a <c>turn_start</c> listener are
        /// every one of them driven by a turn, and a real-time game takes none.
        /// </summary>
        /// <remarks>
        /// Before this, content whose ruleset said <c>clock ticks</c> and which declared moves,
        /// patterns and phases that could never run linted with zero errors, zero warnings and
        /// zero notes, while a <c>2 turns</c> in the same file was CT325. The language checked the
        /// unit an author wrote and not the machinery they used, so half the vocabulary was
        /// silently inert: <c>apply Chill 3</c> on a tick clock was a permanent Chill.
        /// </remarks>
        public const string TurnMachineryWithoutTurns = "CT337";

        /// <summary>
        /// A <c>scenario</c> in a game that says <c>clock ticks</c>. The simulator plays a scenario
        /// by taking turns, and a real-time game has none, so every number it printed was about a
        /// game nobody played.
        /// </summary>
        public const string ScenarioWithoutTurns = "CT338";

        /// <summary>
        /// <c>cost</c> on an ability. An ability is paid for in the seconds it makes you wait;
        /// nothing spends the resource a <c>cost</c> line names, so the number reads like a rule
        /// and is not one.
        /// </summary>
        public const string AbilityCost = "CT339";

        /// <summary>
        /// Error: a <c>target</c> line naming a word that is not one of <c>enemy</c>, <c>ally</c>,
        /// <c>self</c>, <c>any</c> or <c>none</c>.
        /// </summary>
        /// <remarks>
        /// The set is closed (nothing a game registers adds to it) and the engine falls through
        /// to "nobody" for anything else, quietly. So <c>target freind</c> loaded, linted and
        /// tested clean, and the card it was written on stopped asking for a target and stopped
        /// checking the one it was handed: it would deal its damage to the party's own leader, or
        /// to anything else a caller passed, for the whole life of the game. It is an error rather
        /// than a warning for the same reason CT326 is: the alternative is a silent wrong answer.
        /// </remarks>
        public const string UnknownTargetMode = "CT340";

        /// <summary>Below this many runs, a scenario's numbers move about from one run to the next (CT318).</summary>
        private const int FewRuns = 100;

        /// <summary>Tags the engine itself gives meaning to, so using one never needs a declaration.</summary>
        private static readonly string[] EngineTags =
        {
            "attack", "skill", "power", "curse", "exhaust", "retain", "ethereal", "unplayable", "buff", "debuff",
        };

        /// <summary>The tags whose behaviour a card gets only from its <c>tags</c> line (see CT316).</summary>
        private static readonly string[] CardBehaviourTags = { "exhaust", "retain", "ethereal", "unplayable", "power", "attack" };

        /// <summary>The tags that set a status's flags (see CT316).</summary>
        private static readonly string[] StatusBehaviourTags = { "buff", "debuff" };

        /// <summary>Event data the runtime also exposes as bare names inside listeners.</summary>
        private static readonly string[] EventDataNames =
        {
            "stat", "old", "new", "base", "total", "blocked", "overkill", "status", "status_name", "from", "to", "won", "move", "ability",
        };

        /// <summary>
        /// Names that verbs bind for the statements after them, and the X of X-cost cards. Each verb
        /// binds its own participle (<c>choose</c> binds <c>chosen</c>, <c>copy</c> binds
        /// <c>copied</c>), so the line that reads the result says which verb produced it.
        /// </summary>
        private static readonly string[] BoundNames = { "chosen", "created", "copied", "discovered", "played", "index", "x" };

        /// <summary>Stats the runtime writes itself.</summary>
        private static readonly string[] RuntimeStats = { "expires_at", "ready_at" };

        /// <summary>Verbs whose <c>X on Y</c> argument means "X aimed at Y".</summary>
        private static readonly HashSet<string> AimingVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "play", "replay", "cast", "change",
        };

        /// <summary>The blocks in a declaration that the engine runs. Any other is only a label.</summary>
        private static readonly string[] RunBlocks = { "effect", "move" };

        /// <summary>Words that start a listener in other languages, and so in a mistyped one.</summary>
        private static readonly HashSet<string> ListenerWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "when", "whenever", "upon", "at", "after", "before", "instead", "once", "on",
        };

        /// <summary>Listener words that are also the timing of an event, as in <c>before_damaged</c>.</summary>
        private static readonly HashSet<string> TimingWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "before", "instead",
        };

        /// <summary>Words an event's name can do without: <c>start_of_turn</c> is <c>turn_start</c>.</summary>
        private static readonly HashSet<string> FillerWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "of", "on", "at", "is", "my", "your",
        };

        /// <summary>Other words for the words built-in events use.</summary>
        private static readonly Dictionary<string, string> EventSynonyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["begin"] = "start", ["begins"] = "start", ["began"] = "start", ["beginning"] = "start",
            ["finish"] = "end", ["finishes"] = "end", ["finished"] = "end",
            ["death"] = "die", ["dead"] = "die", ["dies"] = "die", ["died"] = "die",
            ["drew"] = "draw",
        };

        /// <summary>Endings taken off a word to compare it with another form of itself.</summary>
        private static readonly string[] WordEndings = { "ing", "ed", "es", "s", "d", "n" };

        /// <summary>Names that only ever mean actors, never a card.</summary>
        private static readonly HashSet<string> ActorWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "player", "leader", "controller", "enemy", "enemies", "allies", "everyone", "actors", "party", "fallen",
        };

        /// <summary>Verbs that act on the effect's target when they have no <c>to</c> clause.</summary>
        private static readonly HashSet<string> TargetingVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "deal", "damage", "apply", "add", "remove", "kill", "emit", "replay", "play", "transform",
        };

        /// <summary>
        /// Verbs a <c>scenario</c> must not use even though the rules know them. A scenario states the
        /// fight and a bot plays it, so a hand-written <c>play</c> there is the documented error it has
        /// always been. Registering <c>play</c> as a rule verb would otherwise delete that diagnostic.
        /// </summary>
        private static readonly HashSet<string> NotInScenarios = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "play",
        };

        private enum BodyKind
        {
            Effect,
            Listener,
            Modifier,
            Verb,
            Test,
            Scenario,
        }

        /// <summary>One piece of content to check: a block, a listener, a modifier, a verb, a test or a scenario.</summary>
        private sealed class Body
        {
            public Body(BodyKind kind, Node anchor, EntityDefinition? owner)
            {
                Kind = kind;
                Anchor = anchor;
                Owner = owner;
            }

            public BodyKind Kind { get; }
            public Node Anchor { get; }
            public EntityDefinition? Owner { get; }
            public ListenerNode? Listener { get; set; }
            public VerbDefinition? Verb { get; set; }
            public BlockNode? Block { get; set; }
            public List<ExprNode> Expressions { get; } = new List<ExprNode>();
            public Facts Facts { get; } = new Facts();
        }

        /// <summary>
        /// The verbs whose <c>into</c> clause binds a name, rather than naming a zone. What a verb
        /// achieved is not what it asked for (block absorbs damage, a heal stops at full health),
        /// so these four bind what really happened.
        /// </summary>
        private static bool BindsIntoAName(string verb) =>
            string.Equals(verb, "deal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verb, "damage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verb, "attack", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verb, "heal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verb, "block", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verb, "gain_block", StringComparison.OrdinalIgnoreCase);

        /// <summary>Everything a walk over one body turns up.</summary>
        private sealed class Facts : AstWalker
        {
            public List<CommandNode> Commands { get; } = new List<CommandNode>();
            public List<NameExpr> Names { get; } = new List<NameExpr>();
            public List<MemberExpr> Members { get; } = new List<MemberExpr>();
            public List<QualifiedExpr> Qualified { get; } = new List<QualifiedExpr>();
            public List<AssignNode> Assigns { get; } = new List<AssignNode>();
            public List<CallExpr> Calls { get; } = new List<CallExpr>();
            public List<BinaryExpr> OnExpressions { get; } = new List<BinaryExpr>();
            public List<BinaryExpr> Comparisons { get; } = new List<BinaryExpr>();
            public List<SelectorExpr> Selectors { get; } = new List<SelectorExpr>();
            public List<ScheduleNode> Schedules { get; } = new List<ScheduleNode>();
            public HashSet<string> Locals { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public override void VisitStatement(StatementNode statement)
            {
                switch (statement)
                {
                    case ScheduleNode schedule:
                        Schedules.Add(schedule);
                        break;
                    case CommandNode command:
                        Commands.Add(command);
                        if (command.Clause("as") is NameExpr alias
                            && (string.Equals(command.Verb, "choose", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(command.Verb, "discover", StringComparison.OrdinalIgnoreCase)))
                            Locals.Add(alias.Name);

                        // `deal 4 to all enemies into dealt` binds what landed, the way
                        // `choose ... as` binds what was chosen. Only the verbs that honour the
                        // clause bind anything, so a name written on one that ignores it still warns.
                        if (command.Clause("into") is NameExpr bound && BindsIntoAName(command.Verb))
                            Locals.Add(bound.Name);
                        break;
                    case ForEachNode loop:
                        Locals.Add(loop.Variable);
                        break;
                    case LetNode let:
                        Locals.Add(let.Name);
                        break;
                    case AssignNode assign:
                        Assigns.Add(assign);
                        break;
                }
                base.VisitStatement(statement);
            }

            public override void VisitExpression(ExprNode expression)
            {
                switch (expression)
                {
                    case NameExpr name:
                        Names.Add(name);
                        break;
                    case MemberExpr member:
                        Members.Add(member);
                        break;
                    case QualifiedExpr qualified:
                        Qualified.Add(qualified);
                        break;
                    case CallExpr call:
                        Calls.Add(call);
                        break;
                    case BinaryExpr { Operator: BinaryOperator.On } on:
                        OnExpressions.Add(on);
                        break;
                    case BinaryExpr binary:
                        Comparisons.Add(binary);
                        break;
                    case SelectorExpr selector:
                        Selectors.Add(selector);
                        break;
                }
                base.VisitExpression(expression);
            }
        }

        private readonly ContentLibrary _content;
        private readonly LintOptions _options;
        private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();
        private readonly HashSet<Node> _reported = new HashSet<Node>();
        private readonly List<Body> _bodies = new List<Body>();
        private readonly HashSet<string> _verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _stats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CommandNode> _emitted = new Dictionary<string, CommandNode>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _listened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether this content declares a <c>hero</c>. CT326 only applies to content that has a
        /// party, because `player` is the right word everywhere else and always was.
        /// </summary>
        private bool _hasParty;
        private readonly HashSet<string> _calledVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Linter(ContentLibrary content, LintOptions? options)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _options = options ?? new LintOptions();
        }

        /// <summary>Runs every check and returns the diagnostics in source order.</summary>
        public static IReadOnlyList<Diagnostic> Lint(ContentLibrary content, LintOptions? options = null) =>
            new Linter(content, options).Run();

        private IReadOnlyList<Diagnostic> Run()
        {
            CollectBodies();
            CollectGlobalFacts();

            foreach (Body body in _bodies)
            {
                CheckVerbs(body);
                CheckInPlay(body);
                CheckEmits(body);
                CheckClauses(body);
                CheckUntilTransforms(body);
                CheckScenario(body); // before CheckNames: `stalls` and `wins` are measurements, not names
                CheckNames(body);
                CheckTags(body);
                CheckEventUse(body);
                CheckStacks(body);
                CheckMoves(body);
                CheckStatusLengths(body);
                CheckPlaces(body);
                CheckPlaceAssignments(body);
                CheckPositionReads(body);
                CheckSpatialSelectors(body);
                CheckRowSelectors(body);
                CheckModifierScope(body);
                CheckPlayerInAParty(body);
            }

            foreach (EntityDefinition definition in _content.Definitions)
            {
                CheckTargetMode(definition);
                CheckReach(definition);
            }

            CheckBlocks();
            CheckClock();
            CheckAbilityCosts();
            CheckIgnoredDurations();
            CheckTagProperties();
            CheckListenedEvents();
            CheckEmittedEvents();
            CheckEventCycles();
            CheckCardTargets();
            CheckUnusedVerbs();
            CheckDescriptions();

            return _diagnostics
                .Where(d => !_options.Suppressed.Contains(d.Code))
                .OrderBy(d => d.Span.File, StringComparer.Ordinal)
                .ThenBy(d => d.Span.Line)
                .ThenBy(d => d.Span.Column)
                .ThenBy(d => d.Code, StringComparer.Ordinal)
                .ToList();
        }

        // Gathering ----------------------------------------------------------------------------

        private void CollectBodies()
        {
            IEnumerable<EntityDefinition> definitions = _content.Definitions
                .Where(d => d.IsThing)
                .OrderBy(d => d.Syntax.Span.File, StringComparer.Ordinal)
                .ThenBy(d => d.Syntax.Span.Line);

            foreach (EntityDefinition definition in definitions)
            {
                foreach (MemberNode member in definition.Syntax.Members)
                {
                    switch (member)
                    {
                        case ListenerNode listener:
                        {
                            var body = new Body(BodyKind.Listener, listener, definition) { Listener = listener, Block = listener.Body };
                            if (listener.Filter != null) body.Expressions.Add(listener.Filter);
                            _bodies.Add(body);
                            break;
                        }

                        case ModifyNode modify:
                        {
                            var body = new Body(BodyKind.Modifier, modify, definition);
                            if (modify.Scope != null) body.Expressions.Add(modify.Scope);
                            if (modify.Filter != null) body.Expressions.Add(modify.Filter);
                            body.Expressions.Add(modify.Amount);
                            _bodies.Add(body);
                            break;
                        }

                        case BlockMemberNode block:
                            _bodies.Add(new Body(BodyKind.Effect, block, definition) { Block = block.Body });
                            break;

                        // `target enemy where it.position <= 1`: the filter is an expression the
                        // rules evaluate per candidate, with `it` in focus, so its names are checked
                        // the way a modifier's `where` is. The side word in front of it is not a name.
                        case PropertyNode property when string.Equals(property.Name, "target", StringComparison.OrdinalIgnoreCase):
                        {
                            foreach (ExprNode value in property.Values)
                            {
                                for (ExprNode node = value; node is WhereExpr where; node = where.Source)
                                {
                                    var body = new Body(BodyKind.Modifier, property, definition);
                                    body.Expressions.Add(where.Predicate);
                                    _bodies.Add(body);
                                }
                            }
                            break;
                        }
                    }
                }
            }

            foreach (VerbDefinition verb in _content.Verbs.OrderBy(v => v.Syntax.Span.File, StringComparer.Ordinal).ThenBy(v => v.Syntax.Span.Line))
            {
                var body = new Body(BodyKind.Verb, verb.Syntax, null) { Verb = verb, Block = verb.Body };
                foreach (string parameter in verb.Parameters) body.Facts.Locals.Add(parameter);
                _bodies.Add(body);
            }

            foreach (TestDefinition test in _content.Tests)
                _bodies.Add(new Body(BodyKind.Test, test.Syntax, null) { Block = test.Syntax.Body });

            foreach (ScenarioDefinition scenario in _content.Scenarios)
                _bodies.Add(new Body(BodyKind.Scenario, scenario.Syntax, null) { Block = scenario.Syntax.Body });

            foreach (Body body in _bodies)
            {
                if (body.Block != null) body.Facts.VisitBlock(body.Block);
                foreach (ExprNode expression in body.Expressions) body.Facts.VisitExpression(expression);
            }
        }

        private void CollectGlobalFacts()
        {
            _hasParty = _content.Definitions.Any(d => d.IsHero);

            // Verbs: everything a runtime would register, plus content, host and (in tests) test verbs.
            var probe = new CardRuntime(_content);
            _verbs.UnionWith(probe.Interpreter.VerbNames);
            _verbs.UnionWith(_options.HostVerbs);

            _stats.UnionWith(Interpreter.CommonStats);
            _stats.UnionWith(RuntimeStats);
            _stats.UnionWith(_content.Resources.Keys);
            // A board's `lanes 3` is its shape, not a stat anything has, and its name is not a tag,
            // so the declarations that are rules rather than things are left out of both.
            foreach (EntityDefinition definition in _content.Definitions.Where(d => d.IsThing)) _stats.UnionWith(definition.Stats.Keys);

            foreach (EntityDefinition definition in _content.Definitions.Where(d => d.IsThing))
            {
                _tags.UnionWith(definition.Tags);
                _tags.Add(definition.Name); // a status's name counts as a tag on its host
            }
            _tags.UnionWith(EngineTags);

            foreach (Body body in _bodies)
            {
                if (body.Listener != null) _listened.Add(EventPart(body.Listener.EventName));

                foreach (AssignNode assign in body.Facts.Assigns)
                {
                    switch (assign.Target)
                    {
                        case NameExpr name when !IsReserved(name.Name): _stats.Add(name.Name); break;
                        case MemberExpr member when !StartsUpper(member.Member): _stats.Add(member.Member); break;
                    }
                }

                foreach (CommandNode command in body.Facts.Commands)
                {
                    _calledVerbs.Add(command.Verb);
                    string verb = command.Verb.ToLowerInvariant();

                    switch (verb)
                    {
                        case "emit":
                            // A built-in name here is CT322, not an event the content raises: it is
                            // refused at run, so nothing downstream should count it as raised.
                            if (WordAt(command, 0) is string emitted && !BuiltinEvents.IsBuiltin(emitted) && !_emitted.ContainsKey(emitted))
                                _emitted[emitted] = command;
                            break;

                        case "gain":
                        case "lose":
                            if (WordAt(command, 1) is string gained && !StartsUpper(gained)) _stats.Add(gained);
                            break;

                        case "change":
                            if (command.Arguments.Count > 0 && StatOfChange(command.Arguments[0]) is string changed) _stats.Add(changed);
                            break;

                        case "add":
                            if (command.Arguments.Count > 0 && command.Arguments[0] is QualifiedExpr { Qualifier: "tag" } added) _tags.Add(added.Name);
                            break;

                        case "deal":
                        case "damage":
                            if (command.Clause("as") is ExprNode damageType && FirstWord(damageType) is string typeTag) _tags.Add(typeTag);
                            break;

                        case "player":
                        case "enemy":
                            // Test and scenario setup write stats by name: `player rage 5`. The name
                            // in `enemy Ghoul hp 12` is not one, defined or not.
                            if (body.Kind == BodyKind.Test || body.Kind == BodyKind.Scenario)
                            {
                                NameExpr? enemyName = verb == "enemy" ? TestEnemyName(command) : null;
                                foreach (ExprNode argument in command.Arguments)
                                {
                                    if (argument is NameExpr stat && stat != enemyName && _content.Find(stat.Name) == null) _stats.Add(stat.Name);
                                }
                            }
                            break;
                    }
                }
            }
        }

        // Per-body checks ------------------------------------------------------------------------

        private void CheckVerbs(Body body)
        {
            foreach (CommandNode command in body.Facts.Commands)
            {
                if (command.Verb.Length == 0) continue; // a statement with no verb: the parser has reported it (CT0010)

                // A scenario's deny-list comes first: `play` is a verb the rules know, and knowing it
                // must not quietly withdraw the diagnostic that says a scenario does not play by hand.
                bool denied = body.Kind == BodyKind.Scenario && NotInScenarios.Contains(command.Verb);
                IReadOnlyCollection<string> allowed = BlockVerbs(body);
                if (!denied)
                {
                    if (_verbs.Contains(command.Verb)) continue;
                    if (allowed.Contains(command.Verb, StringComparer.OrdinalIgnoreCase)) continue;
                }

                // `enemy Ghoul hp 12` is how a test spawns one, and it is too far from `battle` for
                // the spelling guess to reach, so say it outright: it is the likeliest slip of all.
                // Otherwise a scenario line that was spelt right and written in the wrong block gets
                // no guess at all, because the message already says where the verb lives and
                // "did you mean `replay`?" under "`play` only exists inside `test` blocks" argues
                // with itself.
                string elsewhere = Elsewhere(body, command.Verb);
                string? suggestion = body.Kind == BodyKind.Scenario && string.Equals(command.Verb, "enemy", StringComparison.OrdinalIgnoreCase)
                    ? "battle"
                    : body.Kind == BodyKind.Scenario && elsewhere.Length > 0
                        ? null
                        : Suggest.Closest(command.Verb, _verbs.Concat(allowed));

                Error(UnknownVerb, $"Unknown verb `{command.Verb}`.{elsewhere}", command.Span, suggestion);
            }
        }

        /// <summary>
        /// CT320: a verb that acts on something already in the game, handed a name that is content.
        /// <c>copy Strike</c>, <c>play Strike</c>, <c>replay Strike</c> and <c>transform Strike into
        /// Wound</c> all read as if they would act on every Strike, and all four are runtime errors;
        /// the fix is a different verb or a different word in the slot, so the message says which
        /// rather than guessing at a spelling.
        /// </summary>
        private void CheckInPlay(Body body)
        {
            foreach (CommandNode command in body.Facts.Commands)
            {
                string verb = command.Verb.ToLowerInvariant();

                // In a test, `play` is the test's own verb and naming a card is how it is written.
                // In a scenario it is CT301, which already says where the verb belongs.
                bool inRules = body.Kind != BodyKind.Test && body.Kind != BodyKind.Scenario;
                if (verb != "copy" && verb != "transform" && verb != "replay" && verb != "create" && !(verb == "play" && inRules)) continue;

                // A game may have had a verb of its own by one of these names before they were
                // built in, in content or registered in C#. That one still runs, so this check has
                // nothing true to say about the line.
                if (_content.FindVerb(verb) != null || _options.HostVerbs.Contains(verb)) continue;

                ExprNode? first = Aimed(command.Arguments.FirstOrDefault());
                string? written = first switch
                {
                    NameExpr name when !IsReserved(name.Name) && !IsLocal(body, name.Name) => name.Name,
                    StringExpr text => text.Value,
                    _ => null,
                };
                if (written == null || _content.Find(written) == null) continue;

                // `create` is the other way round: content is exactly what it wants, but only the
                // kinds that stand in a zone on their own. A status, keyword or ability is handed
                // out, never made, so a name that is only one of those three is the same mistake in
                // reverse. It used to make orphans that raised `created` and went into saves.
                if (verb == "create")
                {
                    if (_content.FindAny(written, "card", "enemy", "actor", "relic", "item") != null) continue;
                    EntityDefinition? held = _content.FindAny(written, "status", "keyword", "ability");
                    if (held == null) continue;

                    // The count reads across from `create Poison 2` to `apply Poison 2`, so the fix
                    // is the line the author meant rather than a line they have to edit again.
                    string many = command.Arguments.Count > 1 && command.Arguments[1] is NumberExpr count
                        ? count.Value.ToString()
                        : "1";

                    Error(ContentWhereSomethingInPlayIsMeant,
                        $"`{written}` is {A(held.KindName)} and belongs to whoever has it, not to a zone. " +
                        (held.Kind == EntityKind.Ability
                            ? $"An ability is granted to an actor rather than made: a test writes `grant {Written(first!)}`, and a game calls `GrantAbility`."
                            : $"Write `apply {Written(first!)} {many} to <who>` to give one."),
                        command.Span);
                    continue;
                }

                // The fix is a different verb, not a different spelling, so the message says it
                // outright and no "did you mean" is offered to argue with it.
                string spelt = Written(first!);
                string becomes = (command.Clause("into") ?? command.Clause("to")) is ExprNode into ? Written(into) : "Sheepling";
                Error(ContentWhereSomethingInPlayIsMeant, verb switch
                {
                    "copy" => $"`copy` acts on something in the game, and `{written}` is content. Write `create {spelt}` for a fresh one.",
                    "transform" => $"`transform` changes something that is in the game, and `{written}` is content. Name what should change, as in `transform target into {becomes}`.",
                    "replay" => $"`replay` resolves the effect of a card that is in the game, and `{written}` is content. Write `create {spelt} into hand` first, then `play created.first, free`.",
                    _ => $"`play` acts on a card that is in a pile, and `{written}` is content. Write `create {spelt} into hand` first, then `play created.first`.",
                },
                    command.Span);
            }
        }

        /// <summary>
        /// CT325: a length written in units the game's clock cannot measure. Seconds belong to a
        /// tick clock and turns to a turn clock, and the two do not convert into each other; content
        /// says which it is written for with <c>clock turns</c> or <c>clock ticks</c> in its
        /// <c>ruleset</c> block.
        /// </summary>
        /// <remarks>
        /// The two halves used to fail differently and neither said so: <c>on every 1s:</c> in a
        /// turn game registered nothing at all, in silence, while <c>apply Weak 1 for 3s</c> in the
        /// same game threw when the line ran. Content that says nothing is unchanged, so this only
        /// ever fires where the author has stated the clock.
        /// </remarks>
        private void CheckClock()
        {
            ClockKind declared = _content.BuildRuleset(new DiagnosticBag()).Clock;
            if (declared == ClockKind.Unstated) return;

            CheckTurnOrder(declared);
            CheckTurnMachinery(declared);
            CheckScenarioClock(declared);

            IGameClock clock = declared == ClockKind.Ticks ? new TickClock() : (IGameClock)new TurnClock();
            string stated = declared == ClockKind.Ticks ? "ticks" : "turns";
            string measures = declared == ClockKind.Ticks
                ? "This game says `clock ticks`, so it measures time in ticks, seconds and milliseconds."
                : "This game says `clock turns`, so it measures time in turns.";

            void Check(Num amount, string? unit, SourceSpan span, string what)
            {
                if (string.IsNullOrEmpty(unit)) return;   // no unit: whatever the clock counts in
                if (clock.TryConvert(amount, unit, out _)) return;

                Error(WrongClock,
                    $"{what} is written in `{unit}`, which a `{stated}` clock cannot measure. {measures} " +
                    (declared == ClockKind.Ticks
                        ? "Write it in seconds (`3s`), milliseconds (`250ms`) or ticks, or say `clock turns` in the ruleset."
                        : "Write it in turns (`2 turns`), or say `clock ticks` in the ruleset."),
                    span);
            }

            foreach (EntityDefinition definition in _content.Definitions)
            {
                if (definition.Property("cooldown") is PropertyNode cooldown
                    && cooldown.Values.FirstOrDefault() is NumberExpr wait)
                    Check(wait.Value, wait.Unit, cooldown.Span, $"The cooldown of `{definition.Name}`");
            }

            foreach (Body body in _bodies)
            {
                if (body.Listener is ListenerNode listener && !listener.Interval.IsZero)
                    Check(listener.Interval, listener.IntervalUnit, listener.Span, "This `on every ...:` interval");

                foreach (CommandNode command in body.Facts.Commands)
                {
                    if (command.Clause("for") is NumberExpr length)
                        Check(length.Value, length.Unit, length.Span, $"The `for` on this `{command.Verb.ToLowerInvariant()}`");
                }

                foreach (ScheduleNode schedule in body.Facts.Schedules)
                {
                    if (schedule.Delay is NumberExpr delay) Check(delay.Value, delay.Unit, delay.Span, "This delay");
                }
            }
        }

        /// <summary>
        /// CT335: <c>turns:</c> or <c>order:</c> in a game that says <c>clock ticks</c>. Both say how
        /// a turn is shared out, and a real-time game has no turns to share: <c>turn_start</c> and
        /// <c>turn_end</c> never fire there, and the engine ignores both settings. Written on a tick
        /// clock, <c>turns: initiative</c> asks for an interleaved order that will never run and
        /// says so nowhere, which is the same silence CT325 exists for.
        /// </summary>
        private void CheckTurnOrder(ClockKind declared)
        {
            if (declared != ClockKind.Ticks || _content.RulesetSyntax is not RulesetDeclNode ruleset) return;

            foreach (PropertyNode setting in ruleset.Settings)
            {
                bool turns = string.Equals(setting.Name, "turns", StringComparison.OrdinalIgnoreCase);
                if (!turns && !string.Equals(setting.Name, "order", StringComparison.OrdinalIgnoreCase)) continue;

                Error(TurnOrderWithoutTurns,
                    $"`{setting.Name}:` says how a turn is shared out, and this game says `clock ticks`, so it has none. " +
                    (turns
                        ? "`turns: initiative` cannot interleave steps that never happen: `turn_start` and `turn_end` never fire on a tick clock. "
                        : "There is no order to offer the party in: in continuous time every member acts whenever its abilities are ready. ") +
                    $"Drop the `{setting.Name}:` line, or say `clock turns` if this game does take turns.",
                    setting.Span);
            }
        }


        /// <summary>
        /// CT337: machinery a turn drives, written into a game that says <c>clock ticks</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// CT325 asks whether a length is written in a unit this clock can measure. This asks the
        /// larger question behind it: whether the declaration itself is one a tick clock ever
        /// reaches. An enemy's <c>move</c> runs on the enemies' turn, a <c>pattern</c> advances at
        /// the same moment and a <c>phase</c> gates the moves the other two pick, so under ticks
        /// all three are text. A <c>stacking duration</c> status ticks down on its host's
        /// <c>turn_end</c>, which never comes, so it sits on its host for ever; that is a balance
        /// bug a designer would chase for a long time, and it used to lint clean.
        /// </para>
        /// <para>
        /// Every message names the real-time shape of the same idea, because each of these has
        /// one: <c>on every N s:</c> for a move, a filter on a second listener for a phase, a
        /// <c>for N s</c> where a status is applied for a duration, and <c>in N s:</c> for
        /// <c>next turn:</c>. The last line of every one of them offers <c>clock turns</c>,
        /// because "this is really a turn game" is always a legitimate answer.
        /// </para>
        /// </remarks>
        private void CheckTurnMachinery(ClockKind declared)
        {
            if (declared != ClockKind.Ticks) return;

            void Dead(string what, string instead, SourceSpan span) =>
                Error(TurnMachineryWithoutTurns,
                    $"{what} This game says `clock ticks`, so it has no turns and nothing ever runs it. " +
                    instead + " Or say `clock turns` in the ruleset if this game does take turns.",
                    span);

            // Every declaration, not only the things: `reset_on turn_start` is written on a
            // `resource`, which is a rule rather than a thing and so is not in `IsThing`.
            foreach (EntityDefinition definition in _content.Definitions)
            {
                foreach (MemberNode member in definition.Syntax.Members)
                {
                    switch (member)
                    {
                        case BlockMemberNode { Name: "move" } move:
                            Dead("A `move` is what an enemy does when its turn comes round.",
                                "Write the behaviour as a listener on the enemy instead, as in `on every 2s:`.",
                                move.Span);
                            break;

                        case PropertyNode { Name: "pattern" } pattern:
                            Dead("A `pattern` chooses the next move at the enemy's turn.",
                                "In continuous time an enemy keeps its own schedule: give it an `on every <n>s:` for each thing it does, and a filter or a status to say which.",
                                pattern.Span);
                            break;

                        case PropertyNode { Name: "phase" } phase:
                            Dead("A `phase` gates the moves an enemy picks from on its turn.",
                                "Write the change as a filter on a listener of its own, as in `on every 3s (self.hp <= self.max_hp / 2):`.",
                                phase.Span);
                            break;

                        case PropertyNode { Name: "stacking" } stacking when StacksDownOnTurnEnd(stacking) != null:
                            Dead($"`stacking {StacksDownOnTurnEnd(stacking)}` counts down on its host's `turn_end`.",
                                "A status applied on a tick clock ends because something said how long: write `stacking intensity` or `stacking none` and apply it with a length, as in `apply Chill 3 for 4s`.",
                                stacking.Span);
                            break;

                        case PropertyNode { Name: "decay" } decay when TurnEvent(DecayTrigger(decay)):
                            Dead($"`decay ... on {DecayTrigger(decay)}` takes a stack off at the end of a turn.",
                                "Apply the status with a length instead, as in `apply Chill 3 for 4s`, or decay it on an event this game does raise.",
                                decay.Span);
                            break;

                        case PropertyNode { Name: "reset_on" } reset when Words(reset).Any(TurnEvent):
                            Dead($"`reset_on {Words(reset).First(TurnEvent)}` refills this resource when a turn begins or ends.",
                                "Nothing refills it in continuous time unless content says so: reset it from an `on every <n>s:` listener, or on an event this game does raise.",
                                reset.Span);
                            break;

                        case ListenerNode listener when listener.Interval.IsZero && TurnEvent(listener.EventName):
                            Dead($"`{listener.EventName}` is raised when a turn begins or ends.",
                                "Nothing raises it on a tick clock: write `on every <n>s:` for something that happens over and over, or `on battle_start:` for something that happens once.",
                                listener.Span);
                            break;

                        case ListenerNode listener when listener.Limit == LimitScope.Turn:
                            Dead("`once per turn` opens again when the next turn begins.",
                                "There is no next turn here, so it opens once and never again: limit it with `once per battle`, or let the listener's own interval do the limiting.",
                                listener.Span);
                            break;
                    }
                }
            }

            foreach (Body body in _bodies)
            {
                foreach (ScheduleNode schedule in body.Facts.Schedules)
                {
                    switch (schedule.Kind)
                    {
                        case ScheduleKind.NextTurn:
                            Dead("`next turn:` runs its body when the next turn starts.",
                                "Write the delay as a length of time instead, as in `in 2s:`.",
                                schedule.Span);
                            break;

                        case ScheduleKind.Until when TurnEvent(schedule.Deadline):
                            Dead($"`until {schedule.Deadline}:` undoes what it did when the turn ends.",
                                "Give the window a length instead, as in `until 3s:`, or end it on an event this game does raise.",
                                schedule.Span);
                            break;
                    }
                }
            }
        }

        /// <summary>The words of a property, lower-cased, for the settings CT337 reads.</summary>
        private static IEnumerable<string> Words(PropertyNode property) =>
            property.Values.SelectMany(EntityDefinition.ReadWords).Select(w => w.ToLowerInvariant());

        /// <summary>
        /// The stacking mode named on this line when it is one that counts itself down at
        /// <c>turn_end</c>, else null. <c>intensity</c>, <c>none</c> and <c>separate</c> do not,
        /// so they are the three a real-time status is written with.
        /// </summary>
        private static string? StacksDownOnTurnEnd(PropertyNode stacking) =>
            Words(stacking).FirstOrDefault(w => w == "duration" || w == "refresh" || w == "both");

        /// <summary>
        /// Which event a <c>decay</c> line takes a stack off on. <c>decay 1</c> alone means
        /// <c>turn_end</c>, which is exactly the silent case CT337 is here to catch.
        /// </summary>
        private static string? DecayTrigger(PropertyNode decay) => decay.First switch
        {
            NumberExpr => "turn_end",
            BinaryExpr { Operator: BinaryOperator.On, Right: NameExpr trigger } => trigger.Name.ToLowerInvariant(),
            _ => null,
        };

        /// <summary>Whether an event name is one only a turn raises.</summary>
        private static bool TurnEvent(string? name) =>
            string.Equals(name, "turn_start", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "turn_end", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// CT339: a <c>cost</c> on an ability. It parses, <see cref="CardRuntime.CostOf"/> answers
        /// it, and nothing spends it: an ability's price is the seconds it makes you wait. A number
        /// that reads like a rule and is not one is the class of silence this linter exists for, so
        /// it is refused where it is written rather than found by counting energy afterwards.
        /// </summary>
        private void CheckAbilityCosts()
        {
            foreach (EntityDefinition definition in _content.Definitions)
            {
                if (!string.Equals(definition.KindName, "ability", StringComparison.OrdinalIgnoreCase)) continue;
                if (!(definition.Property("cost") is PropertyNode cost)) continue;

                Error(AbilityCost,
                    $"`cost` on ability `{definition.Name}` is never charged: an ability is paid for in the seconds it makes you wait, " +
                    "and nothing takes the resource this names. Write the price as a `cooldown`, or put the effect on a card, which does pay.",
                    cost.Span);
            }
        }

        /// <summary>
        /// CT338: a <c>scenario</c> in a game that says <c>clock ticks</c>. The simulator plays a
        /// scenario by ending turns, so on a tick clock nothing ever advanced the clock: every
        /// listener stayed silent, every ability used once never came back, no battle could end,
        /// and the report said "hp lost 0.0" and "50.0 turns" at exit 0. The command refuses such a
        /// folder now, and this says the same thing where the scenario is written.
        /// </summary>
        private void CheckScenarioClock(ClockKind declared)
        {
            if (declared != ClockKind.Ticks) return;

            foreach (ScenarioDefinition scenario in _content.Scenarios)
                Error(ScenarioWithoutTurns,
                    $"Scenario `{scenario.Name}` cannot be played: `cantrip sim` takes turns, and this game says `clock ticks`. " +
                    "When to act in continuous time is the game's own frame loop, not a bot's. Cover a real-time game with `test` blocks, " +
                    "which drive the clock with `realtime <rate>` and `tick <n>`.",
                    scenario.Syntax.Span);
        }

        /// <summary>
        /// The boards a rule could be running on: the ones content declares, or the default board
        /// when it declares none. A rule is not tied to one board, so a place is only wrong when it
        /// is wrong on every board this game has.
        /// </summary>
        private IReadOnlyList<BoardShape> PossibleBoards =>
            _content.Boards.Count > 0 ? _content.Boards : new[] { _content.DefaultBoard };

        /// <summary>
        /// CT327: a lane or rank no board this game has can hold. <c>it.lane == 2</c> on a one-lane
        /// board matches nothing, and no message ever said so. The filter simply never passed and
        /// the card did nothing, which is exactly the class of silence this linter exists for. The
        /// mirror case is reported too: <c>it.rank &lt;= 3</c> on a two-rank board passes for
        /// everybody, so the <c>where</c> that was meant to limit the card's reach limits nothing.
        /// </summary>
        /// <remarks>
        /// Only a comparison against a literal is checked, because only then is the answer the same
        /// for every actor before the game runs. A rule is checked against every board content
        /// declares and reported only when all of them agree, since a card in a game with a narrow
        /// board and a wide one is legitimately written for the wide one.
        /// </remarks>
        private void CheckPlaces(Body body)
        {
            foreach (BinaryExpr comparison in body.Facts.Comparisons)
            {
                if (!IsPlaceComparison(comparison, out string axis, out BinaryOperator op, out int literal, out ExprNode blame)) continue;

                bool never = true, always = true;
                foreach (BoardShape board in PossibleBoards)
                {
                    int? last = axis == "lane"
                        ? board.Lanes - 1
                        : board.RanksAreUnbounded ? (int?)null : board.Ranks - 1;

                    never &= Never(op, literal, last);
                    always &= Always(op, literal, last);
                }

                string word = axis == "lane" ? PossibleBoards[0].LaneWord : PossibleBoards[0].RankWord;
                string boards = PossibleBoards.Count == 1
                    ? $"This game's board is {PossibleBoards[0].Describe()}"
                    : "Every board this game declares is smaller than that";

                if (never)
                {
                    Warn(OffTheBoard,
                        $"No actor is ever there, so this never matches. {boards}, and {word}s count from 0.",
                        blame.Span);
                }
                else if (always)
                {
                    Warn(OffTheBoard,
                        $"Every actor is there, so this passes for all of them and limits nothing. {boards}, and {word}s count from 0.",
                        blame.Span);
                }
            }

            // A board property that is out of its own board's bounds cannot happen: `lanes` and
            // `ranks` are the bounds. Nothing to check there.
        }

        /// <summary>Whether a comparison is <c>&lt;something&gt;.lane|rank|position OP &lt;literal&gt;</c>.</summary>
        private static bool IsPlaceComparison(BinaryExpr comparison, out string axis, out BinaryOperator op, out int literal, out ExprNode blame)
        {
            axis = string.Empty;
            op = comparison.Operator;
            literal = 0;
            blame = comparison;

            if (!IsComparison(comparison.Operator)) return false;

            if (Axis(comparison.Left) is string left && Literal(comparison.Right) is int right)
            {
                axis = left;
                literal = right;
                return true;
            }

            // `3 >= it.rank` is the same rule written the other way round, so the operator flips.
            if (Axis(comparison.Right) is string other && Literal(comparison.Left) is int value)
            {
                axis = other;
                literal = value;
                op = Mirror(comparison.Operator);
                return true;
            }

            return false;

            static string? Axis(ExprNode node) => node is MemberExpr member ? PlaceWord(member.Member) : null;

            static int? Literal(ExprNode node) => node switch
            {
                NumberExpr number when number.Unit == null && number.Value == number.Value.Floor() => number.Value.ToInt(),
                UnaryExpr { Operator: UnaryOperator.Negate, Operand: NumberExpr negative } when negative.Unit == null && negative.Value == negative.Value.Floor()
                    => -negative.Value.ToInt(),
                _ => null,
            };
        }

        /// <summary>Which axis a member name reads, or null for one that is not a place.</summary>
        private static string? PlaceWord(string member) => member.ToLowerInvariant() switch
        {
            "lane" => "lane",
            "rank" => "rank",
            "position" => "rank",
            _ => null,
        };

        private static bool IsComparison(BinaryOperator op) =>
            op is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less
                or BinaryOperator.LessOrEqual or BinaryOperator.Greater or BinaryOperator.GreaterOrEqual;

        private static BinaryOperator Mirror(BinaryOperator op) => op switch
        {
            BinaryOperator.Less => BinaryOperator.Greater,
            BinaryOperator.LessOrEqual => BinaryOperator.GreaterOrEqual,
            BinaryOperator.Greater => BinaryOperator.Less,
            BinaryOperator.GreaterOrEqual => BinaryOperator.LessOrEqual,
            _ => op,
        };

        /// <summary>Whether the comparison is false for every slot from 0 to <paramref name="last"/>.</summary>
        private static bool Never(BinaryOperator op, int literal, int? last) => op switch
        {
            BinaryOperator.Equal => literal < 0 || (last.HasValue && literal > last.Value),
            BinaryOperator.NotEqual => last == 0 && literal == 0,
            BinaryOperator.Less => literal <= 0,
            BinaryOperator.LessOrEqual => literal < 0,
            BinaryOperator.Greater => last.HasValue && literal >= last.Value,
            BinaryOperator.GreaterOrEqual => last.HasValue && literal > last.Value,
            _ => false,
        };

        /// <summary>Whether the comparison is true for every slot from 0 to <paramref name="last"/>.</summary>
        private static bool Always(BinaryOperator op, int literal, int? last) => op switch
        {
            BinaryOperator.Equal => last == 0 && literal == 0,
            BinaryOperator.NotEqual => literal < 0 || (last.HasValue && literal > last.Value),
            BinaryOperator.Less => last.HasValue && literal > last.Value,
            BinaryOperator.LessOrEqual => last.HasValue && literal >= last.Value,
            BinaryOperator.Greater => false,
            BinaryOperator.GreaterOrEqual => literal <= 0,
            _ => false,
        };

        /// <summary>
        /// CT328: <c>position</c> written to. <c>lane</c> and <c>rank</c> are now real moves, so
        /// only the alias is left here: <c>position</c> names one axis of a place that has two, and
        /// writing it would have to guess which. Reading it still works and always will.
        /// </summary>
        /// <remarks>
        /// This used to refuse <c>lane</c> and <c>rank</c> as well, because assigning either wrote a
        /// stat that the slot shadowed and nothing ever read: a line that looked like a move and
        /// was none. Now that the write moves the actor, refusing it would be refusing the feature.
        /// </remarks>
        private void CheckPlaceAssignments(Body body)
        {
            foreach (AssignNode assign in body.Facts.Assigns)
            {
                string? written = assign.Target switch
                {
                    MemberExpr member => member.Member,
                    NameExpr name => name.Name,
                    _ => null,
                };
                if (written == null || !string.Equals(written, "position", StringComparison.OrdinalIgnoreCase)) continue;

                Error(PlaceAssigned,
                    "`position` is the older name for `rank` and reads the same number, but it cannot be written: " +
                    "it names one axis of a place that now has two, and a move has to say which. Write `rank`.",
                    assign.Span);
            }

            CheckPlaceAssignmentBounds(body);
        }

        /// <summary>
        /// CT327 again, for a move rather than a comparison: <c>target.rank = 4</c> on a three-rank
        /// board names a slot the board does not have. The move stops at the board's edge rather
        /// than failing, so without this the line reads as "to the back" and means "to rank 2".
        /// </summary>
        private void CheckPlaceAssignmentBounds(Body body)
        {
            foreach (AssignNode assign in body.Facts.Assigns)
            {
                if (assign.Operator != AssignOperator.Set) continue;
                if (assign.Target is not MemberExpr member || PlaceWord(member.Member) is not string axis) continue;
                if (string.Equals(member.Member, "position", StringComparison.OrdinalIgnoreCase)) continue;

                if (assign.Value is not NumberExpr number || number.Unit != null || number.Value != number.Value.Floor()) continue;
                int slot = number.Value.ToInt();

                bool beyond = true;
                foreach (BoardShape board in PossibleBoards)
                {
                    beyond &= axis == "lane" ? !board.HasLane(slot) : !board.HasRank(slot);
                }
                if (!beyond) continue;

                string word = axis == "lane" ? PossibleBoards[0].LaneWord : PossibleBoards[0].RankWord;
                Warn(OffTheBoard,
                    $"There is no {word} {slot} to move to, so this stops at the edge of the board instead. " +
                    (PossibleBoards.Count == 1
                        ? $"This game's board is {PossibleBoards[0].Describe()}, and {word}s count from 0."
                        : $"Every board this game declares is smaller than that, and {word}s count from 0."),
                    assign.Span);
            }
        }

        /// <summary>
        /// CT330: a <c>within</c> the engine will not answer the way it reads. A plain number counts
        /// slots on the board, so writing one in a game that declares no board asks about a single
        /// lane; a length with a unit is a question about the world and goes to the game's host,
        /// which a game without one discovers at runtime rather than here.
        /// </summary>
        private void CheckSpatialSelectors(Body body)
        {
            foreach (CallExpr call in body.Facts.Calls)
            {
                if (!string.Equals(call.Name, "within", StringComparison.OrdinalIgnoreCase)) continue;

                if (call.Arguments.Count != 2)
                {
                    Warn(SpatialSelector,
                        "`within` takes what to measure from and how far, as in `within(target, 2)` for slots on the board " +
                        "or `within(target, 5m)` for a length only the game can answer.",
                        call.Span);
                    continue;
                }

                ExprNode radius = call.Arguments[1];
                if (radius is NumberExpr { Unit: not null })
                {
                    Info(SpatialSelector,
                        "`within` with a length is a question about the world, not about the board: the engine hands it to the game " +
                        "(`IEffectHost.TryCall`) and a game that does not answer it gets an error when the line runs. " +
                        "`within(x, 2)`, with a plain number, counts slots and is answered here.",
                        call.Span);
                    continue;
                }

                if (radius is NumberExpr && _content.Boards.Count == 0)
                {
                    Warn(SpatialSelector,
                        "`within` with a plain number counts slots on the board, and this game declares none, so it is measuring " +
                        "across one lane with everything in a line. Declare a `board`, or write a length such as `5m` if this is a " +
                        "distance in the world for the game to answer.",
                        call.Span);
                }
            }
        }

        /// <summary>
        /// CT340: a <c>target</c> line naming a word no mode answers. The five modes are a closed
        /// set, and everything outside it reaches nobody and checks nobody, in silence.
        /// </summary>
        private void CheckTargetMode(EntityDefinition definition)
        {
            PropertyNode? property = definition.Property("target");
            if (property == null || property.Values.Count == 0) return;

            string mode = TargetRule.Of(definition).Mode;
            if (TargetRule.IsMode(mode)) return;

            Error(
                UnknownTargetMode,
                $"`target {mode}` on {definition} names nobody: a `target` line takes " +
                "`enemy`, `ally`, `self`, `any` or `none`. Anything else is never a side, so this " +
                "action asks for no target and checks the one it is handed against nothing, so it will " +
                "point at whoever the game passes it, the party's own leader included.",
                property.Span,
                Suggest.Closest(mode, TargetRule.Modes));
        }

        /// <summary>
        /// CT331 and CT332: a <c>range</c> that decides nothing. Reach narrows what an action may be
        /// <em>pointed at</em>, so one on something that points at nobody is never consulted, and one
        /// as wide as the board limits nothing it is consulted about.
        /// </summary>
        private void CheckReach(EntityDefinition definition)
        {
            PropertyNode? property = definition.Property("range");
            if (property == null || definition.Range is not Reach range) return;

            if (definition.Property("target") == null || TargetRule.Of(definition).Mode == "none")
            {
                Warn(ReachWithoutATarget,
                    $"`range` says how far {definition} may be pointed, and it points at nobody, so nothing ever reads it. " +
                    "Add a `target` line, such as `target enemy`, or take the `range` off.",
                    property.Span);
                return;
            }

            if (range.Min > range.Max)
            {
                Warn(ReachLimitsNothing,
                    $"`range {range.Min}..{range.Max}` starts further away than it ends, so nothing is ever in reach of {definition}.",
                    property.Span);
                return;
            }

            bool everybody = true, nobody = true;
            foreach (BoardShape board in PossibleBoards)
            {
                int? furthest = board.MaxDistance;
                everybody &= range.Min == 0 && furthest.HasValue && range.Max >= furthest.Value;

                // On a facing board the two sides are never on the same slot, so a reach that stops
                // short of one step reaches nobody on the other side.
                nobody &= range.Max == 0 && TargetRule.Of(definition).Mode == "enemy";
            }

            if (nobody)
            {
                Warn(ReachLimitsNothing,
                    $"`range 0` reaches only the slot {definition} is used from, and an enemy is never standing on it, so this can be pointed at nobody. " +
                    "`range 1` is what melee is written as.",
                    property.Span);
            }
            else if (everybody)
            {
                Warn(ReachLimitsNothing,
                    $"Every actor is within `range {range.Max}` on every board this game declares, so this limits nothing. " +
                    (PossibleBoards.Count == 1
                        ? $"The board is {PossibleBoards[0].Describe()}, and nothing on it is more than {PossibleBoards[0].MaxDistance} step(s) away."
                        : "Give the narrowest board a reach it can actually narrow, or take the line off."),
                    property.Span);
            }
        }

        /// <summary>
        /// CT336: an <c>of</c> group that can never hold the one the value belongs to. On
        /// <c>damage</c> and its neighbours the value belongs to an actor, so a group that only ever
        /// holds cards matches nothing whatever the game does. Warned rather than refused, because
        /// a game may name a group of its own that the linter cannot see into; these are the built-in
        /// piles, where there is no doubt.
        /// </summary>
        private void CheckModifierScope(Body body)
        {
            if (!(body.Anchor is ModifyNode modify) || modify.Scope == null) return;
            if (!ChannelBelongsToAnActor(modify.Channel)) return;

            ExprNode scope = modify.Scope;
            while (scope is WhereExpr where) scope = where.Source;
            if (!(scope is NameExpr name) || !CardPileWords.Contains(name.Name)) return;

            Warn(ScopeCannotMatch,
                $"`{name.Name}` holds cards, and `{modify.Channel}` belongs to whoever an action is by or to, " +
                $"so `of {name.Name}` can never match and this modifier will never apply. Name a group of actors: " +
                "`party`, `allies`, `enemies` or `everyone`. Or move the rule to a channel a card carries, such as `cost`.",
                scope.Span);
        }

        /// <summary>Channels whose value belongs to an actor and never to a card.</summary>
        private static bool ChannelBelongsToAnActor(string channel) =>
            ActorOnlyChannels.Contains(channel);

        private static readonly HashSet<string> ActorOnlyChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "damage", "block", "heal", "draw", "damage_taken", "heal_taken", "block_taken", "targetable",
        };

        /// <summary>The built-in names that always mean a pile of cards.</summary>
        private static readonly HashSet<string> CardPileWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hand", "draw_pile", "discard", "discard_pile", "exhaust", "exhaust_pile", "powers", "deck", "cards",
        };

        /// <summary>
        /// CT333: a row selector on a board with only one actor in a row. <c>rank(who)</c> on a
        /// one-lane board is <c>who</c> and nobody else, because one actor stands on a slot. So
        /// "deal 4 to the target's rank" quietly hits the target alone.
        /// </summary>
        private void CheckRowSelectors(Body body)
        {
            foreach (CallExpr call in body.Facts.Calls)
            {
                bool lane = string.Equals(call.Name, "lane", StringComparison.OrdinalIgnoreCase);
                bool rank = string.Equals(call.Name, "rank", StringComparison.OrdinalIgnoreCase);
                if ((!lane && !rank) || call.Arguments.Count != 1) continue;

                bool alone = true;
                foreach (BoardShape board in PossibleBoards) alone &= lane ? board.LaneHoldsOne : board.RankHoldsOne;
                if (!alone) continue;

                BoardShape shape = PossibleBoards[0];
                string word = lane ? shape.LaneWord : shape.RankWord;
                string across = lane ? $"{shape.Ranks} deep" : $"{shape.Lanes} wide";
                Warn(RowOfOne,
                    $"One actor stands on a slot, and a {word} on this board is one slot ({across}), so `{call.Name}(...)` is that one actor and nobody else. " +
                    $"Write the actor itself, or give the board more than one {(lane ? shape.RankWord : shape.LaneWord)}.",
                    call.Span);
            }
        }

        /// <summary>
        /// CT329: a note on every <c>position</c> read, suggesting <c>rank</c>. The old word keeps
        /// working for the whole of the 1.x line and reads the same number, so this is a note and
        /// never a warning: the word fades rather than flips.
        /// </summary>
        private void CheckPositionReads(Body body)
        {
            // A `position` being written to is CT328, which already says to write `rank`. Saying it
            // twice about one line would make the error look like two problems.
            var written = new HashSet<Node>(body.Facts.Assigns.Select(a => (Node)a.Target));

            foreach (MemberExpr member in body.Facts.Members)
            {
                if (!string.Equals(member.Member, "position", StringComparison.OrdinalIgnoreCase)) continue;
                if (written.Contains(member)) continue;
                Info(PositionIsNowRank,
                    "`position` is now called `rank`, because a board has a lane across it as well. " +
                    "It reads the same number and keeps working; write `rank` when you next touch this line.",
                    member.Span);
            }

            foreach (SelectorExpr selector in body.Facts.Selectors)
            {
                if (!string.Equals(selector.Key, "position", StringComparison.OrdinalIgnoreCase)) continue;
                Info(PositionIsNowRank,
                    "`position` is now called `rank`, because a board has a lane across it as well. " +
                    "`lowest rank enemies` and `highest rank enemies` sort by the same number.",
                    selector.Span);
            }
        }

        /// <summary>
        /// CT326: <c>player</c> written in content that has a party, where a party member could
        /// be meant: an enemy's move, a card's or an ability's effect, or a listener on any of
        /// them, on a status or on a relic.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>player</c> means exactly one entity and always will: the party's leader, the one
        /// <c>CreatePlayer</c> made, the one that holds the run's relics and gold. In a game with no
        /// <c>hero</c> that is the whole party, so the word is never wrong and this check never
        /// fires. In a game with a party it is almost always the wrong word wherever somebody is
        /// being acted on: <c>deal 5 to player</c> in an enemy move hits the leader however
        /// carefully the enemy telegraphed somebody else, and it does it quietly.
        /// </para>
        /// <para>
        /// So it is an error, not a warning. It costs nothing to content that has no party,
        /// because it only applies to content that declares a <c>hero</c>, and the three words that
        /// do what was meant are in the message: <c>target</c> for who this action is aimed at,
        /// <c>leader</c> for the run's own actor when that really is who is meant, and <c>party</c>
        /// for all of them.
        /// </para>
        /// <para>
        /// Everywhere else <c>player</c> stays legal and stays right: a run's gold, a test's own
        /// lines, a scenario's setup, the game's own C#, and a status's or relic's listener that
        /// is about its own owner. The whole rule is one sentence: <c>player</c> is refused
        /// wherever a member could be meant. The list of bodies below is only how that
        /// sentence is spelled for a linter.
        /// </para>
        /// </remarks>
        private void CheckPlayerInAParty(Body body)
        {
            if (!_hasParty) return;

            if (!(PartyBody(body) is (string place, string fix))) return;

            foreach (NameExpr name in body.Facts.Names)
            {
                if (!string.Equals(name.Name, "player", StringComparison.OrdinalIgnoreCase)) continue;
                if (body.Facts.Locals.Contains(name.Name)) continue;

                // Which word does what was meant depends on what the declaration is, and the message
                // says it rather than listing all three: `owner` for something carried, `self` for a
                // declaration that is itself the member, `target` for one that acts on somebody else.
                // A carried listener is told the exception in the same breath, because a rule is
                // easier to keep than to look up.
                string wrong = fix switch
                {
                    "owner" => "not whoever is carrying it",
                    "self" => "not this one",
                    _ => "not the member being acted on",
                };
                string advice = fix switch
                {
                    "owner" => "Write `owner` for whoever is carrying it, `leader` if the run's own actor really is meant, or `party` for all of them. " +
                               "`player` stays right in a listener about its own owner, such as `on owner.turn_start:`.",
                    "self" => "Write `self` for this one, `target` for whoever the line is aimed at, `leader` if the run's own actor really is meant, " +
                              "or `party` for all of them.",
                    _ => "Write `target` for whoever this is aimed at, `leader` if the run's own actor really is meant, or `party` for all of them.",
                };

                Error(
                    PlayerWhereAMemberIsMeant,
                    $"`player` in {place} means the party's leader, {wrong}, and this content has a party. {advice}",
                    name.Span,
                    fix);
            }
        }

        /// <summary>
        /// Where the body is, in words for the message, and which word does what was meant there.
        /// Null when <c>player</c> is not refused: a body no declaration owns, or a carried listener
        /// about its own holder.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the rule, rather than a list: <b>every body on every declaration</b>. It was a
        /// list until 1.0 (an enemy's <c>move</c>, a card's and an ability's <c>effect</c>, and
        /// listeners on <c>enemy</c>, <c>card</c>, <c>ability</c>, <c>status</c> and <c>relic</c>),
        /// and a list has holes. The one that mattered: a listener on a <c>hero</c>.
        /// <c>hero "Cleric" / on damaged: block 2 to player</c> blocked the <em>leader</em> whenever
        /// the Cleric was damaged, quietly, which is the exact shape CT326 was made an error to
        /// prevent, in the declaration a party game has most of. A listener on an <c>actor</c> or a
        /// <c>keyword</c>, a <c>modify</c> line and a <c>target ... where</c> filter were out too.
        /// </para>
        /// <para>
        /// There is nothing to carve out for a <c>hero</c> or an <c>actor</c>, which is what makes
        /// the rule affordable: <c>CreatePlayer</c> spawns an actor with no definition behind it, so
        /// the leader is never made from a declaration and <c>player</c> inside one always names
        /// somebody other than the declaration itself. The word there is <c>self</c>.
        /// </para>
        /// <para>
        /// The kinds an actor <em>carries</em> (<see cref="IsCarried"/>) keep the one exception,
        /// which is what lets the whole rule be a sentence rather than a list of places:
        /// <c>player</c> stays legal where the listener is about its own owner.
        /// <see cref="AboutItsOwner"/> says exactly what that means, and why those are the two shapes
        /// in which no member can be meant.
        /// </para>
        /// <para>
        /// A content <c>verb</c> is deliberately <b>not</b> here, and neither is a <c>test</c> or a
        /// <c>scenario</c>. A verb has no owner, so neither <c>self</c> nor <c>owner</c> exists inside
        /// one and its <c>target</c> is whatever its caller bound: there is no word this diagnostic
        /// could name, and CT326 is an error rather than a warning precisely because it can always
        /// name one. <c>player</c> in a verb is also often right, since <c>verb score(c): gain c.chips
        /// chips to player</c> means the run's own pool, and the caller's own body is checked, which
        /// is where the leader-or-member decision is actually written. Severity may rise in a 1.x
        /// release, so a verb can still be warned about later; naming the wrong word now could not be
        /// taken back.
        /// </para>
        /// </remarks>
        private static (string Place, string Fix)? PartyBody(Body body)
        {
            // No owner: a content `verb`, a `test` or a `scenario`. See the remarks above on why a
            // verb is left out rather than warned about.
            if (body.Owner == null) return null;

            string? what = BodyWords(body);
            if (what == null) return null;

            string kind = body.Owner.KindName.ToLowerInvariant();
            string place = $"{(Vowel(kind) ? "an" : "a")} {kind}'s {what}";

            // Carried: a status, a relic, a keyword and an item all fire for whoever is holding them,
            // so the word that means that is `owner`, and `target` names nothing inside
            // `on every 2s:`. A listener about the holder alone is the exception.
            if (IsCarried(kind))
            {
                return body.Kind == BodyKind.Listener && AboutItsOwner(body.Listener) ? null : (place, "owner");
            }

            // A `hero` or an `actor` declaration *is* a member, so `self` is the word. There is
            // nothing to carve out for either: `CreatePlayer` spawns an actor with no definition
            // behind it, so the leader is never made from one of these declarations and `player`
            // inside one is always somebody else.
            if (kind == "hero" || kind == "actor") return (place, "self");

            // An enemy, a card and an ability act on somebody the rules settled on: `target`.
            return (place, "target");
        }

        /// <summary>
        /// The bodies <c>player</c> is refused in, which is every body a declaration can carry: an
        /// <c>effect</c>, a <c>move</c>, a listener, a <c>modify</c> line and a <c>target ... where</c>
        /// filter. Null for nothing else, which is how a body with no statements of its own is
        /// skipped.
        /// </summary>
        private static string? BodyWords(Body body)
        {
            switch (body.Kind)
            {
                case BodyKind.Listener:
                    return "listener";

                case BodyKind.Effect:
                    if (!(body.Anchor is BlockMemberNode block)) return null;
                    if (string.Equals(block.Name, "effect", StringComparison.OrdinalIgnoreCase)) return "effect";
                    if (string.Equals(block.Name, "move", StringComparison.OrdinalIgnoreCase)) return "move";
                    return $"`{block.Name}:` block";

                // A `modify` line's scope, filter and amount, and the predicate of a
                // `target ... where`. Both are expressions the rules evaluate about somebody, so
                // `player` in one is the same wrong answer as `player` in a statement.
                case BodyKind.Modifier:
                    return body.Anchor is PropertyNode ? "`target` filter" : "`modify` line";

                default:
                    return null;
            }
        }

        /// <summary>
        /// The declaration kinds something is <em>carried</em> by an actor as: they fire and apply for
        /// whoever is holding them, so <c>owner</c> is the word, and the owner exception is theirs.
        /// </summary>
        /// <remarks>
        /// <c>item</c> is here beside <c>status</c>, <c>relic</c> and <c>keyword</c> because the
        /// engine already treats it as a relic by another name (<c>InDefaultScope</c> anchors both to
        /// the holder's controller), so leaving it out would refuse <c>player</c> in an item's
        /// own-owner listener where a relic's is allowed.
        /// </remarks>
        private static bool IsCarried(string kind) =>
            kind == "status" || kind == "relic" || kind == "keyword" || kind == "item";

        private static bool Vowel(string word) => "aeiou".IndexOf(word[0]) >= 0;

        /// <summary>
        /// The events that put no actor in view but the leader, so that a <c>player</c> in a
        /// listener for one of them cannot be a member said wrongly.
        /// </summary>
        /// <remarks>
        /// <see cref="BuiltinEvents"/> says what each one carries, and these three are the whole of
        /// the list: <c>battle_start</c>'s source is the player, <c>battle_end</c>'s target is the
        /// player, and <c>obtained</c>'s source is the player taking a relic. Every other built-in
        /// event is about an actor, and in a party that actor can be a member.
        /// </remarks>
        private static readonly HashSet<string> RunLevelEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BuiltinEvents.BattleStart, BuiltinEvents.BattleEnd, BuiltinEvents.Obtained,
        };

        /// <summary>
        /// Whether a carried declaration's listener (a <c>status</c>, a <c>relic</c>, a
        /// <c>keyword</c> or an <c>item</c>) is <em>about its own owner</em>: the one shape in which
        /// <c>player</c> is refused nowhere, because no member is in view to have been meant instead.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It is true in exactly two cases, and the definition is the listener's header alone,
        /// nothing about the body, so the same line is always read the same way:
        /// </para>
        /// <list type="number">
        /// <item>
        /// <description>
        /// The event is <b>scoped to the holder</b>: <c>on owner.turn_start:</c> or
        /// <c>on self.damaged:</c>. The scope word is the one the runtime resolves relative to the
        /// listening entity (<c>Interpreter.ResolveScope</c>), and these two are the two that
        /// resolve to the thing the declaration is attached to. <c>on controller.turn_start:</c>
        /// and <c>on player.turn_start:</c> are scopes too and are <em>not</em> this, because they
        /// name somebody other than the holder. This is the case the decision named:
        /// <c>on owner.turn_start: block 2 to player</c> on a status worn by the leader.
        /// </description>
        /// </item>
        /// <item>
        /// <description>
        /// The event is <b>run-level</b> (<see cref="RunLevelEvents"/>), where the only actor the
        /// engine puts in view is the leader, so <c>player</c> is the only thing it could be.
        /// <c>relic "Purse" / on battle_start: gain 5 gold to player</c> is this one.
        /// </description>
        /// </item>
        /// </list>
        /// <para>
        /// Everything else is refused, and that is the point: <c>on every 2s:</c>,
        /// <c>on damaged:</c>, <c>on turn_start:</c> and <c>on killed(target: enemies):</c> all
        /// fire in a world where a member is standing right there.
        /// </para>
        /// </remarks>
        private static bool AboutItsOwner(ListenerNode? listener)
        {
            if (listener == null) return false;

            // `EventName` still carries the scope: `owner.damaged`, the way `Listener` reads it.
            string name = listener.EventName;
            int dot = name.LastIndexOf('.');
            if (dot <= 0) return RunLevelEvents.Contains(name);

            string scope = name.Substring(0, dot);
            return string.Equals(scope, "owner", StringComparison.OrdinalIgnoreCase)
                || string.Equals(scope, "self", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// CT323: a named clause a built-in verb does not read. The parser knows the clause words
        /// and attaches no meaning to them, so one a verb does not read used to be dropped in
        /// silence: <c>block 8 for 2 turns</c> gave ordinary block, <c>apply Poison 3 at target</c>
        /// ignored the <c>at</c>, and <c>deal 5 against enemy2</c> hit the card's own target.
        /// </summary>
        /// <remarks>
        /// Only built-in verbs are checked. A flag after a comma is not a clause and is never
        /// reported, because a verb a game registers in C# reads its own flags: that is exactly
        /// what <c>--suppress CT301</c> content does.
        /// </remarks>
        private void CheckClauses(Body body)
        {
            foreach (CommandNode command in body.Facts.Commands)
            {
                if (!BuiltinClauses.IsKnownVerb(command.Verb)) continue;

                // A game whose own verb has one of these names runs that instead, and it may read
                // any clause it likes. In a test, `play` is the test runner's own verb.
                if (_content.FindVerb(command.Verb) != null || _options.HostVerbs.Contains(command.Verb)) continue;
                if (body.Kind == BodyKind.Test && BlockVerbs(body).Contains(command.Verb, StringComparer.OrdinalIgnoreCase)) continue;

                foreach (ClauseNode clause in BuiltinClauses.Unread(command))
                    Error(ClauseNotRead, BuiltinClauses.NotRead(command.Verb, clause.Keyword), clause.Span);

                CheckPercentages(command);
            }
        }

        /// <summary>
        /// CT324: a bare percentage where a built-in verb counts whole things. <c>apply Slow 40%</c>
        /// applied forty stacks and printed "Apply 40% Slow", so the generated text stated a
        /// percentage the engine does not implement. A percentage inside a sum is a fraction by the
        /// time the verb sees it, so only one written on its own is reported.
        /// </summary>
        private void CheckPercentages(CommandNode command)
        {
            foreach (ExprNode argument in command.Arguments)
            {
                Num percent;
                switch (argument)
                {
                    case NumberExpr { Unit: "%" } number: percent = number.Value; break;
                    case UnaryExpr { Operator: UnaryOperator.Negate, Operand: NumberExpr { Unit: "%" } negative }: percent = -negative.Value; break;
                    default: continue;
                }

                Error(PercentageWhereACountIsMeant,
                    VerbCall.PercentageMessage(command.Verb, AstPrinter.Print(argument), percent),
                    argument.Span);
            }
        }

        /// <summary>
        /// CT322: <c>emit</c> handed the name of a built-in event. <c>emit damaged 99 to player</c>
        /// dispatched to every <c>on damaged</c> listener although nothing was damaged, and no
        /// history counter moved with it. The event was forged and the record was not. <c>emit</c>
        /// raises a <em>custom</em> event, and the runtime refuses this too.
        /// </summary>
        private void CheckEmits(Body body)
        {
            // A game whose own `emit` verb predates the built-in one runs that instead, and nothing
            // here is true of it.
            if (_content.FindVerb("emit") != null || _options.HostVerbs.Contains("emit")) return;

            foreach (CommandNode command in body.Facts.Commands)
            {
                if (!string.Equals(command.Verb, "emit", StringComparison.OrdinalIgnoreCase)) continue;
                if (WordAt(command, 0) is not string name || !BuiltinEvents.IsBuiltin(name)) continue;

                Error(EmitsBuiltinEvent, BuiltinEvents.CannotBeEmitted(name.ToLowerInvariant()), command.Span);
            }
        }

        /// <summary>
        /// CT321: a <c>transform</c> written inside an <c>until</c> block. <c>until</c> promises to
        /// put back what it did, and none of this can be put back: the stats, the statuses and the
        /// spent <c>once per ...</c> windows are gone. The runtime refuses it too, which is what
        /// catches a content verb containing one; this says so before the effect ever runs.
        /// </summary>
        private void CheckUntilTransforms(Body body)
        {
            if (body.Block == null) return;

            // A game whose own `transform` predates the built-in one runs that instead, and it may
            // well be undoable. Nothing here is true of it.
            if (_content.FindVerb("transform") != null || _options.HostVerbs.Contains("transform")) return;

            var walker = new UntilTransforms();
            walker.VisitBlock(body.Block);

            foreach (CommandNode command in walker.Found)
            {
                Error(TransformInsideUntil,
                    "`transform` cannot be undone, so an `until` block cannot hold one: the stats, statuses and used-up limits it replaces are gone. " +
                    "Transform it outside the block, or apply a status instead.",
                    command.Span);
            }
        }

        /// <summary>
        /// The <c>transform</c> statements lexically inside an <c>until</c> block. A <c>next turn:</c>
        /// or <c>in N turns:</c> block written inside one runs later, on its own, with none of the
        /// block's undo scope, so it starts clear rather than inheriting the <c>until</c>.
        /// </summary>
        private sealed class UntilTransforms : AstWalker
        {
            private int _depth;

            public List<CommandNode> Found { get; } = new List<CommandNode>();

            public override void VisitStatement(StatementNode statement)
            {
                switch (statement)
                {
                    case CommandNode command:
                        if (_depth > 0 && string.Equals(command.Verb, "transform", StringComparison.OrdinalIgnoreCase)) Found.Add(command);
                        break;

                    case ScheduleNode schedule:
                    {
                        int outer = _depth;
                        _depth = schedule.Kind == ScheduleKind.Until ? _depth + 1 : 0;
                        VisitBlock(schedule.Body);
                        _depth = outer;
                        return;
                    }
                }

                base.VisitStatement(statement);
            }
        }

        /// <summary>A name spelt as content would have to write it, with the quotes it needs.</summary>
        private static string Written(ExprNode node) => node switch
        {
            StringExpr text => "\"" + text.Value + "\"",
            NameExpr name => name.Name,
            _ => AstPrinter.Print(node),
        };

        /// <summary>The verbs a body may use beyond the ones any effect can: a test's, or a scenario's.</summary>
        private static IReadOnlyCollection<string> BlockVerbs(Body body) => body.Kind switch
        {
            BodyKind.Test => DslTestRunner.TestVerbs,
            BodyKind.Scenario => Scenario.Verbs,
            _ => Array.Empty<string>(),
        };

        /// <summary>
        /// The rest of a CT301 message when the verb is a real one written in the wrong kind of
        /// block, which is a likelier slip than a typo and deserves to be said rather than guessed at.
        /// </summary>
        private static string Elsewhere(Body body, string verb)
        {
            if (body.Kind != BodyKind.Test && DslTestRunner.TestVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase))
            {
                return body.Kind == BodyKind.Scenario
                    ? " It only exists inside `test` blocks. A `scenario` states the fight; a bot plays it."
                    : " It only exists inside `test` blocks.";
            }

            if (body.Kind != BodyKind.Scenario && Scenario.IsVerb(verb)) return " It only exists inside `scenario` blocks.";

            return string.Empty;
        }

        /// <summary>
        /// CT317, CT318 and CT319: the three lines only a scenario has. A scenario that names no
        /// enemy fights nothing; a count too small to measure with says less than it looks as if it
        /// does; and an <c>expect</c> in a scenario checks one measurement over every run rather
        /// than the state of one game, which a scenario never has to hand.
        /// </summary>
        private void CheckScenario(Body body)
        {
            if (body.Kind != BodyKind.Scenario) return;

            bool fights = false;
            foreach (CommandNode command in body.Facts.Commands)
            {
                switch (command.Verb.ToLowerInvariant())
                {
                    case "battle":
                        if (TestNames(command).Any()) fights = true;
                        else Warn(NothingToFight, "`battle` names no enemy, so it fights nothing.", command.Span, "battle \"Name\"");
                        break;
                    case "runs":
                        CheckRunCount(command);
                        break;
                    case "expect":
                        CheckMeasurement(command);
                        break;
                }
            }

            if (fights) return;

            string name = ((ScenarioDeclNode)body.Anchor).Name;
            Warn(NothingToFight, $"scenario \"{name}\" has no `battle` line, so there is nothing to play and nothing to measure.",
                body.Anchor.Span, "battle \"Name\"");
        }

        /// <summary>CT318: <c>runs</c> takes a whole count, and a small one measures very little.</summary>
        private void CheckRunCount(CommandNode command)
        {
            if (!(command.Arguments.FirstOrDefault() is NumberExpr { Unit: null } count) || !IsWholeCount(count.Value))
            {
                Error(RunCount, "`runs` needs a whole number of runs, one or more.", command.Span, "runs 500");
                return;
            }

            if (count.Value >= FewRuns) return;

            Info(RunCount, $"{count.Value} runs is few enough that the same content answers differently each time. {FewRuns} or more settles it down.", command.Span);
        }

        /// <summary>
        /// CT319: <c>expect no stalls</c> or <c>expect wins &gt;= 55%</c>. A scenario plays many
        /// games, so it has no one <c>enemy.hp</c> to compare: the words it can name are in
        /// <see cref="Scenario.Metrics"/>.
        /// </summary>
        private void CheckMeasurement(CommandNode command)
        {
            IReadOnlyList<ExprNode> arguments = command.Arguments;

            // `expect no stalls`: the same as `expect stalls == 0`, and how it reads best.
            if (arguments.Count == 2 && arguments[0] is NameExpr negated && string.Equals(negated.Name, "no", StringComparison.OrdinalIgnoreCase))
            {
                _reported.Add(negated);
                RequireMeasurement(arguments[1]);
                return;
            }

            if (arguments.Count == 1 && arguments[0] is BinaryExpr comparison && AstPrinter.IsComparison(comparison.Operator))
            {
                RequireMeasurement(comparison.Left);
                if (comparison.Right is NumberExpr { Unit: null or "%" }) return;

                Error(UnknownMeasurement, "A scenario's `expect` compares a measurement with a plain number or a percentage.",
                    comparison.Right.Span, "expect wins >= 55%");
                return;
            }

            foreach (ExprNode argument in arguments) Measured(argument);
            Error(UnknownMeasurement,
                "A scenario's `expect` checks one measurement over every run, not the state of one game, which a scenario never has to hand.",
                command.Span, "expect no errors");
        }

        private void RequireMeasurement(ExprNode node)
        {
            if (node is NameExpr measurement)
            {
                _reported.Add(measurement);
                if (Scenario.IsMetric(measurement.Name)) return;

                Error(UnknownMeasurement, $"A scenario does not measure `{measurement.Name}`.", node.Span,
                    Suggest.Closest(measurement.Name, Scenario.Metrics));
                return;
            }

            Measured(node);
            Error(UnknownMeasurement,
                $"`{AstPrinter.Print(node)}` is not something a scenario measures; it measures {string.Join(", ", Scenario.Metrics)}.",
                node.Span);
        }

        /// <summary>
        /// Marks the words of an <c>expect</c> as already answered for. A scenario's <c>expect</c>
        /// never reads them as names, so CT302 saying `wins` is not a stat would be a second and
        /// wrong complaint about a line CT319 has just explained.
        /// </summary>
        private void Measured(ExprNode node)
        {
            switch (node)
            {
                case NameExpr name:
                    _reported.Add(name);
                    break;
                case UnaryExpr unary:
                    Measured(unary.Operand);
                    break;
                case BinaryExpr binary:
                    Measured(binary.Left);
                    Measured(binary.Right);
                    break;
                case MemberExpr member:
                    Measured(member.Target);
                    break;
            }
        }

        private void CheckNames(Body body)
        {
            // `emit sparked` and `use Chomp` name an event and a move, not values. CT304/CT305 and
            // CT312 check those words instead.
            foreach (CommandNode command in body.Facts.Commands)
            {
                bool namesSomething = string.Equals(command.Verb, "emit", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(command.Verb, "use", StringComparison.OrdinalIgnoreCase);
                if (namesSomething && command.Arguments.FirstOrDefault() is NameExpr word) _reported.Add(word);
            }

            // Positions where only a definition makes sense are errors: the runtime will fail there.
            foreach (CommandNode command in body.Facts.Commands)
            {
                string verb = command.Verb.ToLowerInvariant();
                switch (verb)
                {
                    case "apply":
                        RequireDefinition(body, command.Arguments.FirstOrDefault(), "status", "keyword");
                        break;
                    case "create":
                        RequireDefinition(body, command.Arguments.FirstOrDefault(), "card", "enemy", "actor", "relic", "item", "status", "keyword", "ability");
                        break;
                    case "shuffle":
                        if (command.Arguments.FirstOrDefault() is NameExpr shuffled && !IsReserved(shuffled.Name) && !IsLocal(body, shuffled.Name))
                            RequireDefinition(body, shuffled, "card");
                        break;
                    // What a transform becomes is a definition, as `create`'s argument is. A name
                    // bound by a verb, such as `transform target into discovered`, is a local and
                    // RequireDefinition leaves it alone.
                    case "transform":
                        RequireDefinition(body, command.Clause("into") ?? command.Clause("to"), "card", "enemy", "actor", "relic", "item", "status", "keyword", "ability");
                        break;
                    case "add":
                        if (command.Arguments.FirstOrDefault() is NameExpr)
                            RequireDefinition(body, command.Arguments[0], "status", "keyword");
                        break;
                    case "gain":
                    case "lose":
                        if (command.Arguments.Count > 1 && command.Arguments[1] is NameExpr named && StartsUpper(named.Name))
                            RequireDefinition(body, named, "status", "keyword");
                        break;
                }
            }

            if (body.Kind == BodyKind.Test || body.Kind == BodyKind.Scenario) CheckSetupNames(body);

            foreach (CallExpr call in body.Facts.Calls)
            {
                if (!string.Equals(call.Name, "has", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (ExprNode argument in call.Arguments)
                {
                    if (argument is NameExpr name && StartsUpper(name.Name)) RequireDefinition(body, name, "status", "keyword");
                }
            }

            // In `play Strike on enemy`, `replay card on target` and `change hp -5 on target`, `on`
            // names who an action is aimed at rather than the stacks of a status.
            var aimed = new HashSet<ExprNode>();
            foreach (CommandNode command in body.Facts.Commands)
            {
                if (!AimingVerbs.Contains(command.Verb)) continue;
                foreach (ExprNode argument in command.Arguments)
                {
                    if (argument is BinaryExpr { Operator: BinaryOperator.On }) aimed.Add(argument);
                }

                // `play Cleanse by Sister on Templar`: `on` is an operator and `by` is a clause, so
                // the aim lands inside the clause's own value. It is still an aim, and reading it
                // as "that many stacks of the status Sister" is how it used to be reported.
                foreach (ClauseNode clause in command.Clauses)
                {
                    if (clause.Value is BinaryExpr { Operator: BinaryOperator.On }) aimed.Add(clause.Value);
                }
            }

            foreach (BinaryExpr on in body.Facts.OnExpressions)
            {
                if (aimed.Contains(on)) continue;
                if (on.Left is NameExpr name && !IsLocal(body, name.Name)) RequireDefinition(body, name, "status", "keyword");
            }

            foreach (MemberExpr member in body.Facts.Members)
            {
                if (!StartsUpper(member.Member) || member.Target is NameExpr { Name: "event" }) continue;
                if (_content.FindAny(member.Member, "status", "keyword") != null) continue;

                Error(UnknownName, $"No status called `{member.Member}` is defined.", member.Span,
                    Suggest.Closest(member.Member, StatusNames()));
            }

            foreach (QualifiedExpr qualified in body.Facts.Qualified)
            {
                string? kind = qualified.Qualifier switch
                {
                    "status" => "status",
                    "card" => "card",
                    _ => null,
                };
                if (kind == null || _content.Find(qualified.Name, kind) != null) continue;

                Error(UnknownName, $"No {kind} called `{qualified.Name}` is defined.", qualified.Span,
                    Suggest.Closest(qualified.Name, _content.Definitions.Where(d => d.KindName == kind).Select(d => d.Name)));
            }

            // Everything else is a warning: a host could be supplying the name at runtime.
            foreach (NameExpr name in body.Facts.Names)
            {
                if (_reported.Contains(name) || IsKnownName(body, name.Name)) continue;

                string message = StartsUpper(name.Name)
                    ? $"`{name.Name}` is not defined anywhere in the loaded content."
                    : $"`{name.Name}` is not a stat, name or local the rules know; at runtime this is an error.";

                // "Host" is what the docs call the entity a status is on; content calls it `owner`.
                string? suggestion = string.Equals(name.Name, "host", StringComparison.OrdinalIgnoreCase)
                    ? "owner"
                    : Suggest.Closest(name.Name, NameCandidates(body));
                Warn(UnknownName, message, name.Span, suggestion);
            }
        }

        /// <summary>
        /// The lines of a test or a scenario that name a definition, which the runner looks up and
        /// fails without: <c>hand</c>, <c>deck</c> and <c>discard_pile</c> name cards, <c>relic</c> a
        /// relic or item, <c>grant</c> and <c>cast</c> an ability, <c>play</c> a card, <c>battle</c>
        /// enemies, and <c>enemy</c> an enemy when its first word stands alone as a name
        /// (<c>enemy Ghoul hp 12</c>). Each verb is checked only in the kind of block it belongs to,
        /// so a verb written in the wrong one is CT301 on its own and not CT302 as well.
        /// </summary>
        private void CheckSetupNames(Body body)
        {
            foreach (CommandNode command in body.Facts.Commands)
            {
                switch (command.Verb.ToLowerInvariant())
                {
                    case "hand":
                    case "deck":
                    case "discard_pile":
                        RequireDefinitions(body, TestNames(command), "card");
                        break;
                    case "relic":
                        RequireDefinitions(body, TestNames(command), "relic", "item");
                        break;
                    case "grant":
                        RequireDefinitions(body, TestNames(command), "ability");
                        break;

                    // `hero Crusader hp 30`: the name always comes first, and what follows it is
                    // stat pairs, so only the first word is a definition to look up.
                    case "hero" when body.Kind == BodyKind.Test:
                        RequireDefinition(body, command.Arguments.FirstOrDefault(), "hero");
                        break;
                    case "battle" when body.Kind == BodyKind.Scenario:
                        RequireDefinitions(body, TestNames(command), "enemy");
                        break;
                    case "play" when body.Kind == BodyKind.Test:
                        RequireDefinition(body, Aimed(command.Arguments.FirstOrDefault()), "card");
                        break;
                    case "cast" when body.Kind == BodyKind.Test:
                        RequireDefinition(body, Aimed(command.Arguments.FirstOrDefault()), "ability");
                        break;
                    case "enemy" when body.Kind == BodyKind.Test:
                        RequireDefinition(body, TestEnemyName(command), "enemy");
                        break;

                    // `board "Corridor"` picks the board this test or this scenario is fought on. A
                    // board is out of name lookup, the way a resource is, so it is checked here
                    // against the boards rather than left to CT302, which would call every board
                    // name undefined.
                    case "board":
                        RequireBoard(body, TestNames(command).FirstOrDefault());
                        break;
                }
            }
        }

        /// <summary>The board a test names, which the runner looks up and fails without.</summary>
        private void RequireBoard(Body body, ExprNode? node)
        {
            if (node == null) return;
            _reported.Add(node);

            string name = node is StringExpr text ? text.Value : ((NameExpr)node).Name;
            if (_content.Board(name) != null) return;

            Error(UnknownName,
                $"No board called `{name}` is declared." + (_content.Boards.Count == 0
                    ? " This content declares none, so every battle is fought on the default board."
                    : string.Empty),
                node.Span,
                Suggest.Closest(name, _content.Boards.Select(b => b.Name)));
        }

        /// <summary>Checks each name a line lists once, so <c>deck Strik, Strik</c> is one error.</summary>
        private void RequireDefinitions(Body body, IEnumerable<ExprNode> nodes, params string[] kinds)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ExprNode node in nodes)
            {
                string name = node is StringExpr text ? text.Value : ((NameExpr)node).Name;
                if (seen.Add(name)) RequireDefinition(body, node, kinds);
                else _reported.Add(node);
            }
        }

        /// <summary><c>Strike</c> in <c>play Strike on enemy</c>.</summary>
        private static ExprNode? Aimed(ExprNode? argument) =>
            argument is BinaryExpr { Operator: BinaryOperator.On } on ? on.Left : argument;

        /// <summary>
        /// The names a test verb lists, the way the runner reads them: <c>deck Strike Defend</c>,
        /// <c>deck Strike, Defend</c>, <c>deck "Twin Strike", Defend</c>. A name after a comma is a
        /// flag to the parser, which keeps it in lower case.
        /// </summary>
        private static IEnumerable<ExprNode> TestNames(CommandNode command)
        {
            foreach (ExprNode argument in command.Arguments)
            {
                if (argument is NameExpr || argument is StringExpr) yield return argument;
            }
            foreach (ClauseNode clause in command.Clauses)
            {
                if (clause.Value == null) yield return new NameExpr(clause.Keyword, clause.Span);
                else if (clause.Value is NameExpr || clause.Value is StringExpr) yield return clause.Value;
            }
        }

        /// <summary>
        /// The first word of a test's <c>enemy</c> line when the runner takes it for an enemy's name:
        /// a bare word, not a status, with no value of its own after it, as in <c>enemy Ghoul</c> and
        /// <c>enemy Ghoul hp 12</c>, but not <c>enemy hp 12</c>. A quoted first word that names no
        /// enemy is a label for a plain one, so it is never checked.
        /// </summary>
        private NameExpr? TestEnemyName(CommandNode command)
        {
            IReadOnlyList<ExprNode> nodes = command.Arguments;
            if (nodes.Count % 2 == 0 || !(nodes[0] is NameExpr name)) return null;
            if (nodes.Count > 1 && !(nodes[1] is NameExpr)) return null;
            if (_content.FindAny(name.Name, "status", "keyword") != null) return null;
            return name;
        }

        private void RequireDefinition(Body body, ExprNode? node, params string[] kinds)
        {
            string? written = node switch
            {
                NameExpr name => name.Name,
                StringExpr text => text.Value,
                _ => null,
            };
            if (written == null || node == null) return;
            if (node is NameExpr local && IsLocal(body, local.Name)) return;

            _reported.Add(node);
            if (_content.FindAny(written, kinds) != null) return;

            EntityDefinition? other = _content.Find(written);
            if (other != null)
            {
                Error(UnknownName, $"`{written}` is {A(other.KindName)}, but {A(string.Join(" or ", kinds.Take(2)))} is needed here.", node.Span);
                return;
            }

            Error(UnknownName, $"Nothing called `{written}` is defined.", node.Span,
                Suggest.Closest(written, _content.Definitions.Where(d => kinds.Contains(d.KindName)).Select(d => d.Name)));
        }

        private void CheckTags(Body body)
        {
            foreach (QualifiedExpr qualified in body.Facts.Qualified)
            {
                if (qualified.Qualifier != "tag" && qualified.Qualifier != "keyword") continue;
                if (_tags.Contains(qualified.Name)) continue;

                Warn(UnknownTag, $"No definition has the tag `{qualified.Name}`, so this never matches.", qualified.Span,
                    Suggest.Closest(qualified.Name, _tags));
            }
        }

        private void CheckEventUse(Body body)
        {
            bool eventAvailable = body.Kind == BodyKind.Listener || body.Kind == BodyKind.Verb;
            if (!eventAvailable)
            {
                foreach (NameExpr name in body.Facts.Names)
                {
                    if (!string.Equals(name.Name, "event", StringComparison.OrdinalIgnoreCase) || IsLocal(body, name.Name)) continue;
                    _reported.Add(name);
                    Error(EventOutsideListener, "`event` is only available inside an `on ...:` listener.", name.Span);
                }
            }

            if (body.Listener != null && body.Listener.Phase == EventPhase.After)
            {
                foreach (CommandNode command in body.Facts.Commands)
                {
                    if (!string.Equals(command.Verb, "cancel", StringComparison.OrdinalIgnoreCase)) continue;
                    Error(CancelAfterEvent, $"`cancel` cannot undo `{body.Listener.EventName}` after it has happened; listen to `before_{EventPart(body.Listener.EventName)}` instead.", command.Span);
                }
            }
        }

        private void CheckStacks(Body body)
        {
            if (body.Kind == BodyKind.Verb || body.Kind == BodyKind.Test || body.Owner == null) return;
            if (body.Owner.Kind == EntityKind.Status || body.Owner.Kind == EntityKind.Keyword) return;

            foreach (NameExpr name in body.Facts.Names)
            {
                if (!string.Equals(name.Name, "stacks", StringComparison.OrdinalIgnoreCase) || IsLocal(body, name.Name)) continue;
                _reported.Add(name);
                Warn(StacksOutsideStatus, $"`stacks` only means something inside a status; in {body.Owner} it reads a stat that is probably never set.", name.Span);
            }
        }

        private void CheckMoves(Body body)
        {
            foreach (CommandNode command in body.Facts.Commands)
            {
                if (!string.Equals(command.Verb, "use", StringComparison.OrdinalIgnoreCase)) continue;
                string? move = WordAt(command, 0);
                if (move == null) continue;

                if (body.Owner == null || body.Owner.Moves.Count == 0)
                {
                    if (body.Kind != BodyKind.Verb)
                        Error(UnknownMove, "Only enemies with moves can `use` one.", command.Span);
                    continue;
                }

                if (body.Owner.Moves.Any(m => string.Equals(m.Name, move, StringComparison.OrdinalIgnoreCase))) continue;

                Error(UnknownMove, $"`{body.Owner.Name}` has no move called `{move}`.", command.Span,
                    Suggest.Closest(move, body.Owner.Moves.Select(m => m.Name)));
            }
        }

        /// <summary>
        /// CT314: <c>for N turns</c> on a <c>duration</c> or <c>refresh</c> status, longer than the
        /// amount applied. Such a status loses its duration on its host's turns and lasts as many of
        /// them as the amount, while <c>for</c> only adds a deadline, so <c>apply Weak for 2 turns</c>
        /// is Weak for one turn. A <c>for</c> no longer than the amount can cut the status short, and
        /// one on a status that does not tick down on turns, or that may land on a card, which has no
        /// turns, is what removes it, so those are left alone.
        /// </summary>
        private void CheckStatusLengths(Body body)
        {
            foreach (CommandNode command in body.Facts.Commands)
            {
                bool apply = string.Equals(command.Verb, "apply", StringComparison.OrdinalIgnoreCase);
                bool add = string.Equals(command.Verb, "add", StringComparison.OrdinalIgnoreCase);
                if (!apply && !add) continue;

                // Seconds and ticks belong to a real-time clock, where a duration status may never
                // see a turn end and `for` is what removes it.
                if (!(command.Clause("for") is NumberExpr { Unit: "turn" or "turns" or "t" } length) || !IsWholeCount(length.Value)) continue;

                // `add "Weak"` adds a tag, so only `apply` names a status with a string.
                string? name = command.Arguments.FirstOrDefault() switch
                {
                    NameExpr named when !IsLocal(body, named.Name) => named.Name,
                    StringExpr quoted when apply => quoted.Value,
                    _ => null,
                };
                EntityDefinition? status = name == null ? null : _content.FindAny(name, "status", "keyword");
                if (status == null || (status.Stacking != StackingMode.Duration && status.Stacking != StackingMode.Refresh)) continue;
                if (status.DecayAmount < Num.One || !(status.DecayOn is "turn_end" or "turn_start")) continue;
                if (!LandsOnActors(body, command.Clause("to"))) continue;

                Num amount = Num.One;
                if (command.Arguments.Count > 1)
                {
                    if (!(command.Arguments[1] is NumberExpr { Unit: null } written) || !IsWholeCount(written.Value)) continue;
                    amount = written.Value;
                }
                if (amount >= length.Value) continue;

                string turns = length.Value + (length.Value == Num.One ? " turn" : " turns");
                ExprNode? to = command.Clause("to");
                Warn(ForOnDurationStatus,
                    $"`for {turns}` does not make {status.Name} last {turns}. A `stacking {status.Stacking.ToString().ToLowerInvariant()}` status lasts as many of its host's turns as the amount applied, here {amount}; `for` only sets a deadline, which can end it sooner but never later.",
                    command.Span,
                    $"{command.Verb.ToLowerInvariant()} {AstPrinter.Name(status.Name)} {length.Value}" + (to == null ? string.Empty : " to " + AstPrinter.Print(to)));
            }
        }

        /// <summary>
        /// Whether a status applied to <paramref name="to"/> lands on actors, whose turns tick it
        /// down: a word that only ever means actors, or, in a card's or ability's effect or an enemy's
        /// move, the effect's own target. Anything else may be a card.
        /// </summary>
        private static bool LandsOnActors(Body body, ExprNode? to) => to switch
        {
            null => body.Kind == BodyKind.Effect,
            NameExpr { Name: var name } when IsLocal(body, name) => false,
            NameExpr { Name: var name } when string.Equals(name, "target", StringComparison.OrdinalIgnoreCase) => body.Kind == BodyKind.Effect,
            NameExpr { Name: var name } => ActorWords.Contains(name) || (body.Kind == BodyKind.Test && IsTestBinding(name)),
            SelectorExpr selector => LandsOnActors(body, selector.Source),
            WhereExpr where => LandsOnActors(body, where.Source),
            _ => false,
        };

        // Whole-content checks -----------------------------------------------------------------

        /// <summary>
        /// CT313: a line in a declaration that ends in a colon but is neither a listener nor a block
        /// the engine runs. The parser has to accept it, because a game may run blocks of its own from
        /// C# (<see cref="LintOptions.HostBlocks"/>), so a listener written another way, such as
        /// <c>when card_played:</c>, would otherwise load cleanly and never fire.
        /// </summary>
        private void CheckBlocks()
        {
            IEnumerable<EntityDefinition> definitions = _content.Definitions
                .OrderBy(d => d.Syntax.Span.File, StringComparer.Ordinal)
                .ThenBy(d => d.Syntax.Span.Line);

            foreach (EntityDefinition definition in definitions)
            {
                foreach (MemberNode member in definition.Syntax.Members)
                {
                    if (!(member is BlockMemberNode block)) continue;
                    if (RunBlocks.Contains(block.Name) || _options.HostBlocks.Contains(block.Name)) continue;

                    string header = string.Join(" ", new[] { block.Name }.Concat(block.Arguments.Select(AstPrinter.Print)));
                    Warn(UnknownBlock,
                        $"`{header}:` in {definition} never runs. A line ending in `:` is only a label unless it is `effect:`, `move ...:` or a listener, which starts with `on`.",
                        block.Span,
                        SuggestBlock(block));
                }
            }
        }

        /// <summary>
        /// What a CT313 block was probably meant to be: a listener when an event can be found in its
        /// first line, else the closest of <c>effect:</c> and <c>move</c>, else a listener on the
        /// closest event.
        /// </summary>
        private string? SuggestBlock(BlockMemberNode block)
        {
            var words = new List<(string Word, IReadOnlyList<ExprNode>? Filter)> { (block.Name, null) };
            foreach (ExprNode argument in block.Arguments) words.AddRange(HeaderWords(argument));

            // The event: a word that names one, or names one once `on_` or `when_` is taken off it, or
            // two or three words that do once joined (`at turn start`), or else what follows `on`, or
            // `when` and the like, as long as it can stand for an event (see TakeUnknownEvent). A
            // timing written into the word, as in `before_damaged`, stays with it.
            int at = words.FindIndex(w => IsKnownListenerEvent(w.Word));
            if (at < 0) at = words.FindIndex(w => IsKnownListenerEvent(StripListenerPrefix(w.Word)));
            if (at < 0) JoinSplitEvent(words, ref at);
            if (at < 0)
            {
                int on = words.FindIndex(w => string.Equals(w.Word, "on", StringComparison.OrdinalIgnoreCase));
                if (on >= 0 && on + 1 < words.Count) at = on + 1;
                else if (ListenerWords.Contains(block.Name) && words.Count > 1 && !ListenerWords.Contains(words[1].Word)) at = 1;

                if (at >= 0 && !TakeUnknownEvent(words, at)) return null;
            }

            if (at >= 0)
            {
                string eventName = StripListenerPrefix(words[at].Word);

                // `every 2 turns:` keeps its interval, which is a number rather than a word.
                if (string.Equals(eventName, "every", StringComparison.OrdinalIgnoreCase))
                {
                    NumberExpr? interval = block.Arguments.OfType<NumberExpr>().FirstOrDefault();
                    return interval == null ? null : $"on every {AstPrinter.Print(interval)}:";
                }

                if (Parser.NormalizeEventName(eventName).Phase == EventPhase.After && !eventName.StartsWith("after_", StringComparison.OrdinalIgnoreCase))
                    eventName = TimingWrittenBefore(words, at, block.Name) + eventName;

                string filter = words[at].Filter is IReadOnlyList<ExprNode> clauses ? "(" + string.Join(", ", clauses.Select(AstPrinter.Print)) + ")" : string.Empty;

                string limit = string.Empty;
                for (int i = 0; i + 2 < words.Count; i++)
                {
                    if (string.Equals(words[i].Word, "once", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(words[i + 1].Word, "per", StringComparison.OrdinalIgnoreCase)
                        && words[i + 2].Word.ToLowerInvariant() is "turn" or "battle" or "run" or "chain")
                        limit = " once per " + words[i + 2].Word.ToLowerInvariant();
                }

                return $"on {eventName}{filter}{limit}:";
            }

            string? known = Suggest.Closest(block.Name, RunBlocks);
            if (known == "effect") return "effect:";
            if (known == "move") return string.Join(" ", new[] { "move" }.Concat(block.Arguments.Select(AstPrinter.Print))) + ":";

            // Not `every`, which needs an interval that a label with none cannot supply.
            IEnumerable<string> events = ListenableEvents().Where(e => !string.Equals(e, BuiltinEvents.Every, StringComparison.OrdinalIgnoreCase));
            (string named, EventPhase timing) = Parser.NormalizeEventName(StripListenerPrefix(block.Name));
            string? closeEvent = SuggestEvent(named, events, partial: false);
            return closeEvent == null ? null : $"on {TimingPrefix(timing)}{closeEvent}:";
        }

        /// <summary>
        /// Whether the words from <paramref name="at"/> to the colon, or to a <c>once per</c> or
        /// <c>priority</c>, can stand for the event when the linter knows none of them. An event
        /// close to them joined takes their place, keeping any timing and scope written with them:
        /// <c>turn starts</c> and <c>start of turn</c> are <c>turn_start</c>, and
        /// <c>before_damagd</c> is <c>before_damaged</c>. Failing that, one word is kept, as an
        /// event the game may raise from C#, while several are prose that suggests no listener, as
        /// in <c>whenever player takes damage:</c>.
        /// </summary>
        private bool TakeUnknownEvent(List<(string Word, IReadOnlyList<ExprNode>? Filter)> words, int at)
        {
            var rest = words.Skip(at)
                .TakeWhile(w => !string.Equals(w.Word, "once", StringComparison.OrdinalIgnoreCase) && !string.Equals(w.Word, "priority", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (rest.Count == 0) return false;

            (string named, EventPhase timing) = Parser.NormalizeEventName(StripListenerPrefix(string.Join("_", rest.Select(w => w.Word))));
            int dot = named.LastIndexOf('.');
            string? close = SuggestEvent(named.Substring(dot + 1), ListenableEvents(), partial: false);
            if (close != null)
            {
                words[at] = (TimingPrefix(timing) + named.Substring(0, dot + 1) + close, rest[rest.Count - 1].Filter);
                return true;
            }
            return rest.Count == 1;
        }

        /// <summary>
        /// A timing written as a word of its own just before the event, as in <c>before damaged</c>,
        /// <c>once per battle instead of died</c> and <c>when before owner.damaged</c>, or else as the
        /// block's first word, as its prefix: <c>before_</c>, <c>instead_of_</c> or none.
        /// </summary>
        private static string TimingWrittenBefore(List<(string Word, IReadOnlyList<ExprNode>? Filter)> words, int at, string first)
        {
            string previous = at > 0 ? words[at - 1].Word : string.Empty;
            if (string.Equals(previous, "of", StringComparison.OrdinalIgnoreCase) && at > 1) previous = words[at - 2].Word + "_of";

            foreach (string word in new[] { previous, first })
            {
                switch (word.ToLowerInvariant())
                {
                    case "before":
                        return "before_";
                    case "instead":
                    case "instead_of":
                        return "instead_of_";
                }
            }
            return string.Empty;
        }

        /// <summary>The prefix that writes a timing into an event's name: <c>before_</c>, <c>instead_of_</c> or none.</summary>
        private static string TimingPrefix(EventPhase timing) => timing switch
        {
            EventPhase.Before => "before_",
            EventPhase.Instead => "instead_of_",
            _ => string.Empty,
        };

        /// <summary>The events a listener can hear: the built-in ones, those content emits and the game's own.</summary>
        private IEnumerable<string> ListenableEvents() => BuiltinEvents.Names.Concat(_emitted.Keys).Concat(_options.HostEvents);

        /// <summary>
        /// The words of a block's first line in order, each with the <c>(...)</c> after it, if any.
        /// <c>once per battle on card_played</c> arrives as the expressions <c>per</c> and
        /// <c>battle on card_played</c>, and comes back out as its five words.
        /// </summary>
        private static IEnumerable<(string Word, IReadOnlyList<ExprNode>? Filter)> HeaderWords(ExprNode node)
        {
            switch (node)
            {
                case NameExpr name:
                    yield return (name.Name, null);
                    break;
                case MemberExpr member:
                    yield return (AstPrinter.Print(member), null);
                    break;
                case CallExpr call:
                    yield return ((call.Receiver == null ? string.Empty : AstPrinter.Print(call.Receiver) + ".") + call.Name, call.Arguments);
                    break;
                case BinaryExpr binary:
                    foreach (var word in HeaderWords(binary.Left)) yield return word;
                    yield return (AstPrinter.Symbol(binary.Operator), null);
                    foreach (var word in HeaderWords(binary.Right)) yield return word;
                    break;
            }
        }

        /// <summary>
        /// <c>at turn start:</c> writes <c>turn_start</c> as two words. Finds the first run of two or
        /// three words that names an event once joined with <c>_</c>, and puts the joined event in
        /// place of its first word.
        /// </summary>
        private void JoinSplitEvent(List<(string Word, IReadOnlyList<ExprNode>? Filter)> words, ref int at)
        {
            for (int start = 0; start < words.Count; start++)
            {
                for (int length = 2; length <= 3 && start + length <= words.Count; length++)
                {
                    string joined = string.Join("_", words.Skip(start).Take(length).Select(w => w.Word));
                    if (!IsKnownListenerEvent(joined)) continue;

                    words[start] = (joined, words[start + length - 1].Filter);
                    at = start;
                    return;
                }
            }
        }

        /// <summary>True for an event a listener could hear, written with or without a scope and timing.</summary>
        private bool IsKnownListenerEvent(string word)
        {
            if (word.Length == 0 || ListenerWords.Contains(word)) return false;
            (string name, _) = Parser.NormalizeEventName(word);
            return IsKnownEvent(EventPart(name));
        }

        /// <summary>
        /// <c>on_turn_start</c> and <c>when_damaged</c>: the event with the listener word taken off.
        /// <c>before_</c> and <c>instead_of_</c> stay, because they are the timing of the event the
        /// listener hears, and <c>on damaged</c> in place of <c>on before_damaged</c> is a different
        /// listener, one that can no longer <c>cancel</c>.
        /// </summary>
        private static string StripListenerPrefix(string word)
        {
            foreach (string prefix in ListenerWords)
            {
                if (TimingWords.Contains(prefix)) continue;
                if (word.Length > prefix.Length + 1 && word.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
                    return word.Substring(prefix.Length + 1);
            }
            return word;
        }

        /// <summary>
        /// The event among <paramref name="candidates"/> that <paramref name="written"/> most
        /// likely meant. First the same words in another order or form, as <c>start_of_turn</c> is
        /// <c>turn_start</c> and <c>play_card</c> is <c>card_played</c>; then a misspelling, as
        /// <c>turn_strat</c> is <c>turn_start</c>; then, when <paramref name="partial"/>, the event
        /// with the most words that <paramref name="written"/> has among others, as
        /// <c>card_drawn</c> has <c>drawn</c>.
        /// </summary>
        private static string? SuggestEvent(string written, IEnumerable<string> candidates, bool partial)
        {
            List<string> events = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> words = EventWords(written);

            string? same = events.FirstOrDefault(e => SameWords(EventWords(e), words));
            if (same != null) return same;

            string? close = Suggest.Closest(written, events);
            if (close != null || !partial) return close;

            string? best = null;
            int bestCount = 0;
            foreach (string candidate in events)
            {
                List<string> needed = EventWords(candidate);
                if (needed.Count <= bestCount || needed.Count >= words.Count) continue;
                if (needed.All(n => words.Any(w => SameWord(n, w))))
                {
                    best = candidate;
                    bestCount = needed.Count;
                }
            }
            return best;
        }

        /// <summary>The words of an event's name that carry meaning: <c>start_of_turn</c> is start, turn.</summary>
        private static List<string> EventWords(string name) =>
            name.Split(new[] { '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !FillerWords.Contains(w))
                .ToList();

        /// <summary>The same words in any order, each in any of its forms.</summary>
        private static bool SameWords(List<string> a, List<string> b)
        {
            if (a.Count == 0 || a.Count != b.Count) return false;
            var unmatched = new List<string>(b);
            foreach (string word in a)
            {
                int at = unmatched.FindIndex(w => SameWord(word, w));
                if (at < 0) return false;
                unmatched.RemoveAt(at);
            }
            return true;
        }

        /// <summary><c>played</c> and <c>play</c>, <c>drawn</c> and <c>draw</c>, <c>begin</c> and <c>start</c>.</summary>
        private static bool SameWord(string a, string b) => WordForms(a).Intersect(WordForms(b)).Any();

        private static IEnumerable<string> WordForms(string word)
        {
            string lower = word.ToLowerInvariant();
            if (EventSynonyms.TryGetValue(lower, out string? same))
            {
                yield return same;
                yield break;
            }

            yield return lower;
            if (lower.Length > 4 && lower.EndsWith("ied", StringComparison.Ordinal)) yield return lower.Substring(0, lower.Length - 3) + "y";
            foreach (string ending in WordEndings)
            {
                if (lower.Length - ending.Length >= 3 && lower.EndsWith(ending, StringComparison.Ordinal))
                    yield return lower.Substring(0, lower.Length - ending.Length);
            }
        }

        /// <summary>
        /// CT315: a <c>duration</c> line on a <c>duration</c>, <c>refresh</c> or <c>both</c> status.
        /// Creating the status sets its duration from whoever applies it, as in <c>apply Weak 2</c>,
        /// so the line's number never reaches a status in play. On any other status it is a stat
        /// content may read, so it is left alone there.
        /// </summary>
        private void CheckIgnoredDurations()
        {
            // The number is still readable where the definition stands in for the status: in a
            // `discover` filter, or as `event.status.duration` before the status exists. Content that
            // reads a `duration` member anywhere, or a game that reads the stat from C#, keeps it.
            if (_options.HostNames.Contains("duration")) return;
            bool read = _bodies.Any(b =>
                b.Facts.Members.Any(m => string.Equals(m.Member, "duration", StringComparison.OrdinalIgnoreCase))
                || (b.Facts.Commands.Any(c => string.Equals(c.Verb, "discover", StringComparison.OrdinalIgnoreCase))
                    && b.Facts.Names.Any(n => string.Equals(n.Name, "duration", StringComparison.OrdinalIgnoreCase))));
            if (read) return;

            foreach (EntityDefinition definition in _content.Definitions.OrderBy(d => d.Syntax.Span.File, StringComparer.Ordinal).ThenBy(d => d.Syntax.Span.Line))
            {
                if (definition.Kind != EntityKind.Status && definition.Kind != EntityKind.Keyword) continue;
                if (definition.Stacking != StackingMode.Duration && definition.Stacking != StackingMode.Refresh && definition.Stacking != StackingMode.Both) continue;

                PropertyNode? duration = definition.Property("duration");
                if (duration == null) continue;

                // `text: "Lasts {duration} turns."` prints the number, which is a use of a kind.
                if (definition.Text?.IndexOf("{duration}", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                Warn(IgnoredDuration,
                    $"`duration` on {definition} does nothing: a `stacking {definition.Stacking.ToString().ToLowerInvariant()}` status takes its duration from whoever applies it, as in `apply {AstPrinter.Name(definition.Name)} 2`, never from its declaration.",
                    duration.Span);
            }
        }

        /// <summary>
        /// CT316: a line of its own on a card, such as <c>exhaust</c>, that names a tag with built-in
        /// behaviour. It loads as a property with no value, which is not even a stat, so the card is
        /// discarded as usual. The same goes for <c>buff</c> and <c>debuff</c> on a status. Only a
        /// bare line, or one set to <c>true</c> or <c>yes</c>, is reported: <c>power 3</c> and
        /// <c>attack 2</c> are stats content may read, the second by the <c>attack</c> verb.
        /// </summary>
        private void CheckTagProperties()
        {
            foreach (EntityDefinition definition in _content.Definitions.OrderBy(d => d.Syntax.Span.File, StringComparer.Ordinal).ThenBy(d => d.Syntax.Span.Line))
            {
                string[] tags = definition.Kind switch
                {
                    EntityKind.Card => CardBehaviourTags,
                    EntityKind.Status or EntityKind.Keyword => StatusBehaviourTags,
                    _ => Array.Empty<string>(),
                };
                if (tags.Length == 0) continue;

                foreach (MemberNode member in definition.Syntax.Members)
                {
                    if (!(member is PropertyNode property) || !tags.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (definition.HasTag(property.Name)) continue;

                    bool flagLike = property.Values.Count == 0
                        || (property.Values.Count == 1 && property.Values[0] is NameExpr { Name: var value }
                            && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)));
                    if (!flagLike) continue;

                    string tag = property.Name.ToLowerInvariant();
                    Warn(TagWrittenAsProperty,
                        $"`{property.Name}` on a line of its own does nothing to {definition}: `{tag}` works only as a tag.",
                        property.Span,
                        "tags " + string.Join(", ", definition.Tags.Concat(new[] { tag })));
                }
            }
        }

        private void CheckListenedEvents()
        {
            foreach (Body body in _bodies)
            {
                if (body.Listener == null) continue;
                string name = EventPart(body.Listener.EventName);
                if (IsKnownEvent(name)) continue;

                if (BuiltinEvents.TryParseStatChanged(name, out string stat))
                {
                    Warn(UnknownEvent, $"`{name}` is raised when `{stat}` changes, but nothing in the content has a stat called `{stat}`.", body.Listener.Span,
                        Suggest.Closest(stat, _stats) is string closeStat ? BuiltinEvents.StatChanged(closeStat) : null);
                    continue;
                }

                // The nearest event, whether misspelt (`turn_strat`), worded another way
                // (`start_of_turn`, `play_card`) or with words to spare (`card_drawn`).
                IEnumerable<string> events = ListenableEvents().Concat(_stats.OrderBy(s => s, StringComparer.Ordinal).Select(BuiltinEvents.StatChanged));
                Warn(UnknownEvent, $"Nothing raises an event called `{name}`: it is not built in and no content emits it.", body.Listener.Span,
                    SuggestEvent(name, events, partial: true));
            }
        }

        private void CheckEmittedEvents()
        {
            foreach (var emitted in _emitted)
            {
                if (_listened.Contains(emitted.Key) || _options.HostEvents.Contains(emitted.Key)) continue;
                Info(UnheardEvent, $"`{emitted.Key}` is emitted but no content listens for it.", emitted.Value.Span);
            }
        }

        /// <summary>
        /// Finds events whose listeners can raise the same event again, directly or through other
        /// events and content verbs. The runtime stops each listener after one pass, so this is
        /// information for the author rather than an error.
        /// </summary>
        private void CheckEventCycles()
        {
            var verbBodies = _bodies.Where(b => b.Kind == BodyKind.Verb).ToDictionary(b => b.Verb!.Name, StringComparer.OrdinalIgnoreCase);
            var edges = new SortedDictionary<string, List<(string To, ListenerNode Via)>>(StringComparer.Ordinal);

            foreach (Body body in _bodies)
            {
                if (body.Listener == null) continue;
                string from = EventPart(body.Listener.EventName);

                foreach (string raised in RaisedEvents(body.Facts, verbBodies, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).OrderBy(e => e, StringComparer.Ordinal))
                {
                    if (!_listened.Contains(raised)) continue;
                    if (!edges.TryGetValue(from, out var list)) edges[from] = list = new List<(string, ListenerNode)>();
                    if (!list.Any(e => e.To == raised)) list.Add((raised, body.Listener));
                }
            }

            var reportedCycles = new HashSet<string>(StringComparer.Ordinal);
            foreach (string start in edges.Keys)
            {
                var path = new List<(string Event, ListenerNode Via)>();
                FindCycles(start, start, edges, path, new HashSet<string>(StringComparer.Ordinal), reportedCycles);
            }
        }

        private void FindCycles(
            string start,
            string current,
            SortedDictionary<string, List<(string To, ListenerNode Via)>> edges,
            List<(string Event, ListenerNode Via)> path,
            HashSet<string> visiting,
            HashSet<string> reportedCycles)
        {
            if (!edges.TryGetValue(current, out var next)) return;
            visiting.Add(current);

            foreach (var (to, via) in next)
            {
                path.Add((current, via));
                if (to == start)
                {
                    string key = string.Join(",", path.Select(p => p.Event).OrderBy(e => e, StringComparer.Ordinal));
                    if (reportedCycles.Add(key))
                    {
                        string chain = string.Join(" → ", path.Select(p => $"`{p.Event}`")) + $" → `{start}`";
                        Info(EventCycle, $"These listeners can re-trigger each other: {chain}. Loop protection stops each after one pass; check that is intended.", path[0].Via.Span);
                    }
                }
                else if (!visiting.Contains(to) && string.CompareOrdinal(to, start) > 0)
                {
                    // Only walk to events that sort after the start, so every cycle is found once,
                    // from its smallest event.
                    FindCycles(start, to, edges, path, visiting, reportedCycles);
                }
                path.RemoveAt(path.Count - 1);
            }

            visiting.Remove(current);
        }

        /// <summary>
        /// Whether a command can really raise one of the events its verb's table lists. <c>gain</c>,
        /// <c>lose</c> and <c>change</c> cover a resource and a status with one word, so the table
        /// has to name a death and a status applied for all three; <c>gain 2 gold</c> does neither,
        /// and only <c>hp</c> ever kills. Reading the word the command names is what stops CT306
        /// calling <c>on killed: gain 2 gold</c> a loop with itself (one listener, paired with
        /// itself, in the relic every roguelite ships).
        /// </summary>
        private static bool CanReallyRaise(CommandNode command, string raised)
        {
            string verb = command.Verb.ToLowerInvariant();
            if (verb != "gain" && verb != "lose" && verb != "change") return true;

            string? named = verb == "change"
                ? (command.Arguments.Count > 0 ? StatOfChange(command.Arguments[0]) : null)
                : WordAt(command, 1);

            // Nothing to read (a computed name, or a shape this does not know), so say nothing.
            if (named == null) return true;

            bool status = StartsUpper(named);
            if (raised == BuiltinEvents.Died || raised == BuiltinEvents.Killed)
                return !status && string.Equals(named, "hp", StringComparison.OrdinalIgnoreCase);
            if (raised == BuiltinEvents.StatusApplied || raised == BuiltinEvents.StatusResisted || raised == BuiltinEvents.StatusRemoved)
                return status;
            return true;
        }

        private IEnumerable<string> RaisedEvents(Facts facts, Dictionary<string, Body> verbBodies, HashSet<string> visitingVerbs)
        {
            foreach (CommandNode command in facts.Commands)
            {
                foreach (string raised in BuiltinEvents.RaisedBy(command.Verb))
                {
                    if (CanReallyRaise(command, raised)) yield return raised;
                }

                string verb = command.Verb.ToLowerInvariant();
                if (verb == "emit" && WordAt(command, 0) is string emitted) yield return emitted;
                if ((verb == "gain" || verb == "lose") && WordAt(command, 1) is string gained && !StartsUpper(gained)) yield return BuiltinEvents.StatChanged(gained);
                if (verb == "change" && command.Arguments.Count > 0 && StatOfChange(command.Arguments[0]) is string changed) yield return BuiltinEvents.StatChanged(changed);

                if (verbBodies.TryGetValue(command.Verb, out Body? verbBody) && visitingVerbs.Add(command.Verb))
                {
                    foreach (string raised in RaisedEvents(verbBody.Facts, verbBodies, visitingVerbs)) yield return raised;
                }
            }

            foreach (AssignNode assign in facts.Assigns)
            {
                switch (assign.Target)
                {
                    case NameExpr name: yield return BuiltinEvents.StatChanged(name.Name); break;
                    case MemberExpr member when !StartsUpper(member.Member): yield return BuiltinEvents.StatChanged(member.Member); break;
                }
            }
        }

        private void CheckCardTargets()
        {
            foreach (Body body in _bodies)
            {
                // An ability reads its `target` line the way a card does, so an unused one is the
                // same mistake: it can now refuse the cast, and nothing is done with what it asked for.
                if (body.Kind != BodyKind.Effect) continue;
                if (body.Owner?.Kind != EntityKind.Card && body.Owner?.Kind != EntityKind.Ability) continue;
                if (!(body.Anchor is BlockMemberNode { Name: "effect" })) continue;

                string target = TargetRule.Of(body.Owner).Mode;
                if (target != "enemy" && target != "ally" && target != "any") continue;

                bool usesTarget = body.Facts.Names.Any(n => string.Equals(n.Name, "target", StringComparison.OrdinalIgnoreCase))
                    || body.Facts.Commands.Any(c => TargetingVerbs.Contains(c.Verb) && c.Clause("to") == null)
                    || body.Facts.Commands.Any(c => _content.FindVerb(c.Verb) != null);
                if (usesTarget) continue;

                Warn(UnusedTarget, $"{body.Owner} asks the player to choose a target (`target {target}`), but its effect never uses it.", body.Owner.Syntax.Span);
            }
        }

        /// <summary>Description drift: CT401 to CT403, from <see cref="DescriptionBuilder.Validate"/>.</summary>
        private void CheckDescriptions()
        {
            var descriptions = new DescriptionBuilder(_content);
            IEnumerable<EntityDefinition> definitions = _content.Definitions
                .Where(d => d.IsThing)
                .OrderBy(d => d.Syntax.Span.File, StringComparer.Ordinal)
                .ThenBy(d => d.Syntax.Span.Line);

            foreach (EntityDefinition definition in definitions) _diagnostics.AddRange(descriptions.Validate(definition));
        }

        private void CheckUnusedVerbs()
        {
            foreach (VerbDefinition verb in _content.Verbs.OrderBy(v => v.Name, StringComparer.Ordinal))
            {
                if (_calledVerbs.Contains(verb.Name) || _options.HostVerbs.Contains(verb.Name)) continue;
                Info(UnusedVerb, $"verb `{verb.Name}` is never used.", verb.Syntax.Span);
            }
        }

        // Helpers ------------------------------------------------------------------------------

        private bool IsKnownEvent(string name) =>
            BuiltinEvents.IsBuiltin(name)
            || _emitted.ContainsKey(name)
            || _options.HostEvents.Contains(name)
            || (BuiltinEvents.TryParseStatChanged(name, out string stat) && _stats.Contains(stat));

        private bool IsKnownName(Body body, string name) =>
            IsReserved(name)
            || IsLocal(body, name)
            || _stats.Contains(name)
            || _content.Find(name) != null
            || _options.HostNames.Contains(name)
            || name.EndsWith("_this_turn", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_this_battle", StringComparison.OrdinalIgnoreCase)
            || ((body.Kind == BodyKind.Listener || body.Kind == BodyKind.Verb) && EventDataNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            || (body.Kind == BodyKind.Test && IsTestBinding(name));

        private static bool IsReserved(string name) => Interpreter.ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase);

        private static bool IsLocal(Body body, string name) =>
            body.Facts.Locals.Contains(name) || BoundNames.Contains(name, StringComparer.OrdinalIgnoreCase);

        /// <summary>The test runner binds each spawned enemy as <c>enemy1</c>, <c>enemy2</c>...</summary>
        private static bool IsTestBinding(string name) =>
            name.Length > 5 && name.StartsWith("enemy", StringComparison.OrdinalIgnoreCase) && name.Substring(5).All(char.IsDigit);

        private IEnumerable<string> NameCandidates(Body body) =>
            Interpreter.ReservedNames
                .Concat(body.Facts.Locals)
                .Concat(_stats)
                .Concat(_content.AllNames);

        private IEnumerable<string> StatusNames() =>
            _content.Definitions.Where(d => d.Kind == EntityKind.Status || d.Kind == EntityKind.Keyword).Select(d => d.Name);

        private static string EventPart(string eventName)
        {
            int dot = eventName.LastIndexOf('.');
            return (dot >= 0 ? eventName.Substring(dot + 1) : eventName).ToLowerInvariant();
        }

        private static bool StartsUpper(string name) => name.Length > 0 && char.IsUpper(name[0]);

        /// <summary>"a card", "an ability".</summary>
        private static string A(string noun) => ("aeiou".IndexOf(char.ToLowerInvariant(noun[0])) >= 0 ? "an " : "a ") + noun;

        /// <summary>A whole number of one or more, as a count of turns is.</summary>
        private static bool IsWholeCount(Num value) => value >= Num.One && value == value.Floor();

        private static string? WordAt(CommandNode command, int index) =>
            index < command.Arguments.Count ? FirstWord(command.Arguments[index]) : null;

        private static string? FirstWord(ExprNode node) => node switch
        {
            NameExpr name => name.Name,
            StringExpr text => text.Value,
            QualifiedExpr qualified => qualified.Name,
            _ => null,
        };

        /// <summary>The stat in <c>change hp by 5</c> or the compact <c>change hp -5 on target</c>.</summary>
        private static string? StatOfChange(ExprNode node) => node switch
        {
            NameExpr name => name.Name,
            BinaryExpr { Operator: BinaryOperator.On } on => StatOfChange(on.Left),
            BinaryExpr { Operator: BinaryOperator.Add or BinaryOperator.Subtract, Left: NameExpr name } => name.Name,
            _ => null,
        };

        private void Error(string code, string message, SourceSpan span, string? suggestion = null) =>
            _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, code, message, span, suggestion));

        private void Warn(string code, string message, SourceSpan span, string? suggestion = null) =>
            _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, code, message, span, suggestion));

        private void Info(string code, string message, SourceSpan span) =>
            _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, code, message, span));
    }
}
