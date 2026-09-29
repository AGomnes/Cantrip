using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Cantrip.Syntax;

namespace Cantrip
{
    /// <summary>
    /// What a top-level action did. Every call that can stop for a choice reports it here:
    /// <see cref="CardRuntime.Play(Entity, Entity, Entity)"/>, <see cref="CardRuntime.StartBattle"/>,
    /// <see cref="CardRuntime.EndTurn"/>, <see cref="CardRuntime.Execute"/>,
    /// <see cref="CardRuntime.UseAbility"/> and <see cref="CardRuntime.Answer(int[])"/>.
    /// </summary>
    public enum ActionResult
    {
        /// <summary>The action ran to the end. For <see cref="CardRuntime.Play(Entity, Entity, Entity)"/>, the card was played.</summary>
        Played,

        /// <summary>
        /// There is nothing to act on: the entity is not a card, or not an ability, or it has been
        /// removed, or the actor it belongs to is dead.
        /// </summary>
        NotACard,

        NotInHand,
        Unplayable,
        NotEnoughEnergy,
        InvalidTarget,
        Cancelled,

        /// <summary>
        /// The action needs a decision from the player. The game has been rolled back to where it
        /// was; see <see cref="CardRuntime.Pending"/> and answer with <see cref="CardRuntime.Answer(int[])"/>.
        /// </summary>
        ChoicePending,

        /// <summary>An ability that is still on cooldown. See <see cref="CardRuntime.IsReady"/>.</summary>
        NotReady,
    }

    public sealed class RuntimeOptions
    {
        /// <summary>
        /// The run's seed. Any 64-bit number is a seed of its own, 0 and negative ones included, so
        /// a host can hand it whatever number it computed.
        /// </summary>
        /// <remarks>
        /// It used to be <c>ulong</c>, which compiled for the literal the guide shows and for
        /// nothing a host works out for itself: every game that seeded a run from an <c>int</c>
        /// wrote <c>(ulong)(uint)seed</c>, and Godot's own seed export is signed.
        /// </remarks>
        public long Seed { get; set; } = 1;

        /// <summary>Defaults to a <see cref="TurnClock"/>. Pass a <see cref="TickClock"/> for real-time games.</summary>
        public IGameClock? Clock { get; set; }

        public IEffectHost? Host { get; set; }
        public IChoiceProvider? Chooser { get; set; }

        /// <summary>Overrides the ruleset declared in content.</summary>
        public Ruleset? Rules { get; set; }

        public bool Trace { get; set; }

        public ExecutionMode Execution { get; set; } = ExecutionMode.Headless;
    }

    /// <summary>
    /// The entry point for games: load content, set up actors and decks, then drive
    /// battles through <see cref="StartBattle"/>, <see cref="Play(Entity, Entity, Entity)"/> and <see cref="EndTurn"/>, or
    /// real-time play through <see cref="Tick"/> and <see cref="UseAbility"/>.
    /// </summary>
    public sealed class CardRuntime : IDisposable
    {
        public CardRuntime(ContentLibrary content, RuntimeOptions? options = null)
        {
            Content = content ?? throw new ArgumentNullException(nameof(content));
            options ??= new RuntimeOptions();

            Ruleset rules = options.Rules ?? content.BuildRuleset();
            IGameClock clock = options.Clock ?? DeclaredClock(rules);

            // A game that hands in a clock its content was not written for is the same mistake the
            // ruleset setting exists to catch, one layer up: `on every 1s:` would register nothing
            // and `for 3s` would throw when the line ran. Say it here, where the runtime is built.
            if (rules.Clock == ClockKind.Turns && !(clock is TurnClock))
                throw new InvalidOperationException("This content says `clock turns`, but the runtime was given a tick clock. Remove the clock, or change the ruleset.");
            if (rules.Clock == ClockKind.Ticks && !(clock is TickClock))
                throw new InvalidOperationException("This content says `clock ticks`, but the runtime was given a turn clock. Pass a `TickClock`, or change the ruleset.");

            State = new GameState(content, rules, clock, unchecked((ulong)options.Seed));
            State.Trace.Enabled = options.Trace;
            Interpreter = new Interpreter(State, options.Host);
            if (options.Chooser != null) Interpreter.Chooser = options.Chooser;
            Execution = options.Execution;

            RegisterRuntimeVerbs();
            clock.Advanced += OnClockAdvanced;
        }

        private bool _detached;

        /// <summary>
        /// Lets go of the clock. A runtime listens to <see cref="IGameClock.Advanced"/> from the
        /// moment it is built, which is how scheduled work, periodic triggers and timed statuses
        /// run; until it lets go, the clock holds it alive and keeps driving it.
        /// </summary>
        /// <remarks>
        /// It only matters when a game passes its own clock in <see cref="RuntimeOptions.Clock"/> and
        /// outlives a runtime that used it — two runtimes sharing one clock both keep running, and
        /// the one nobody uses any more goes on resolving effects on a game that has been replaced.
        /// A runtime that made its own clock is collected with it, so nothing is leaked by not
        /// calling this. Nothing else is torn down: the state, the content and the entities are
        /// ordinary objects, and calling any other member afterwards still works, minus the clock.
        /// Calling it twice does nothing the second time.
        /// </remarks>
        public void Dispose()
        {
            if (_detached) return;
            _detached = true;
            State.Clock.Advanced -= OnClockAdvanced;
        }

        /// <summary>
        /// The clock content asked for, or a turn clock when it did not say — which is what every
        /// game written before the <c>clock</c> setting existed gets.
        /// </summary>
        private static IGameClock DeclaredClock(Ruleset rules) =>
            rules.Clock == ClockKind.Ticks ? new TickClock() : (IGameClock)new TurnClock();

        /// <summary>Loads content from text and throws if it has any errors.</summary>
        public static CardRuntime FromText(string text, RuntimeOptions? options = null)
        {
            ContentLibrary content = ContentLibrary.FromText(text);
            content.Diagnostics.ThrowIfErrors();
            return new CardRuntime(content, options);
        }

        public ContentLibrary Content { get; }
        public GameState State { get; }
        public Interpreter Interpreter { get; }
        public ExecutionMode Execution { get; set; }

        /// <summary>
        /// Who answers a choice content asks for. Setting it to null throws rather than quietly
        /// putting a <see cref="FirstOptionChooser"/> in its place: a game that meant to install its
        /// own UI and handed in a null would otherwise find every decision made for it.
        /// </summary>
        public IChoiceProvider Chooser
        {
            get => Interpreter.Chooser;
            set => Interpreter.Chooser = value ?? throw new ArgumentNullException(nameof(value), "A runtime always has a chooser; pass a FirstOptionChooser to keep the default.");
        }

        /// <summary>
        /// The party's leader: the actor <see cref="CreatePlayer"/> made, who holds the run's deck,
        /// relics and gold.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Before <see cref="CreatePlayer"/>. Every other call that needs a player needs it too, so
        /// the window in which this is empty is the few lines between building a runtime and setting
        /// the game up — and a game that asks in that window has its order wrong rather than a
        /// player that might not be there.
        /// </exception>
        /// <remarks>
        /// Not nullable, deliberately. It used to be, and a host with
        /// <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c> — which this repository's own
        /// <c>Directory.Build.props</c> sets, with <c>WarningsAsErrors=nullable</c> — could not
        /// write <c>runtime.Player.GetInt("hp")</c>, the line the guide gives, without a <c>!</c>
        /// the guide never mentions. <see cref="HasPlayer"/> is the question worth asking, and it is
        /// asked once, at setup.
        /// </remarks>
        public Entity Player =>
            State.Player ?? throw new InvalidOperationException(
                "There is no player yet. Call CreatePlayer first; it is the one call every game makes before a battle.");

        /// <summary>
        /// Whether <see cref="CreatePlayer"/> has been called. False only between building a runtime
        /// and setting the game up, and after <c>NewRun</c> on the Godot node.
        /// </summary>
        public bool HasPlayer => State.Player != null;

        /// <summary>
        /// The living members of the party, in the order they take their steps. A game that never
        /// declares a <c>hero</c> has one member: <see cref="Player"/>.
        /// </summary>
        public IReadOnlyList<Entity> Party => State.Party;

        /// <summary>
        /// The party's dead, in the order they fell — what <see cref="Party"/> and
        /// <c>State.Actors</c> leave out, and what content calls <c>fallen</c>.
        /// </summary>
        /// <remarks>
        /// A run that offers to raise a fallen member used to have to keep its own list of member
        /// ids from the moment it created them, save it, version it and keep it in step with the
        /// snapshot, because a corpse was in none of <c>Party</c>, <c>State.Actors</c> or the
        /// content groups. It is in the snapshot like everyone else; only the way to ask was
        /// missing.
        /// </remarks>
        public IReadOnlyList<Entity> Fallen => State.Fallen(Team.Player);

        /// <summary>
        /// Whose step it is, or null when the turn order has no single answer. <c>turns: sides</c>,
        /// the only mode this release has, gives the whole party one turn and lets the game act
        /// with its members in any order, so it is always null there and a game asks
        /// <see cref="CanAct"/> of each member instead.
        /// </summary>
        public Entity? ActiveMember
        {
            get
            {
                if (RealTime) throw NoTurns(nameof(ActiveMember));
                return State.ActiveMember;
            }
        }

        /// <summary>
        /// Whether this runtime measures time in ticks rather than turns: a real-time game.
        /// </summary>
        /// <remarks>
        /// A runtime gets a tick clock two ways, and both count. The game hands one to
        /// <see cref="RuntimeOptions.Clock"/>, or the content says <c>clock ticks</c> and a runtime
        /// that was given no clock starts one.
        /// </remarks>
        internal bool RealTime => State.Clock is TickClock;

        /// <summary>
        /// The refusal every turn-shaped call makes on a real-time runtime, the mirror of the one
        /// <see cref="Tick"/> makes on a turn-based one.
        /// </summary>
        /// <remarks>
        /// These calls used to answer. <c>EndTurn()</c> ran a whole turn cycle, enemy moves and
        /// all, in a game with no turns, so a front end that left its <b>End turn</b> button wired
        /// up gave the player a button that fired every enemy's move at once for free. Refusing is
        /// a breaking change, which is why it was made before the surface was promised rather than
        /// after.
        /// </remarks>
        private static InvalidOperationException NoTurns(string what) =>
            new InvalidOperationException(
                $"{what} needs turns; this runtime uses a TickClock, so it has none. " +
                "Time passes here by calling Tick() from a fixed timestep, and every actor acts when its own cooldowns are ready. " +
                "If this game does take turns, give the runtime no clock, or a TurnClock, and say `clock turns` in the ruleset.");

        /// <summary>
        /// Whether this member still has its step this round: it is a member, it is alive, a battle
        /// is running, and it has not passed. Under <c>turns: sides</c> it also has to be the
        /// party's turn; under <c>turns: initiative</c> it has to be this member's own step, because
        /// there the order is the rule rather than a suggestion.
        /// </summary>
        public bool CanAct(Entity member)
        {
            if (member == null) throw new ArgumentNullException(nameof(member));
            if (RealTime) throw NoTurns(nameof(CanAct));
            if (!State.InBattle || !member.IsPartyMember || !member.IsAlive) return false;
            if (State.Acted.Contains(member.Id)) return false;

            return Initiative
                ? ReferenceEquals(State.ActiveMember, member)
                : State.ActiveTeam == Team.Player;
        }

        /// <summary>Whether every combatant takes its own step in one order across both sides.</summary>
        private bool Initiative => State.Rules.Turns == TurnMode.Initiative;

        /// <summary>
        /// That member is done for this turn. When the last one that could act has passed, the
        /// party's turn ends and the enemies take theirs — so for a party of one this is
        /// <see cref="EndTurn"/>, to the byte.
        /// </summary>
        /// <returns>
        /// <see cref="ActionResult.Played"/>, or <see cref="ActionResult.ChoicePending"/> when the
        /// turn it ended stopped to ask the player something.
        /// </returns>
        /// <remarks>
        /// It is a verb of its own rather than an <c>EndTurn(member)</c> overload because "end turn"
        /// already means "end the side's turn" in the tests, the reference and the Godot node, and
        /// one word meaning two things is exactly the silent break a frozen API cannot afford.
        /// </remarks>
        public ActionResult Pass(Entity member)
        {
            if (member == null) throw new ArgumentNullException(nameof(member));
            if (RealTime) throw NoTurns(nameof(Pass));

            int memberId = member.Id;
            return Attempt(
                () => { PassCore(member); return ActionResult.Played; },
                () => Live(memberId) is Entity again ? Pass(again) : ActionResult.Played);
        }

        private void PassCore(Entity member)
        {
            if (!State.InBattle) throw new InvalidOperationException("No battle is running, so there is no turn to pass in.");
            if (State.ActiveTeam != Team.Player) throw new InvalidOperationException("It is not the party's turn.");
            if (!member.IsPartyMember)
                throw new ArgumentException($"{member} is not a party member, so it has no step to pass. Only the leader and the heroes beside it take one.", nameof(member));

            // A member that fell has no step left to give up, and marking it would be bookkeeping
            // about somebody who is not in the party any more.
            if (!member.IsAlive) return;

            if (Initiative)
            {
                // The order is the rule here, so passing out of turn is refused rather than
                // silently reordering the round.
                if (!ReferenceEquals(State.ActiveMember, member))
                    throw new InvalidOperationException($"It is not {member}'s step. Under `turns: initiative` the combatants act in one order, and ActiveMember says whose step it is.");

                EndMemberStep(member);
                if (!State.InBattle) return;
                State.MarkActed(member);
                AdvanceInitiative();
                return;
            }

            State.MarkActed(member);

            foreach (Entity other in State.Party)
            {
                if (!State.Acted.Contains(other.Id)) return;
            }

            EndTurnCore();
        }

        /// <summary>
        /// Every member of the party, living or fallen: what the end of a turn discards from and
        /// what the end of a battle tidies up. <see cref="Party"/> is the living ones, which is who
        /// still acts; this is who still owns cards and statuses.
        /// </summary>
        private List<Entity> AllMembers()
        {
            var members = new List<Entity>();
            if (State.Player != null && State.Player.IsPartyMember && !State.Player.IsRemoved) members.Add(State.Player);
            foreach (Entity entity in State.Entities)
            {
                if (entity.IsPartyMember && !entity.IsRemoved && entity != State.Player) members.Add(entity);
            }
            return members;
        }

        /// <summary>Null while a battle is running or before the first one; then whether the player won.</summary>
        public bool? Won { get; private set; }

        public void RegisterVerb(string name, VerbHandler handler) => Interpreter.RegisterVerb(name, handler);

        // Setup --------------------------------------------------------------------------------

        public Entity CreatePlayer(string name = "Player", int hp = 80, int maxEnergy = 3)
        {
            if (State.Player != null) throw new InvalidOperationException("A player already exists.");

            Entity player = State.Spawn(name, EntityKind.Actor, null, Team.Player, Zones.Board);
            player.SetBase("max_hp", hp);
            player.SetBase("hp", hp);
            player.SetBase("block", 0);
            player.SetBase("max_energy", maxEnergy);
            player.SetBase("energy", maxEnergy);

            // A party of one, and this member is the leader. Every rule the party added reads the
            // same here as the rule it replaced, which is why a game that never calls
            // <see cref="AddHero"/> plays exactly as it did.
            player.IsPartyMember = true;
            State.Player = player;
            return player;
        }

        /// <summary>
        /// Adds a party member from a <c>hero</c> definition: an actor on the player's side that
        /// the game asks for input, with the abilities its <c>abilities</c> line grants.
        /// </summary>
        /// <param name="name">A <c>hero</c> declared in content.</param>
        /// <param name="hp">Overrides the printed hp, as <see cref="SpawnEnemy"/> does.</param>
        /// <remarks>
        /// The leader <see cref="CreatePlayer"/> made is already a member, so a party of four is one
        /// <c>CreatePlayer</c> and three <c>AddHero</c>. Content adds one with <c>create Vestal</c>,
        /// which is the same arrival by a different door: a mid-run recruit, or a summon that acts.
        /// </remarks>
        public Entity AddHero(string name, int? hp = null)
        {
            if (State.Player == null) throw new InvalidOperationException("Create the party's leader with CreatePlayer before adding heroes.");

            EntityDefinition definition = Require(name, "hero");
            Entity hero = State.Instantiate(definition, null, Team.Player, Zones.Board);

            if (hp.HasValue)
            {
                hero.SetBase("max_hp", hp.Value);
                hero.SetBase("hp", hp.Value);
            }
            if (!hero.HasStat("block")) hero.SetBase("block", 0);

            Interpreter.JoinParty(hero);
            return hero;
        }

        public Entity AddCard(string name, string zone = Zones.Draw, Entity? owner = null)
        {
            EntityDefinition definition = Require(name, "card");
            owner ??= State.Player ?? throw new InvalidOperationException("Create a player before adding cards.");
            return State.Instantiate(definition, owner, Team.Neutral, zone);
        }

        public IReadOnlyList<Entity> AddDeck(params string[] names) => names.Select(n => AddCard(n)).ToList();

        public Entity AddRelic(string name, Entity? owner = null)
        {
            EntityDefinition definition = Content.Find(name, "relic") ?? Require(name, "item");
            owner ??= State.Player ?? throw new InvalidOperationException("Create a player before adding relics.");
            Entity relic = State.Instantiate(definition, owner, Team.Neutral, Zones.Relics);

            Run(owner, context => Interpreter.Raise(new GameEvent("obtained") { Source = owner, Target = relic }, context));
            return relic;
        }

        /// <summary>
        /// Puts an enemy on the board and announces it with <c>created</c>, the same event
        /// content's own <c>create</c> raises.
        /// </summary>
        /// <remarks>
        /// The event is an announcement rather than a gate: the enemy is already in the game when
        /// it is raised, so a <c>before created:</c> listener cannot stop a spawn the host has
        /// decided on. What it buys is the thing a wave game needs and had no way to write — an
        /// arrival effect, a self-placement, a relic that hears anything entering the fight:
        /// <c>on created(target:self): self.rank = 3</c> on the enemy's own declaration. In a turn
        /// game a spawn happens once, before the battle, so this never came up; in a real-time game
        /// it is the most frequent event there is.
        /// </remarks>
        public Entity SpawnEnemy(string name, int? hp = null)
        {
            EntityDefinition definition = Content.Find(name, "enemy") ?? Require(name, "actor");
            Entity enemy = State.Instantiate(definition, null, Team.Enemy, Zones.Board);

            if (hp.HasValue)
            {
                enemy.SetBase("max_hp", hp.Value);
                enemy.SetBase("hp", hp.Value);
            }
            if (!enemy.HasStat("block")) enemy.SetBase("block", 0);

            if (State.InBattle) RollIntent(enemy);

            Run(enemy, context => Interpreter.Raise(new GameEvent("created") { Source = enemy, Target = enemy }, context));
            return enemy;
        }

        /// <summary>
        /// Stands an actor at <paramref name="lane"/>, <paramref name="rank"/>, raising
        /// <c>moved</c> so content hears it and <c>before_moved</c> can refuse it. The same move
        /// content writes as <c>target.rank = 0</c>, from C#.
        /// </summary>
        /// <returns>
        /// True when the actor stands there afterwards; false when a <c>before_moved</c> listener
        /// refused the move.
        /// </returns>
        /// <remarks>
        /// Where somebody stands is a rule and not a view, so the Godot node has nothing that does
        /// this — but a rule still has to be reachable from the game that owns the fight above it.
        /// A host with waves to place had to execute a string of content for every spawn, which
        /// turned a typo in a lane number from a compile error into a <c>DslException</c> on a hot
        /// path. A slot off the board is refused here with the board's own name and shape, rather
        /// than clamped: a wave walking in at a rank that does not exist is a bug in the schedule,
        /// and quietly standing it somewhere else would hide it.
        /// </remarks>
        public bool Place(Entity actor, int lane, int rank)
        {
            if (actor == null) throw new ArgumentNullException(nameof(actor));
            if (actor.Kind != EntityKind.Actor || actor.Zone != Zones.Board)
                throw new ArgumentException($"{actor} is not an actor on the board, so it has no slot to stand in.", nameof(actor));
            if (!State.Board.Holds(lane, rank))
                throw new ArgumentException(
                    $"Board \"{State.Board.Name}\" ({State.Board.Describe()}) has no {State.Board.LaneWord} {lane}, {State.Board.RankWord} {rank}.",
                    nameof(lane));

            if (actor.Lane == lane && actor.Rank == rank) return true;

            bool moved = false;
            Run(actor, context => moved = Interpreter.MoveActor(actor, lane, rank, context));
            return moved;
        }

        /// <summary>
        /// Applies a status from game code. <paramref name="source"/> defaults to the player, which
        /// matters for <c>flags unique</c> statuses and for <c>source:</c> filters.
        /// </summary>
        public Entity? ApplyStatus(string status, Entity target, int stacks = 1, Entity? source = null)
        {
            source ??= State.Player;
            EntityDefinition definition = Content.FindAny(status, "status", "keyword") ?? Require(status, "status");
            Entity? result = null;
            Run(source, context => result = Interpreter.ApplyStatus(definition, target, stacks, null, context));
            return result;
        }

        /// <summary>Gives an actor an ability (real-time games).</summary>
        public Entity GrantAbility(string name, Entity owner)
        {
            EntityDefinition definition = Require(name, "ability");
            Entity ability = State.Instantiate(definition, owner);
            State.Attach(owner, ability);
            return ability;
        }

        private ArgumentException NoSuchBoard(string name)
        {
            string? close = Suggest.Closest(name, Content.Boards.Select(b => b.Name));
            return new ArgumentException(
                $"No board named \"{name}\" is declared." + (close == null ? string.Empty : $" Did you mean \"{close}\"?") +
                $" A board is content, not something a game makes up at runtime: declare it with `board {name}`.",
                "board");
        }

        private EntityDefinition Require(string name, string kind)
        {
            EntityDefinition? definition = Content.Find(name, kind);
            if (definition != null) return definition;

            string? suggestion = Suggest.Closest(name, Content.Definitions.Where(d => d.KindName == kind).Select(d => d.Name));
            throw new ArgumentException(
                $"No {kind} named \"{name}\" is loaded." + (suggestion == null ? string.Empty : $" Did you mean \"{suggestion}\"?"),
                nameof(name));
        }

        // Battle flow --------------------------------------------------------------------------

        private bool _skipNextDraw;

        /// <param name="shuffle">Shuffle the draw pile first. Tests turn this off to control draw order.</param>
        /// <param name="drawOpeningHand">Draw the first hand. Tests turn this off to set the hand explicitly.</param>
        /// <returns>
        /// <see cref="ActionResult.Played"/>, or <see cref="ActionResult.ChoicePending"/> when a
        /// <c>battle_start</c> effect asks the player something: the whole call is rolled back and
        /// runs again from <see cref="Answer(int[])"/>.
        /// </returns>
        /// <param name="board">
        /// The board to fight this battle on, by name, or null to keep the one in play — which
        /// before the first battle is <see cref="ContentLibrary.DefaultBoard"/>. Content owns the
        /// shapes: a name no <c>board</c> declaration matches is refused rather than invented,
        /// because the linter has to know how deep a board is to check what reaches across it.
        /// </param>
        public ActionResult StartBattle(bool shuffle = true, bool drawOpeningHand = true, string? board = null) =>
            Attempt(
                () => { StartBattleCore(shuffle, drawOpeningHand, board); return ActionResult.Played; },
                () => { StartBattle(shuffle, drawOpeningHand, board); return Outcome(); });

        private void StartBattleCore(bool shuffle, bool drawOpeningHand, string? board)
        {
            Entity player = State.Player ?? throw new InvalidOperationException("Create a player before starting a battle.");

            if (board != null)
            {
                BoardShape shape = Content.Board(board) ?? throw NoSuchBoard(board);
                State.UseBoard(shape);
            }

            State.ResetBattleHistory();
            State.BattleNumber++;
            State.Turn = 0;
            State.InBattle = true;
            Won = null;
            _skipNextDraw = !drawOpeningHand;

            // Enemies that died in an earlier battle are done with. Leaving them in would make every
            // later battle count as already having had enemies, and so end before its own arrive.
            foreach (Entity fallen in State.Entities.Where(e => e.Kind == EntityKind.Actor && e.Team == Team.Enemy && e.IsDead && !e.IsRemoved).ToArray())
                State.Remove(fallen);

            if (!player.HasStat("block")) player.SetBase("block", 0);
            if (shuffle) Interpreter.ShuffleZone(player, Zones.Draw);
            foreach (Entity enemy in State.Actors(Team.Enemy)) RollIntent(enemy);

            Run(player, context => Interpreter.Raise(new GameEvent("battle_start") { Source = player }, context));
            if (CheckBattleOver()) return;

            if (RealTime) BeginRealTime();
            else if (Initiative) StartRound();
            else StartTurn(Team.Player);
        }

        /// <summary>
        /// Opens a battle on a tick clock: no turn, so no <c>turn_start</c>, no <c>State.Turn</c>
        /// and no turn order, but the opening hand is still dealt, because a hand is not a turn.
        /// </summary>
        /// <remarks>
        /// <c>turn_start</c> used to fire here, exactly once per real-time battle, while CT335 told
        /// authors it never fired at all. Content written against the diagnostic got one reset of
        /// every <c>reset_on turn_start</c> resource, one pass of every <c>turn_start</c> listener
        /// and a <c>State.Turn</c> of 1 in a game with no turns; content written against the engine
        /// got a rule the linter refused to let it say. The diagnostic was the honest half, so the
        /// engine moved: on a tick clock the turn events do not fire, and <c>State.Turn</c> stays
        /// at 0 for the whole fight.
        /// </remarks>
        private void BeginRealTime()
        {
            State.ActiveTeam = Team.Player;
            State.ResetTurnHistory();
            State.ClearActed();

            if (_skipNextDraw) _skipNextDraw = false;
            else DrawForEveryMember();
        }

        /// <summary>Ends the player's turn, runs the enemies' turn, and starts the next player turn.</summary>
        /// <returns>
        /// <see cref="ActionResult.Played"/>, or <see cref="ActionResult.ChoicePending"/> when
        /// something in the turn asks the player to choose: the whole call, enemy turn included, is
        /// rolled back and runs again from <see cref="Answer(int[])"/>.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// This runtime uses a <see cref="TickClock"/>, so it has no turns to end.
        /// </exception>
        public ActionResult EndTurn()
        {
            if (RealTime) throw NoTurns(nameof(EndTurn));

            return Attempt(
                () => { EndTurnCore(); return ActionResult.Played; },
                () => { EndTurn(); return Outcome(); });
        }

        private void EndTurnCore()
        {
            if (!State.InBattle) return;
            if (State.ActiveTeam != Team.Player) throw new InvalidOperationException("It is not the player's turn.");

            if (Initiative) { EndRoundEarly(); return; }

            if (EndTurnFor(Team.Player)) return;

            // Every member's hand, not only the leader's. Before this a hero's hand was never
            // discarded at all and grew by a full draw every round.
            foreach (Entity member in AllMembers()) DiscardHand(member);
            State.ClearActed();

            StartTurn(Team.Enemy);
            if (!State.InBattle) return;

            foreach (Entity enemy in State.Actors(Team.Enemy).ToArray())
            {
                RunEnemyMove(enemy);
                if (!State.InBattle) return;
            }

            if (EndTurnFor(Team.Enemy)) return;
            foreach (Entity enemy in State.Actors(Team.Enemy)) RollIntent(enemy);

            StartTurn(Team.Player);
        }

        // Initiative ---------------------------------------------------------------------------
        //
        // One round is one turn here too. `State.Turn` is the round number in both modes, because
        // `on every N turns`, `once per turn`, the history counters, the saved turn and the
        // simulator's stall limit all key off it, and four heroes must not make it mean four things.
        // What changes is where each combatant's `turn_start` and `turn_end` fall: at its own step
        // rather than all at once at the side's.

        /// <summary>
        /// A round cannot take more steps than this. Each step but a party member's marks somebody
        /// as having acted, so the only way to reach it is content that summons without end; the
        /// round is cut short rather than the process hanging.
        /// </summary>
        private const int MaxStepsPerRound = 512;

        /// <summary>Begins a round: the turn number, the histories, the clock, then the first step.</summary>
        private void StartRound()
        {
            State.Turn++;
            State.ResetTurnHistory();
            State.ClearActed();

            // Time moves once a round, as it does under `turns: sides`, but at the round's start
            // rather than after the side's turn starts: here the combatants start their turns one
            // at a time and there is no moment at which they all have. So `on every 2 turns` fires
            // for everybody before anybody acts, which is the answer a game can reason about; work
            // hung on `in N turns:` lands there too, ahead of the turn-start resets, and content
            // that wants it after its own reset writes `next turn:`, which fires at its own step.
            if (State.Turn > 1 && State.Clock is TurnClock turns)
            {
                turns.AdvanceTurn();
                if (!State.InBattle) return;
            }

            AdvanceInitiative();
        }

        /// <summary>
        /// Runs steps until a living party member's is next — at which point the game is asked what
        /// it does — or until the round is over.
        /// </summary>
        private void AdvanceInitiative()
        {
            for (int step = 0; step < MaxStepsPerRound; step++)
            {
                Entity? next = NextUp();
                if (next == null) { EndRound(); return; }

                State.ActiveTeam = next.Team;
                BeginStep(next);
                if (!State.InBattle) return;

                // The party is who the game is asked about, so its step is where control goes back.
                // A member that its own turn start killed has no step to take, and is marked so
                // that the round moves past it rather than waiting on a corpse.
                if (next.IsPartyMember && next.IsAlive) return;

                if (next.IsAlive)
                {
                    // A summoned minion takes a step of its own — its `turn_start` and `turn_end`
                    // fire, which is how a Monster Train or Hearthstone minion attacks — but nobody
                    // is asked what it does, so the round runs straight on through it. Stopping
                    // there would wait forever for a pass that only a member can give.
                    if (next.Team == Team.Enemy) RunEnemyMove(next);
                    if (!State.InBattle) return;
                    EndStep(next);
                    if (!State.InBattle) return;
                }

                State.MarkActed(next);
            }

            EndRound();
        }

        /// <summary>
        /// The next combatant with a step still to take, or null when the round is done. Read off
        /// the live order every time, so a death, a summon or a revival mid-round is already in it.
        /// </summary>
        private Entity? NextUp()
        {
            foreach (Entity combatant in State.TurnOrder)
            {
                if (!State.Acted.Contains(combatant.Id)) return combatant;
            }
            return null;
        }

        /// <summary>One combatant's turn beginning: its <c>turn_start</c>, its scheduled work, its draw.</summary>
        private void BeginStep(Entity actor)
        {
            // Only work scheduled before this step began runs now, as under `turns: sides`.
            var due = State.Scheduled.Where(s => s.Timing == ScheduleTiming.NextTurn && s.Owner == actor).ToList();

            Run(actor, context => Interpreter.Raise(new GameEvent("turn_start") { Source = actor, Target = actor }, context));

            foreach (ScheduledAction action in due)
            {
                State.Unschedule(action);
                Run(actor, _ => Interpreter.RunScheduled(action));
            }

            if (CheckBattleOver()) return;

            // A member draws its own hand at its own step, which is where `sides` draws it too —
            // there, every member's turn starts at once. One with no pile of its own draws nothing.
            if (actor.IsPartyMember && actor.IsAlive && !_skipNextDraw)
            {
                Run(actor, context => Interpreter.Draw(actor, State.Rules.HandSize, context));
                CheckBattleOver();
            }
        }

        /// <summary>One combatant's turn ending: its <c>turn_end</c>, and nothing else.</summary>
        private void EndStep(Entity actor)
        {
            Run(actor, context => Interpreter.Raise(new GameEvent("turn_end") { Source = actor, Target = actor }, context));
            CheckBattleOver();
        }

        /// <summary>A member's turn ending: its <c>turn_end</c>, then its hand.</summary>
        private void EndMemberStep(Entity member)
        {
            EndStep(member);
            if (!State.InBattle) return;
            DiscardHand(member);
        }

        /// <summary>Every living enemy re-telegraphs, and the next round begins.</summary>
        private void EndRound()
        {
            // The opening hand is skipped for the whole of the first round, not only for whoever
            // stepped first.
            _skipNextDraw = false;

            foreach (Entity enemy in State.Actors(Team.Enemy)) RollIntent(enemy);
            if (!State.InBattle) return;

            StartRound();
        }

        /// <summary>
        /// <c>EndTurn</c> under <c>turns: initiative</c>: every member of ours that still has a step
        /// this round gives it up, and the round runs to its end around them — the enemies still
        /// take their steps, in their places. For a party of one that is exactly the old
        /// <c>EndTurn</c>: pass the one member, the enemies answer, the next round begins.
        /// </summary>
        private void EndRoundEarly()
        {
            int round = State.Turn;
            for (int step = 0; step < MaxStepsPerRound && State.InBattle && State.Turn == round; step++)
            {
                if (!(State.ActiveMember is Entity member)) return;
                PassCore(member);
            }
        }

        private void StartTurn(Team team)
        {
            State.ActiveTeam = team;

            if (team == Team.Player)
            {
                State.Turn++;
                State.ResetTurnHistory();
                State.ClearActed();
            }

            foreach (Entity actor in State.Actors(team).ToArray())
            {
                if (!actor.IsAlive) continue;

                // Only work scheduled before this turn began runs now; `next turn:` written during
                // this turn start belongs to the following turn.
                var due = State.Scheduled.Where(s => s.Timing == ScheduleTiming.NextTurn && s.Owner == actor).ToList();

                // Resources that reset and statuses that decay on turn_start are handled by the
                // event itself (reset_on / decay ... on), so they work for any event, not just turns.
                Run(actor, context => Interpreter.Raise(new GameEvent("turn_start") { Source = actor, Target = actor }, context));

                foreach (ScheduledAction action in due)
                {
                    State.Unschedule(action);
                    Run(actor, _ => Interpreter.RunScheduled(action));
                }

                if (CheckBattleOver()) return;
            }

            // Time moves once every actor has started its turn, so work scheduled for this turn
            // (`in 1 turn: gain 1 energy`) runs after the turn-start resets instead of being wiped.
            if (team == Team.Player && State.Turn > 1 && State.Clock is TurnClock turns)
            {
                turns.AdvanceTurn();
                if (!State.InBattle) return;
            }

            // Each member draws from its own pile. One with no pile of its own draws nothing —
            // Draw stops at an empty draw and an empty discard without touching the generator — and
            // plays from the party's hand instead, which is the second of the two shapes and needs
            // no setting to tell them apart.
            if (team == Team.Player && State.Player != null && (State.Player.IsAlive || State.Party.Count > 0))
            {
                if (_skipNextDraw) _skipNextDraw = false;
                else if (!DrawForEveryMember()) return;

                CheckBattleOver();
            }
        }

        /// <summary>
        /// Deals a hand to every living member. Returns false when the battle ended while dealing.
        /// </summary>
        /// <remarks>
        /// Each member draws from its own pile. One with no pile of its own draws nothing — Draw
        /// stops at an empty draw and an empty discard without touching the generator — and plays
        /// from the party's hand instead, which is the second of the two shapes and needs no
        /// setting to tell them apart.
        /// </remarks>
        private bool DrawForEveryMember()
        {
            if (State.Player == null) return State.InBattle;

            foreach (Entity member in State.Party)
            {
                if (!member.IsAlive) continue;
                Entity drawing = member;
                Run(drawing, context => Interpreter.Draw(drawing, State.Rules.HandSize, context));
                if (!State.InBattle) return false;
            }
            return true;
        }

        /// <summary>Returns true if the battle ended during the turn end.</summary>
        private bool EndTurnFor(Team team)
        {
            foreach (Entity actor in State.Actors(team).ToArray())
            {
                if (!actor.IsAlive) continue;

                Run(actor, context => Interpreter.Raise(new GameEvent("turn_end") { Source = actor, Target = actor }, context));

                if (CheckBattleOver()) return true;
            }
            return false;
        }

        /// <summary>End-of-turn discard. Retained cards stay; ethereal cards exhaust.</summary>
        private void DiscardHand(Entity player)
        {
            Run(player, context =>
            {
                foreach (Entity card in State.ZoneOf(player, Zones.Hand).ToArray())
                {
                    if (card.HasTag("retain") || card.FindAttached("Retain") != null) continue;
                    if (card.HasTag("ethereal") || card.FindAttached("Ethereal") != null)
                        Interpreter.MoveCard(card, Zones.Exhaust, "exhausted", context);
                    else
                        State.MoveTo(card, Zones.Discard);
                }
            });
        }

        /// <summary>Ends the battle if one side is gone. Returns true if no battle is running afterwards.</summary>
        internal bool CheckBattleOver()
        {
            if (!State.InBattle) return true;

            // The battle is lost when no member of the party is alive, not when the leader dies. A
            // summon standing beside the corpses does not keep the fight going, and a party that
            // has lost its leader fights on. For a party of one the two readings are the same
            // sentence, which is why nothing a game does today notices the change.
            if (State.Player != null && State.Party.Count == 0)
            {
                FinishBattle(won: false);
                return true;
            }

            // `ends: called` means an empty board is just an empty board. A wave game clears one
            // wave two seconds before the next arrives, and under the default that gap wins it the
            // fight; there, the game says when the fight is over and this asks nothing.
            if (State.Rules.Ends == BattleEnd.Called) return false;

            bool hadEnemies = State.Entities.Any(e => e.Kind == EntityKind.Actor && e.Team == Team.Enemy && !e.IsRemoved);
            if (hadEnemies && State.Actors(Team.Enemy).Count == 0)
            {
                FinishBattle(won: true);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Ends the running battle, won or lost, as though the last enemy had just fallen:
        /// <c>battle_end</c> is raised, temporary statuses end, cards go home and
        /// <see cref="Won"/> answers.
        /// </summary>
        /// <returns>
        /// <see cref="ActionResult.Played"/>, <see cref="ActionResult.Unplayable"/> when no battle
        /// is running, or <see cref="ActionResult.ChoicePending"/> when a <c>battle_end</c> effect
        /// stops to ask the player something.
        /// </returns>
        /// <remarks>
        /// The companion of <c>ends: called</c>: a game that has turned the automatic ending off
        /// needs a way to say the fight is over, and a wave game's ending is a rule of the game
        /// above the fight — a timer ran out, a boss arrived, the gate held. It works under
        /// <see cref="BattleEnd.LastEnemy"/> too, for a retreat or a surrender.
        /// </remarks>
        public ActionResult EndBattle(bool won)
        {
            if (!State.InBattle) return ActionResult.Unplayable;

            return Attempt(
                () => { FinishBattle(won); return ActionResult.Played; },
                () => Outcome());
        }

        private void FinishBattle(bool won)
        {
            State.InBattle = false;
            Won = won;
            Entity? player = State.Player;

            Run(player, context =>
            {
                var gameEvent = new GameEvent("battle_end") { Source = player, Target = player };
                gameEvent.Data["won"] = Value.FromBool(won);
                Interpreter.Raise(gameEvent, context);
            });

            // Temporary effects end with the battle; statuses flagged persistent carry over. The
            // reverts raise events of their own, so they run inside an action and drain with it.
            Run(player, _ =>
            {
                foreach (ScheduledAction action in State.Scheduled.ToArray())
                {
                    State.Unschedule(action);
                    if (action.Timing == ScheduleTiming.Until) Interpreter.Revert(action);
                }
            });

            if (player == null) return;

            // Every member is tidied up, not only the leader. Before this a hero carried its
            // statuses and its whole hand into the next battle, because only the leader was cleaned.
            foreach (Entity member in AllMembers())
            {
                foreach (Entity status in member.Attached.ToArray())
                {
                    if (status.Definition != null && (status.Definition.Flags & StatusFlags.Persistent) != 0) continue;
                    State.Remove(status);
                }

                foreach (string zone in new[] { Zones.Hand, Zones.Discard, Zones.Exhaust, Zones.Play, Zones.Powers })
                {
                    foreach (Entity card in State.ZoneOf(member, zone).ToArray()) State.MoveTo(card, Zones.Draw);
                }
            }
        }

        // Cards --------------------------------------------------------------------------------

        public bool IsXCost(Entity card) =>
            card.Definition?.Property("cost")?.First is NameExpr { Name: var name } && string.Equals(name, "x", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The resource a card's cost is paid in: <c>energy</c>, or whatever its <c>cost</c> names.
        /// </summary>
        public string CostResourceOf(Entity card) => card.Definition?.CostResource ?? "energy";

        /// <summary>
        /// The card's current cost after modifiers. An X cost spends everything the payer has of the
        /// resource the card is priced in.
        /// </summary>
        public int CostOf(Entity card)
        {
            if (IsXCost(card)) return card.Controller.GetInt(CostResourceOf(card));

            // From the base cost: card.Get("cost") would already have run the cost channel once.
            var query = new ModifierQuery("cost") { Subject = card, Source = card.Controller, Card = card, Tags = card.Tags.ToArray() };
            Num cost = State.Modifiers.Compute(query, card.GetBase("cost"));
            return Math.Max(0, cost.Floor().ToInt());
        }

        /// <summary>Plays a card from hand: checks energy and target, pays, resolves, and drains triggers.</summary>
        /// <param name="card">The card to play, from whichever member's hand it is in.</param>
        /// <param name="target">Who it is aimed at, or null to settle from the card's <c>target</c> line.</param>
        /// <param name="performer">
        /// The member doing it, or null for the card's own controller — which is what every game
        /// before a party existed means, and what a party of one always has.
        /// </param>
        /// <remarks>
        /// The cost is paid by the card's controller, because that is whose pool the card is in.
        /// Everything else is the performer's: <c>card_played.source</c> is the performer, the
        /// damage comes from the performer, <c>source:</c> filters match the performer, and the
        /// performer's own statuses and modifiers apply. A card that draws draws into the
        /// performer's pile, so a member with no pile of its own draws nothing — which is the same
        /// sentence as the rule that gave it the party's hand to play from.
        /// </remarks>
        public ActionResult Play(Entity card, Entity? target = null, Entity? performer = null)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));

            int cardId = card.Id;
            int targetId = target?.Id ?? 0;
            int performerId = performer?.Id ?? 0;
            return Attempt(
                () => PlayCore(card, target, performer: performer),
                () => Live(cardId) is Entity again ? Play(again, Live(targetId), Live(performerId)) : ActionResult.NotACard);
        }

        /// <summary>
        /// The whole of playing a card. Used by <see cref="Play(Entity, Entity, Entity)"/> for a card the player
        /// plays from hand, and by the <c>play</c> verb for a card an effect plays out of a pile.
        /// </summary>
        /// <param name="card">The card to play.</param>
        /// <param name="target">Who it is aimed at, or null to settle from the card's own <c>target</c> line.</param>
        /// <param name="from">
        /// The zone the card must be in. <see cref="Play(Entity, Entity, Entity)"/> passes <c>hand</c>, which
        /// is what playing a card means for a player. The <c>play</c> verb passes null, meaning any
        /// pile the card is sitting in — the whole point of "play the top card of your draw pile" —
        /// and a card that is already in <c>play</c> or <c>powers</c> is still refused, because a card
        /// being played cannot be played again.
        /// </param>
        /// <param name="free">Skips the payment. The card still sees its own cost, and an X cost still binds what the payer has.</param>
        /// <param name="performer">The member doing it, or null for the card's own controller.</param>
        /// <param name="chain">
        /// The causal chain to continue. A nested play extends its caller's, so <c>once per chain</c>
        /// counts one chain across a cascade rather than restarting at every play boundary.
        /// </param>
        private ActionResult PlayCore(Entity card, Entity? target, string? from = Zones.Hand, bool free = false, Chain? chain = null, Entity? performer = null)
        {
            if (card.Kind != EntityKind.Card || card.IsRemoved) return ActionResult.NotACard;
            if (from != null ? card.Zone != from : NotInAPile(card)) return ActionResult.NotInHand;
            if (card.HasTag("unplayable")) return ActionResult.Unplayable;

            // Who pays, and who does it. They are the same entity unless a party member was named,
            // and they are always the same entity for a party of one.
            Entity payer = card.Controller;
            Entity player = performer != null && performer.Kind == EntityKind.Actor && performer.IsAlive ? performer : payer;
            int cost = CostOf(card);
            // A card priced in something else is refused the same way, so NotEnoughEnergy now means
            // "not enough of whatever this costs".
            string resource = CostResourceOf(card);
            if (!free && !IsXCost(card) && payer.GetInt(resource) < cost) return ActionResult.NotEnoughEnergy;

            if (!TryResolveTarget(card, ref target, automatic: from == null, user: player)) return ActionResult.InvalidTarget;

            if (_runDepth == 0) Interpreter.ResetSteps();
            string startedIn = card.Zone;
            var context = new EvalContext(card) { Source = player, Target = target, Card = card, Chain = chain ?? Interpreter.NewChain() };

            // `, free` changes what is paid, never what the card sees: an X card played free still
            // spends nothing but still knows how much the payer had, or it would do nothing at all.
            if (IsXCost(card)) context.SetLocal("x", Value.FromNumber(Num.FromInt(cost)));
            int paid = free ? 0 : cost;

            // What was actually paid, so "gain 1 hp per energy spent" stays honest about a free play.
            var gameEvent = new GameEvent("card_played") { Source = player, Target = target, Card = card, Amount = paid };
            foreach (string tag in card.Tags) gameEvent.Tags.Add(tag);

            _runDepth++;
            try
            {
                Interpreter.Raise(
                    gameEvent,
                    context,
                    action: () =>
                    {
                        BlockNode? effect = card.Definition?.Effect;
                        if (effect != null) Interpreter.Execute(effect, context);
                    },
                    committed: () =>
                    {
                        if (paid > 0) Interpreter.ChangeStat(payer, resource, AssignOperator.Subtract, paid, context);
                        State.MoveTo(card, Zones.Play);
                        State.RecordHistory("cards_played", player, Num.One);
                        if (card.HasTag("attack")) State.RecordHistory("attacks", player, Num.One);
                    });

                // Cancelled before the play committed, so the card never left the pile it was in.
                // Cancelled in the instead phase, it is already in `play` and still has to be filed.
                if (gameEvent.Cancelled && card.Zone == startedIn)
                {
                    if (_runDepth == 1) Interpreter.Drain();
                    return ActionResult.Cancelled;
                }

                // The effect may already have moved the card (exhausted it, shuffled it away).
                if (card.Zone == Zones.Play && !card.IsRemoved)
                {
                    if (card.HasTag("exhaust") || card.FindAttached("Exhaust") != null)
                        Interpreter.MoveCard(card, Zones.Exhaust, "exhausted", context);
                    else if (card.HasTag("power"))
                        State.MoveTo(card, Zones.Powers);
                    else
                        State.MoveTo(card, Zones.Discard);
                }

                // Only the outermost play drains. A nested one that drained here would resolve the
                // outer effect's already-queued after-listeners in the middle of that effect, which
                // nothing else in the language does.
                if (_runDepth == 1) Interpreter.Drain();
            }
            catch
            {
                Interpreter.AbandonPending();
                throw;
            }
            finally
            {
                _runDepth--;
            }

            if (_runDepth == 0) CheckBattleOver();
            return ActionResult.Played;
        }

        /// <summary>
        /// Whether a card is somewhere the <c>play</c> verb cannot take it from: nowhere at all, or
        /// mid-play already. Every other zone is a pile, including ones a game names itself.
        /// </summary>
        private static bool NotInAPile(Entity card) =>
            card.Zone.Length == 0 || card.Zone == Zones.Play || card.Zone == Zones.Powers;

        /// <summary>Plays the first card of that name in hand, for a game that thinks in names.</summary>
        /// <param name="cardName">The card to look for, matched without regard to case.</param>
        /// <param name="target">What to aim it at, or null for one that needs no target.</param>
        /// <param name="performer">
        /// The member making the play, or null for the card's controller. The card is looked for in
        /// that member's own hand first and then in the party's, which is the leader's — the same
        /// two shapes a party can take, told apart by whether the member has a hand at all.
        /// </param>
        public ActionResult Play(string cardName, Entity? target = null, Entity? performer = null)
        {
            Entity player = State.Player ?? throw new InvalidOperationException("There is no player.");

            // With no performer there is only the leader's hand, which is where this always looked.
            Entity? card = Named(performer ?? player) ?? (performer == null ? null : Named(player));
            return card == null ? ActionResult.NotInHand : Play(card, target, performer);

            Entity? Named(Entity owner) =>
                State.ZoneOf(owner, Zones.Hand).FirstOrDefault(c => string.Equals(c.Name, cardName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// What an action asks to be aimed at: <c>enemy</c>, <c>ally</c>, <c>self</c>, <c>any</c>,
        /// or <c>none</c> for one with no <c>target</c> line. A card or an ability; both read their
        /// <c>target</c> line the same way.
        /// </summary>
        public string TargetMode(Entity action) => TargetRule.Of(action.Definition).Mode;

        /// <summary>
        /// The entities this action may be pointed at right now, in board order, after its own
        /// <c>target … where</c> filter and content's <c>targetable</c> rules: exactly what
        /// <see cref="Play(Entity, Entity, Entity)"/> and <see cref="UseAbility(Entity, Entity)"/> accept.
        /// Empty for one that takes no target. A <c>target any</c> action may also be played at
        /// nothing, which this list cannot say, so a UI that wants to offer that asks
        /// <see cref="TargetMode"/>.
        /// </summary>
        /// <param name="action">A card or an ability.</param>
        public IReadOnlyList<Entity> LegalTargets(Entity action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            TargetRule rule = TargetRule.Of(action.Definition);
            return rule.Mode == "none" ? Array.Empty<Entity>() : Interpreter.LegalTargets(rule, action.Controller, action);
        }

        /// <summary>
        /// Whether <see cref="Play(Entity, Entity, Entity)"/> would accept this card now, aimed somewhere legal: it is in
        /// hand, playable, affordable, and a card that needs someone to point at has someone.
        /// Content can still cancel it while it resolves, which no check made in advance can see.
        /// </summary>
        public bool CanPlay(Entity card)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));
            if (card.Kind != EntityKind.Card || card.IsRemoved || card.Zone != Zones.Hand) return false;
            if (card.HasTag("unplayable")) return false;
            if (!IsXCost(card) && card.Controller.GetInt(CostResourceOf(card)) < CostOf(card)) return false;

            return !TargetRule.Of(card.Definition).NeedsSomeone || LegalTargets(card).Count > 0;
        }

        /// <summary>Settles who an action is aimed at, and whether that target is legal.</summary>
        /// <param name="action">The card or ability being used.</param>
        /// <param name="target">The target given, if any; set to the one settled on.</param>
        /// <param name="automatic">
        /// True when nobody is choosing: the <c>play</c> verb playing a card out of a pile. The
        /// chooser is never consulted, because a targeting dialog in the middle of "play the top card
        /// of your draw pile" contradicts the verb's reason for existing. A target is rolled instead,
        /// from the game's own snapshotted RNG, so the roll replays and saves like any other.
        /// </param>
        /// <param name="user">Who is using it, or null for the action's own controller.</param>
        /// <remarks>
        /// Which candidates there are is <see cref="Interpreter.LegalTargets(TargetRule, Entity, Entity)"/>'s
        /// answer and nothing else's; what is left here is only what to do with the list — take the
        /// one, ask, or roll. The ask happens inside the rolled-back action, so a target chosen
        /// through a <see cref="DeferredChooser"/> is answer number one and replays in the same
        /// place as any other choice the action goes on to make.
        /// </remarks>
        private bool TryResolveTarget(Entity action, ref Entity? target, bool automatic = false, Entity? user = null)
        {
            user ??= action.Controller;
            TargetRule rule = TargetRule.Of(action.Definition);

            switch (rule.Mode)
            {
                case "enemy":
                case "ally":
                {
                    if (target != null) return Interpreter.IsLegalTarget(rule, user, action, target);

                    IReadOnlyList<Entity> candidates = Interpreter.LegalTargets(rule, user, action);
                    if (candidates.Count == 0) return false;

                    // One candidate is not a choice, so nobody is asked and nothing is rolled — which
                    // is also why a party of one behaves exactly as it always has.
                    target = candidates.Count == 1 ? candidates[0]
                        : automatic ? candidates[State.Rng.NextInt(0, candidates.Count - 1)]
                        : Chooser.Choose(new ChoiceRequest("choose a target", candidates, 1, 1, user, action.Definition!.Syntax.Span), State)?.FirstOrDefault(candidates.Contains) ?? candidates[0];
                    return true;
                }

                case "self":
                    target = user;
                    return true;

                case "any":
                    return target == null || Interpreter.IsLegalTarget(rule, user, action, target);

                default:
                    return true;
            }
        }

        // Target validity ----------------------------------------------------------------------
        //
        // Every rule about what may be aimed at lives in Interpreter's targeting section, which is
        // the only place any of the four filters is applied. What is left in this file is who asks,
        // and what to do with the answer.
        //
        // Content adds its own rules through the `targetable` channel, where a value of zero or less
        // means "not this one". With no `of` scope a modifier anchors to its owner, so
        // `modify targetable: set 0` on a status hides its host; a scope is how one entity speaks
        // for others, which is what Taunt needs:
        // `modify targetable of allies where source:enemies, not it.has(Taunt): set 0`.
        //
        // Only the `target` words that name someone ask — `enemy`, `ally` and `any`. `target self`
        // is not a choice, so nothing is asked of it. Area and random effects resolve through the
        // interpreter's own selectors rather than here, which is deliberate: Taunt constrains what a
        // card may be pointed at, not what a blast reaches.

        private const string TargetableChannel = Interpreter.TargetableChannel;

        // Enemies ------------------------------------------------------------------------------

        /// <summary>Picks an enemy's next move from its pattern, so the UI can show intents in advance.</summary>
        public void RollIntent(Entity enemy) => Interpreter.RollIntent(enemy);

        /// <summary>
        /// Who <paramref name="enemy"/> is telegraphing its next move against as things stand: the
        /// member it rolled while that member is still a legal target, and otherwise the first who
        /// is. Null when it has no intent. Recomputed on every ask, so a taunt applied mid-turn
        /// changes what a UI shows with no event and no second roll.
        /// </summary>
        public Entity? IntentTargetOf(Entity enemy) =>
            Interpreter.IntentTargetOf(enemy ?? throw new ArgumentNullException(nameof(enemy)));

        private void RunEnemyMove(Entity enemy)
        {
            if (!enemy.IsAlive || enemy.Intent == null || enemy.Definition == null) return;

            // The move goes at the member the enemy telegraphed, which for a party of one is that
            // one member and so is the player, exactly as it was.
            UseMove(enemy, enemy.Intent, IntentTargetOf(enemy) ?? State.Player);
            enemy.LastMove = enemy.Intent;
            CheckBattleOver();
        }

        private void UseMove(Entity enemy, string moveName, Entity? target)
        {
            MoveDefinition? move = enemy.Definition?.Moves.FirstOrDefault(m => string.Equals(m.Name, moveName, StringComparison.OrdinalIgnoreCase));
            if (move == null) return;

            target = MoveTarget(enemy, target, move);

            Run(enemy, context =>
            {
                context.Target = target;
                var gameEvent = new GameEvent("move") { Source = enemy, Target = target };
                gameEvent.Data["move"] = Value.FromText(move.Name);
                Interpreter.Raise(gameEvent, context, () => Interpreter.Execute(move.Body, context));
            });
        }

        /// <summary>
        /// Who a move is really aimed at, after its own <c>range</c> and content's <c>targetable</c>
        /// rules. A move points at somebody the way a card does, so a taunt that takes the player off
        /// the table sends the move to whoever is left — which is the whole of what a taunt is.
        /// </summary>
        /// <remarks>
        /// The move keeps the target it was given whenever that target is still legal, and falls
        /// back to it when nothing on that side is: an enemy whose every option is hidden still
        /// takes its turn, against the one it was going to hit. Only the target the move is handed
        /// moves; a <c>deal 5 to all enemies</c> inside the move reaches whoever it reaches, as an
        /// area effect does everywhere else.
        /// </remarks>
        private Entity? MoveTarget(Entity enemy, Entity? target, MoveDefinition? move)
        {
            TargetRule rule = TargetRule.OfMove(move);
            if (target == null || Interpreter.IsLegalTarget(rule, enemy, null, target)) return target;

            IReadOnlyList<Entity> side = Interpreter.LegalTargets(rule, enemy, null, State.Actors(target.Team));
            return side.Count > 0 ? side[0] : target;
        }

        // Real time ----------------------------------------------------------------------------

        /// <summary>Advances a <see cref="TickClock"/>. Call from the engine's fixed timestep.</summary>
        public void Tick(int count = 1)
        {
            if (!(State.Clock is TickClock ticks)) throw new InvalidOperationException("Tick needs a TickClock; this runtime uses turns.");
            for (int i = 0; i < count; i++)
            {
                Interpreter.ResetSteps();
                ticks.Tick();
            }
        }

        public bool IsReady(Entity ability) => ability.GetBase("ready_at") <= Num.FromInt(State.Clock.Now);

        /// <summary>
        /// How much longer this ability has to wait, in clock units: ticks on a
        /// <see cref="TickClock"/>, turns on a <see cref="TurnClock"/>. Zero when it is ready.
        /// </summary>
        /// <remarks>
        /// The number a cooldown sweep is drawn from, and the whole of how a real-time game
        /// communicates. Divide by <see cref="TickClock.TicksPerSecond"/> for the seconds a player
        /// reads. The answer used to live in an undocumented <c>ready_at</c> stat that does not
        /// exist at all until the ability has been used once, so a front end had to find the stat
        /// by reflection and then know to treat "absent" as ready rather than as zero seconds left.
        /// </remarks>
        public long ReadyIn(Entity ability)
        {
            if (ability == null) throw new ArgumentNullException(nameof(ability));

            long left = ability.GetBase("ready_at").Ceiling().ToLong() - State.Clock.Now;
            return left > 0 ? left : 0;
        }

        /// <summary>
        /// The abilities an actor is carrying, in the order they were granted: the row of buttons a
        /// real-time front end draws.
        /// </summary>
        /// <remarks>
        /// An ability is an entity in <see cref="Zones.Attached"/> whose <see cref="Entity.Kind"/>
        /// is <see cref="EntityKind.Ability"/>, which is a true sentence that a game should not
        /// have to learn: none of the zones the C# guide lists holds an ability, so the only way to
        /// list them was to read the assembly.
        /// </remarks>
        public IReadOnlyList<Entity> AbilitiesOf(Entity owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            return State.ZoneOf(owner, Zones.Attached).Where(e => e.Kind == EntityKind.Ability).ToList();
        }

        /// <summary>
        /// Whether <see cref="UseAbility(Entity, Entity)"/> would accept this ability now: it is an
        /// ability, its owner is alive, it is off cooldown, and one that needs somebody to point at
        /// has somebody. The companion of <see cref="CanPlay"/>, and the answer a UI greys a button
        /// out on.
        /// </summary>
        public bool CanUse(Entity ability)
        {
            if (ability == null) throw new ArgumentNullException(nameof(ability));
            if (ability.Kind != EntityKind.Ability || ability.IsRemoved) return false;
            if (ability.Owner == null || !ability.Owner.IsAlive) return false;
            if (!IsReady(ability)) return false;

            return !TargetRule.Of(ability.Definition).NeedsSomeone || LegalTargets(ability).Count > 0;
        }

        /// <summary>
        /// Brings a fallen actor back at <paramref name="hp"/> hp, raising <c>revived</c>. Content
        /// writes <c>revive Vestal 10</c>; this is the same thing from C#.
        /// </summary>
        /// <returns>True when the actor is alive afterwards. False for one that was never dead.</returns>
        /// <remarks>
        /// It exists because <c>heal</c> refuses a dead target, deliberately, and so there was no
        /// way at all to put a fallen party member back on its feet.
        /// </remarks>
        public bool Revive(Entity actor, int hp = 1)
        {
            if (actor == null) throw new ArgumentNullException(nameof(actor));
            if (!actor.IsDead || actor.IsRemoved) return false;

            bool revived = false;
            Run(State.Player, context => revived = Interpreter.Revive(actor, Num.FromInt(hp), context));
            if (State.InBattle) CheckBattleOver();
            return revived;
        }

        /// <summary>
        /// Sets a stat on an actor, or on a card: the same thing content's <c>speed = 6</c> does, with
        /// the resource's own bounds, the <c>&lt;stat&gt;_changed</c> event, and death when hp reaches
        /// zero. Returns the change that was actually applied, which a bound or a listener may have
        /// cut short.
        /// </summary>
        /// <remarks>
        /// There was no typed way to write a number onto an entity at all, so a game that finished
        /// creating its leader — a <c>speed</c> for <c>order: speed</c>, a starting shard count —
        /// or spent gold in a shop had to build a statement and call <c>Execute</c>:
        /// <c>runtime.Execute($"lose {amount} gold")</c>, a string-interpolated statement, unchecked
        /// until it runs, for subtracting an integer.
        /// </remarks>
        public Num SetStat(Entity entity, string stat, long value) => WriteStat(entity, stat, AssignOperator.Set, value);

        /// <summary>
        /// Adds to a stat, or takes away with a negative amount: content's <c>gain 2 gold</c> and
        /// <c>lose 3 gold</c>, from C#, with the same bounds, events and consequences.
        /// </summary>
        public Num ChangeStat(Entity entity, string stat, long by) => WriteStat(entity, stat, AssignOperator.Add, by);

        private Num WriteStat(Entity entity, string stat, AssignOperator op, long amount)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (string.IsNullOrWhiteSpace(stat)) throw new ArgumentException("A stat has a name.", nameof(stat));

            Num applied = Num.Zero;
            Entity? actor = entity.Kind == EntityKind.Actor ? entity : entity.Controller;
            Run(actor, context =>
            {
                context.Self = entity;
                applied = Interpreter.ChangeStat(entity, stat, op, Num.FromInt(amount), context);
            });
            if (State.InBattle) CheckBattleOver();
            return applied;
        }

        /// <summary>
        /// Takes a card out of the game for good, as <c>destroy</c> does in content: the way to
        /// remove a card from the deck between battles, or to swap one for its upgraded definition.
        /// Content hears it as <c>destroyed</c>.
        /// </summary>
        /// <returns>
        /// Whether the card is gone. False, having changed nothing, for anything that is not a card
        /// still in the game.
        /// </returns>
        /// <remarks>
        /// The Godot node has had this since the addon shipped, with a worked <c>upgrade_card</c>
        /// recipe beside it, while C# was told to write <c>Execute("destroy target", target: card)</c>
        /// — the same operation as a typed call on one side of the engine and a parsed string on the
        /// other.
        /// </remarks>
        public bool RemoveCard(Entity card)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));
            if (card.Kind != EntityKind.Card || card.IsRemoved) return false;

            Execute("destroy target", target: card);
            return card.IsRemoved;
        }

        /// <summary>Uses an ability if it is off cooldown.</summary>
        /// <returns>
        /// <see cref="ActionResult.Played"/> when it ran, <see cref="ActionResult.NotReady"/> while it
        /// is still on cooldown, <see cref="ActionResult.NotACard"/> when the ability has been removed
        /// or its owner is gone or dead, and <see cref="ActionResult.ChoicePending"/> when it stopped
        /// to ask the player something.
        /// </returns>
        /// <remarks>
        /// An ability reads its own <c>target</c> line exactly as a card does, so
        /// <see cref="ActionResult.InvalidTarget"/> means the same here as there: an ability that
        /// asks for an enemy and has none left is refused rather than run at nobody. An ability has
        /// no cost, so it still never answers <see cref="ActionResult.NotEnoughEnergy"/>; that one is
        /// here for the day it does, so adding it is not a change to this signature.
        /// </remarks>
        public ActionResult UseAbility(Entity ability, Entity? target = null)
        {
            if (ability == null) throw new ArgumentNullException(nameof(ability));

            int abilityId = ability.Id;
            int targetId = target?.Id ?? 0;
            return Attempt(
                () => UseAbilityCore(ability, target),
                () => Live(abilityId) is Entity again ? UseAbility(again, Live(targetId)) : ActionResult.NotACard);
        }

        private ActionResult UseAbilityCore(Entity ability, Entity? target)
        {
            if (ability.IsRemoved || ability.Owner == null || !ability.Owner.IsAlive) return ActionResult.NotACard;
            if (!IsReady(ability)) return ActionResult.NotReady;

            // An ability aims the way a card does: the chooser is offered only legal candidates, a
            // taunt narrows them, and nothing to aim at refuses the cast rather than running it at
            // nobody. Before this, `target` on an ability was read by nothing at all.
            if (!TryResolveTarget(ability, ref target)) return ActionResult.InvalidTarget;

            Entity owner = ability.Owner;
            bool used = false;

            Run(owner, context =>
            {
                context.Self = ability;
                context.Target = target;
                var gameEvent = new GameEvent("ability_used") { Source = owner, Target = target };
                gameEvent.Data["ability"] = Value.FromEntity(ability);
                used = true;
                Interpreter.Raise(gameEvent, context, () =>
                {
                    BlockNode? effect = ability.Definition?.Effect;
                    if (effect != null) Interpreter.Execute(effect, context);
                });
            });

            if (ability.Definition?.Property("cooldown")?.First is NumberExpr cooldown
                && State.Clock.TryConvert(cooldown.Value, cooldown.Unit, out long units))
            {
                ability.SetBase("ready_at", Num.FromInt(State.Clock.Now + CooldownOf(ability, owner, units)));
            }

            return used ? ActionResult.Played : ActionResult.NotACard;
        }

        private const string CooldownChannel = "cooldown";

        /// <summary>
        /// How long an ability waits, in clock units, after the <c>cooldown</c> channel has had it.
        /// </summary>
        /// <remarks>
        /// Modifiers see the converted duration rather than the number content wrote, so <c>x0.75</c>
        /// means the same three quarters whether the ability was authored in seconds or in turns.
        /// The cost of that choice is that an additive amount is in clock units — ticks in real time,
        /// turns otherwise — so a multiplier is the spelling that travels. Rounding is up, matching
        /// how a duration converts in the first place rather than how damage rounds down, and a
        /// cooldown never falls below nothing. The text a card prints still shows the cooldown as
        /// written, the way a printed cost does.
        /// </remarks>
        private long CooldownOf(Entity ability, Entity owner, long units)
        {
            if (!State.Modifiers.HasChannel(CooldownChannel)) return units;

            var query = new ModifierQuery(CooldownChannel)
            {
                Subject = ability,
                Source = owner,
                Tags = ability.Tags.ToArray(),
            };
            return Math.Max(0, State.Modifiers.Compute(query, Num.FromInt(units)).Ceiling().ToInt());
        }

        private void OnClockAdvanced(long now)
        {
            foreach (ScheduledAction action in State.Scheduled.Where(s => s.Timing == ScheduleTiming.AtTime && s.DueAt <= now).ToArray())
            {
                State.Unschedule(action);
                Run(action.Owner, _ => Interpreter.RunScheduled(action));
            }

            Run(null, _ => Interpreter.RunDuePeriodic(now));
            Run(null, _ => Interpreter.ExpireTimedStatuses());
            if (State.InBattle) CheckBattleOver();
        }

        // Player choices ------------------------------------------------------------------------

        private Func<ActionResult>? _replay;
        private bool _deferring;

        /// <summary>
        /// The decision a UI still has to make, set when an action returned
        /// <see cref="ActionResult.ChoicePending"/>. Null when nothing is waiting.
        /// </summary>
        public PendingChoice? Pending { get; private set; }

        /// <summary>
        /// Answers <see cref="Pending"/> and replays the action that asked. Returns what the replayed
        /// action returned, which is <see cref="ActionResult.ChoicePending"/> again if it needs a
        /// further decision.
        /// </summary>
        public ActionResult Answer(params int[] entityIds) => Answer((IEnumerable<int>)entityIds);

        public ActionResult Answer(IEnumerable<Entity> entities) => Answer((entities ?? Enumerable.Empty<Entity>()).Select(e => e.Id));

        public ActionResult Answer(IEnumerable<int> entityIds)
        {
            if (Pending == null) throw new InvalidOperationException("No choice is pending.");
            if (Pending.IsOffer) throw new InvalidOperationException("This choice offers content, not entities: answer it with Answer(EntityDefinition).");
            return Replay(entityIds);
        }

        /// <summary>
        /// Answers a pending offer of content, as <c>discover</c> makes, with the candidate the
        /// player picked from <see cref="PendingChoice.Definitions"/>, and replays the action.
        /// </summary>
        public ActionResult Answer(EntityDefinition chosen)
        {
            if (chosen == null) throw new ArgumentNullException(nameof(chosen));
            if (Pending == null) throw new InvalidOperationException("No choice is pending.");
            if (!Pending.IsOffer) throw new InvalidOperationException("This choice is between entities: answer it with their ids.");

            // The offered instance, or one a reload put in its place: kind and name identify it.
            int index = -1;
            for (int i = 0; i < Pending.Definitions.Count; i++)
            {
                if (ReferenceEquals(Pending.Definitions[i], chosen)) { index = i; break; }
            }
            for (int i = 0; index < 0 && i < Pending.Definitions.Count; i++)
            {
                if (DeferredChooser.SameDefinition(Pending.Definitions[i], chosen)) index = i;
            }
            if (index < 0) throw new ArgumentException($"{chosen} was not one of the offers for \"{Pending.Prompt}\".", nameof(chosen));

            return Replay(new[] { index }, Pending.Definitions[index]);
        }

        private ActionResult Replay(IEnumerable<int> answer, EntityDefinition? pick = null)
        {
            if (!(Chooser is DeferredChooser deferred)) throw new InvalidOperationException("Answering a choice needs a DeferredChooser.");

            Func<ActionResult> replay = _replay ?? throw new InvalidOperationException("There is no action to replay.");
            deferred.Add(answer, pick);
            Pending = null;
            _replay = null;
            try
            {
                return replay();
            }
            finally
            {
                // Answers belong to the action they were given for. Unless it stopped to ask again,
                // that action is over, whether it finished, threw, or found its card gone.
                if (Pending == null) deferred.Clear();
            }
        }

        /// <summary>
        /// Abandons the pending choice. The game is already back where it was before the action that
        /// asked, so nothing else has to be undone.
        /// </summary>
        public void CancelPending()
        {
            Pending = null;
            _replay = null;
            (Chooser as DeferredChooser)?.Clear();
        }

        private ActionResult Outcome() => Pending == null ? ActionResult.Played : ActionResult.ChoicePending;

        private Entity? Live(int id) => id == 0 ? null : State.Find(id);

        /// <summary>
        /// Runs a top-level action so that a decision nobody has answered stops it cleanly: the game
        /// is restored to the snapshot the action started from, the choice is reported, and
        /// <see cref="Answer(int[])"/> replays it. Without a <see cref="DeferredChooser"/> this is
        /// nothing but a direct call.
        /// </summary>
        private ActionResult Attempt(Func<ActionResult> action, Func<ActionResult> replay)
        {
            if (_deferring || !(Chooser is DeferredChooser deferred)) return action();

            // The rollback never leaves this process, so waiting blocks are kept as the objects they
            // are, including those a save could not hold, rather than looked up again by address.
            // Definitions are kept the same way: after a hot reload, an entity whose definition has
            // gone plays on with the one it had, which a lookup by name would no longer find.
            var blocks = new Dictionary<long, BlockNode>();
            Func<ScheduledSnapshot, BlockNode?> kept = record => blocks.TryGetValue(record.Id, out BlockNode? block) ? block : null;
            var definitions = new Dictionary<int, EntityDefinition>();
            foreach (Entity entity in State.Entities)
            {
                if (entity.Definition != null) definitions[entity.Id] = entity.Definition;
            }
            Func<EntitySnapshot, EntityDefinition?> bound = record => definitions.TryGetValue(record.Id, out EntityDefinition? definition) ? definition : null;

            GameSnapshot before;
            try
            {
                before = CaptureState((waiting, _) => blocks[waiting.Id] = waiting.Body!);
            }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException(
                    "Deferred choices need a game that can be snapshotted. " + error.Message, error);
            }

            // A new action abandons a choice left unanswered, and the answers collected for it, so they
            // cannot be read as answers to this action's questions. A replay never gets here with one
            // pending: Answer clears Pending before replaying.
            if (Pending != null) CancelPending();

            int traceMark = State.Trace.Entries.Count;
            _deferring = true;
            deferred.Rewind();
            deferred.Armed = true;
            Interpreter.BeginHostBuffer();

            try
            {
                ActionResult result = action();

                // Only a completed action is real: now the game may hear about its events.
                Interpreter.FlushHostBuffer();
                deferred.Clear();
                Pending = null;
                _replay = null;
                return result;
            }
            catch (ChoicePendingException pending)
            {
                Interpreter.AbandonPending();
                Interpreter.DiscardHostBuffer();

                // Options belong to the state being thrown away; ids survive the round trip.
                int[] optionIds = pending.Request.Options.Select(o => o.Id).ToArray();
                int chooserId = pending.Request.Chooser?.Id ?? 0;

                RestoreState(before, kept, bound);
                State.Trace.TruncateTo(traceMark);

                Pending = new PendingChoice(
                    pending.Request.Prompt,
                    optionIds.Select(Live).Where(e => e != null).ToList()!,
                    pending.Request.Min,
                    pending.Request.Max,
                    Live(chooserId),
                    pending.Request.Span);
                _replay = replay;
                return ActionResult.ChoicePending;
            }
            catch (OfferPendingException pending)
            {
                Interpreter.AbandonPending();
                Interpreter.DiscardHostBuffer();

                // Definitions are immutable content, so they survive the rollback as they are.
                int chooserId = pending.Offer.Chooser?.Id ?? 0;

                RestoreState(before, kept, bound);
                State.Trace.TruncateTo(traceMark);

                Pending = new PendingChoice(pending.Offer.Prompt, pending.Offer.Options, Live(chooserId), pending.Offer.Span);
                _replay = replay;
                return ActionResult.ChoicePending;
            }
            catch
            {
                Interpreter.DiscardHostBuffer();
                deferred.Clear();
                throw;
            }
            finally
            {
                deferred.Armed = false;
                _deferring = false;
            }
        }

        // Hot reload ----------------------------------------------------------------------------

        /// <summary>What <see cref="ApplyContentChanges"/> did, for tools and logs.</summary>
        public sealed class ReloadReport
        {
            /// <summary>Live entities pointed at a newly loaded definition.</summary>
            public int Rebound { get; internal set; }

            /// <summary>Definitions entities still use that the reloaded content no longer has.</summary>
            public List<string> Missing { get; } = new List<string>();

            /// <summary>The ruleset changed; a running game keeps the rules it started with.</summary>
            public bool RulesetChanged { get; internal set; }

            public override string ToString() =>
                $"{Rebound} rebound, {Missing.Count} missing" + (RulesetChanged ? ", ruleset changed (needs a new game)" : string.Empty);
        }

        /// <summary>
        /// Rebinds every live entity to the definition now loaded under its kind and name, so edits
        /// to content take effect in a running game. Call it after reloading files
        /// into <see cref="Content"/>.
        /// </summary>
        /// <remarks>
        /// Stats the game has changed keep their values; stats still at the old definition's number
        /// take the new one. An entity whose definition has disappeared keeps the one it has and is
        /// listed in the report.
        /// </remarks>
        public ReloadReport ApplyContentChanges()
        {
            if (Interpreter.HasPendingWork) throw new InvalidOperationException("Cannot reload content while effects are still resolving.");

            var report = new ReloadReport();

            foreach (Entity entity in State.Entities.ToArray())
            {
                EntityDefinition? old = entity.Definition;
                if (old == null || entity.IsRemoved) continue;

                EntityDefinition? current = Content.Find(old.Name, old.KindName);
                if (current == null)
                {
                    if (!report.Missing.Contains(old.ToString())) report.Missing.Add(old.ToString());
                    continue;
                }

                if (ReferenceEquals(current, old)) continue;

                State.Rebind(entity, current);
                report.Rebound++;
            }

            report.RulesetChanged = !State.Rules.SameAs(Content.BuildRuleset());
            return report;
        }

        // Save and load -------------------------------------------------------------------------

        private BlockAddressBook? _addresses;
        private int _addressGeneration = -1;

        private BlockAddressBook Addresses
        {
            get
            {
                if (_addresses == null || _addressGeneration != Content.Generation)
                {
                    _addresses = BlockAddressBook.Build(Content);
                    _addressGeneration = Content.Generation;
                }
                return _addresses;
            }
        }

        // Blocks that statements run by Execute can schedule, with the text they were parsed from.
        // Weak, so the text is forgotten once nothing it scheduled can run any more.
        private readonly ConditionalWeakTable<BlockNode, ExecutedStatements> _executed = new ConditionalWeakTable<BlockNode, ExecutedStatements>();

        /// <summary>
        /// Whether <see cref="Capture"/> would succeed right now, so a UI can grey out its save
        /// button instead of catching an exception. It is false while effects are still resolving,
        /// since snapshots are only valid between actions (a host callback that runs in the middle
        /// of a card's effect sees it false too), and while a <c>next turn:</c> or
        /// <c>in N turns:</c> block is waiting whose statements a save cannot hold: one a reload has
        /// changed since it was scheduled, or one game code parsed and ran itself rather than
        /// through <see cref="Execute"/>. Such a block stops blocking saves once it has run.
        /// </summary>
        public bool CanCapture => _runDepth == 0 && !Interpreter.HasPendingWork && State.Scheduled.All(s => s.Body == null || Describe(s) != null);

        /// <summary>
        /// Captures the full rules state. Only valid between actions; the returned object is plain
        /// data that any serializer can store. Throws <see cref="InvalidOperationException"/>
        /// whenever <see cref="CanCapture"/> is false.
        /// </summary>
        public GameSnapshot Capture()
        {
            // An action's own effect can be running with nothing queued yet, as when a host
            // callback asks for a save part way through it: that would save half an action.
            if (_runDepth > 0) throw new InvalidOperationException("Cannot snapshot while effects are still resolving.");
            return CaptureState(RecordBlock);
        }

        private void RecordBlock(ScheduledAction waiting, ScheduledSnapshot record)
        {
            var (address, hash, statements) = Describe(waiting) ?? throw new InvalidOperationException(
                $"The block waiting at {waiting.Body!.Span} cannot be saved: the loaded content does not contain its statements, because a reload has changed them " +
                "or game code parsed them itself instead of calling Execute. Save once it has run.");
            record.BlockAddress = address;
            record.BlockHash = hash;
            record.Statements = statements;
        }

        private GameSnapshot CaptureState(Action<ScheduledAction, ScheduledSnapshot> recordBlock)
        {
            if (Interpreter.HasPendingWork) throw new InvalidOperationException("Cannot snapshot while effects are still resolving.");

            GameSnapshot snapshot = State.Capture(recordBlock);
            snapshot.NextChainRoot = Interpreter.ChainCounter;
            snapshot.Won = Won;
            snapshot.SkipNextDraw = _skipNextDraw;
            return snapshot;
        }

        /// <summary>
        /// How a save finds the block of a waiting action again: its place in content, or in the
        /// statements <see cref="Execute"/> ran, with a hash of its statements. Null when a save
        /// cannot hold it.
        /// </summary>
        private (string Address, string Hash, string? Statements)? Describe(ScheduledAction waiting)
        {
            BlockNode block = waiting.Body!;
            BlockAddressBook content = Addresses;
            string? address = content.AddressOf(block);
            if (address != null) return (address, content.HashOf(block), null);

            if (_executed.TryGetValue(block, out ExecutedStatements? executed))
                return (executed.Book.AddressOf(block)!, executed.Book.HashOf(block), executed.Text);

            // A block from content a reload has since replaced, or one game code parsed itself. The
            // same statements in the loaded content do exactly the same, so a save can name those.
            // It names the ones in the definition of the card, relic or enemy that scheduled the
            // block if it can, which is where they came from, so that a later patch to some other
            // definition with the same statements does not turn the save away.
            string hash = BlockHash.Of(block);
            EntityDefinition? origin = waiting.Bindings.TryGetValue("self", out Value self) ? self.Entity?.Definition : null;
            address = (origin == null ? null : content.Find(hash, origin.KindName + ":" + origin.Name)) ?? content.Find(hash);
            return address == null ? null : (address, hash, (string?)null);
        }

        /// <summary>
        /// Replaces the current state with a snapshot. The runtime must have the same content
        /// loaded; the next inputs then play out exactly as they would have in the original game.
        /// A choice left pending is abandoned: it belonged to the game being replaced.
        /// </summary>
        /// <remarks>
        /// Everything the snapshot needs is looked up before anything changes, so when it throws
        /// <see cref="InvalidOperationException"/>, because a definition the snapshot names is not
        /// loaded or a block waiting in it has changed, the game in progress is left as it was.
        /// </remarks>
        public void Restore(GameSnapshot snapshot)
        {
            Dictionary<string, ExecutedStatements>? parsed = null;
            RestoreState(snapshot, record => Saved(record, ref parsed));
            CancelPending();
        }

        /// <summary>The state alone, also used to roll back an action whose pending choice must survive.</summary>
        private void RestoreState(GameSnapshot snapshot, Func<ScheduledSnapshot, BlockNode?> resolveBlock, Func<EntitySnapshot, EntityDefinition?>? keptDefinition = null)
        {
            if (Interpreter.HasPendingWork) throw new InvalidOperationException("Cannot restore while effects are still resolving.");

            State.Restore(snapshot, resolveBlock, keptDefinition);
            Interpreter.ChainCounter = snapshot.NextChainRoot;
            Won = snapshot.Won;
            _skipNextDraw = snapshot.SkipNextDraw;
        }

        /// <summary>
        /// The block a saved action runs. Content blocks are found by address and checked against
        /// the saved hash; statements <see cref="Execute"/> ran are parsed again, once per text.
        /// </summary>
        private BlockNode? Saved(ScheduledSnapshot record, ref Dictionary<string, ExecutedStatements>? parsed)
        {
            string? address = record.BlockAddress;
            if (address == null) return null;

            if (record.Statements != null)
            {
                parsed ??= new Dictionary<string, ExecutedStatements>(StringComparer.Ordinal);
                if (!parsed.TryGetValue(record.Statements, out ExecutedStatements? executed))
                {
                    BlockNode statements;
                    try
                    {
                        statements = ParseStatements(record.Statements);
                    }
                    catch (DslException error)
                    {
                        throw new InvalidOperationException("The snapshot has work waiting from statements run by Execute that no longer parse. " + error.Message, error);
                    }

                    executed = new ExecutedStatements(record.Statements, statements);
                    Remember(executed);
                    parsed[record.Statements] = executed;
                }

                // A save always holds statements with the hash of the block, which is the body of a
                // `next turn:` or `in N turns:` among them. Anything else did not come from Capture.
                BlockNode? block = executed.Book.Resolve(address);
                if (block == null || !executed.Book.CanWait(block) || record.BlockHash == null || executed.Book.HashOf(block) != record.BlockHash)
                    throw new InvalidOperationException($"The snapshot has work waiting (`{address}`) that does not match the statements run by Execute saved with it, so the save has been altered.");
                return block;
            }

            BlockAddressBook content = Addresses;
            BlockNode? found = content.Resolve(address);

            // A save made before hashes were recorded names its blocks by place alone.
            if (record.BlockHash == null) return found ?? throw Unmatched(content, address);
            if (found != null && content.HashOf(found) == record.BlockHash) return found;

            // Lines added or removed above a block move it within its definition: its statements
            // are what identify it.
            string? moved = content.Find(record.BlockHash, content.RootOf(address));
            return moved != null ? content.Resolve(moved) : throw Unmatched(content, address);
        }

        private static InvalidOperationException Unmatched(BlockAddressBook content, string address)
        {
            string root = content.RootOf(address);
            int colon = root.IndexOf(':');
            string kind = colon < 0 ? root : root.Substring(0, colon);
            string name = colon < 0 ? string.Empty : root.Substring(colon + 1);
            string what = kind == "verb" ? $"verb `{name}`" : $"{kind} \"{name}\"";

            return new InvalidOperationException(content.HasRoot(root)
                ? $"The snapshot has work waiting from {what} that the loaded {what} no longer contains (`{address}`): its statements have changed since the save was made."
                : $"The snapshot has work waiting from {what}, which is not loaded (`{address}`).");
        }

        /// <summary>Lets every block these statements can schedule be saved as their text.</summary>
        private void Remember(ExecutedStatements executed)
        {
            foreach (BlockNode body in executed.Book.ScheduleBodies) _executed.AddOrUpdate(body, executed);
        }

        // Ad hoc execution ---------------------------------------------------------------------

        /// <summary>
        /// Runs DSL statements directly, as the REPL and tests do: <c>runtime.Execute("deal 5 to enemy")</c>.
        /// A <c>next turn:</c> or <c>in N turns:</c> block among them is saved with their text, so a
        /// snapshot taken while it waits restores whatever content is loaded.
        /// </summary>
        /// <returns>
        /// <see cref="ActionResult.Played"/>, or <see cref="ActionResult.ChoicePending"/> when the
        /// statements ask the player something: they are rolled back and run again from
        /// <see cref="Answer(int[])"/>.
        /// </returns>
        public ActionResult Execute(string statements, Entity? self = null, Entity? target = null)
        {
            int selfId = self?.Id ?? 0;
            int targetId = target?.Id ?? 0;
            return Attempt(
                () => { ExecuteCore(statements, self, target); return ActionResult.Played; },
                () => { Execute(statements, Live(selfId), Live(targetId)); return Outcome(); });
        }

        private void ExecuteCore(string statements, Entity? self, Entity? target)
        {
            BlockNode block = ParseStatements(statements);
            if (ExecutedStatements.Schedules(block)) Remember(new ExecutedStatements(statements, block));
            Entity? actor = self == null ? State.Player : self.Kind == EntityKind.Actor ? self : self.Controller;

            Run(actor, context =>
            {
                context.Self = self ?? actor;
                context.Target = target;
                Interpreter.Execute(block, context);
            });
            if (State.InBattle) CheckBattleOver();
        }

        /// <summary>Parses free-standing statements by wrapping them in a synthetic block.</summary>
        public static BlockNode ParseStatements(string statements, string file = "<execute>")
        {
            string[] lines = statements.Replace("\r\n", "\n").Split('\n');
            string wrapped = "test \"execute\"\n" + string.Join("\n", lines.Select(l => "  " + l)) + "\n";

            var diagnostics = new DiagnosticBag();
            SourceFileNode file_ = Parser.Parse(wrapped, file, diagnostics);
            diagnostics.ThrowIfErrors();
            return file_.Declarations.OfType<TestDeclNode>().First().Body;
        }

        private int _runDepth;

        /// <summary>
        /// Every top-level operation runs through here: a fresh chain, a fresh step budget and a
        /// full drain. A nested operation (an enemy's <c>use</c> inside a move) shares the outer
        /// budget, so a loop cannot escape the sandbox by calling back into the runtime. If the
        /// operation fails, whatever it queued is dropped with it.
        /// </summary>
        private void Run(Entity? actor, Action<EvalContext> body)
        {
            if (_runDepth == 0) Interpreter.ResetSteps();

            _runDepth++;
            try
            {
                var context = new EvalContext(actor) { Source = actor, Chain = Interpreter.NewChain() };
                body(context);
                Interpreter.Drain();
            }
            catch
            {
                Interpreter.AbandonPending();
                throw;
            }
            finally
            {
                _runDepth--;
            }
        }

        // Runtime-level verbs -----------------------------------------------------------------

        private int _playDepth;

        private void RegisterRuntimeVerbs()
        {
            // `play draw.first, free`: play a card out of a pile, with its cost and its triggers,
            // without the player choosing it (Havoc, Mayhem, Monster Train's automatic plays).
            Interpreter.RegisterRuntimeVerb("play", VerbPlay);

            // `replay card on target`: resolve a card's effect again, for free (Burst, Echo Form).
            Interpreter.RegisterRuntimeVerb("replay", call =>
            {
                ExprNode? node = call.ArgumentNode(0) ?? throw call.Error("expected a card.");
                Entity? target = call.Context.Target;
                if (node is BinaryExpr { Operator: BinaryOperator.On } on)
                {
                    node = on.Left;
                    target = Interpreter.Evaluate(on.Right, call.Context).AsEntities().FirstOrDefault();
                }

                Value value = Interpreter.Evaluate(node, call.Context);

                // `replay Strike` reads as if it repeated a Strike, and there is no Strike: it would
                // run the printed effect for nothing — no card, no cost, no play — which is the one
                // free lunch the other three verbs were closed against. A quoted name is text and
                // lands in the same place, so both are refused as `copy`, `play` and `transform`
                // refuse them.
                if (value.Kind == ValueKind.Definition || value.Kind == ValueKind.Text)
                {
                    string named = value.Definition?.Name ?? value.Text!;
                    if (value.Kind == ValueKind.Text && Content.Find(named) == null)
                        throw call.Error($"nothing named `{named}` is defined.");

                    string spelt = named.IndexOf(' ') >= 0 ? "\"" + named + "\"" : named;
                    throw call.Error(
                        $"`replay` resolves the effect of a card that is in the game, and `{named}` is content. " +
                        $"Write `create {spelt} into hand` first, then `play created.first, free`.");
                }

                Entity? card = value.Kind == ValueKind.Entity ? value.Entity : null;
                EntityDefinition? definition = card?.Definition ?? value.Definition;
                if (definition?.Effect == null) throw call.Error($"{value} has no effect to replay.");

                EvalContext replay = call.Context.Derive();
                replay.Self = card ?? call.Context.Self;
                replay.Card = card ?? call.Context.Card;
                replay.Target = target;
                Interpreter.Execute(definition.Effect, replay);
            });

            // `use Bellow`: an enemy performs one of its own moves.
            Interpreter.RegisterRuntimeVerb("use", call =>
            {
                Entity self = call.Context.Self ?? throw call.Error("only enemies can use moves.");
                string move = call.ArgumentNode(0) switch
                {
                    NameExpr n => n.Name,
                    StringExpr s => s.Value,
                    _ => throw call.Error("expected a move name."),
                };
                if (self.Definition?.Moves.Any(m => string.Equals(m.Name, move, StringComparison.OrdinalIgnoreCase)) != true)
                    throw call.Error($"`{self.Name}` has no move `{move}`.");
                UseMove(self, move, call.Context.Target ?? State.Player);
            });
        }

        /// <summary>
        /// <c>play draw.first</c>, <c>play chosen on enemy</c>, <c>play discard.first, free</c>:
        /// plays a card out of a pile, with its cost, its <c>card_played</c> and its triggers.
        /// Binds <c>played</c> to the card, or to <c>none</c> when it was not played.
        /// </summary>
        /// <remarks>
        /// Where <c>replay</c> resolves an effect again for nothing, this is a real play: the card
        /// leaves its pile, pays, counts, and is filed afterwards by its tags. A refusal the rules
        /// allow — a Curse, an unaffordable cost, no legal target — is not an error, because "play the
        /// top card of your draw pile" must not crash the first time the top card is a Curse.
        /// <c>played == none</c> is the language's own way of asking.
        /// </remarks>
        private void VerbPlay(VerbCall call)
        {
            ExprNode node = call.ArgumentNode(0) ?? throw call.Error("expected a card, as in `play draw.first`.");
            Entity? aim = null;
            bool aimed = false;
            if (node is BinaryExpr { Operator: BinaryOperator.On } on)
            {
                node = on.Left;
                aim = Interpreter.Evaluate(on.Right, call.Context).AsEntities().FirstOrDefault();
                aimed = true;
            }

            // The name exists whatever happens next, so `if played == none:` is always answerable.
            call.Context.SetLocal("played", Value.None);

            Value value = Interpreter.Evaluate(node, call.Context);

            // A quoted name evaluates to text rather than to a definition, and both read as if they
            // would make a card. Neither is a card in a pile, so both are refused the same way.
            if (value.Kind == ValueKind.Definition || value.Kind == ValueKind.Text)
            {
                string named = value.Definition?.Name ?? value.Text!;
                if (value.Kind == ValueKind.Text && Content.Find(named) == null)
                    throw call.Error($"nothing named `{named}` is defined.");

                string spelt = named.IndexOf(' ') >= 0 ? "\"" + named + "\"" : named;
                throw call.Error(
                    $"`play` plays a card that is in a pile. Write `create {spelt} into hand` first, then `play created.first`.");
            }

            Entity? card = value.Kind == ValueKind.Entity ? value.Entity : value.AsEntities().FirstOrDefault();

            // An empty pile is an ordinary answer, not a mistake: `played` stays `none`.
            if (card == null || card.IsRemoved) return;
            if (card.Kind != EntityKind.Card) throw call.Error($"`{card.Name}` is not a card.");

            if (_playDepth >= State.Rules.MaxCallDepth)
                throw call.Error($"cards have played each other more than {State.Rules.MaxCallDepth} deep; is a card playing itself?");

            Entity? target = aimed ? aim : Inherited(card, call.Context.Target);

            ActionResult result;
            _playDepth++;
            try
            {
                result = PlayCore(card, target, from: null, free: call.Flag("free"), chain: call.Context.Chain);
            }
            finally
            {
                _playDepth--;
            }

            if (result == ActionResult.Played) call.Context.SetLocal("played", Value.FromEntity(card));
        }

        /// <summary>
        /// The effect's own target, but only where the card could really be pointed at it.
        /// </summary>
        /// <remarks>
        /// A target written with <c>on</c> is an instruction and is passed through as it stands, legal
        /// or not. One taken from the running effect is a hint, and a `play` written in a listener
        /// inherits whatever that event happened to be about — the actor whose turn started, the card
        /// that was drawn, the player who was hit. Handing that to a <c>target enemy</c> card refuses
        /// the play, silently, which is "play the top card of your draw pile" not working in the one
        /// place it is most often written (Mayhem, Monster Train's automatic plays). So an inherited
        /// target the card cannot legally take is dropped, and the target is rolled as if none had
        /// been given.
        /// </remarks>
        private Entity? Inherited(Entity card, Entity? target)
        {
            if (target == null) return null;

            switch (TargetMode(card))
            {
                // Settled from the controller regardless, so there is nothing to inherit.
                case "self":
                    return null;

                case "enemy":
                case "ally":
                case "any":
                    return LegalTargets(card).Contains(target) ? target : null;

                // No `target` line: nothing for the inherited target to be illegal against, and the
                // card's own effect may still read `target`, so it comes across as it always did.
                default:
                    return target;
            }
        }
    }
}
