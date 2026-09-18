using System;
using System.Collections.Generic;
using System.Text;
using GameplayEffects.Syntax;

namespace GameplayEffects.Runtime
{
    /// <summary>An active <c>modify</c> line, bound to the entity that declared it.</summary>
    public sealed class Modifier
    {
        internal Modifier(int id, Entity owner, ModifyNode syntax, long order)
        {
            Id = id;
            Owner = owner;
            Syntax = syntax;
            Order = order;
        }

        public int Id { get; }
        public Entity Owner { get; }
        public ModifyNode Syntax { get; }
        public string Channel => Syntax.Channel;
        public ModifierLayer Layer => Syntax.Layer;

        /// <summary>Registration order. Within the override layer the latest modifier wins.</summary>
        public long Order { get; }

        public override string ToString() => $"{Owner.Name}: modify {Channel}";
    }

    /// <summary>
    /// What a value is being computed for. Stats use only <see cref="Subject"/>; action channels
    /// such as <c>damage</c> also carry the source, the card and the action's tags.
    /// </summary>
    public sealed class ModifierQuery
    {
        private static readonly IReadOnlyCollection<string> NoTags = new string[0];

        public ModifierQuery(string channel) => Channel = channel;

        public string Channel { get; }

        /// <summary>The entity whose value this is: the stat holder, the damage target, the card whose cost is read.</summary>
        public Entity? Subject { get; set; }

        /// <summary>Who is acting: the attacker for <c>damage</c>, the healer for <c>heal</c>.</summary>
        public Entity? Source { get; set; }

        public Entity? Card { get; set; }

        public IReadOnlyCollection<string> Tags { get; set; } = NoTags;
    }

    /// <summary>One step of a modifier breakdown, for the "base 6 → +3 Strength → ×1.5 Codex → 13" view.</summary>
    public readonly struct ModifierStep
    {
        public ModifierStep(Modifier modifier, Num amount, Num before, Num after)
        {
            Modifier = modifier;
            Amount = amount;
            Before = before;
            After = after;
        }

        public Modifier Modifier { get; }
        public Num Amount { get; }
        public Num Before { get; }
        public Num After { get; }

        public override string ToString()
        {
            string op = Modifier.Layer switch
            {
                ModifierLayer.Add => Amount.IsNegative ? Amount.ToString() : "+" + Amount,
                ModifierLayer.Multiply => "×" + Amount,
                ModifierLayer.Clamp => "clamp",
                ModifierLayer.Override => "=" + Amount,
                _ => Amount.ToString(),
            };
            return op + " " + Modifier.Owner.Name;
        }
    }

    public sealed class ModifierResult
    {
        internal ModifierResult(Num baseValue, Num final, IReadOnlyList<ModifierStep> steps)
        {
            Base = baseValue;
            Final = final;
            Steps = steps;
        }

        public Num Base { get; }
        public Num Final { get; }
        public IReadOnlyList<ModifierStep> Steps { get; }

        public override string ToString()
        {
            var text = new StringBuilder("base ").Append(Base);
            foreach (ModifierStep step in Steps) text.Append(" → ").Append(step);
            text.Append(" → ").Append(Final);
            return text.ToString();
        }
    }

    /// <summary>Evaluates a modifier's scope, filter and amount. Implemented by the interpreter.</summary>
    public interface IModifierEvaluator
    {
        bool Applies(Modifier modifier, ModifierQuery query);

        Value Amount(Modifier modifier, ModifierQuery query);
    }

    /// <summary>
    /// The modifier pipeline from section 3.7. Values pass through fixed layers in ruleset order
    /// (add, multiply, clamp, override by default). Stat reads are cached and the cache is dropped
    /// whenever <see cref="GameState.Version"/> moves, which covers every mutation that could
    /// change a modifier's scope, filter or amount.
    /// </summary>
    public sealed class ModifierPipeline
    {
        private readonly GameState _state;
        private readonly Dictionary<string, List<Modifier>> _byChannel = new Dictionary<string, List<Modifier>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, List<Modifier>> _byOwner = new Dictionary<int, List<Modifier>>();
        private readonly Dictionary<(int, string), Num> _statCache = new Dictionary<(int, string), Num>();
        private readonly HashSet<(int, string)> _inProgress = new HashSet<(int, string)>();
        private long _cacheVersion = -1;
        private int _nextId = 1;
        private long _nextOrder = 1;

        internal ModifierPipeline(GameState state) => _state = state;

        public IModifierEvaluator? Evaluator { get; set; }

        public int Count { get; private set; }

        /// <summary>Stat reads served from the cache. Exposed for profiling and tests.</summary>
        public long CacheHits { get; private set; }

        internal Modifier Register(Entity owner, ModifyNode syntax)
        {
            var modifier = new Modifier(_nextId++, owner, syntax, _nextOrder++);

            if (!_byChannel.TryGetValue(modifier.Channel, out List<Modifier>? list))
            {
                list = new List<Modifier>();
                _byChannel[modifier.Channel] = list;
            }
            list.Add(modifier);

            if (!_byOwner.TryGetValue(owner.Id, out List<Modifier>? owned))
            {
                owned = new List<Modifier>();
                _byOwner[owner.Id] = owned;
            }
            owned.Add(modifier);

            Count++;
            _state.Touch();
            return modifier;
        }

        internal void UnregisterAll(Entity owner)
        {
            if (!_byOwner.TryGetValue(owner.Id, out List<Modifier>? owned)) return;

            foreach (Modifier modifier in owned)
            {
                if (_byChannel.TryGetValue(modifier.Channel, out List<Modifier>? list) && list.Remove(modifier)) Count--;
            }

            _byOwner.Remove(owner.Id);
            _state.Touch();
        }

        /// <summary>
        /// Every modifier one entity declared. The event bus answers the same question for
        /// listeners, and an inspector needs both to say what a single thing is doing to a game.
        /// </summary>
        public IReadOnlyList<Modifier> OwnedBy(Entity owner) =>
            _byOwner.TryGetValue(owner.Id, out List<Modifier>? owned) ? owned : (IReadOnlyList<Modifier>)Array.Empty<Modifier>();

        public IReadOnlyList<Modifier> OnChannel(string channel) =>
            _byChannel.TryGetValue(channel, out List<Modifier>? list) ? list : (IReadOnlyList<Modifier>)Array.Empty<Modifier>();

        public bool HasChannel(string channel) => _byChannel.TryGetValue(channel, out List<Modifier>? list) && list.Count > 0;

        /// <summary>Computes a stat through the pipeline, using the cache when state has not changed.</summary>
        public Num ComputeStat(Entity entity, string stat, Num baseValue)
        {
            if (Evaluator == null || !HasChannel(stat)) return baseValue;

            if (_cacheVersion != _state.Version)
            {
                _statCache.Clear();
                _cacheVersion = _state.Version;
            }

            var key = (entity.Id, stat.ToLowerInvariant());
            if (_statCache.TryGetValue(key, out Num cached))
            {
                CacheHits++;
                return cached;
            }

            // A modifier whose amount reads the stat it modifies would recurse forever. Inside
            // that cycle the stat reads as its base value, which is the only sane fixed point.
            if (!_inProgress.Add(key)) return baseValue;

            try
            {
                Num result = Apply(new ModifierQuery(stat) { Subject = entity }, baseValue, null);
                if (_cacheVersion == _state.Version) _statCache[key] = result;
                return result;
            }
            finally
            {
                _inProgress.Remove(key);
            }
        }

        /// <summary>Computes an action value such as damage or cost. Not cached: the query is ad hoc.</summary>
        public Num Compute(ModifierQuery query, Num baseValue)
        {
            if (Evaluator == null || !HasChannel(query.Channel)) return baseValue;
            return Apply(query, baseValue, null);
        }

        /// <summary>Like <see cref="Compute"/>, but records every step for debugging tools.</summary>
        public ModifierResult Explain(ModifierQuery query, Num baseValue)
        {
            var steps = new List<ModifierStep>();
            Num final = Evaluator == null ? baseValue : Apply(query, baseValue, steps);
            return new ModifierResult(baseValue, final, steps);
        }

        private Num Apply(ModifierQuery query, Num value, List<ModifierStep>? steps)
        {
            if (!_byChannel.TryGetValue(query.Channel, out List<Modifier>? all) || all.Count == 0) return value;

            // Snapshot the candidates: evaluating a filter must not be able to change the set
            // being iterated, even if it somehow triggers activation changes.
            var applicable = new List<(Modifier Modifier, Value Amount)>();
            foreach (Modifier modifier in all.ToArray())
            {
                if (modifier.Owner.IsRemoved) continue;
                if (!Evaluator!.Applies(modifier, query)) continue;
                applicable.Add((modifier, Evaluator.Amount(modifier, query)));
            }

            if (applicable.Count == 0) return value;

            foreach (ModifierLayer layer in _state.Rules.ModifierLayers)
            {
                switch (layer)
                {
                    case ModifierLayer.Add:
                        foreach (var (modifier, amount) in applicable)
                        {
                            if (modifier.Layer != ModifierLayer.Add) continue;
                            Num before = value;
                            value += amount.Number;
                            steps?.Add(new ModifierStep(modifier, amount.Number, before, value));
                        }
                        break;

                    case ModifierLayer.Multiply:
                        foreach (var (modifier, amount) in applicable)
                        {
                            if (modifier.Layer != ModifierLayer.Multiply) continue;
                            Num before = value;
                            Num factor = amount.Unit == "%" ? Num.Percent(amount.Number) : amount.Number;
                            value *= factor;
                            steps?.Add(new ModifierStep(modifier, factor, before, value));
                        }
                        break;

                    case ModifierLayer.Clamp:
                        foreach (var (modifier, amount) in applicable)
                        {
                            if (modifier.Layer != ModifierLayer.Clamp) continue;
                            Num before = value;
                            // `clamp 0..10` bounds both ends; a bare number is a ceiling.
                            value = amount.Kind == ValueKind.Range
                                ? Num.Clamp(value, amount.Number, amount.RangeHigh)
                                : Num.Min(value, amount.Number);
                            steps?.Add(new ModifierStep(modifier, value, before, value));
                        }
                        break;

                    case ModifierLayer.Override:
                        // The most recently created source wins. Creation order rather than
                        // registration order, because a snapshot restore re-registers everything
                        // and must not change which override wins.
                        (Modifier Modifier, Value Amount)? winner = null;
                        foreach (var entry in applicable)
                        {
                            if (entry.Modifier.Layer != ModifierLayer.Override) continue;
                            if (winner == null || IsLater(entry.Modifier, winner.Value.Modifier)) winner = entry;
                        }
                        if (winner != null)
                        {
                            Num before = value;
                            value = winner.Value.Amount.Number;
                            steps?.Add(new ModifierStep(winner.Value.Modifier, value, before, value));
                        }
                        break;
                }
            }

            return value;
        }

        private static bool IsLater(Modifier candidate, Modifier current)
        {
            if (candidate.Owner.Sequence != current.Owner.Sequence) return candidate.Owner.Sequence > current.Owner.Sequence;
            return candidate.Order > current.Order;
        }
    }
}
