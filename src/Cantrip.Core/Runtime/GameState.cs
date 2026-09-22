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
        public const string None = "";
        public const string Board = "board";
        public const string Hand = "hand";
        public const string Draw = "draw";
        public const string Discard = "discard";
        public const string Exhaust = "exhaust";
        public const string Play = "play";

        /// <summary>Played cards that stay in effect for the rest of the battle.</summary>
        public const string Powers = "powers";

        public const string Relics = "relics";
        public const string Attached = "attached";
        public const string Dead = "dead";
    }

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
        public TemporaryChange(Entity entity, string? tag = null, Entity? attached = null, string? stat = null, Num delta = default)
        {
            Entity = entity;
            Tag = tag;
            Attached = attached;
            Stat = stat;
            Delta = delta;
        }

        public Entity Entity { get; }
        public string? Tag { get; }
        public Entity? Attached { get; }
        public string? Stat { get; }
        public Num Delta { get; }
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

        public long Id { get; }
        public ScheduleTiming Timing { get; }
        public Entity Owner { get; }
        public BlockNode? Body { get; }
        public long DueAt { get; internal set; }
        public string? Deadline { get; internal set; }

        /// <summary>Names captured when the block was scheduled (<c>target</c>, <c>source</c>...).</summary>
        public Dictionary<string, Value> Bindings { get; }

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
        private int _nextId = 1;
        private long _nextSequence = 1;
        private long _nextScheduleId = 1;

        public GameState(ContentLibrary content, Ruleset rules, IGameClock clock, ulong seed)
        {
            Content = content ?? throw new ArgumentNullException(nameof(content));
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Rng = new Rng(seed);
            Modifiers = new ModifierPipeline(this);
            Events = new EventBus();
            Trace = new TraceLog();

            // Modifiers can read `now`, so time passing is a state change like any other.
            clock.Advanced += _ => Touch();
        }

        public ContentLibrary Content { get; }
        public Ruleset Rules { get; }
        public IGameClock Clock { get; }
        public Rng Rng { get; }
        public ModifierPipeline Modifiers { get; }
        public EventBus Events { get; }
        public TraceLog Trace { get; }

        /// <summary>Incremented by every mutation. Caches compare against it instead of tracking dependencies.</summary>
        public long Version { get; private set; }

        internal void Touch() => Version++;

        private int _turn;
        private int _battleNumber;
        private Team _activeTeam = Team.Player;
        private bool _inBattle;
        private Entity? _player;

        // Modifiers can read `turn` and friends, so each of these bumps the version when it changes.

        public int Turn
        {
            get => _turn;
            internal set { if (_turn != value) { _turn = value; Touch(); } }
        }

        public int BattleNumber
        {
            get => _battleNumber;
            internal set { if (_battleNumber != value) { _battleNumber = value; Touch(); } }
        }

        public Team ActiveTeam
        {
            get => _activeTeam;
            internal set { if (_activeTeam != value) { _activeTeam = value; Touch(); } }
        }

        public bool InBattle
        {
            get => _inBattle;
            internal set { if (_inBattle != value) { _inBattle = value; Touch(); } }
        }

        public Entity? Player
        {
            get => _player;
            internal set { if (_player != value) { _player = value; Touch(); } }
        }

        public IReadOnlyList<Entity> Entities => _entities;
        public IReadOnlyList<ScheduledAction> Scheduled => _scheduled;

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

        private void Place(Entity entity, string zone, bool toTop)
        {
            entity.Zone = zone ?? Zones.None;
            if (entity.Zone.Length > 0)
            {
                List<Entity> list = MutableZone(ZoneOwner(entity), entity.Zone);
                if (toTop) list.Insert(0, entity);
                else list.Add(entity);
            }

            if (entity.Kind == EntityKind.Actor && entity.Zone == Zones.Board)
            {
                // One past the highest live slot, so positions stay unique after a death.
                int next = 0;
                foreach (Entity mate in Actors(entity.Team))
                {
                    if (mate != entity) next = Math.Max(next, mate.Position + 1);
                }
                entity.Position = next;
            }

            Touch();
            RefreshActivation(entity);
        }

        private void TakeFromZone(Entity entity)
        {
            if (entity.Zone.Length == 0) return;
            if (_zones.TryGetValue((ZoneOwner(entity)?.Id ?? 0, entity.Zone), out List<Entity>? list)) list.Remove(entity);
            entity.Zone = Zones.None;
            Touch();
        }

        /// <summary>Actors share one board list per team; everything else is kept per owner.</summary>
        private static Entity? ZoneOwner(Entity entity) =>
            entity.Kind == EntityKind.Actor ? null : entity.Owner?.Controller;

        /// <summary>Live actors on the board, optionally for one team, in position order.</summary>
        public IReadOnlyList<Entity> Actors(Team? team = null)
        {
            var result = new List<Entity>();
            foreach (Entity entity in ZoneOf(null, Zones.Board))
            {
                if (entity.Kind != EntityKind.Actor || !entity.IsAlive) continue;
                if (team.HasValue && entity.Team != team.Value) continue;
                result.Add(entity);
            }
            return result;
        }

        // Activation ---------------------------------------------------------------------------

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
            Mix(Clock.Now);
            var (s0, s1, s2, s3) = Rng.GetState();
            Mix((long)s0); Mix((long)s1); Mix((long)s2); Mix((long)s3);

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
                Mix(entity.Position);
                Mix(entity.PatternIndex);
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
