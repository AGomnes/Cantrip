using System;
using System.Collections.Generic;
using System.Linq;
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
        public const int CurrentFormat = 1;

        public int FormatVersion { get; set; } = CurrentFormat;

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
        public string Zone { get; set; } = string.Empty;
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

    public sealed class ZoneSnapshot
    {
        public int OwnerId { get; set; }
        public string Zone { get; set; } = string.Empty;
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
        public string? Block { get; set; }

        /// <summary>
        /// A hash of the block's statements. A restore that finds other statements at
        /// <see cref="Block"/> looks for these elsewhere in the same definition, and refuses the
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
        public string? Deadline { get; set; }
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

            var (s0, s1, s2, s3) = Rng.GetState();
            snapshot.Rng = new[] { s0, s1, s2, s3 };

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
                    Deadline = action.Deadline,
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
            if (snapshot.FormatVersion != GameSnapshot.CurrentFormat)
                throw new InvalidOperationException($"Snapshot format {snapshot.FormatVersion} is not supported (expected {GameSnapshot.CurrentFormat}).");

            // Everything that can refuse the snapshot is looked up before the game is touched, so a
            // refused save leaves the game in progress exactly as it was.
            CheckComplete(snapshot);
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
            if (snapshot.Rng == null || snapshot.Rng.Length != 4)
                throw new InvalidOperationException("The snapshot's random number generator state is not four numbers.");
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
            Rng.SetState((snapshot.Rng[0], snapshot.Rng[1], snapshot.Rng[2], snapshot.Rng[3]));
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
                    Deadline = record.Deadline,
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
