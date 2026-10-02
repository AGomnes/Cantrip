using System;
using System.Collections.Generic;
using System.Text;
using Cantrip.Syntax;

namespace Cantrip.Runtime
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

        /// <summary>A number unique within this game, stable while the modifier is registered. It is not saved.</summary>
        public int Id { get; }

        /// <summary>The entity whose declaration this came from. Its controller is what the bare, unscoped form of the modifier is anchored to.</summary>
        public Entity Owner { get; }

        /// <summary>The parsed <c>modify</c> line, for a tool that needs the scope, the filter or the span.</summary>
        public ModifyNode Syntax { get; }

        /// <summary>
        /// What this modifies: a stat name, or an action channel such as <c>damage</c> or <c>cost</c>.
        /// Which end of an action an <c>of</c> group names is the channel's decision, not the modifier's.
        /// </summary>
        public string Channel => Syntax.Channel;

        /// <summary>Which layer of the pipeline it applies in, taken from how the amount was written: <c>+2</c> adds, <c>x150%</c> multiplies, <c>set 1</c> overrides.</summary>
        public ModifierLayer Layer => Syntax.Layer;

        /// <summary>Registration order. Within the override layer the latest modifier wins.</summary>
        public long Order { get; }

        /// <summary>The owner and the channel, for a log or an inspector.</summary>
        public override string ToString() => $"{Owner.Name}: modify {Channel}";
    }

    /// <summary>
    /// What a value is being computed for. Stats use only <see cref="Subject"/>; action channels
    /// such as <c>damage</c> also carry the source, the card and the action's tags.
    /// </summary>
    public sealed class ModifierQuery
    {
        private static readonly IReadOnlyCollection<string> NoTags = new string[0];

        /// <summary>
        /// A question for the pipeline. Set <see cref="Subject"/> at least; for an action channel set
        /// <see cref="Source"/>, <see cref="Action"/> and <see cref="Tags"/> too, or filters that look
        /// at them will not match.
        /// </summary>
        public ModifierQuery(string channel) => Channel = channel;

        /// <summary>The stat or action channel being computed.</summary>
        public string Channel { get; }

        /// <summary>The entity whose value this is: the stat holder, the damage target, the card whose cost is read.</summary>
        public Entity? Subject { get; set; }

        /// <summary>Who is acting: the attacker for <c>damage</c>, the healer for <c>heal</c>.</summary>
        public Entity? Source { get; set; }

        /// <summary>
        /// The action this value came from, when there is one: the card being played, or the ability
        /// being used. It is what the <c>card:</c> qualifier reads.
        /// </summary>
        /// <remarks>
        /// It was called <c>Card</c> until 1.0 and held an ability all along. The <c>targetable</c>
        /// channel sets it from whatever is being aimed, and <c>cost</c> and <c>cooldown</c> from
        /// whatever is priced, so the name was wrong on the day it was written and would have been
        /// frozen wrong. The DSL's <c>card:</c> filter keeps its word, because that is frozen content
        /// vocabulary and reads correctly in the case content overwhelmingly writes.
        /// </remarks>
        public Entity? Action { get; set; }

        /// <summary>
        /// The action's own tags (<c>fire</c> on fire damage), which a <c>tag:</c> filter tests. Empty
        /// rather than null by default, so a filter never has to guard.
        /// </summary>
        public IReadOnlyCollection<string> Tags { get; set; } = NoTags;
    }

    /// <summary>One step of a modifier breakdown, for the "base 6 → +3 Strength → ×1.5 Codex → 13" view.</summary>
    public readonly struct ModifierStep
    {
        /// <summary>Records one modifier's effect on a value. Built by the pipeline; a game reads these rather than making them.</summary>
        public ModifierStep(Modifier modifier, Num amount, Num before, Num after)
        {
            Modifier = modifier;
            Amount = amount;
            Before = before;
            After = after;
        }

        /// <summary>Which modifier this step was, so a breakdown can name the relic or status responsible.</summary>
        public Modifier Modifier { get; }

        /// <summary>What the modifier said, in its own terms: the addend, the multiplier, the override value. Not the difference it made.</summary>
        public Num Amount { get; }

        /// <summary>The running value going in.</summary>
        public Num Before { get; }

        /// <summary>The running value coming out. <c>After - Before</c> is the difference this step actually made, which for a clamp may be zero.</summary>
        public Num After { get; }

        /// <summary>The step as a breakdown line: <c>+3 Strength</c>, <c>×1.5 Codex</c>, <c>clamp Ward</c>.</summary>
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

    /// <summary>
    /// A value and everything that was done to it: what a "base 6 → +3 Strength → ×1.5 Codex → 13"
    /// tooltip is drawn from.
    /// </summary>
    public sealed class ModifierResult
    {
        internal ModifierResult(Num baseValue, Num final, IReadOnlyList<ModifierStep> steps)
        {
            Base = baseValue;
            Final = final;
            Steps = steps;
        }

        /// <summary>The value before any modifier: the printed number.</summary>
        public Num Base { get; }

        /// <summary>The value after every step, which is what the rules use.</summary>
        public Num Final { get; }

        /// <summary>
        /// Every modifier that applied, in the order they were applied. A modifier whose filter did not
        /// match is not here at all, which is why an empty list and an unchanged value mean the same
        /// thing to a reader and different things to a designer hunting a rule that is not firing.
        /// </summary>
        public IReadOnlyList<ModifierStep> Steps { get; }

        /// <summary>The whole breakdown on one line, as a trace prints it.</summary>
        public override string ToString()
        {
            var text = new StringBuilder("base ").Append(Base);
            foreach (ModifierStep step in Steps) text.Append(" → ").Append(step);
            text.Append(" → ").Append(Final);
            return text.ToString();
        }
    }

    /// <summary>
    /// Evaluates a modifier's scope, filter and amount. Implemented by the interpreter, and by
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// Internal because it is unimplementable from outside and always was. <c>Interpreter</c> is
    /// the only implementer, implements both members explicitly, and is handed to the pipeline by
    /// the runtime that builds it. The setter of <see cref="ModifierPipeline.Evaluator"/> has been
    /// internal since a null there was found to stop every modifier applying in silence. So nothing
    /// outside the library could ever supply one, and the pipeline is not one of the four seams
    /// <c>docs/stability.md</c> promises may grow a member with a default. Left public it would
    /// have carried an interface's whole freeze cost (a member added in 1.x breaking every
    /// implementation of it) and none of the escape hatch, for a surface nobody can implement.
    /// </remarks>
    internal interface IModifierEvaluator
    {
        /// <summary>Whether this modifier's scope and filter match the value being computed.</summary>
        bool Applies(Modifier modifier, ModifierQuery query);

        /// <summary>
        /// What the modifier's right-hand side evaluates to for this query. It is evaluated per query,
        /// not once, because an amount may read the owner's stats.
        /// </summary>
        Value Amount(Modifier modifier, ModifierQuery query);
    }

    /// <summary>
    /// The modifier pipeline. Values pass through fixed layers in ruleset order
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

        /// <summary>
        /// The interpreter, set when it is built. Internal both ways, because
        /// <see cref="IModifierEvaluator"/> is: a null here stops every modifier applying, silently,
        /// and the pipeline is not a seam a game replaces.
        /// </summary>
        internal IModifierEvaluator? Evaluator { get; set; }

        /// <summary>How many modifiers are registered, across every channel and owner.</summary>
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

        /// <summary>
        /// Every modifier registered on a channel, in registration order rather than the order they
        /// are applied in, which the layers decide. Empty for a channel nothing modifies.
        /// </summary>
        public IReadOnlyList<Modifier> OnChannel(string channel) =>
            _byChannel.TryGetValue(channel, out List<Modifier>? list) ? list : (IReadOnlyList<Modifier>)Array.Empty<Modifier>();

        /// <summary>
        /// Whether anything at all modifies this channel, which is the cheap pre-check before building a
        /// query. It says nothing about whether any of them would match.
        /// </summary>
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
