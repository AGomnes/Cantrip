using System;
using System.Collections.Generic;
using System.Linq;
using GameplayEffects.Content;

namespace GameplayEffects.Runtime
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

        /// <summary>Content address of the block to run, e.g. <c>card:Prepare/effect/0.body</c>.</summary>
        public string? Block { get; set; }

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

        public long Window { get; set; }
    }

    public sealed partial class GameState
    {
        internal GameSnapshot Capture(BlockAddressBook addresses)
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

                IReadOnlyList<Listener> listeners = Events.OwnedBy(entity);
                for (int i = 0; i < listeners.Count; i++)
                {
                    if (listeners[i].LimitWindow != long.MinValue)
                        snapshot.ListenerLimits.Add(new ListenerLimitSnapshot { OwnerId = entity.Id, Index = i, Window = listeners[i].LimitWindow });
                }
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
                string? block = null;
                if (action.Body != null)
                {
                    block = addresses.AddressOf(action.Body) ?? throw new InvalidOperationException(
                        "A scheduled block that is not part of loaded content (for example one started by Execute or a test) cannot be saved.");
                }

                var record = new ScheduledSnapshot
                {
                    Id = action.Id,
                    Timing = (int)action.Timing,
                    OwnerId = action.Owner.Id,
                    Block = block,
                    DueAt = action.DueAt,
                    Deadline = action.Deadline,
                };
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

        internal void Restore(GameSnapshot snapshot, BlockAddressBook addresses)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.FormatVersion != GameSnapshot.CurrentFormat)
                throw new InvalidOperationException($"Snapshot format {snapshot.FormatVersion} is not supported (expected {GameSnapshot.CurrentFormat}).");

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

            foreach (EntitySnapshot record in snapshot.Entities)
            {
                EntityDefinition? definition = null;
                if (record.DefinitionKind != null)
                {
                    definition = Content.Find(record.DefinitionName ?? string.Empty, record.DefinitionKind)
                        ?? throw new InvalidOperationException($"The snapshot needs {record.DefinitionKind} \"{record.DefinitionName}\", which is not loaded.");
                }

                Entity entity;
                if (previous.TryGetValue(record.Id, out Entity? existing)
                    && existing.Kind == (EntityKind)record.Kind
                    && string.Equals(existing.Name, record.Name, StringComparison.Ordinal))
                {
                    entity = existing;
                    entity.ResetForRestore();
                    entity.Definition = definition;
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

            foreach (ScheduledSnapshot record in snapshot.Scheduled)
            {
                BlockNodeOrNull body = record.Block == null
                    ? default
                    : new BlockNodeOrNull(addresses.Resolve(record.Block) ?? throw new InvalidOperationException(
                        $"The snapshot schedules content block `{record.Block}`, which does not exist in the loaded content."));

                var action = new ScheduledAction(record.Id, (ScheduleTiming)record.Timing, Lookup(record.OwnerId) ?? throw Missing(record.OwnerId), body.Block)
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

            foreach (ListenerLimitSnapshot limit in snapshot.ListenerLimits)
            {
                Entity? owner = Lookup(limit.OwnerId);
                if (owner == null) continue;
                IReadOnlyList<Listener> listeners = Events.OwnedBy(owner);
                if (limit.Index < listeners.Count) listeners[limit.Index].LimitWindow = limit.Window;
            }

            Touch();
        }

        private Entity? Lookup(int id) => id == 0 ? null : Find(id);

        private static InvalidOperationException Missing(int id) =>
            new InvalidOperationException($"The snapshot refers to entity #{id}, which it does not contain.");

        /// <summary>Wraps a nullable block so the ternary above stays readable under C# 9 typing rules.</summary>
        private readonly struct BlockNodeOrNull
        {
            public BlockNodeOrNull(Syntax.BlockNode block) => Block = block;

            public Syntax.BlockNode? Block { get; }
        }

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
