using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Syntax;

namespace Cantrip.Runtime
{
    /// <summary>Well-known zone names. Games may use any other string as well.</summary>
    public static class Zones
    {
        /// <summary>Nowhere. What an entity that has been removed, or never placed, reports.</summary>
        public const string None = "";

        /// <summary>Where actors stand. An actor is in here while it has a slot, alive or dead, until it is buried.</summary>
        public const string Board = "board";

        /// <summary>Cards held. A card listens from here, which is what a curse that hurts while held relies on.</summary>
        public const string Hand = "hand";

        /// <summary>The pile drawn from, and where deck building puts cards. A card here is inert: it neither listens nor modifies.</summary>
        public const string Draw = "draw";

        /// <summary>Where played and discarded cards go, and what a shuffle pulls back into the draw pile.</summary>
        public const string Discard = "discard";

        /// <summary>Out of the battle. A shuffle does not reach it, which is the whole point of exhausting a card.</summary>
        public const string Exhaust = "exhaust";

        /// <summary>Where a card sits while its effect resolves. A card is here only for the length of its own play.</summary>
        public const string Play = "play";

        /// <summary>Played cards that stay in effect for the rest of the battle.</summary>
        public const string Powers = "powers";

        /// <summary>Where an actor's relics and items live, from the moment they are obtained until the run ends.</summary>
        public const string Relics = "relics";

        /// <summary>
        /// Statuses, keywords and abilities hanging off an actor. It is the zone that is easiest to
        /// miss: nothing in the C# guide's list of piles holds an ability, and
        /// <c>CardRuntime.AbilitiesOf(Entity)</c> exists so no game has to learn that.
        /// </summary>
        public const string Attached = "attached";

        /// <summary>Where the buried go. A fallen party member is here, which is how <c>CardRuntime.Fallen</c> can list one to revive.</summary>
        public const string Dead = "dead";

        /// <summary>
        /// The names above, in the order they are declared, so a tool can list them and a message
        /// about a misspelt one can name them all.
        /// </summary>
        public static IReadOnlyList<string> WellKnown { get; } = new[]
        {
            None, Board, Hand, Draw, Discard, Exhaust, Play, Powers, Relics, Attached, Dead,
        };

        /// <summary>
        /// Whether a name is one of the zones above. A game may still use any other string, so this
        /// asks about the vocabulary, not about what is allowed: it is what lets a tool say that
        /// "hnd" is probably a typo without refusing a zone a game invented.
        /// </summary>
        public static bool IsWellKnown(string? zone) => zone != null && Known.Contains(zone);

        private static readonly HashSet<string> Known = new HashSet<string>(WellKnown, StringComparer.Ordinal);
    }

    /// <summary>
    /// What makes a piece of deferred work come due. It is saved with the game, so the numbers are
    /// fixed for the whole 1.x line.
    /// </summary>
    public enum ScheduleTiming
    {
        // Saved games store these as numbers: never renumber or reuse one, only add.

        /// <summary>Runs once when the clock reaches <see cref="ScheduledAction.DueAt"/>.</summary>
        AtTime = 0,

        /// <summary>Runs at the owner's next turn start.</summary>
        NextTurn = 1,

        /// <summary>Undoes <see cref="ScheduledAction.Undo"/> when <see cref="ScheduledAction.Deadline"/> fires.</summary>
        Until = 2,
    }

    /// <summary>One thing an <c>until</c> block did, so it can be reverted at the deadline.</summary>
    public sealed class TemporaryChange
    {
        /// <summary>
        /// Records one reversible change made inside an <c>until</c> block. Exactly one of the three
        /// optional halves is meant per change: a tag added, an entity attached, or a stat moved by
        /// <paramref name="delta"/>.
        /// </summary>
        /// <param name="entity">What was changed.</param>
        /// <param name="tag">The tag that was added, to be taken off again.</param>
        /// <param name="attached">The status or keyword that was attached, to be removed again.</param>
        /// <param name="stat">The stat that moved.</param>
        /// <param name="delta">How far it moved, to be subtracted back off. Undoing a stat puts the delta back rather than restoring the old number, so a change made in between survives.</param>
        public TemporaryChange(Entity entity, string? tag = null, Entity? attached = null, string? stat = null, Num delta = default)
        {
            Entity = entity;
            Tag = tag;
            Attached = attached;
            Stat = stat;
            Delta = delta;
        }

        /// <summary>A move made inside an <c>until</c> block, with the slot to put the actor back on.</summary>
        public TemporaryChange(Entity entity, (int Lane, int Rank) slot)
        {
            Entity = entity;
            Slot = slot;
        }

        /// <summary>What was changed, and what the undo will be applied to.</summary>
        public Entity Entity { get; }

        /// <summary>The tag that was added, or null when this change is not about a tag.</summary>
        public string? Tag { get; }

        /// <summary>The status or keyword that was attached, or null when this change is not an attachment.</summary>
        public Entity? Attached { get; }

        /// <summary>The stat that moved, or null when this change is not about a stat.</summary>
        public string? Stat { get; }

        /// <summary>How far the stat moved. The undo subtracts this rather than restoring the old value, so anything that changed the stat in between is kept.</summary>
        public Num Delta { get; }

        /// <summary>
        /// The slot the actor stood on before an <c>until</c> block moved it, or null for a change
        /// that is not a move. This is where a place differs from a <c>transform</c>, which
        /// <c>until</c> refuses (CT321) because nothing remembers the old form: a place is two
        /// integers, its inverse is exact, and putting the actor back is the same swap that brought
        /// it here.
        /// </summary>
        public (int Lane, int Rank)? Slot { get; }
    }

    /// <summary>Deferred work from <c>next turn:</c>, <c>in 2 turns:</c> and <c>until turn_end:</c>.</summary>
    public sealed class ScheduledAction
    {
        internal ScheduledAction(long id, ScheduleTiming timing, Entity owner, BlockNode? body)
        {
            Id = id;
            Timing = timing;
            Owner = owner;
            Body = body;
            Bindings = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>A number unique within this game, kept in the save, so a scheduled block survives a round trip and can be cancelled by id.</summary>
        public long Id { get; }

        /// <summary>What makes it come due, which decides whether <see cref="DueAt"/> or <see cref="Deadline"/> is the one to read.</summary>
        public ScheduleTiming Timing { get; }

        /// <summary>Whose block this is: whose turn ends it, and what <c>self</c> means when it runs.</summary>
        public Entity Owner { get; }

        /// <summary>
        /// The statements to run, or null for an <see cref="ScheduleTiming.Until"/> entry, whose whole
        /// job is to put <see cref="Undo"/> back. A body the game itself parsed is also null after a
        /// save, which is why such a block blocks <c>CardRuntime.Capture</c> until it has run.
        /// </summary>
        public BlockNode? Body { get; }

        /// <summary>The absolute clock time this is due at, for <see cref="ScheduleTiming.AtTime"/>. Meaningless for the other two timings.</summary>
        public long DueAt { get; internal set; }

        /// <summary>The event that ends an <c>until</c> block, such as <c>turn_end</c>. Null for the other timings.</summary>
        public string? Deadline { get; internal set; }

        /// <summary>Names captured when the block was scheduled (<c>target</c>, <c>source</c>...).</summary>
        public Dictionary<string, Value> Bindings { get; }

        /// <summary>
        /// What to put back when the deadline fires, in the order it was done — it is reverted from the
        /// end. Empty for a plain <c>next turn:</c> block, which changes nothing that has to be undone.
        /// </summary>
        public List<TemporaryChange> Undo { get; } = new List<TemporaryChange>();
    }

    /// <summary>
    /// All rules state for one game: entities, zones, listeners, modifiers, clock, RNG, history
    /// and scheduled work. Presentation state lives in the game, never here.
    /// </summary>
    public sealed partial class GameState
    {
        private readonly List<Entity> _entities = new List<Entity>();
        private readonly Dictionary<int, Entity> _byId = new Dictionary<int, Entity>();
        private readonly Dictionary<(int Owner, string Zone), List<Entity>> _zones = new Dictionary<(int, string), List<Entity>>();
        private readonly HashSet<int> _active = new HashSet<int>();
        private readonly Dictionary<string, Num> _turnHistory = new Dictionary<string, Num>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Num> _battleHistory = new Dictionary<string, Num>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ScheduledAction> _scheduled = new List<ScheduledAction>();

        /// <summary>
        /// Ids of the party members that have already taken their step this turn. Ids, not indices,
        /// so that a member that changes slot mid-turn keeps having acted.
        /// </summary>
        private readonly HashSet<int> _acted = new HashSet<int>();

        /// <summary>
        /// Who stands where. One actor per slot, and the only account of it: <see cref="Entity.Lane"/>
        /// and <see cref="Entity.Rank"/> are what this index is keyed by, so the two cannot drift
        /// apart. An actor is in here exactly while it is in the <c>board</c> zone, alive or not — a
        /// corpse that has not been buried yet is still standing in its slot.
        /// </summary>
        private readonly Dictionary<(int Side, int Lane, int Rank), Entity> _slots =
            new Dictionary<(int, int, int), Entity>();

        private int _nextId = 1;
        private long _nextSequence = 1;
        private long _nextScheduleId = 1;

        /// <summary>
        /// Builds the rules state for one game. <c>CardRuntime</c> does this; a game builds one directly
        /// only when it drives the interpreter itself.
        /// </summary>
        /// <param name="content">The library the game is played from.</param>
        /// <param name="rules">The effective ruleset, usually <c>ContentLibrary.BuildRuleset(DiagnosticBag)</c>.</param>
        /// <param name="clock">Turns or ticks. It is not checked against the ruleset here; <c>CardRuntime</c> is where that mismatch is caught.</param>
        /// <param name="seed">The run's seed. Every roll in the game comes from it.</param>
        public GameState(ContentLibrary content, Ruleset rules, IGameClock clock, ulong seed)
        {
            Content = content ?? throw new ArgumentNullException(nameof(content));
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Rng = new Rng(seed);
            Modifiers = new ModifierPipeline(this);
            Events = new EventBus();
            Trace = new TraceLog();
            _board = content.DefaultBoard;

            // Modifiers can read `now`, so time passing is a state change like any other.
            clock.Advanced += _ => Touch();
        }

        /// <summary>The library this game is played from. Reloading into it needs <c>CardRuntime.ApplyContentChanges</c> to take effect on live entities.</summary>
        public ContentLibrary Content { get; }

        /// <summary>
        /// The rules this game started with. A game keeps them for its whole life: content reloaded with
        /// a different ruleset does not change a game in progress, which is what the reload report says
        /// out loud.
        /// </summary>
        public Ruleset Rules { get; }

        /// <summary>Time. Whether it is a <see cref="TurnClock"/> or a <see cref="TickClock"/> is what makes this a turn-based or a real-time game.</summary>
        public IGameClock Clock { get; }

        /// <summary>
        /// The game's own generator, and the one every roll in the rules comes out of. Drawing from it
        /// directly shifts every later shuffle, so a game that wants a number of its own forks it.
        /// </summary>
        public Rng Rng { get; }

        /// <summary>Every live <c>modify</c>, and what computes a value from them. <c>Entity.Get(string)</c> is the usual way in.</summary>
        public ModifierPipeline Modifiers { get; }

        /// <summary>Every live listener, and who hears what in which order. It does not run anything; the interpreter does.</summary>
        public EventBus Events { get; }

        /// <summary>The causality log. Disabled unless <c>RuntimeOptions.Trace</c> asked for it, and free while disabled.</summary>
        public TraceLog Trace { get; }

        /// <summary>Incremented by every mutation. Caches compare against it instead of tracking dependencies.</summary>
        public long Version { get; private set; }

        internal void Touch() => Version++;

        private int _turn;
        private int _battleNumber;
        private Team _activeTeam = Team.Player;
        private bool _inBattle;
        private Entity? _player;
        private BoardShape _board;

        // Modifiers can read `turn` and friends, so each of these bumps the version when it changes.

        /// <summary>
        /// The turn number within the current battle, counting from 1, and 0 before the first one. A
        /// real-time game has no turns, so it stays at 0 for the whole fight — which is a battle in
        /// progress, not one that has not started.
        /// </summary>
        public int Turn
        {
            get => _turn;
            internal set { if (_turn != value) { _turn = value; Touch(); } }
        }

        /// <summary>How many battles this run has started, counting from 1. It survives a battle ending, which is what a run's difficulty curve reads.</summary>
        public int BattleNumber
        {
            get => _battleNumber;
            internal set { if (_battleNumber != value) { _battleNumber = value; Touch(); } }
        }

        /// <summary>Whose turn it is. In a real-time game it stays on <see cref="Team.Player"/>, since nothing hands it over.</summary>
        public Team ActiveTeam
        {
            get => _activeTeam;
            internal set { if (_activeTeam != value) { _activeTeam = value; Touch(); } }
        }

        /// <summary>
        /// Whether a battle is running. It is false between battles and during a run's shops and rests,
        /// where most of the battle vocabulary means nothing.
        /// </summary>
        public bool InBattle
        {
            get => _inBattle;
            internal set { if (_inBattle != value) { _inBattle = value; Touch(); } }
        }

        /// <summary>
        /// The run's leader, or null before <c>CardRuntime.CreatePlayer(string, int, int)</c>.
        /// <c>CardRuntime.Player</c> is the same actor without the null.
        /// </summary>
        public Entity? Player
        {
            get => _player;
            internal set { if (_player != value) { _player = value; Touch(); } }
        }

        /// <summary>
        /// The living members of the player's party, in the order they take their steps: where they
        /// stand, which on the default board is the order <see cref="Actors(Team?)"/> already gives.
        /// It is derived every time it is asked and never stored, so a death, a summon or a swap
        /// mid-turn changes it with no bookkeeping.
        /// </summary>
        /// <remarks>
        /// A game that never declares a <c>hero</c> has a party of one: the leader
        /// <c>CreatePlayer</c> made. That is why every rule written against the party is exactly
        /// today's rule for content that has none.
        /// </remarks>
        public IReadOnlyList<Entity> Party
        {
            get
            {
                var members = new List<Entity>();
                foreach (Entity actor in Actors(Team.Player))
                {
                    if (actor.IsPartyMember) members.Add(actor);
                }
                if (Rules.Order == PartyOrder.Speed) members.Sort(BySpeed);
                return members;
            }
        }

        /// <summary>
        /// Every living combatant on both sides in the one order <c>turns: initiative</c> runs them
        /// in. Under <c>order: position</c> the party comes first, in the order it stands in, and
        /// then the enemies, so a party of one against one enemy takes the same two steps it always
        /// did; under <c>order: speed</c> the two sides interleave by the <c>speed</c> stat.
        /// </summary>
        /// <remarks>
        /// Derived every time it is asked, like <see cref="Party"/>: a death, a summon or a change
        /// of <c>speed</c> mid-round changes who is up next with no bookkeeping and nothing extra in
        /// the snapshot. <see cref="HasActed"/> is what says who has already had their step, and it
        /// holds ids rather than places for exactly this reason.
        /// </remarks>
        public IReadOnlyList<Entity> TurnOrder
        {
            get
            {
                var order = new List<Entity>(Actors());
                if (Rules.Order == PartyOrder.Speed) order.Sort(BySpeed);
                else order.Sort(BySide);
                return order;
            }
        }

        /// <summary>Fastest first, then where they stand, then id, so the order is total.</summary>
        private static int BySpeed(Entity a, Entity b)
        {
            int speed = b.GetInt("speed").CompareTo(a.GetInt("speed"));
            return speed != 0 ? speed : BySlot(a, b);
        }

        /// <summary>The player's side first, then where they stand: position order across both sides.</summary>
        private static int BySide(Entity a, Entity b)
        {
            int team = ((int)a.Team).CompareTo((int)b.Team);
            return team != 0 ? team : BySlot(a, b);
        }

        /// <summary>
        /// The member whose step it is, or null when none of the party's is: outside a battle, on
        /// the enemies' turn, and once every member has acted.
        /// </summary>
        /// <remarks>
        /// Under <c>turns: initiative</c> it is binding: that member and no other may act, and the
        /// round does not move on until it passes. Under <c>turns: sides</c> the whole party shares
        /// one turn and a game may act with its members in any order it likes, so this is the one
        /// the engine <em>would</em> offer next — the first that has not acted — and a suggestion
        /// rather than a rule. A game that wants the rule asks <see cref="CardRuntime.CanAct"/>.
        /// It used to be null in <c>sides</c>, which meant a host polling it got nothing to show.
        /// </remarks>
        public Entity? ActiveMember
        {
            get
            {
                if (!InBattle) return null;

                if (Rules.Turns == TurnMode.Initiative)
                {
                    foreach (Entity combatant in TurnOrder)
                    {
                        if (_acted.Contains(combatant.Id)) continue;
                        return combatant.IsPartyMember ? combatant : null;
                    }
                    return null;
                }

                if (ActiveTeam != Team.Player) return null;
                foreach (Entity member in Party)
                {
                    if (!_acted.Contains(member.Id)) return member;
                }
                return null;
            }
        }

        /// <summary>Whether this combatant has already taken its step this round.</summary>
        public bool HasActed(Entity actor) =>
            actor != null ? _acted.Contains(actor.Id) : throw new ArgumentNullException(nameof(actor));

        /// <summary>The party members that have already acted this turn.</summary>
        internal HashSet<int> Acted => _acted;

        internal void ClearActed()
        {
            if (_acted.Count == 0) return;
            _acted.Clear();
            Touch();
        }

        internal void MarkActed(Entity member)
        {
            if (_acted.Add(member.Id)) Touch();
        }

        /// <summary>
        /// The board this battle is fought on. Content owns the shapes; a game picks one per battle
        /// with <see cref="CardRuntime.StartBattle"/>, and a game that names none is played on
        /// <see cref="ContentLibrary.DefaultBoard"/>.
        /// </summary>
        public BoardShape Board => _board;

        /// <summary>
        /// Plays the rest of this battle on another board, moving anyone who no longer fits. Each
        /// actor keeps its slot when the new shape has one free there; otherwise it takes the lowest
        /// free slot in its own lane, and failing that the lowest free slot anywhere, so nobody is
        /// ever lost. A board with no room at all for who is standing on it is refused by name.
        /// </summary>
        public void UseBoard(BoardShape board)
        {
            if (board == null) throw new ArgumentNullException(nameof(board));
            if (ReferenceEquals(board, _board)) return;

            // Everyone on the board, in the order they stand in, so the re-seating is deterministic
            // and an actor that already fits never loses its slot to one that was behind it.
            List<Entity> standing = ZoneOf(null, Zones.Board)
                .Where(e => e.Kind == EntityKind.Actor && !e.IsRemoved)
                .OrderBy(e => (int)e.Team).ThenBy(e => e.Lane).ThenBy(e => e.Rank).ThenBy(e => e.Id)
                .ToList();

            _slots.Clear();
            _board = board;
            Touch();

            var homeless = new List<Entity>();
            foreach (Entity actor in standing)
            {
                if (board.Holds(actor.Lane, actor.Rank) && !_slots.ContainsKey(Key(actor.Team, actor.Lane, actor.Rank)))
                    _slots[Key(actor.Team, actor.Lane, actor.Rank)] = actor;
                else
                    homeless.Add(actor);
            }

            foreach (Entity actor in homeless)
            {
                (int Lane, int Rank)? slot = FreeSlot(actor.Team, actor.Lane) ?? FreeSlot(actor.Team, null);
                if (slot == null)
                {
                    throw new InvalidOperationException(
                        $"Board \"{board.Name}\" ({board.Describe()}) has no room for {actor}, which was standing at " +
                        $"{board.LaneWord} {actor.Lane}, {board.RankWord} {actor.Rank}.");
                }

                actor.Lane = slot.Value.Lane;
                actor.Rank = slot.Value.Rank;
                _slots[Key(actor.Team, actor.Lane, actor.Rank)] = actor;
            }
        }

        /// <summary>
        /// Every entity ever made in this game, in creation order, the removed and the dead included.
        /// Filter it: <c>Actors(Team?)</c>, <c>ZoneOf(Entity, string)</c> and <c>Party</c> are the
        /// questions usually meant.
        /// </summary>
        public IReadOnlyList<Entity> Entities => _entities;

        /// <summary>Everything waiting to run or to be undone, in the order it was scheduled. It is saved with the game.</summary>
        public IReadOnlyList<ScheduledAction> Scheduled => _scheduled;

        /// <summary>
        /// The entity with that id, or null. Ids are stable across a save and restore, which is why a
        /// game that has to remember one entity between sessions remembers the id and not the object.
        /// </summary>
        public Entity? Find(int id) => _byId.TryGetValue(id, out Entity? entity) ? entity : null;

        /// <summary>Finds a live entity by name, preferring actors on the board.</summary>
        public Entity? FindByName(string name)
        {
            Entity? fallback = null;
            foreach (Entity entity in _entities)
            {
                if (entity.IsRemoved || !string.Equals(entity.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (entity.Kind == EntityKind.Actor && entity.Zone == Zones.Board) return entity;
                fallback ??= entity;
            }
            return fallback;
        }

        // Creation and removal ----------------------------------------------------------------

        /// <summary>Creates an entity from a definition, copying its stats and tags.</summary>
        public Entity Instantiate(EntityDefinition definition, Entity? owner = null, Team team = Team.Neutral, string zone = Zones.None)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));

            Entity entity = CreateBare(definition.Name, definition.Kind, definition, owner, team);
            foreach (KeyValuePair<string, Num> stat in definition.Stats) entity.SetBase(stat.Key, stat.Value);
            foreach (string tag in definition.Tags) entity.AddTag(tag);
            Place(entity, zone, toTop: false);
            return entity;
        }

        /// <summary>
        /// Makes a second entity from one already in the game: the same definition, but the stats and
        /// tags it has <em>now</em> rather than the ones it was printed with, and a fresh instance of
        /// everything attached to it. This is what <c>copy</c> is built from, and the whole difference
        /// between it and <c>create</c>: an upgraded, discounted or poisoned thing is duplicated as it
        /// stands.
        /// </summary>
        /// <remarks>
        /// The side and the owner are the caller's to decide, because a copy takes them from the
        /// original rather than from whoever made it: copying an enemy's minion must not hand it to
        /// the player. Statuses come across as a snapshot of a state — no <c>status_applied</c> is
        /// raised and <c>immune</c> is not consulted — so <c>stacks</c>, <c>duration</c> and
        /// <c>expires_at</c> arrive exactly as they stand, because all three are ordinary stats.
        /// </remarks>
        public Entity Duplicate(Entity original, Entity? owner = null, Team team = Team.Neutral, string zone = Zones.None)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));

            EntityDefinition definition = original.Definition
                ?? throw new ArgumentException($"{original} was not made from content, so there is nothing to copy it from.", nameof(original));

            Entity copy = CreateBare(original.Name, original.Kind, definition, owner, team);
            CopyStateInto(original, copy);
            Place(copy, zone, toTop: false);

            foreach (Entity attached in original.Attached.ToArray())
            {
                if (attached.IsRemoved || attached.Definition == null) continue;

                Entity again = CreateBare(attached.Name, attached.Kind, attached.Definition, copy, Team.Neutral);
                CopyStateInto(attached, again);
                again.Source = attached.Source;
                Attach(copy, again);
            }

            return copy;
        }

        private static void CopyStateInto(Entity from, Entity to)
        {
            foreach (string stat in from.StatNames) to.SetBase(stat, from.GetBase(stat));
            foreach (string tag in from.Tags) to.AddTag(tag);
        }

        /// <summary>Creates an entity with no definition, for players, test fixtures and host-owned objects.</summary>
        public Entity Spawn(string name, EntityKind kind, Entity? owner = null, Team team = Team.Neutral, string zone = Zones.None)
        {
            Entity entity = CreateBare(name, kind, null, owner, team);
            Place(entity, zone, toTop: false);
            return entity;
        }

        private Entity CreateBare(string name, EntityKind kind, EntityDefinition? definition, Entity? owner, Team team)
        {
            var entity = new Entity(this, _nextId++, name, kind, definition)
            {
                Owner = owner,
                Team = team,
                Sequence = _nextSequence++,
            };
            _entities.Add(entity);
            _byId[entity.Id] = entity;
            Touch();
            return entity;
        }

        /// <summary>
        /// Takes an entity out of the game: listeners and modifiers stop, attachments go with it.
        /// Removing a relic during its own trigger is safe; already-queued work checks
        /// <see cref="Entity.IsRemoved"/> before running.
        /// </summary>
        public void Remove(Entity entity)
        {
            if (entity.IsRemoved) return;

            foreach (Entity child in entity.Attached.ToArray()) Remove(child);

            entity.Owner?.Detach(entity);
            TakeFromZone(entity);
            entity.IsRemoved = true;
            SetActive(entity, false);
            _scheduled.RemoveAll(s => s.Owner == entity && s.Timing != ScheduleTiming.Until);
            Touch();
        }

        /// <summary>
        /// Replaces what an entity <em>is</em> while keeping who it is. The same object keeps its id,
        /// owner, source, side, sequence, zone and place in that zone, its board slot and its history
        /// counters, and now wears another definition's name, stats, tags, listeners and modifiers.
        /// This is what <c>transform</c> is built from.
        /// </summary>
        /// <remarks>
        /// Everything the old definition brought goes, and goes silently. Statuses and keywords leave
        /// through <see cref="Remove"/> rather than raising <c>status_removed</c>, because one verb
        /// raising a variable number of cancellable events — each able to destroy the host half way
        /// through — is not something content could reason about. <c>destroy</c> already takes its
        /// attachments the same way. The one event is <c>transformed</c>, which the interpreter
        /// raises around this call.
        /// </remarks>
        public void Become(Entity entity, EntityDefinition definition)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (entity.IsRemoved) return;

            // Deactivating first drops the Listener objects, and with them the `once per ...` windows
            // the old definition had spent: the new thing's limits are its own.
            SetActive(entity, false);

            foreach (Entity attached in entity.Attached.ToArray()) Remove(attached);

            entity.ClearStatsAndTags();
            entity.Definition = definition;
            entity.Name = definition.Name;

            foreach (KeyValuePair<string, Num> stat in definition.Stats) entity.SetBase(stat.Key, stat.Value);
            foreach (string tag in definition.Tags) entity.AddTag(tag);
            if (entity.Kind == EntityKind.Actor && !entity.HasStat("block")) entity.SetBase("block", Num.Zero);

            // An Ogre that scheduled 7 damage and then became a Sheep deals the Sheep's 1, and the
            // move the player was shown is not one this thing has.
            entity.PatternIndex = 0;
            entity.LastMove = null;
            entity.Intent = null;
            entity.Phase = null;

            _scheduled.RemoveAll(s => s.Owner == entity && s.Timing != ScheduleTiming.Until);

            // An `until` revert aimed at a stat this entity no longer has would take a buff given to
            // the Ogre off the Sheep, off a stat it may not even have. It goes with the definition
            // that earned it, exactly as `Remove` drops the plans of something leaving the game.
            foreach (ScheduledAction action in _scheduled)
            {
                if (action.Timing == ScheduleTiming.Until) action.Undo.RemoveAll(change => change.Entity == entity);
            }

            RefreshActivation(entity);
            Touch();
        }

        /// <summary>Attaches a status or keyword to an entity.</summary>
        public void Attach(Entity parent, Entity child)
        {
            child.Owner = parent;
            parent.Attach(child);
            Place(child, Zones.Attached, toTop: false);
        }

        /// <summary>
        /// Marks an actor dead. It stays on the board, still listening, until <see cref="Bury"/>, so
        /// its own <c>on died</c> listeners can hear the death. Selectors already skip it.
        /// </summary>
        internal void MarkDead(Entity actor) => actor.IsDead = true;

        /// <summary>Takes a dead actor off the board; it and its attachments stop listening.</summary>
        internal void Bury(Entity actor)
        {
            if (actor.IsDead && actor.Zone == Zones.Board) MoveTo(actor, Zones.Dead);
        }

        // Zones --------------------------------------------------------------------------------

        /// <summary>
        /// Entities an owner has in a zone, in order. For the draw pile, index 0 is the top.
        /// </summary>
        public IReadOnlyList<Entity> ZoneOf(Entity? owner, string zone)
        {
            return _zones.TryGetValue((owner?.Id ?? 0, zone), out List<Entity>? list) ? list : (IReadOnlyList<Entity>)Array.Empty<Entity>();
        }

        /// <summary>Moves an entity between zones, updating listener registration as needed.</summary>
        public void MoveTo(Entity entity, string zone, bool toTop = false)
        {
            if (entity.IsRemoved) return;
            TakeFromZone(entity);
            Place(entity, zone, toTop);
        }

        /// <summary>Reorders a zone in place, for shuffles.</summary>
        internal List<Entity> MutableZone(Entity? owner, string zone)
        {
            var key = (owner?.Id ?? 0, zone);
            if (!_zones.TryGetValue(key, out List<Entity>? list))
            {
                list = new List<Entity>();
                _zones[key] = list;
            }
            return list;
        }

        private int? _placingInLane;

        /// <summary>
        /// Puts the next actor that goes onto the board in <paramref name="lane"/> rather than in the
        /// first lane with room, until the returned scope is disposed. This is how a summon arrives
        /// beside the thing that made it without every creation path having to carry a lane.
        /// </summary>
        internal IDisposable PlacingInLane(int lane)
        {
            int? before = _placingInLane;
            _placingInLane = lane;
            return new LaneScope(this, before);
        }

        private sealed class LaneScope : IDisposable
        {
            private readonly GameState _state;
            private readonly int? _before;

            public LaneScope(GameState state, int? before)
            {
                _state = state;
                _before = before;
            }

            public void Dispose() => _state._placingInLane = _before;
        }

        private void Place(Entity entity, string zone, bool toTop)
        {
            Place(entity, zone, toTop, preferredLane: _placingInLane);
        }

        /// <summary>
        /// The one place an entity is put anywhere. For an actor going onto the board that means a
        /// slot search: the lowest free rank, in <paramref name="preferredLane"/> when one is named
        /// and across the lanes in order when none is. It throws when there is no room, which the
        /// callers that can meet a full board — <c>create</c> and <c>copy</c> — ask about first.
        /// </summary>
        private void Place(Entity entity, string zone, bool toTop, int? preferredLane)
        {
            (int Lane, int Rank)? slot = null;
            if (entity.Kind == EntityKind.Actor && zone == Zones.Board)
            {
                slot = FreeSlot(entity.Team, preferredLane)
                    ?? throw new InvalidOperationException(
                        $"Board \"{Board.Name}\" ({Board.Describe()}) has no free slot for {entity}.");
            }

            entity.Zone = zone ?? Zones.None;
            if (entity.Zone.Length > 0)
            {
                List<Entity> list = MutableZone(ZoneOwner(entity), entity.Zone);
                if (toTop) list.Insert(0, entity);
                else list.Add(entity);
            }

            if (slot != null)
            {
                entity.Lane = slot.Value.Lane;
                entity.Rank = slot.Value.Rank;
                _slots[Key(entity.Team, entity.Lane, entity.Rank)] = entity;
            }

            Touch();
            RefreshActivation(entity);
        }

        private void TakeFromZone(Entity entity)
        {
            if (entity.Zone.Length == 0) return;
            if (_zones.TryGetValue((ZoneOwner(entity)?.Id ?? 0, entity.Zone), out List<Entity>? list)) list.Remove(entity);

            bool wasOnBoard = entity.Kind == EntityKind.Actor && entity.Zone == Zones.Board;
            int lane = entity.Lane, rank = entity.Rank;
            Team side = entity.Team;

            entity.Zone = Zones.None;

            if (wasOnBoard && _slots.TryGetValue(Key(side, lane, rank), out Entity? standing) && standing == entity)
            {
                _slots.Remove(Key(side, lane, rank));
                if (Board.OnVacated == OnVacated.CloseRanks) CloseRanks(side, lane, rank);
            }

            Touch();
        }

        /// <summary>Actors share one board list per team; everything else is kept per owner.</summary>
        private static Entity? ZoneOwner(Entity entity) =>
            entity.Kind == EntityKind.Actor ? null : entity.Owner?.Controller;

        /// <summary>
        /// Live actors on the board, optionally for one side, ordered by <c>(lane, rank)</c> — which
        /// is where they stand, and so the order a party takes its steps in and the order an area
        /// effect reaches them in.
        /// </summary>
        /// <remarks>
        /// It used to be insertion order, which its own summary already called position order; the
        /// two agreed only because a new actor always landed past everyone. Now that a freed slot is
        /// filled again, they do not, and where an actor stands is the answer that means something.
        /// </remarks>
        public IReadOnlyList<Entity> Actors(Team? team = null)
        {
            var result = new List<Entity>();
            foreach (Entity entity in ZoneOf(null, Zones.Board))
            {
                if (entity.Kind != EntityKind.Actor || !entity.IsAlive) continue;
                if (team.HasValue && entity.Team != team.Value) continue;
                result.Add(entity);
            }
            result.Sort(BySlot);
            return result;
        }

        /// <summary>
        /// The dead, optionally for one side, in the order they fell. Content says <c>fallen</c>,
        /// and this is the same list: the group <c>revive fallen.first 8</c> reads, and what
        /// <see cref="Actors(Team?)"/>, <see cref="Party"/> and <c>allies</c> all deliberately leave
        /// out.
        /// </summary>
        /// <remarks>
        /// A game with a party needs this before it needs anything else above the battle, because a
        /// fallen member is in none of the other lists and a run has to be able to offer to raise
        /// one. Without it the only way to find a corpse was to keep a roster of ids from the moment
        /// they were created and check each one, outside the save the engine writes.
        /// <para>
        /// A dead actor is still on the board until it is buried, so that its own <c>on died</c>
        /// listeners can hear the death; those come last here, because they are the ones still
        /// falling.
        /// </para>
        /// </remarks>
        public IReadOnlyList<Entity> Fallen(Team? team = null)
        {
            var result = new List<Entity>();
            foreach (Entity entity in ZoneOf(null, Zones.Dead))
            {
                if (entity.Kind != EntityKind.Actor || entity.IsRemoved || !entity.IsDead) continue;
                if (team.HasValue && entity.Team != team.Value) continue;
                result.Add(entity);
            }

            foreach (Entity entity in ZoneOf(null, Zones.Board))
            {
                if (entity.Kind != EntityKind.Actor || entity.IsRemoved || !entity.IsDead) continue;
                if (team.HasValue && entity.Team != team.Value) continue;
                result.Add(entity);
            }

            return result;
        }

        /// <summary>Where an actor stands, then its id, so the order is total and deterministic.</summary>
        private static int BySlot(Entity a, Entity b)
        {
            int lane = a.Lane.CompareTo(b.Lane);
            if (lane != 0) return lane;
            int rank = a.Rank.CompareTo(b.Rank);
            return rank != 0 ? rank : a.Id.CompareTo(b.Id);
        }

        // The board ----------------------------------------------------------------------------

        /// <summary>
        /// Which grid a side stands on. A <c>facing</c> board gives each side its own, mirrored, so
        /// rank 0 is the front for both; a <c>shared</c> board is one grid for everyone.
        /// </summary>
        private int Key(Team team) => Board.Sides == BoardSides.Shared ? 0 : (int)team;

        private (int Side, int Lane, int Rank) Key(Team team, int lane, int rank) => (Key(team), lane, rank);

        /// <summary>Who is standing on a slot, alive or not, or null when it is free.</summary>
        public Entity? At(Team team, int lane, int rank) =>
            _slots.TryGetValue(Key(team, lane, rank), out Entity? standing) ? standing : null;

        /// <summary>
        /// The lowest free rank in <paramref name="lane"/>, or — when no lane is named — the lowest
        /// free rank in the first lane that has one. Null when there is no room at all.
        /// </summary>
        private (int Lane, int Rank)? FreeSlot(Team team, int? lane)
        {
            if (lane.HasValue)
            {
                if (!Board.HasLane(lane.Value)) return null;
                int? rank = FreeRank(team, lane.Value);
                return rank == null ? null : (lane.Value, rank.Value);
            }

            for (int l = 0; l < Board.Lanes; l++)
            {
                int? rank = FreeRank(team, l);
                if (rank != null) return (l, rank.Value);
            }
            return null;
        }

        private int? FreeRank(Team team, int lane)
        {
            int side = Key(team);

            // An unbounded lane is only ever as deep as what is standing in it, so one rank past
            // every occupied slot in the game is certainly free: the search has a floor either way.
            int floor = Board.RanksAreUnbounded ? _slots.Count + 1 : Board.Ranks;
            for (int rank = 0; rank < floor; rank++)
            {
                if (!_slots.ContainsKey((side, lane, rank))) return rank;
            }
            return null;
        }

        /// <summary>
        /// Whether a side has a free slot in a lane, which is what <c>create</c> asks before it makes
        /// anything: a lane with no room refuses the summon rather than making an actor with nowhere
        /// to stand.
        /// </summary>
        public bool HasRoom(Team team, int lane) => FreeRank(team, lane) != null;

        /// <summary>
        /// Puts an actor on a slot. Assigning a slot someone else is standing on <em>swaps</em> the
        /// two: total, deterministic, and its own inverse, which is what lets a rollback put a board
        /// back exactly as it was.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The slot is not one this board has.</exception>
        public void Assign(Entity actor, int lane, int rank)
        {
            if (actor == null) throw new ArgumentNullException(nameof(actor));
            if (actor.Kind != EntityKind.Actor || actor.Zone != Zones.Board)
                throw new ArgumentException($"{actor} is not an actor on the board, so it has no slot.", nameof(actor));
            if (!Board.Holds(lane, rank))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lane),
                    $"Board \"{Board.Name}\" ({Board.Describe()}) has no {Board.LaneWord} {lane}, {Board.RankWord} {rank}.");
            }

            if (actor.Lane == lane && actor.Rank == rank) return;

            Entity? sitting = At(actor.Team, lane, rank);
            (int Lane, int Rank) from = (actor.Lane, actor.Rank);

            _slots.Remove(Key(actor.Team, from.Lane, from.Rank));
            actor.Lane = lane;
            actor.Rank = rank;
            _slots[Key(actor.Team, lane, rank)] = actor;

            if (sitting != null && sitting != actor)
            {
                sitting.Lane = from.Lane;
                sitting.Rank = from.Rank;
                _slots[Key(sitting.Team, from.Lane, from.Rank)] = sitting;
            }

            Touch();
        }

        /// <summary>
        /// Steps everyone behind <paramref name="vacated"/> in a lane forward one rank. This is what
        /// <c>on_vacated close_ranks</c> asks for; the default is <c>gap</c>, where survivors never
        /// move and the hole waits for the next thing put in it.
        /// </summary>
        public void CloseRanks(Team team, int lane, int vacated)
        {
            int side = Key(team);
            List<Entity> behind = _slots
                .Where(s => s.Key.Side == side && s.Key.Lane == lane && s.Key.Rank > vacated)
                .Select(s => s.Value)
                .OrderBy(e => e.Rank)
                .ToList();

            if (behind.Count == 0) return;

            foreach (Entity actor in behind) _slots.Remove((side, lane, actor.Rank));
            int next = vacated;
            foreach (Entity actor in behind)
            {
                _shuffled.Add((actor, actor.Lane, actor.Rank));
                actor.Rank = next++;
                _slots[(side, lane, actor.Rank)] = actor;
            }
            Touch();
        }

        private readonly List<(Entity Actor, int Lane, int Rank)> _shuffled = new List<(Entity, int, int)>();

        /// <summary>
        /// Actors the board itself has moved since this was last asked, each with the slot it stepped
        /// out of. Asking clears the list, and only <see cref="CloseRanks"/> fills it.
        /// </summary>
        /// <remarks>
        /// The board closes inside <see cref="TakeFromZone"/>, which runs in the middle of a death
        /// and has no interpreter to raise an event with. So the moves are recorded here and the
        /// interpreter announces them as <c>moved</c> once the thing that caused them has finished.
        /// That is also why a <c>close_ranks</c> shuffle is never offered to <c>before_moved</c>:
        /// the row has already closed, and refusing half of it would put two actors on one slot.
        /// </remarks>
        internal IReadOnlyList<(Entity Actor, int Lane, int Rank)> TakeShuffled()
        {
            if (_shuffled.Count == 0) return Array.Empty<(Entity, int, int)>();
            var taken = _shuffled.ToArray();
            _shuffled.Clear();
            return taken;
        }

        /// <summary>
        /// How many steps apart two actors are, in slots, by the board's metric.
        /// </summary>
        /// <remarks>
        /// On a <c>facing</c> board the two sides are mirrored, so a rank means a different place on
        /// each: across the sides the rank term is <c>a.rank + b.rank + 1</c>, which makes two
        /// front-rank actors one step apart however deep the board is. Within one side, and on a
        /// <c>shared</c> board — where a rank is the same place for everyone — it is the plain
        /// metric over <c>(lane, rank)</c>. Anything not standing on the board has no distance to
        /// anything, and gets <see cref="int.MaxValue"/>.
        /// </remarks>
        public int Distance(Entity a, Entity b)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (b == null) throw new ArgumentNullException(nameof(b));
            if (!OnBoard(a) || !OnBoard(b)) return int.MaxValue;
            if (a == b) return 0;

            int lanes = Math.Abs(a.Lane - b.Lane);
            int ranks = Board.Sides == BoardSides.Facing && a.Team != b.Team
                ? a.Rank + b.Rank + 1
                : Math.Abs(a.Rank - b.Rank);

            return Board.Metric == BoardMetric.Chebyshev ? Math.Max(lanes, ranks) : lanes + ranks;
        }

        /// <summary>
        /// The live actors one step from this one, <em>on its own side</em>. This is what
        /// <c>adjacent</c> is built from, and it is deliberately no wider: reaching across the board
        /// is what <see cref="Distance"/> is for.
        /// </summary>
        public IReadOnlyList<Entity> Neighbours(Entity actor)
        {
            if (actor == null) throw new ArgumentNullException(nameof(actor));
            if (!OnBoard(actor)) return Array.Empty<Entity>();

            var result = new List<Entity>();
            foreach (Entity other in Actors(actor.Team))
            {
                if (other != actor && Distance(actor, other) == 1) result.Add(other);
            }
            return result;
        }

        private static bool OnBoard(Entity entity) =>
            entity.Kind == EntityKind.Actor && entity.Zone == Zones.Board && !entity.IsRemoved;

        // Activation ---------------------------------------------------------------------------

        /// <summary>
        /// Whether this entity's listeners and modifiers are live, which depends on where it is: an
        /// actor on the board and alive, a card in hand (or, for a power, in play), a relic held, a
        /// status whose host is itself active. A card in the draw pile is in the game and does nothing.
        /// </summary>
        public bool IsActive(Entity entity) => _active.Contains(entity.Id);

        /// <summary>
        /// Decides whether an entity's listeners and modifiers should be live, based on where it
        /// is. Cards listen from hand; statuses and keywords listen while their host does.
        /// </summary>
        private bool ShouldBeActive(Entity entity)
        {
            if (entity.IsRemoved) return false;

            switch (entity.Kind)
            {
                case EntityKind.Actor:
                    return entity.Zone == Zones.Board && !entity.IsDead;
                case EntityKind.Card:
                    // A power does nothing until it has been played. Other cards listen from hand,
                    // which is what curses that hurt while held rely on.
                    if (entity.HasTag("power")) return entity.Zone == Zones.Powers;
                    return entity.Zone == Zones.Hand || entity.Zone == Zones.Play;
                case EntityKind.Relic:
                case EntityKind.Item:
                    return entity.Zone == Zones.Relics || entity.Zone == Zones.Board;
                case EntityKind.Status:
                case EntityKind.Keyword:
                case EntityKind.Ability:
                    return entity.Owner != null && entity.Zone == Zones.Attached && IsActive(entity.Owner);
                case EntityKind.Global:
                    return true;
                default:
                    return false;
            }
        }

        internal void RefreshActivation(Entity entity)
        {
            SetActive(entity, ShouldBeActive(entity));
            foreach (Entity child in entity.Attached) RefreshActivation(child);
        }

        private void SetActive(Entity entity, bool active)
        {
            bool wasActive = _active.Contains(entity.Id);
            if (active == wasActive) return;

            if (active)
            {
                _active.Add(entity.Id);
                EntityDefinition? definition = entity.Definition;
                if (definition != null)
                {
                    foreach (ListenerNode listener in definition.Listeners)
                    {
                        // The interval is authored in seconds or turns; only the clock can say what
                        // that is in units, and a unit it cannot convert (seconds on a turn clock)
                        // leaves the listener unregistered as periodic rather than half-working.
                        long units = 0;
                        if (!listener.Interval.IsZero) Clock.TryConvert(listener.Interval, listener.IntervalUnit, out units);

                        Listener registered = Events.Register(entity, listener, units);
                        if (units > 0) registered.NextDueAt = Clock.Now + units;
                    }
                    foreach (ModifyNode modifier in definition.Modifiers) Modifiers.Register(entity, modifier);
                }
            }
            else
            {
                _active.Remove(entity.Id);
                Events.UnregisterAll(entity);
                Modifiers.UnregisterAll(entity);
            }

            Touch();
        }

        /// <summary>Re-registers an entity's listeners, used after hot reload swaps its definition.</summary>
        internal void Reactivate(Entity entity)
        {
            SetActive(entity, false);
            RefreshActivation(entity);
        }

        /// <summary>
        /// Points a live entity at a reloaded definition: listeners and modifiers are re-registered
        /// from the new content, and stats and tags are brought forward.
        /// </summary>
        /// <remarks>
        /// A stat the game has changed keeps its value, because a designer editing a card's cost
        /// must not heal the enemy that is halfway through a fight. A stat still sitting at the old
        /// definition's number takes the new one, which is what makes tweaking numbers live work.
        /// Used <c>once per ...</c> limits and <c>on every</c> timers stay with their listeners,
        /// matched as a restored save matches them, so a reload does not let a listener that has
        /// fired this battle fire again.
        /// </remarks>
        internal void Rebind(Entity entity, EntityDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));

            var limits = new List<ListenerLimitSnapshot>();
            var dues = new List<ListenerDueSnapshot>();
            CaptureListeners(entity, limits, dues);

            EntityDefinition? old = entity.Definition;
            SetActive(entity, false);
            entity.Definition = definition;

            if (old != null)
            {
                foreach (KeyValuePair<string, Num> stat in old.Stats)
                {
                    if (!entity.HasStat(stat.Key) || entity.GetBase(stat.Key) != stat.Value) continue;

                    if (definition.Stats.TryGetValue(stat.Key, out Num updated)) entity.SetBase(stat.Key, updated);
                    else entity.RemoveStat(stat.Key);
                }

                // Tags the game added at runtime are not the definition's to take away.
                foreach (string tag in old.Tags)
                {
                    if (!definition.HasTag(tag)) entity.RemoveTag(tag);
                }
            }

            foreach (KeyValuePair<string, Num> stat in definition.Stats)
            {
                if (!entity.HasStat(stat.Key)) entity.SetBase(stat.Key, stat.Value);
            }
            foreach (string tag in definition.Tags) entity.AddTag(tag);

            RefreshActivation(entity);
            RestoreListeners(limits, dues);
            Touch();
        }

        // History ------------------------------------------------------------------------------

        /// <summary>
        /// Per-turn or per-battle counters behind history queries such as
        /// <c>damage taken this turn</c>. Keys are recorded per entity and in aggregate.
        /// </summary>
        public Num History(string key, Entity? who = null, bool battle = false)
        {
            Dictionary<string, Num> table = battle ? _battleHistory : _turnHistory;
            return table.TryGetValue(HistoryKey(key, who), out Num value) ? value : Num.Zero;
        }

        internal void RecordHistory(string key, Entity? who, Num amount)
        {
            foreach (Dictionary<string, Num> table in new[] { _turnHistory, _battleHistory })
            {
                Bump(table, HistoryKey(key, null), amount);
                if (who != null) Bump(table, HistoryKey(key, who), amount);
            }
        }

        internal void ResetTurnHistory() => _turnHistory.Clear();

        internal void ResetBattleHistory()
        {
            _turnHistory.Clear();
            _battleHistory.Clear();
        }

        internal IReadOnlyDictionary<string, Num> TurnHistory => _turnHistory;
        internal IReadOnlyDictionary<string, Num> BattleHistory => _battleHistory;

        internal void RestoreHistory(IEnumerable<KeyValuePair<string, Num>> turn, IEnumerable<KeyValuePair<string, Num>> battle)
        {
            _turnHistory.Clear();
            _battleHistory.Clear();
            foreach (var kv in turn) _turnHistory[kv.Key] = kv.Value;
            foreach (var kv in battle) _battleHistory[kv.Key] = kv.Value;
        }

        private static string HistoryKey(string key, Entity? who) => who == null ? key : key + "#" + who.Id;

        private static void Bump(Dictionary<string, Num> table, string key, Num amount)
        {
            table.TryGetValue(key, out Num current);
            table[key] = current + amount;
        }

        // Scheduling ---------------------------------------------------------------------------

        internal ScheduledAction Schedule(ScheduleTiming timing, Entity owner, BlockNode? body)
        {
            var action = new ScheduledAction(_nextScheduleId++, timing, owner, body);
            _scheduled.Add(action);
            Touch();
            return action;
        }

        internal void Unschedule(ScheduledAction action)
        {
            if (_scheduled.Remove(action)) Touch();
        }

        // Determinism --------------------------------------------------------------------------

        /// <summary>
        /// A hash of all rules state. Two games fed the same content, seed and inputs by the same
        /// version of Cantrip produce the same hash after every step. Tests use it to prove that
        /// replays and restored saves are exact, and a game can compare it to check that a replay,
        /// or a second machine playing the same inputs, has not drifted.
        /// </summary>
        public ulong ComputeHash()
        {
            ulong hash = 14695981039346656037UL;

            void Mix(long value)
            {
                unchecked
                {
                    for (int i = 0; i < 8; i++)
                    {
                        hash ^= (byte)(value >> (i * 8));
                        hash *= 1099511628211UL;
                    }
                }
            }

            void MixText(string text)
            {
                foreach (char c in text) Mix(char.ToLowerInvariant(c));
                Mix(-1);
            }

            Mix(Turn);
            Mix(BattleNumber);
            Mix((long)ActiveTeam);
            Mix(InBattle ? 1 : 0);
            Mix(Player?.Id ?? 0);
            // Who has already acted decides who still may, so it belongs in the hash beside the
            // turn. Sorted, because a set has no order of its own. `ActiveMember` is not mixed: it
            // is read off this set and the turn order, so it could only ever repeat them.
            foreach (int id in _acted.OrderBy(i => i)) Mix(id);
            Mix(-1);
            Mix(Clock.Now);

            // The board decides who can reach whom and where the next summon lands, so two games on
            // different boards have different futures and must never hash the same.
            MixText(Board.Name);
            Mix(Board.Lanes);
            Mix(Board.Ranks);
            Mix((long)Board.Sides);
            Mix((long)Board.Metric);
            Mix((long)Board.OnVacated);
            foreach (ulong word in Rng.GetState()) Mix((long)word);

            foreach (Entity entity in _entities)
            {
                Mix(entity.Id);
                MixText(entity.Name);
                Mix(entity.IsRemoved ? 1 : 0);
                if (entity.IsRemoved) continue;

                Mix(entity.IsDead ? 1 : 0);
                MixText(entity.Zone);
                Mix(entity.Owner?.Id ?? 0);
                Mix(entity.Source?.Id ?? 0);
                Mix((long)entity.RawTeam);
                Mix(entity.Lane);
                Mix(entity.Rank);
                Mix(entity.PatternIndex);
                Mix(entity.IsPartyMember ? 1 : 0);
                // Who an enemy is telegraphing against decides who it hits, so two games that have
                // rolled different targets have different futures and must not hash the same.
                Mix(entity.IntentTarget?.Id ?? 0);
                MixText(entity.Intent ?? string.Empty);
                MixText(entity.LastMove ?? string.Empty);
                MixText(entity.Phase ?? string.Empty);
                foreach (string stat in entity.StatNames.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                {
                    MixText(stat);
                    Mix(entity.GetBase(stat).Raw);
                }
                foreach (string tag in entity.Tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)) MixText(tag);
                foreach (Entity attached in entity.Attached) Mix(attached.Id);
                foreach (Listener listener in Events.OwnedBy(entity))
                {
                    Mix(listener.LimitWindow);
                    Mix(listener.NextDueAt);
                }
            }

            foreach (var zone in _zones.OrderBy(z => z.Key.Owner).ThenBy(z => z.Key.Zone, StringComparer.Ordinal))
            {
                // An emptied zone and a zone that never existed are the same game state.
                if (zone.Value.Count == 0) continue;
                Mix(zone.Key.Owner);
                MixText(zone.Key.Zone);
                foreach (Entity entity in zone.Value) Mix(entity.Id);
            }

            // Two states with different futures must never hash the same, so pending work and the
            // counters behind history queries count too.
            foreach (var entry in _turnHistory.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                MixText(entry.Key);
                Mix(entry.Value.Raw);
            }
            Mix(-2);
            foreach (var entry in _battleHistory.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                MixText(entry.Key);
                Mix(entry.Value.Raw);
            }
            Mix(-3);
            foreach (ScheduledAction action in _scheduled)
            {
                Mix(action.Id);
                Mix((long)action.Timing);
                Mix(action.Owner.Id);
                Mix(action.DueAt);
                MixText(action.Deadline ?? string.Empty);

                // What the block will do, not where it was written: the same statements restored
                // from a patch that moved them play the same, and different ones never hash alike.
                MixText(action.Body == null ? string.Empty : BlockHash.Of(action.Body));
                Mix(action.Undo.Count);
            }

            return hash;
        }
    }
}
