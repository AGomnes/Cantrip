using System;
using System.Collections.Generic;
using GameplayEffects.Content;

namespace GameplayEffects.Runtime
{
    /// <summary>
    /// Everything in the game is an entity: actors, cards, relics, statuses and keywords all
    /// share tags, stats and listeners (section 3.1). A status on an actor is itself an entity,
    /// attached to that actor, whose <c>stacks</c> is an ordinary stat. That is what lets
    /// <c>stacks -1</c> and <c>remove tag:dot</c> work without special cases.
    /// </summary>
    public sealed class Entity
    {
        private readonly Dictionary<string, Num> _base = new Dictionary<string, Num>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Entity> _attached = new List<Entity>();

        internal Entity(GameState state, int id, string name, EntityKind kind, EntityDefinition? definition)
        {
            State = state;
            Id = id;
            Name = name;
            Kind = kind;
            Definition = definition;
            Zone = string.Empty;
        }

        public GameState State { get; }

        /// <summary>Stable identifier, unique within a <see cref="GameState"/> and preserved by snapshots.</summary>
        public int Id { get; }

        public string Name { get; }
        public EntityKind Kind { get; }
        public EntityDefinition? Definition { get; }

        /// <summary>The entity this one belongs to: a card's or status's actor, a relic's holder.</summary>
        public Entity? Owner { get; internal set; }

        /// <summary>The entity that created or applied this one, when that matters (status sources).</summary>
        public Entity? Source { get; internal set; }

        /// <summary>Side for actors. Other entities report their controller's team.</summary>
        public Team Team
        {
            get => Kind == EntityKind.Actor || Owner == null ? _team : Controller.Team;
            internal set => _team = value;
        }

        private Team _team;

        /// <summary>The stored team, before falling back to the controller's. Snapshots save this.</summary>
        internal Team RawTeam => _team;

        /// <summary>
        /// Where the entity lives: <c>hand</c>, <c>draw</c>, <c>discard</c>, <c>exhaust</c>,
        /// <c>board</c>, <c>relics</c>, <c>attached</c>, or empty for "nowhere in particular".
        /// </summary>
        public string Zone { get; internal set; }

        /// <summary>Slot on the board, used by adjacency selectors.</summary>
        public int Position { get; internal set; }

        public bool IsDead { get; internal set; }

        /// <summary>True once the entity has left the game entirely. Removed entities never fire listeners.</summary>
        public bool IsRemoved { get; internal set; }

        /// <summary>Activation order, used for the deterministic "play order" tie-break between listeners.</summary>
        public long Sequence { get; internal set; }

        // Enemy AI state. Kept on the entity so it is captured by snapshots.
        internal int PatternIndex { get; set; }
        internal string? LastMove { get; set; }

        /// <summary>The move this enemy will use on its next turn, once intents have been rolled.</summary>
        public string? Intent { get; internal set; }

        /// <summary>
        /// The actor this entity ultimately answers to. An actor controls itself; a card, status or
        /// relic is controlled by whoever owns it. <c>source:self</c> filters compare controllers,
        /// so a relic's "damage you deal" modifier matches damage from its holder's cards.
        /// </summary>
        public Entity Controller
        {
            get
            {
                Entity current = this;
                for (int guard = 0; guard < 64 && current.Kind != EntityKind.Actor && current.Owner != null; guard++)
                    current = current.Owner;
                return current;
            }
        }

        // Tags -----------------------------------------------------------------------------

        public IReadOnlyCollection<string> Tags => _tags;

        public bool HasTag(string tag) => _tags.Contains(tag);

        public void AddTag(string tag)
        {
            if (_tags.Add(tag.ToLowerInvariant())) State.Touch();
        }

        public void RemoveTag(string tag)
        {
            if (_tags.Remove(tag)) State.Touch();
        }

        // Stats ----------------------------------------------------------------------------

        public IEnumerable<string> StatNames => _base.Keys;

        public bool HasStat(string stat) => _base.ContainsKey(stat);

        /// <summary>The stored value, before modifiers.</summary>
        public Num GetBase(string stat) => _base.TryGetValue(stat, out Num value) ? value : Num.Zero;

        /// <summary>The value after every active modifier has been applied.</summary>
        public Num Get(string stat) => State.Modifiers.ComputeStat(this, stat, GetBase(stat));

        /// <summary>Stat value rounded to an integer, for the common case of reading hp, block or cost.</summary>
        public int GetInt(string stat) => Get(stat).ToInt();

        /// <summary>
        /// Writes a base stat without resource clamping or events. Content goes through the
        /// interpreter's <c>change</c> verb instead; this is for setup code and snapshot restore.
        /// </summary>
        public void SetBase(string stat, Num value)
        {
            if (_base.TryGetValue(stat, out Num existing) && existing == value) return;
            _base[stat] = value;
            State.Touch();
        }

        internal void RemoveStat(string stat)
        {
            if (_base.Remove(stat)) State.Touch();
        }

        // Attachments ----------------------------------------------------------------------

        /// <summary>Statuses and keywords currently attached to this entity, in application order.</summary>
        public IReadOnlyList<Entity> Attached => _attached;

        internal void Attach(Entity child)
        {
            _attached.Add(child);
            State.Touch();
        }

        internal void Detach(Entity child)
        {
            if (_attached.Remove(child)) State.Touch();
        }

        /// <summary>The first attached status or keyword with the given name, if any.</summary>
        public Entity? FindAttached(string name)
        {
            for (int i = 0; i < _attached.Count; i++)
            {
                if (string.Equals(_attached[i].Name, name, StringComparison.OrdinalIgnoreCase) && !_attached[i].IsRemoved)
                    return _attached[i];
            }
            return null;
        }

        /// <summary>Total stacks of a named status, summed across separate instances.</summary>
        public int StacksOf(string statusName)
        {
            Num total = Num.Zero;
            for (int i = 0; i < _attached.Count; i++)
            {
                Entity child = _attached[i];
                if (!child.IsRemoved && string.Equals(child.Name, statusName, StringComparison.OrdinalIgnoreCase))
                    total += child.Get("stacks");
            }
            return total.ToInt();
        }

        public bool IsAlive => !IsDead && !IsRemoved;

        public override string ToString() => $"{Name}#{Id}";
    }
}
