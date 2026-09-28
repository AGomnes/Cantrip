using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Cantrip.Content;

namespace Cantrip.Runtime
{
    /// <summary>
    /// Everything in the game is an entity: actors, cards, relics, statuses and keywords all
    /// share tags, stats and listeners. A status on an actor is itself an entity,
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

        /// <summary>
        /// What this entity is called. It is settable only within the library, and only two things
        /// change it: <c>transform</c>, which replaces what an entity is while keeping who it is,
        /// and restoring a snapshot into an instance the game is already holding. A game that keys
        /// a cache on a card or actor name has to expect it to change.
        /// </summary>
        public string Name { get; internal set; }

        public EntityKind Kind { get; }

        /// <summary>
        /// The content this entity came from. Hot reload swaps it for the newly loaded definition
        /// of the same kind and name, which is why it is not read-only.
        /// </summary>
        public EntityDefinition? Definition { get; internal set; }

        // Every property a modifier filter could read bumps GameState.Version when it changes, so
        // the stat cache can never serve a value computed against stale state.
        private Entity? _owner;
        private Entity? _source;
        private Team _team;
        private string _zone = string.Empty;
        private int _lane;
        private int _rank;
        private bool _isDead;
        private bool _isRemoved;
        private string? _intent;

        /// <summary>The entity this one belongs to: a card's or status's actor, a relic's holder.</summary>
        public Entity? Owner
        {
            get => _owner;
            internal set => Set(ref _owner, value);
        }

        /// <summary>The entity that created or applied this one, when that matters (status sources).</summary>
        public Entity? Source
        {
            get => _source;
            internal set => Set(ref _source, value);
        }

        /// <summary>Side for actors. Other entities report their controller's team.</summary>
        public Team Team
        {
            get => Kind == EntityKind.Actor || Owner == null ? _team : Controller.Team;
            internal set => Set(ref _team, value);
        }

        /// <summary>The stored team, before falling back to the controller's. Snapshots save this.</summary>
        internal Team RawTeam => _team;

        /// <summary>
        /// Where the entity lives: <c>hand</c>, <c>draw</c>, <c>discard</c>, <c>exhaust</c>,
        /// <c>board</c>, <c>relics</c>, <c>attached</c>, or empty for "nowhere in particular".
        /// </summary>
        public string Zone
        {
            get => _zone;
            internal set => Set(ref _zone, value ?? string.Empty);
        }

        /// <summary>
        /// Slot across the board, counting from 0. Always 0 on a one-lane board, which is what
        /// content that declares no board is played on.
        /// </summary>
        public int Lane
        {
            get => _lane;
            internal set => Set(ref _lane, value);
        }

        /// <summary>
        /// Slot along the facing axis, counting from 0. On a <c>facing</c> board rank 0 is the front
        /// — nearest the other side — and a rank never means the same place on both sides.
        /// </summary>
        public int Rank
        {
            get => _rank;
            internal set => Set(ref _rank, value);
        }

        /// <summary>
        /// What <see cref="Rank"/> was called before a board had two axes. It is an alias, not a
        /// second number, and it keeps working for the whole 1.x line.
        /// </summary>
        public int Position
        {
            get => Rank;
            internal set => Rank = value;
        }

        /// <summary>The slot an actor stands on, as a pair. Zero for anything not on the board.</summary>
        public (int Lane, int Rank) Slot => (Lane, Rank);

        public bool IsDead
        {
            get => _isDead;
            internal set => Set(ref _isDead, value);
        }

        /// <summary>True once the entity has left the game entirely. Removed entities never fire listeners.</summary>
        public bool IsRemoved
        {
            get => _isRemoved;
            internal set => Set(ref _isRemoved, value);
        }

        /// <summary>Activation order, used for the deterministic "play order" tie-break between listeners.</summary>
        public long Sequence { get; internal set; }

        // Enemy AI state. Kept on the entity so it is captured by snapshots.
        internal int PatternIndex { get; set; }
        internal string? LastMove { get; set; }

        /// <summary>
        /// The behaviour phase this enemy is in, when its definition declares any. Null until
        /// intents have been rolled, and for enemies with no phases at all.
        /// </summary>
        public string? Phase { get; internal set; }

        /// <summary>The move this enemy will use on its next turn, once intents have been rolled.</summary>
        public string? Intent
        {
            get => _intent;
            internal set => Set(ref _intent, value);
        }

        private void Set<T>(ref T field, T value)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            State.Touch();
        }

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

        private ReadOnlySetView<string>? _tagsView;

        /// <summary>
        /// This entity's runtime tags. A view, not the set itself: adding a tag has to go through
        /// <see cref="AddTag"/>, which tells the state, because modifier scopes and the stat cache
        /// are keyed on tags and a tag added behind their back would leave stale numbers in play.
        /// </summary>
        public IReadOnlyCollection<string> Tags => _tagsView ??= new ReadOnlySetView<string>(_tags);

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

        /// <summary>
        /// The value after every active modifier has been applied. This reads stats only: a status
        /// such as Burn is not a stat, so <c>Get("Burn")</c> is 0 where content's <c>enemy.Burn</c>
        /// is its stacks. Use <see cref="CounterOf"/> for that.
        /// </summary>
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

        /// <summary>
        /// Drops every stat and tag this entity has, so another definition's can take their place.
        /// Used by <c>transform</c>: what the old definition put there, and what the game gave it
        /// while it was that thing, both belong to the thing it no longer is.
        /// </summary>
        internal void ClearStatsAndTags()
        {
            if (_base.Count == 0 && _tags.Count == 0) return;
            _base.Clear();
            _tags.Clear();
            State.Touch();
        }

        /// <summary>
        /// Wipes everything a snapshot fully describes, so this instance can be reused when a game
        /// is restored. Reusing instances is what lets an <see cref="Entity"/> a game is holding
        /// stay valid across a load, or across an action that was rolled back for a player choice.
        /// </summary>
        internal void ResetForRestore()
        {
            _base.Clear();
            _tags.Clear();
            _attached.Clear();
            Owner = null;
            Source = null;
            Zone = string.Empty;
            Lane = 0;
            Rank = 0;
            IsDead = false;
            IsRemoved = false;
            PatternIndex = 0;
            LastMove = null;
            Intent = null;
            Phase = null;
            State.Touch();
        }

        // Attachments ----------------------------------------------------------------------

        private ReadOnlyCollection<Entity>? _attachedView;

        /// <summary>
        /// Statuses and keywords currently attached to this entity, in application order. A view,
        /// not the list itself, for the same reason <see cref="Tags"/> is.
        /// </summary>
        public IReadOnlyList<Entity> Attached => _attachedView ??= new ReadOnlyCollection<Entity>(_attached);

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

        /// <summary>
        /// The number a status "has" when content names it, as in <c>enemy.Vulnerable</c>: its
        /// remaining duration for duration and refresh statuses, its stacks otherwise. Reads and
        /// writes of <c>host.Status</c> both use this counter, so a self-assignment is a no-op.
        /// </summary>
        public int CounterOf(string statusName)
        {
            Num total = Num.Zero;
            for (int i = 0; i < _attached.Count; i++)
            {
                Entity child = _attached[i];
                if (child.IsRemoved || !string.Equals(child.Name, statusName, StringComparison.OrdinalIgnoreCase)) continue;
                total += child.Get(CounterStat(child));
            }
            return total.ToInt();
        }

        /// <summary>Which stat counts a status down: <c>duration</c> or <c>stacks</c>.</summary>
        internal static string CounterStat(Entity status) =>
            status.Definition?.Stacking is StackingMode.Duration or StackingMode.Refresh ? "duration" : "stacks";

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

    /// <summary>
    /// A read-only window on a set. <see cref="System.Collections.ObjectModel.ReadOnlyCollection{T}"/>
    /// wraps a list; there is no such wrapper for a set, and handing the set itself out lets a
    /// caller cast it back and mutate it behind the cache that reads it.
    /// </summary>
    /// <remarks>
    /// It implements <see cref="ICollection{T}"/> as well as <see cref="IReadOnlyCollection{T}"/>,
    /// with every mutator throwing, so that callers such as <c>ToArray</c> still take the
    /// count-and-copy path rather than growing a list one item at a time.
    /// </remarks>
    internal sealed class ReadOnlySetView<T> : IReadOnlyCollection<T>, ICollection<T>
    {
        private readonly ICollection<T> _items;

        internal ReadOnlySetView(ICollection<T> items) => _items = items;

        public int Count => _items.Count;

        public bool IsReadOnly => true;

        public bool Contains(T item) => _items.Contains(item);

        public void CopyTo(T[] array, int index) => _items.CopyTo(array, index);

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        void ICollection<T>.Add(T item) => throw new NotSupportedException("Tags are added with Entity.AddTag, which tells the game state.");

        bool ICollection<T>.Remove(T item) => throw new NotSupportedException("Tags are removed with Entity.RemoveTag, which tells the game state.");

        void ICollection<T>.Clear() => throw new NotSupportedException("Tags are removed with Entity.RemoveTag, which tells the game state.");
    }
}
