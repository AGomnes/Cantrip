using System;
using System.Collections.Generic;
using Cantrip.Runtime;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// Converts between the rules engine's values and Godot's, for the two places they meet: the
    /// arguments a host function is called with, and the answer it gives back.
    /// </summary>
    /// <remarks>
    /// It is a property rather than a static so the runtime node can plug its own mapping in and
    /// keep one conversion for the whole addon.
    /// </remarks>
    public interface IValueMarshal
    {
        Variant ToVariant(Value value);

        /// <summary>
        /// Reads a value back. Entity ids need <paramref name="state"/> to resolve; without one,
        /// anything that names entities comes back empty rather than wrong.
        /// </summary>
        Value ToValue(Variant variant, GameState? state);
    }

    /// <summary>
    /// The adapter's <c>IEffectHost</c>: it records resolved events for the game to animate later,
    /// and answers the names and functions the rules cannot know from Callables the game registered.
    /// </summary>
    /// <remarks>
    /// <see cref="OnEvent"/> must never call back into the runtime. It fires in the middle of
    /// resolution, where the interpreter is not re-entrant, so a signal handler that played a card
    /// from here would corrupt a half-resolved action. All it does is append to <see cref="Buffer"/>;
    /// the runtime node drains that to signals once the top-level call has returned.
    /// <para>
    /// <see cref="TryCall"/> and <see cref="TryResolveName"/> genuinely do run script mid-resolution,
    /// because the rules are waiting for the answer. <see cref="InCallback"/> is true while they do,
    /// so the node's entry points can refuse a call that came back round instead of re-entering.
    /// </para>
    /// </remarks>
    public sealed class GodotEffectHost : IEffectHost
    {
        /// <summary>The built-in conversion, used unless the node supplies its own.</summary>
        public static readonly IValueMarshal DefaultMarshal = new IdMarshal();

        private readonly Dictionary<string, Callable> _names = new Dictionary<string, Callable>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Callable> _functions = new Dictionary<string, Callable>(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<string> _trackedStats = EventBuffer.DefaultTrackedStats;
        private IValueMarshal _marshal = DefaultMarshal;
        private int _depth;

        public GodotEffectHost(EventBuffer? buffer = null)
        {
            Buffer = buffer ?? new EventBuffer();
        }

        /// <summary>Where resolved events wait until the action that raised them has finished.</summary>
        public EventBuffer Buffer { get; }

        /// <summary>
        /// The game being hosted. The node sets it as soon as the runtime exists; until then, and
        /// for anything it misses, the event's own entities are asked instead.
        /// </summary>
        public GameState? State { get; set; }

        /// <summary>Stats snapshotted on every event. Keep it short: each one is read per event.</summary>
        public IReadOnlyList<string> TrackedStats
        {
            get => _trackedStats;
            set => _trackedStats = value ?? EventBuffer.DefaultTrackedStats;
        }

        public IValueMarshal Marshal
        {
            get => _marshal;
            set => _marshal = value ?? DefaultMarshal;
        }

        /// <summary>True while a game Callable is running, so entry points can refuse to re-enter.</summary>
        public bool InCallback => _depth > 0;

        /// <summary>Names the game answers, for the linter's <c>LintOptions</c>.</summary>
        public IReadOnlyCollection<string> Names => _names.Keys;

        /// <summary>Functions the game answers, for the linter's <c>LintOptions</c>.</summary>
        public IReadOnlyCollection<string> Functions => _functions.Keys;

        // Registration ---------------------------------------------------------------------------

        /// <summary>
        /// Answers a name the content uses but the rules cannot know, such as a spatial group.
        /// The Callable is called as <c>f(context)</c> and returns null to fall through.
        /// </summary>
        public void RegisterName(string name, Callable callable)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("A host name cannot be empty.", nameof(name));
            _names[name] = callable;
        }

        public bool UnregisterName(string name) => name != null && _names.Remove(name);

        /// <summary>
        /// Answers a function such as <c>within(5)</c>. The Callable is called as
        /// <c>f(args, context)</c>, where args is an Array of the converted arguments, and returns
        /// null to fall through.
        /// </summary>
        public void RegisterFunction(string name, Callable callable)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("A host function cannot be empty.", nameof(name));
            _functions[name] = callable;
        }

        public bool UnregisterFunction(string name) => name != null && _functions.Remove(name);

        /// <summary>Drops every registration, as when a scene that owned them leaves the tree.</summary>
        public void ClearCallbacks()
        {
            _names.Clear();
            _functions.Clear();
        }

        // IEffectHost ----------------------------------------------------------------------------

        public bool TryResolveName(string name, EvalContext context, out Value value)
        {
            value = Value.None;
            if (name == null || !_names.TryGetValue(name, out Callable callable)) return false;

            Variant result = Invoke(callable, ContextOf(context));
            if (result.VariantType == Variant.Type.Nil) return false;

            value = Marshal.ToValue(result, StateOf(context));
            return true;
        }

        public bool TryCall(string function, IReadOnlyList<Value> arguments, EvalContext context, out Value value)
        {
            value = Value.None;
            if (function == null || !_functions.TryGetValue(function, out Callable callable)) return false;

            var args = new Godot.Collections.Array();
            if (arguments != null)
            {
                for (int i = 0; i < arguments.Count; i++) args.Add(Marshal.ToVariant(arguments[i]));
            }

            Variant result = Invoke(callable, args, ContextOf(context));
            if (result.VariantType == Variant.Type.Nil) return false;

            value = Marshal.ToValue(result, StateOf(context));
            return true;
        }

        /// <summary>Records the event. Deliberately the only thing that happens here; see the remarks.</summary>
        public void OnEvent(GameEvent gameEvent)
        {
            if (gameEvent == null) return;

            GameState? state = State
                ?? gameEvent.Target?.State
                ?? gameEvent.Source?.State
                ?? gameEvent.Card?.State;
            if (state == null) return;

            Buffer.Add(gameEvent, state, TrackedStats);
        }

        // Plumbing -------------------------------------------------------------------------------

        /// <summary>
        /// Calls into script. Errors are left to propagate: a broken host function should fail the
        /// action, which the runtime already unwinds cleanly, rather than quietly answer "nothing"
        /// and let the rules carry on with a wrong number.
        /// </summary>
        private Variant Invoke(Callable callable, params Variant[] args)
        {
            _depth++;
            try
            {
                return callable.Call(args);
            }
            finally
            {
                _depth--;
            }
        }

        /// <summary>Who is acting, as ids, so script never holds an engine object.</summary>
        private static Godot.Collections.Dictionary ContextOf(EvalContext? context)
        {
            var map = new Godot.Collections.Dictionary();
            map["self"] = context?.Self?.Id ?? 0;
            map["source"] = context?.Source?.Id ?? 0;
            map["target"] = context?.Target?.Id ?? 0;
            map["card"] = context?.Card?.Id ?? 0;
            map["event"] = context?.Event?.Name ?? string.Empty;
            return map;
        }

        private GameState? StateOf(EvalContext? context) =>
            State ?? context?.Self?.State ?? context?.Source?.State ?? context?.Target?.State;

        /// <summary>
        /// Entities cross as ids and nothing else, per the addon's rule for the script boundary.
        /// </summary>
        /// <remarks>
        /// An Array can only mean entities: the core has no list-of-numbers value, so a selector
        /// answering <c>[3, 7]</c> is unambiguous, while a bare number is always a number.
        /// </remarks>
        private sealed class IdMarshal : IValueMarshal
        {
            public Variant ToVariant(Value value)
            {
                switch (value.Kind)
                {
                    case ValueKind.Number:
                        return value.Number.ToDouble();
                    case ValueKind.Bool:
                        return value.AsBool();
                    case ValueKind.Text:
                        return value.Text ?? string.Empty;
                    case ValueKind.Definition:
                        return value.Definition?.Name ?? string.Empty;
                    case ValueKind.Entity:
                        return value.Entity?.Id ?? 0;
                    case ValueKind.List:
                    {
                        var array = new Godot.Collections.Array();
                        IReadOnlyList<Entity> entities = value.AsEntities();
                        for (int i = 0; i < entities.Count; i++) array.Add(entities[i].Id);
                        return array;
                    }
                    default:
                        return default;
                }
            }

            public Value ToValue(Variant variant, GameState? state)
            {
                switch (variant.VariantType)
                {
                    case Variant.Type.Bool:
                        return Value.FromBool(variant.AsBool());
                    case Variant.Type.Int:
                        return Value.FromNumber(Num.FromInt(variant.AsInt64()));
                    case Variant.Type.Float:
                        return Value.FromNumber(FromDouble(variant.AsDouble()));
                    case Variant.Type.String:
                    case Variant.Type.StringName:
                    case Variant.Type.NodePath:
                        return Value.FromText(variant.AsString());
                    case Variant.Type.Array:
                        return Entities(variant.AsGodotArray(), state);
                    case Variant.Type.PackedInt32Array:
                        return Entities(variant.AsInt32Array(), state);
                    case Variant.Type.PackedInt64Array:
                        return Entities(variant.AsInt64Array(), state);
                    default:
                        return Value.None;
                }
            }

            private static Value Entities(Godot.Collections.Array array, GameState? state)
            {
                var entities = new List<Entity>();
                if (state != null)
                {
                    foreach (Variant item in array)
                    {
                        Entity? entity = state.Find((int)item.AsInt64());
                        if (entity != null) entities.Add(entity);
                    }
                }
                return Value.FromEntities(entities);
            }

            private static Value Entities(int[] ids, GameState? state)
            {
                var entities = new List<Entity>();
                if (state != null)
                {
                    foreach (int id in ids)
                    {
                        Entity? entity = state.Find(id);
                        if (entity != null) entities.Add(entity);
                    }
                }
                return Value.FromEntities(entities);
            }

            private static Value Entities(long[] ids, GameState? state)
            {
                var entities = new List<Entity>();
                if (state != null)
                {
                    foreach (long id in ids)
                    {
                        Entity? entity = state.Find((int)id);
                        if (entity != null) entities.Add(entity);
                    }
                }
                return Value.FromEntities(entities);
            }

            /// <summary>Clamps rather than wraps: a script returning infinity is a bug, not a huge negative.</summary>
            private static Num FromDouble(double value)
            {
                double scaled = Math.Round(value * Num.Scale);
                if (double.IsNaN(scaled)) return Num.Zero;
                if (scaled >= Num.MaxValue.Raw) return Num.MaxValue;
                if (scaled <= Num.MinValue.Raw) return Num.MinValue;
                return Num.FromRaw((long)scaled);
            }
        }
    }
}
