using System;
using System.Collections.Generic;
using Cantrip.Content;
using Cantrip.Runtime;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// One status or keyword on an entity, as a status bar shows it.
    /// </summary>
    /// <remarks>
    /// Both counters are carried because they answer different questions and a UI needs the right
    /// one. <see cref="Stacks"/> is the intensity: five stacks of Poison deal five damage.
    /// <see cref="Counter"/> is the number the status is named by and removed at, which for a
    /// duration status is its remaining turns and for an intensity status is its stacks again. A
    /// bar that always showed stacks would print "1" on a Vulnerable that has two turns to run.
    /// </remarks>
    public sealed class StatusView
    {
        private static readonly IReadOnlyList<string> NoTags = new string[0];

        private StatusView(
            int id,
            string name,
            int counter,
            int stacks,
            int duration,
            bool hidden,
            IReadOnlyList<string> tags)
        {
            Id = id;
            Name = name;
            Counter = counter;
            Stacks = stacks;
            Duration = duration;
            Hidden = hidden;
            Tags = tags;
        }

        public int Id { get; }

        public string Name { get; }

        /// <summary>
        /// The number the content means by the status's own name, and the one it is removed at:
        /// <see cref="Duration"/> for duration and refresh stacking, <see cref="Stacks"/> otherwise.
        /// Summed over instances this is <c>Entity.CounterOf</c>.
        /// </summary>
        public int Counter { get; }

        /// <summary>Intensity, as <c>Entity.StacksOf</c> sums it. One for most duration statuses.</summary>
        public int Stacks { get; }

        /// <summary>Remaining duration; zero for a status that has no timer.</summary>
        public int Duration { get; }

        /// <summary>
        /// True for <c>flags hidden</c>: rules the game runs but does not advertise, such as a
        /// tutorial marker. A status bar leaves these out; a debug view still wants them.
        /// </summary>
        public bool Hidden { get; }

        /// <summary>The status's tags, ordinally sorted so two runs agree.</summary>
        public IReadOnlyList<string> Tags { get; }

        /// <summary>Reads one live status entity. Values go through the modifier pipeline.</summary>
        public static StatusView Of(Entity status)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));

            EntityDefinition? definition = status.Definition;
            int stacks = status.Get("stacks").ToInt();
            int duration = status.Get("duration").ToInt();

            return new StatusView(
                status.Id,
                status.Name,
                CountsByDuration(definition) ? duration : stacks,
                stacks,
                duration,
                definition != null && (definition.Flags & StatusFlags.Hidden) != 0,
                EntityView.SortedTags(status.Tags));
        }

        /// <summary>
        /// Which stat counts the status down. This mirrors the core's own rule; it is repeated
        /// rather than shared because the core keeps it internal to the entity.
        /// </summary>
        private static bool CountsByDuration(EntityDefinition? definition) =>
            definition != null && definition.Stacking is StackingMode.Duration or StackingMode.Refresh;

        public override string ToString() => Name + " " + Counter;
    }

    /// <summary>
    /// Everything a game shows about one entity, as plain data: no engine types, no live
    /// references, and every identity an <see cref="int"/>.
    /// </summary>
    /// <remarks>
    /// This is where the boundary is drawn. Handing script a live <c>Entity</c> would let a UI
    /// mutate the rules state between two frames of an animation; handing it a number cannot. The
    /// mapping lives here rather than in the Godot layer so that it can be tested against a real
    /// game with no engine running, which is also why nothing in this file names the engine.
    /// <para>
    /// Stats are read through the modifier pipeline, so they are what the rules would use now,
    /// not the printed values.
    /// </para>
    /// </remarks>
    public sealed class EntityView
    {
        private static readonly IReadOnlyList<string> NoTags = new string[0];
        private static readonly IReadOnlyList<int> NoIds = new int[0];
        private static readonly IReadOnlyList<StatusView> NoStatuses = new StatusView[0];
        private static readonly IReadOnlyDictionary<string, int> NoStats = new Dictionary<string, int>(0);

        private EntityView(
            int id,
            string name,
            string kind,
            string team,
            string zone,
            int position,
            bool alive,
            bool dead,
            bool removed,
            string intent,
            int owner,
            int source,
            IReadOnlyList<string> tags,
            IReadOnlyDictionary<string, int> stats,
            IReadOnlyList<StatusView> statuses,
            IReadOnlyList<int> abilities)
        {
            Id = id;
            Name = name;
            Kind = kind;
            Team = team;
            Zone = zone;
            Position = position;
            Alive = alive;
            Dead = dead;
            Removed = removed;
            Intent = intent;
            Owner = owner;
            Source = source;
            Tags = tags;
            Stats = stats;
            Statuses = statuses;
            Abilities = abilities;
        }

        public int Id { get; }

        public string Name { get; }

        /// <summary>Lower-case <c>EntityKind</c>: actor, card, status, relic, ability, keyword, item, global.</summary>
        public string Kind { get; }

        /// <summary>Lower-case side: neutral, player or enemy.</summary>
        public string Team { get; }

        /// <summary>Where it lives: hand, draw, discard, board, relics, attached, or empty.</summary>
        public string Zone { get; }

        /// <summary>Board slot, which is what adjacency reads. Zero for anything not on the board.</summary>
        public int Position { get; }

        public bool Alive { get; }

        /// <summary>
        /// Dead but not yet gone. Both this and <see cref="Alive"/> are carried because a corpse
        /// still on the board is neither alive nor absent, and a UI usually draws it differently.
        /// </summary>
        public bool Dead { get; }

        /// <summary>Out of the game entirely. A view of a removed entity is still valid to read.</summary>
        public bool Removed { get; }

        /// <summary>The move this enemy will make next, or empty before intents are rolled.</summary>
        public string Intent { get; }

        /// <summary>The entity this one belongs to: a card's actor, a status's host. Zero for none.</summary>
        public int Owner { get; }

        /// <summary>Whoever applied or created it, which is what <c>source:</c> filters compare. Zero for none.</summary>
        public int Source { get; }

        public IReadOnlyList<string> Tags { get; }

        /// <summary>Current stats, after modifiers, ordinally keyed.</summary>
        public IReadOnlyDictionary<string, int> Stats { get; }

        /// <summary>Attached statuses and keywords, in application order. Hidden ones are included and flagged.</summary>
        public IReadOnlyList<StatusView> Statuses { get; }

        /// <summary>Ids of attached abilities, which a real-time game needs to fire them.</summary>
        public IReadOnlyList<int> Abilities { get; }

        /// <summary>
        /// Reads a live entity.
        /// </summary>
        /// <param name="stats">
        /// Which stats to read, or null for every stat the entity has. A game that shows three bars
        /// per actor passes those three: each stat read runs the modifier pipeline.
        /// </param>
        public static EntityView Of(Entity entity, IReadOnlyList<string>? stats = null)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            return new EntityView(
                entity.Id,
                entity.Name,
                entity.Kind.ToString().ToLowerInvariant(),
                entity.Team.ToString().ToLowerInvariant(),
                entity.Zone,
                entity.Position,
                entity.IsAlive,
                entity.IsDead,
                entity.IsRemoved,
                entity.Intent ?? string.Empty,
                entity.Owner?.Id ?? 0,
                entity.Source?.Id ?? 0,
                SortedTags(entity.Tags),
                ReadStats(entity, stats),
                ReadStatuses(entity),
                ReadAbilities(entity));
        }

        /// <summary>Tags in ordinal order: the order a set enumerates in is not a fact about the game.</summary>
        internal static IReadOnlyList<string> SortedTags(IReadOnlyCollection<string> tags)
        {
            if (tags == null || tags.Count == 0) return NoTags;

            var sorted = new List<string>(tags);
            sorted.Sort(StringComparer.Ordinal);
            return sorted;
        }

        private static IReadOnlyDictionary<string, int> ReadStats(Entity entity, IReadOnlyList<string>? wanted)
        {
            var names = new List<string>();
            if (wanted == null)
            {
                foreach (string stat in entity.StatNames) names.Add(stat);
                names.Sort(StringComparer.Ordinal);
            }
            else
            {
                // A stat the entity does not have is left out rather than reported as zero: the
                // difference between "no block" and "0 block" is a real one to a UI.
                for (int i = 0; i < wanted.Count; i++)
                {
                    if (!string.IsNullOrEmpty(wanted[i]) && entity.HasStat(wanted[i])) names.Add(wanted[i]);
                }
            }

            if (names.Count == 0) return NoStats;

            var stats = new Dictionary<string, int>(names.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Count; i++) stats[names[i]] = entity.GetInt(names[i]);
            return stats;
        }

        private static IReadOnlyList<StatusView> ReadStatuses(Entity entity)
        {
            List<StatusView>? statuses = null;
            IReadOnlyList<Entity> attached = entity.Attached;

            for (int i = 0; i < attached.Count; i++)
            {
                Entity child = attached[i];
                if (child.IsRemoved) continue;
                if (child.Kind != EntityKind.Status && child.Kind != EntityKind.Keyword) continue;

                statuses ??= new List<StatusView>();
                statuses.Add(StatusView.Of(child));
            }

            return statuses ?? NoStatuses;
        }

        private static IReadOnlyList<int> ReadAbilities(Entity entity)
        {
            List<int>? abilities = null;
            IReadOnlyList<Entity> attached = entity.Attached;

            for (int i = 0; i < attached.Count; i++)
            {
                Entity child = attached[i];
                if (child.IsRemoved || child.Kind != EntityKind.Ability) continue;

                abilities ??= new List<int>();
                abilities.Add(child.Id);
            }

            return abilities ?? NoIds;
        }

        public override string ToString() => Name + "#" + Id;
    }
}
