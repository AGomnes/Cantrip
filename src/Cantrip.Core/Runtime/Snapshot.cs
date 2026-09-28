using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cantrip.Content;
using Cantrip.Syntax;

namespace Cantrip.Runtime
{
    /// <summary>
    /// A complete, plain-data picture of the rules state: entities, zones, RNG, clock, history,
    /// scheduled work and listener limits. It holds only ints, longs, strings, lists and
    /// dictionaries, so any serializer (System.Text.Json, Godot's, MessagePack) can store it.
    /// </summary>
    /// <remarks>
    /// Restoring a snapshot into a runtime with the same content and then feeding it the same
    /// inputs reproduces the original game exactly; <see cref="GameState.ComputeHash"/> matches
    /// after every step. Snapshots can only be taken between actions, never mid-resolution.
    /// </remarks>
    public sealed class GameSnapshot
    {
        /// <summary>
        /// The save format this build writes. It only ever increases, and a build reads every
        /// format up to its own: a save made by an earlier release loads here, brought forward by
        /// <see cref="Upgrade"/>, and only a save that needs a reader this build is not is refused.
        /// </summary>
        public const int CurrentFormat = 2;

        /// <summary>
        /// The oldest reader a save this build writes can be given to, which a reader compares
        /// against its own <see cref="CurrentFormat"/>. It moves only when a change would make an
        /// older reader get the game wrong rather than merely miss something it never knew about,
        /// so that adding an optional field can bump <see cref="CurrentFormat"/> — saying honestly
        /// that the shape changed — without locking every earlier build out of the save.
        /// </summary>
        public const int CurrentMinimumReader = 2;

        /// <summary>
        /// The generator behind <see cref="Rng"/> in a save this build writes. A save that names
        /// another one is refused rather than read as four meaningless numbers; an empty name is
        /// this one, which is what a save made before the name was recorded carries.
        /// </summary>
        public const string CurrentRng = "xoshiro256**";

        /// <summary>The version of Cantrip.Core doing the writing, as <see cref="WrittenBy"/> records it.</summary>
        public static readonly string CurrentWriter = ReadVersion();

        /// <summary>
        /// The format this save is written in. A hand-built snapshot is in this build's format,
        /// which is why this is the one field of the four that defaults to the current value.
        /// </summary>
        public int FormatVersion { get; set; } = CurrentFormat;

        /// <summary>
        /// The oldest <see cref="CurrentFormat"/> that can read this save. Zero in a save made
        /// before it was recorded, which is read as <see cref="FormatVersion"/>.
        /// </summary>
        public int MinimumReader { get; set; }

        /// <summary>
        /// The version of Cantrip.Core that wrote this save, such as <c>0.1.0-preview.6</c>. Empty
        /// in a save made before it was recorded, and in one built by hand rather than captured.
        /// </summary>
        /// <remarks>
        /// It is for people, not for rules: nothing branches on it, and a refusal quotes it so that
        /// "this save needs a newer Cantrip" can say which one. Store it with a replay or a bug
        /// report, as <c>docs/stability.md</c> asks.
        /// </remarks>
        public string WrittenBy { get; set; } = string.Empty;

        public int Turn { get; set; }
        public int BattleNumber { get; set; }
        public int ActiveTeam { get; set; }
        public bool InBattle { get; set; }
        public int PlayerId { get; set; }

        public int NextEntityId { get; set; }
        public long NextSequence { get; set; }
        public long NextScheduleId { get; set; }
        public long NextChainRoot { get; set; }

        public long ClockNow { get; set; }

        /// <summary>
        /// The generator <see cref="Rng"/> came from, empty for <see cref="CurrentRng"/>. Four
        /// numbers are only a game's random future while something says what reads them, and a
        /// release may change the generator, which <c>docs/stability.md</c> allows.
        /// </summary>
        public string RngGenerator { get; set; } = string.Empty;

        /// <summary>The generator's state, as <see cref="RngGenerator"/> means it.</summary>
        public ulong[] Rng { get; set; } = new ulong[4];

        public bool? Won { get; set; }
        public bool SkipNextDraw { get; set; }

        public List<EntitySnapshot> Entities { get; set; } = new List<EntitySnapshot>();
        public List<ZoneSnapshot> Zones { get; set; } = new List<ZoneSnapshot>();
        public List<ScheduledSnapshot> Scheduled { get; set; } = new List<ScheduledSnapshot>();
        public List<ListenerLimitSnapshot> ListenerLimits { get; set; } = new List<ListenerLimitSnapshot>();
        public List<ListenerDueSnapshot> ListenerDues { get; set; } = new List<ListenerDueSnapshot>();

        /// <summary>History counters, as raw <see cref="Num"/> values.</summary>
        public Dictionary<string, long> TurnHistory { get; set; } = new Dictionary<string, long>();

        public Dictionary<string, long> BattleHistory { get; set; } = new Dictionary<string, long>();

        /// <summary>
        /// The oldest <see cref="CurrentFormat"/> that can read this save, as a restore judges it:
        /// what <see cref="MinimumReader"/> says, or, in a save from before it said, the format the
        /// save is written in. A host can ask before restoring, to tell a player that a save needs
        /// a newer version of the game rather than let the restore throw.
        /// </summary>
        public static int ReaderNeededBy(GameSnapshot snapshot) =>
            snapshot.MinimumReader > 0 ? snapshot.MinimumReader : snapshot.FormatVersion;

        /// <summary>
        /// Brings a save written in an older format up to this one, in place. Each step is the work
        /// of one format change; a save already in this format never reaches here.
        /// </summary>
        /// <remarks>
        /// Nothing moves here yet. Format 1 became format 2 by renaming two fields of
        /// <see cref="ScheduledSnapshot"/>, which that type reads under both names as it is
        /// deserialized, and by recording who wrote the save, its minimum reader and the name of
        /// its generator — three things a format 1 save cannot know, and whose absence means
        /// exactly what it should: an unknown earlier writer, a reader as old as the format, and
        /// the generator of the day. So this is a no-op with a place to put the next one, and the
        /// tests that pin a format 1 save's restore are what prove that no-op is the truth.
        /// </remarks>
        internal static void Upgrade(GameSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            // 1 -> 2: nothing to move.

            snapshot.FormatVersion = CurrentFormat;
            if (snapshot.MinimumReader > CurrentMinimumReader) snapshot.MinimumReader = CurrentMinimumReader;
        }

        private static string ReadVersion()
        {
            string version = typeof(GameSnapshot).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;

            // Strip the build metadata SourceLink appends (`0.1.0-preview.6+<commit>`): the release
            // is what a player, a bug report and a changelog line all name.
            int build = version.IndexOf('+');
            return build < 0 ? version : version.Substring(0, build);
        }
    }

    public sealed class EntitySnapshot
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Kind { get; set; }

        /// <summary>Kind and name of the definition, or null for definition-less entities such as the player.</summary>
        public string? DefinitionKind { get; set; }

        public string? DefinitionName { get; set; }

        public int OwnerId { get; set; }
        public int SourceId { get; set; }
        public int Team { get; set; }

        /// <summary>
        /// The zone this entity is in, or empty for none. The same fact as the entity's place in a
        /// <see cref="ZoneSnapshot.Entities"/> list, written here so that an entity can be read
        /// without searching the zones; <see cref="ZoneSnapshot"/> is the one that decides, and a
        /// restore refuses a save whose two accounts disagree.
        /// </summary>
        public string Zone { get; set; } = string.Empty;

        /// <summary>Slot on the board. Not a place in <see cref="ZoneSnapshot.Entities"/>.</summary>
        public int Position { get; set; }
        public bool IsDead { get; set; }
        public bool IsRemoved { get; set; }
        public long Sequence { get; set; }
        public int PatternIndex { get; set; }
        public string? LastMove { get; set; }
        public string? Intent { get; set; }
        public string? Phase { get; set; }

        /// <summary>Base stats, as raw <see cref="Num"/> values.</summary>
        public Dictionary<string, long> Stats { get; set; } = new Dictionary<string, long>();

        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>Attached statuses and keywords, in attachment order.</summary>
        public List<int> Attached { get; set; } = new List<int>();
    }

    /// <summary>One owner's zone and what is in it, in order.</summary>
    /// <remarks>
    /// This is where a card is. <see cref="EntitySnapshot.Zone"/> says the same thing from the
    /// entity's side, and a capture always writes the two consistently, but only this one carries
    /// the order — which for the draw pile is the next card the player will see. A restore checks
    /// that every entity naming a zone is in that zone's list and in no other, and that every
    /// entity naming none is in no list at all, and refuses the save before it touches the game
    /// when they disagree, so that a future change cannot quietly let the two drift apart in a
    /// format that has already been written to disk.
    /// </remarks>
    public sealed class ZoneSnapshot
    {
        /// <summary>The owner of the zone, which for an actor on the board is 0: one board per team.</summary>
        public int OwnerId { get; set; }

        public string Zone { get; set; } = string.Empty;

        /// <summary>The entities in it, in order, by <see cref="EntitySnapshot.Id"/>.</summary>
        public List<int> Entities { get; set; } = new List<int>();
    }

    public sealed class ScheduledSnapshot
    {
        public long Id { get; set; }
        public int Timing { get; set; }
        public int OwnerId { get; set; }

        /// <summary>
        /// Where the block to run is: its place in content, such as <c>card:Prepare/effect/0.body</c>,
        /// or within <see cref="Statements"/> when those are set, such as <c>execute/0.body</c>.
        /// </summary>
        /// <remarks>
        /// It was called <c>Block</c> through format 1, which reads as a `block` — the stat, the
        /// verb and the modifier channel — everywhere else in this language. Saves in format 1
        /// still carry that name, and <see cref="Block"/> takes it.
        /// </remarks>
        public string? BlockAddress { get; set; }

        /// <summary>
        /// The name <see cref="BlockAddress"/> had in format 1, so that a save written before the
        /// rename still restores. It has no getter on purpose: a serializer deserializes through
        /// it but never writes it back, so a save this build makes carries the new name alone.
        /// </summary>
        public string? Block
        {
            set
            {
                if (value != null && BlockAddress == null) BlockAddress = value;
            }
        }

        /// <summary>
        /// A hash of the block's statements. A restore that finds other statements at
        /// <see cref="BlockAddress"/> looks for these elsewhere in the same definition, and refuses the
        /// snapshot if they are gone. Null in saves made before it was recorded, which are
        /// matched by place alone.
        /// </summary>
        public string? BlockHash { get; set; }

        /// <summary>
        /// The statements <c>CardRuntime.Execute</c> ran, when the block is part of them rather than
        /// of content. A restore parses them again, so such work needs nothing from content.
        /// </summary>
        public string? Statements { get; set; }

        public long DueAt { get; set; }

        /// <summary>
        /// The event an <c>until</c> block waits for, such as <c>turn_end</c>; null for work that
        /// waits on <see cref="DueAt"/> instead.
        /// </summary>
        /// <remarks>
        /// It was called <c>Deadline</c> through format 1, which reads as a time, beside a
        /// <see cref="DueAt"/> that is one. Saves in format 1 carry that name, and
        /// <see cref="Deadline"/> takes it.
        /// </remarks>
        public string? UntilEvent { get; set; }

        /// <summary>
        /// The name <see cref="UntilEvent"/> had in format 1, kept for reading as
        /// <see cref="Block"/> is.
        /// </summary>
        public string? Deadline
        {
            set
            {
                if (value != null && UntilEvent == null) UntilEvent = value;
            }
        }

        public Dictionary<string, ValueSnapshot> Bindings { get; set; } = new Dictionary<string, ValueSnapshot>();
        public List<UndoSnapshot> Undo { get; set; } = new List<UndoSnapshot>();
    }

    public sealed class ValueSnapshot
    {
        public int Kind { get; set; }
        public long Number { get; set; }
        public long High { get; set; }
        public string? Unit { get; set; }
        public string? Text { get; set; }
        public List<int>? Entities { get; set; }
        public string? DefinitionKind { get; set; }
        public string? DefinitionName { get; set; }
        public string? Qualifier { get; set; }
    }

    public sealed class UndoSnapshot
    {
        public int EntityId { get; set; }
        public string? Tag { get; set; }
        public int AttachedId { get; set; }
        public string? Stat { get; set; }
        public long Delta { get; set; }
    }

    /// <summary>A <c>once per ...</c> window already used by one listener of one entity.</summary>
    public sealed class ListenerLimitSnapshot
    {
        public int OwnerId { get; set; }

        /// <summary>Index of the listener among its owner's listeners, in definition order.</summary>
        public int Index { get; set; }

        /// <summary>
        /// A hash of the listener as written: of its <c>on</c> line, then, after a colon, of its body.
        /// A restore gives the window back to the listener with this hash, wherever it now is among
        /// its owner's, or else to the listener at <see cref="Index"/> if only its body has changed,
        /// and otherwise drops it. Null in saves made before it was recorded, which are matched by
        /// <see cref="Index"/> alone.
        /// </summary>
        public string? ListenerHash { get; set; }

        public long Window { get; set; }
    }

    /// <summary>When an <c>on every ...:</c> listener of one entity is next due to fire.</summary>
    public sealed class ListenerDueSnapshot
    {
        public int OwnerId { get; set; }

        /// <summary>Index of the listener among its owner's listeners, in definition order.</summary>
        public int Index { get; set; }

        /// <summary>The listener's hash, matched as <see cref="ListenerLimitSnapshot.ListenerHash"/> is.</summary>
        public string? ListenerHash { get; set; }

        public long DueAt { get; set; }
    }

    public sealed partial class GameState
    {
        /// <summary>
        /// Captures the state. <paramref name="recordBlock"/> records how the block of each waiting
        /// action that has one is found again: by address for a save, or kept in memory for a
        /// rollback. It throws if it cannot.
        /// </summary>
        internal GameSnapshot Capture(Action<ScheduledAction, ScheduledSnapshot> recordBlock)
        {
            var snapshot = new GameSnapshot
            {
                FormatVersion = GameSnapshot.CurrentFormat,
                MinimumReader = GameSnapshot.CurrentMinimumReader,
                WrittenBy = GameSnapshot.CurrentWriter,
                RngGenerator = GameSnapshot.CurrentRng,
                Turn = Turn,
                BattleNumber = BattleNumber,
                ActiveTeam = (int)ActiveTeam,
                InBattle = InBattle,
                PlayerId = Player?.Id ?? 0,
                NextEntityId = _nextId,
                NextSequence = _nextSequence,
                NextScheduleId = _nextScheduleId,
                ClockNow = Clock.Now,
            };

            snapshot.Rng = Rng.GetState();

            foreach (Entity entity in _entities)
            {
                var record = new EntitySnapshot
                {
                    Id = entity.Id,
                    Name = entity.Name,
                    Kind = (int)entity.Kind,
                    DefinitionKind = entity.Definition?.KindName,
                    DefinitionName = entity.Definition?.Name,
                    OwnerId = entity.Owner?.Id ?? 0,
                    SourceId = entity.Source?.Id ?? 0,
                    Team = (int)entity.RawTeam,
                    Zone = entity.Zone,
                    Position = entity.Position,
                    IsDead = entity.IsDead,
                    IsRemoved = entity.IsRemoved,
                    Sequence = entity.Sequence,
                    PatternIndex = entity.PatternIndex,
                    LastMove = entity.LastMove,
                    Intent = entity.Intent,
                    Phase = entity.Phase,
                };
                foreach (string stat in entity.StatNames.OrderBy(s => s, StringComparer.Ordinal)) record.Stats[stat] = entity.GetBase(stat).Raw;
                record.Tags.AddRange(entity.Tags.OrderBy(t => t, StringComparer.Ordinal));
                record.Attached.AddRange(entity.Attached.Select(a => a.Id));
                snapshot.Entities.Add(record);

                CaptureListeners(entity, snapshot.ListenerLimits, snapshot.ListenerDues);
            }

            foreach (var zone in _zones.OrderBy(z => z.Key.Owner).ThenBy(z => z.Key.Zone, StringComparer.Ordinal))
            {
                if (zone.Value.Count == 0) continue;
                snapshot.Zones.Add(new ZoneSnapshot { OwnerId = zone.Key.Owner, Zone = zone.Key.Zone, Entities = zone.Value.Select(e => e.Id).ToList() });
            }

            foreach (var entry in _turnHistory) snapshot.TurnHistory[entry.Key] = entry.Value.Raw;
            foreach (var entry in _battleHistory) snapshot.BattleHistory[entry.Key] = entry.Value.Raw;

            foreach (ScheduledAction action in _scheduled)
            {
                var record = new ScheduledSnapshot
                {
                    Id = action.Id,
                    Timing = (int)action.Timing,
                    OwnerId = action.Owner.Id,
                    DueAt = action.DueAt,
                    UntilEvent = action.Deadline,
                };
                if (action.Body != null) recordBlock(action, record);
                foreach (var binding in action.Bindings) record.Bindings[binding.Key] = ToSnapshot(binding.Value);
                foreach (TemporaryChange change in action.Undo)
                {
                    record.Undo.Add(new UndoSnapshot
                    {
                        EntityId = change.Entity.Id,
                        Tag = change.Tag,
                        AttachedId = change.Attached?.Id ?? 0,
                        Stat = change.Stat,
                        Delta = change.Delta.Raw,
                    });
                }
                snapshot.Scheduled.Add(record);
            }

            return snapshot;
        }

        /// <summary>
        /// Replaces the state with a snapshot. <paramref name="resolveBlock"/> gives the block a
        /// waiting action runs, or null for one without a block; it throws when the snapshot's block
        /// cannot be found, and is asked about every action before anything changes.
        /// <paramref name="keptDefinition"/>, when given, supplies an entity's definition as it was
        /// captured, for a rollback that never left this process; otherwise, and wherever it gives
        /// null, a definition is looked up in the loaded content by kind and name.
        /// </summary>
        internal void Restore(GameSnapshot snapshot, Func<ScheduledSnapshot, BlockNode?> resolveBlock, Func<EntitySnapshot, EntityDefinition?>? keptDefinition = null)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            // Only a save this build cannot read is refused. A newer one says which reader it
            // needs, and is welcome while this build is new enough; an older one is brought
            // forward. The number only ever goes up, so "older" and "newer" are the whole of it.
            int needs = GameSnapshot.ReaderNeededBy(snapshot);
            if (needs > GameSnapshot.CurrentFormat)
            {
                throw new InvalidOperationException(
                    $"This save is in format {snapshot.FormatVersion} and needs a Cantrip that reads format {needs}; " +
                    $"this one reads up to format {GameSnapshot.CurrentFormat}. It was written by {Wrote(snapshot)}.");
            }

            if (snapshot.FormatVersion < GameSnapshot.CurrentFormat) GameSnapshot.Upgrade(snapshot);

            // Everything that can refuse the snapshot is looked up before the game is touched, so a
            // refused save leaves the game in progress exactly as it was.
            CheckComplete(snapshot);
            CheckZonesAgree(snapshot);
            var definitions = new EntityDefinition?[snapshot.Entities.Count];
            var ids = new HashSet<int>(snapshot.Entities.Count);
            for (int i = 0; i < snapshot.Entities.Count; i++)
            {
                EntitySnapshot record = snapshot.Entities[i];
                ids.Add(record.Id);
                if (record.DefinitionKind != null)
                {
                    definitions[i] = keptDefinition?.Invoke(record)
                        ?? Content.Find(record.DefinitionName ?? string.Empty, record.DefinitionKind)
                        ?? throw new InvalidOperationException($"The snapshot needs {record.DefinitionKind} \"{record.DefinitionName}\", which is not loaded.");
                }
            }

            void Require(int id)
            {
                if (!ids.Contains(id)) throw Missing(id);
            }

            foreach (EntitySnapshot record in snapshot.Entities)
            {
                foreach (int attached in record.Attached) Require(attached);
            }
            foreach (ZoneSnapshot zone in snapshot.Zones)
            {
                foreach (int id in zone.Entities) Require(id);
            }
            foreach (ScheduledSnapshot record in snapshot.Scheduled)
            {
                Require(record.OwnerId);
                foreach (UndoSnapshot change in record.Undo) Require(change.EntityId);
            }
            // The generator names itself, so that a release that changes it — which
            // docs/stability.md allows — turns a save it cannot continue away instead of feeding
            // another generator four numbers that mean nothing to it. An empty name is this
            // generator: that is what a save written before the name was recorded carries.
            string generator = snapshot.RngGenerator ?? string.Empty;
            if (generator.Length != 0 && !string.Equals(generator, GameSnapshot.CurrentRng, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"This save's random numbers come from {generator}, which this Cantrip cannot continue; it has {GameSnapshot.CurrentRng}. " +
                    $"The save was written by {Wrote(snapshot)}.");
            }

            if (snapshot.Rng == null || snapshot.Rng.Length != 4)
                throw new InvalidOperationException($"The snapshot's random number generator state is not four numbers, as {GameSnapshot.CurrentRng} needs.");
            // All zeros is the one state the generator never leaves, and one Capture never writes:
            // it is what an empty or truncated save deserialises to.
            if (snapshot.Rng.All(part => part == 0))
                throw new InvalidOperationException("The snapshot is damaged: its random number generator state is all zeros.");

            var bodies = new BlockNode?[snapshot.Scheduled.Count];
            for (int i = 0; i < bodies.Length; i++) bodies[i] = resolveBlock(snapshot.Scheduled[i]);

            // Tear down: every listener and modifier goes, then every entity. The instances are
            // kept aside first: restoring into the same game reuses the ones whose ids match, so an
            // Entity the game is holding stays the same object across a load or a rolled-back action.
            var previous = new Dictionary<int, Entity>(_byId);
            foreach (Entity entity in _entities.ToArray()) SetActive(entity, false);
            _entities.Clear();
            _byId.Clear();
            _zones.Clear();
            _active.Clear();
            _scheduled.Clear();

            for (int i = 0; i < snapshot.Entities.Count; i++)
            {
                EntitySnapshot record = snapshot.Entities[i];
                EntityDefinition? definition = definitions[i];

                Entity entity;

                // Id and kind identify it, and nothing else may: a name is not fixed for the life of
                // an entity — `transform` changes it — and matching on one would quietly abandon the
                // instance the game is holding for a new object with the saved name. That breaks the
                // promise three lines above every time an effect transforms something and then asks
                // the player a question, because a deferred choice restores the snapshot to roll back.
                if (previous.TryGetValue(record.Id, out Entity? existing) && existing.Kind == (EntityKind)record.Kind)
                {
                    entity = existing;
                    entity.ResetForRestore();
                    entity.Definition = definition;
                    entity.Name = record.Name;
                    previous.Remove(record.Id);
                }
                else
                {
                    entity = new Entity(this, record.Id, record.Name, (EntityKind)record.Kind, definition);
                }

                entity.Team = (Team)record.Team;
                entity.Zone = record.Zone ?? string.Empty;
                entity.Position = record.Position;
                entity.IsDead = record.IsDead;
                entity.IsRemoved = record.IsRemoved;
                entity.Sequence = record.Sequence;
                entity.PatternIndex = record.PatternIndex;
                entity.LastMove = record.LastMove;
                entity.Intent = record.Intent;
                entity.Phase = record.Phase;

                foreach (var stat in record.Stats) entity.SetBase(stat.Key, Num.FromRaw(stat.Value));
                foreach (string tag in record.Tags) entity.AddTag(tag);

                _entities.Add(entity);
                _byId[entity.Id] = entity;
            }

            // Whatever the snapshot does not mention never existed in the timeline being restored.
            // Anything still holding one of those entities sees it as removed, which is the truth.
            foreach (Entity gone in previous.Values)
            {
                gone.Owner = null;
                gone.Zone = Zones.None;
                gone.IsRemoved = true;
            }

            foreach (EntitySnapshot record in snapshot.Entities)
            {
                Entity entity = _byId[record.Id];
                entity.Owner = Lookup(record.OwnerId);
                entity.Source = Lookup(record.SourceId);
                foreach (int attached in record.Attached) entity.Attach(Lookup(attached) ?? throw Missing(attached));
            }

            foreach (ZoneSnapshot zone in snapshot.Zones)
                MutableZone(Lookup(zone.OwnerId), zone.Zone).AddRange(zone.Entities.Select(id => Lookup(id) ?? throw Missing(id)));

            Turn = snapshot.Turn;
            BattleNumber = snapshot.BattleNumber;
            ActiveTeam = (Team)snapshot.ActiveTeam;
            InBattle = snapshot.InBattle;
            Player = Lookup(snapshot.PlayerId);
            _nextId = snapshot.NextEntityId;
            _nextSequence = snapshot.NextSequence;
            _nextScheduleId = snapshot.NextScheduleId;
            Rng.SetState(snapshot.Rng);
            Clock.Restore(snapshot.ClockNow);

            RestoreHistory(
                snapshot.TurnHistory.Select(kv => new KeyValuePair<string, Num>(kv.Key, Num.FromRaw(kv.Value))),
                snapshot.BattleHistory.Select(kv => new KeyValuePair<string, Num>(kv.Key, Num.FromRaw(kv.Value))));

            for (int i = 0; i < snapshot.Scheduled.Count; i++)
            {
                ScheduledSnapshot record = snapshot.Scheduled[i];
                var action = new ScheduledAction(record.Id, (ScheduleTiming)record.Timing, Lookup(record.OwnerId) ?? throw Missing(record.OwnerId), bodies[i])
                {
                    DueAt = record.DueAt,
                    Deadline = record.UntilEvent,
                };
                foreach (var binding in record.Bindings) action.Bindings[binding.Key] = FromSnapshot(binding.Value);
                foreach (UndoSnapshot change in record.Undo)
                {
                    action.Undo.Add(new TemporaryChange(
                        Lookup(change.EntityId) ?? throw Missing(change.EntityId),
                        change.Tag,
                        Lookup(change.AttachedId),
                        change.Stat,
                        Num.FromRaw(change.Delta)));
                }
                _scheduled.Add(action);
            }

            // Re-register listeners and modifiers in creation order, hosts before attachments.
            foreach (Entity entity in _entities.OrderBy(e => e.Sequence)) SetActive(entity, ShouldBeActive(entity));
            foreach (Entity entity in _entities.OrderBy(e => e.Sequence)) SetActive(entity, ShouldBeActive(entity));

            RestoreListeners(snapshot.ListenerLimits, snapshot.ListenerDues);

            Touch();
        }

        /// <summary>
        /// Refuses a snapshot with a list or map missing, or a record in one that is null.
        /// <see cref="Capture"/> never writes one, but a damaged or hand-edited save can, and
        /// finding it part way through a restore would leave the game half taken apart.
        /// </summary>
        private static void CheckComplete(GameSnapshot snapshot)
        {
            static InvalidOperationException Damaged(string what) =>
                new InvalidOperationException($"The snapshot is damaged: {what} is missing.");

            if (snapshot.Entities == null) throw Damaged("its list of entities");
            if (snapshot.Zones == null) throw Damaged("its list of zones");
            if (snapshot.Scheduled == null) throw Damaged("its list of waiting work");
            if (snapshot.ListenerLimits == null) throw Damaged("its list of listener limits");
            if (snapshot.ListenerDues == null) throw Damaged("its list of listener timers");
            if (snapshot.TurnHistory == null || snapshot.BattleHistory == null) throw Damaged("its history");

            foreach (EntitySnapshot? record in snapshot.Entities)
            {
                if (record == null) throw Damaged("an entity");
                if (record.Stats == null) throw Damaged($"the stats of entity {record.Id}");
                if (record.Tags == null || record.Tags.Contains(null!)) throw Damaged($"the tags of entity {record.Id}");
                if (record.Attached == null) throw Damaged($"what is attached to entity {record.Id}");
            }
            foreach (ZoneSnapshot? zone in snapshot.Zones)
            {
                if (zone == null || zone.Zone == null) throw Damaged("a zone");
                if (zone.Entities == null) throw Damaged($"the contents of zone {zone.Zone}");
            }
            foreach (ScheduledSnapshot? record in snapshot.Scheduled)
            {
                if (record == null) throw Damaged("a waiting action");
                if (record.Bindings == null || record.Bindings.Values.Contains(null!)) throw Damaged($"the bindings of waiting action {record.Id}");
                if (record.Undo == null || record.Undo.Contains(null!)) throw Damaged($"what waiting action {record.Id} undoes");
            }
            if (snapshot.ListenerLimits.Contains(null!)) throw Damaged("a listener limit");
            if (snapshot.ListenerDues.Contains(null!)) throw Damaged("a listener timer");
        }

        /// <summary>
        /// Refuses a snapshot whose two accounts of where a card is disagree: the zone lists, which
        /// decide, and each entity's own <see cref="EntitySnapshot.Zone"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="Capture"/> always writes the two consistently, so this never fires on a save
        /// this engine made. It is here because the format cannot be re-versioned freely once 1.0
        /// has shipped, and two records of one fact are two things every later change has to keep
        /// in step: if one ever stops matching the other, a restore says so, before it has taken
        /// the running game apart, instead of building a game where a card is in the player's hand
        /// and in the draw pile at once.
        /// </remarks>
        private void CheckZonesAgree(GameSnapshot snapshot)
        {
            static InvalidOperationException Disagrees(string what) =>
                new InvalidOperationException($"The snapshot does not agree with itself about where a card is: {what}.");

            // Reused rather than allocated: a restore is the bot's hot path in `cantrip sim`,
            // which rolls one back for every play it tries, tens of thousands of times in a run.
            Dictionary<int, string> listed = _zoneOfEntity;
            listed.Clear();
            foreach (ZoneSnapshot zone in snapshot.Zones)
            {
                foreach (int id in zone.Entities)
                {
                    if (!listed.TryGetValue(id, out string? already)) listed[id] = zone.Zone;
                    else if (string.Equals(already, zone.Zone, StringComparison.Ordinal))
                        throw Disagrees($"entity #{id} is in the \"{zone.Zone}\" zone twice");
                    else
                        throw Disagrees($"entity #{id} is in the \"{already}\" zone and in the \"{zone.Zone}\" zone");
                }
            }

            foreach (EntitySnapshot record in snapshot.Entities)
            {
                string zone = record.Zone ?? string.Empty;
                bool placed = listed.TryGetValue(record.Id, out string? where);
                if (zone.Length == 0)
                {
                    if (placed) throw Disagrees($"entity #{record.Id} is in no zone, and the \"{where}\" zone lists it");
                }
                else if (!placed)
                {
                    throw Disagrees($"entity #{record.Id} is in the \"{zone}\" zone, which does not list it");
                }
                else if (!string.Equals(where, zone, StringComparison.Ordinal))
                {
                    throw Disagrees($"entity #{record.Id} is in the \"{zone}\" zone, and the \"{where}\" zone lists it");
                }
            }
        }

        /// <summary>
        /// Records what an entity's registered listeners remember: the window each used
        /// <c>once per ...</c> limit was last used in, and when each <c>on every</c> listener is next due.
        /// </summary>
        private void CaptureListeners(Entity entity, List<ListenerLimitSnapshot> limits, List<ListenerDueSnapshot> dues)
        {
            IReadOnlyList<Listener> listeners = Events.OwnedBy(entity);
            for (int i = 0; i < listeners.Count; i++)
            {
                Listener listener = listeners[i];
                if (listener.LimitWindow != long.MinValue)
                {
                    limits.Add(new ListenerLimitSnapshot
                    {
                        OwnerId = entity.Id,
                        Index = i,
                        ListenerHash = BlockHash.Of(listener.Syntax),
                        Window = listener.LimitWindow,
                    });
                }

                if (listener.IntervalUnits > 0)
                {
                    dues.Add(new ListenerDueSnapshot
                    {
                        OwnerId = entity.Id,
                        Index = i,
                        ListenerHash = BlockHash.Of(listener.Syntax),
                        DueAt = listener.NextDueAt,
                    });
                }
            }
        }

        /// <summary>Gives the listeners registered now the records <see cref="CaptureListeners"/> made, as <see cref="MatchListeners"/> pairs them.</summary>
        private void RestoreListeners(List<ListenerLimitSnapshot> limits, List<ListenerDueSnapshot> dues)
        {
            Listener?[] limited = MatchListeners(limits, limit => (limit.OwnerId, limit.Index, limit.ListenerHash));
            for (int i = 0; i < limited.Length; i++)
            {
                Listener? listener = limited[i];
                if (listener != null) listener.LimitWindow = limits[i].Window;
            }

            Listener?[] timed = MatchListeners(dues, due => (due.OwnerId, due.Index, due.ListenerHash));
            for (int i = 0; i < timed.Length; i++)
            {
                Listener? listener = timed[i];
                if (listener != null) listener.NextDueAt = dues[i].DueAt;
            }
        }

        /// <summary>
        /// The listener each recorded limit or timer belongs to, now that listeners have been registered
        /// again from the loaded content by a restore or a hot reload, or null where there is none.
        /// Each listener takes at most one record.
        /// </summary>
        /// <remarks>
        /// A content patch may have added, removed, reordered or changed its owner's <c>on</c> blocks,
        /// so the recorded place alone could name another listener. A record goes to the listener at
        /// its place if that is unchanged, else to the unchanged listener wherever it has moved, else
        /// to the listener at its place if only that listener's body has changed: a rebalanced
        /// listener is still the one that fired, and its <c>on</c> line, which decides when it fires
        /// and what its window means, is the same. Anything else is dropped, and that listener starts
        /// afresh, as if newly added: a record never goes to a listener with a different <c>on</c>
        /// line. A record without a hash, from a save made before hashes were recorded, is matched by
        /// place alone.
        /// </remarks>
        private Listener?[] MatchListeners<T>(List<T> records, Func<T, (int Owner, int Index, string? Hash)> read)
        {
            if (records.Count == 0) return Array.Empty<Listener?>();

            var found = new Listener?[records.Count];
            var claimed = new HashSet<Listener>();

            Listener? AtPlace(int owner, int index)
            {
                Entity? entity = Lookup(owner);
                if (entity == null) return null;
                IReadOnlyList<Listener> listeners = Events.OwnedBy(entity);
                return index >= 0 && index < listeners.Count ? listeners[index] : null;
            }

            // Unchanged, and where it was.
            for (int i = 0; i < records.Count; i++)
            {
                var (owner, index, hash) = read(records[i]);
                Listener? listener = AtPlace(owner, index);
                if (listener == null || (hash != null && BlockHash.Of(listener.Syntax) != hash)) continue;
                if (claimed.Add(listener)) found[i] = listener;
            }

            // Unchanged, but moved among its owner's listeners.
            for (int i = 0; i < records.Count; i++)
            {
                var (owner, _, hash) = read(records[i]);
                Entity? entity = found[i] == null && hash != null ? Lookup(owner) : null;
                if (entity == null) continue;
                foreach (Listener listener in Events.OwnedBy(entity))
                {
                    if (claimed.Contains(listener) || BlockHash.Of(listener.Syntax) != hash) continue;
                    claimed.Add(listener);
                    found[i] = listener;
                    break;
                }
            }

            // Where it was, with the same `on` line and another body.
            for (int i = 0; i < records.Count; i++)
            {
                var (owner, index, hash) = read(records[i]);
                if (found[i] != null || hash == null) continue;
                Listener? listener = AtPlace(owner, index);
                if (listener == null || claimed.Contains(listener)) continue;
                if (BlockHash.OnLineOf(BlockHash.Of(listener.Syntax)) != BlockHash.OnLineOf(hash)) continue;
                claimed.Add(listener);
                found[i] = listener;
            }

            return found;
        }

        /// <summary>What a refusal calls the writer of a save, which an old save does not record.</summary>
        private static string Wrote(GameSnapshot snapshot) =>
            string.IsNullOrEmpty(snapshot.WrittenBy) ? "a version of Cantrip that did not record which" : "Cantrip " + snapshot.WrittenBy;

        // Scratch for CheckZonesAgree, which runs on every restore.
        private readonly Dictionary<int, string> _zoneOfEntity = new Dictionary<int, string>();

        private Entity? Lookup(int id) => id == 0 ? null : Find(id);

        private static InvalidOperationException Missing(int id) =>
            new InvalidOperationException($"The snapshot refers to entity #{id}, which it does not contain.");

        internal static ValueSnapshot ToSnapshot(Value value)
        {
            var record = new ValueSnapshot { Kind = (int)value.Kind, Number = value.Number.Raw, Unit = value.Unit };
            switch (value.Kind)
            {
                case ValueKind.Text:
                    record.Text = value.Text;
                    break;
                case ValueKind.Entity:
                    record.Entities = new List<int> { value.Entity!.Id };
                    break;
                case ValueKind.List:
                    record.Entities = value.AsEntities().Select(e => e.Id).ToList();
                    break;
                case ValueKind.Definition:
                    record.DefinitionKind = value.Definition!.KindName;
                    record.DefinitionName = value.Definition.Name;
                    break;
                case ValueKind.Qualified:
                    record.Qualifier = value.Qualified!.Qualifier;
                    record.Text = value.Qualified.Name;
                    break;
                case ValueKind.Range:
                    record.High = value.RangeHigh.Raw;
                    break;
            }
            return record;
        }

        internal Value FromSnapshot(ValueSnapshot record)
        {
            switch ((ValueKind)record.Kind)
            {
                case ValueKind.None: return Value.None;
                case ValueKind.Number: return Value.FromNumber(Num.FromRaw(record.Number), record.Unit);
                case ValueKind.Bool: return Value.FromBool(record.Number != 0);
                case ValueKind.Text: return Value.FromText(record.Text ?? string.Empty);
                case ValueKind.Entity: return Value.FromEntity(record.Entities?.Count > 0 ? Lookup(record.Entities[0]) : null);
                case ValueKind.List: return Value.FromEntities((record.Entities ?? new List<int>()).Select(Lookup).Where(e => e != null).ToList()!);
                case ValueKind.Definition:
                {
                    EntityDefinition? definition = Content.Find(record.DefinitionName ?? string.Empty, record.DefinitionKind);
                    return definition == null ? Value.None : Value.FromDefinition(definition);
                }
                case ValueKind.Qualified: return Value.FromQualified(record.Qualifier ?? string.Empty, record.Text ?? string.Empty);
                case ValueKind.Range: return Value.FromRange(Num.FromRaw(record.Number), Num.FromRaw(record.High));
                default: return Value.None;
            }
        }
    }
}
