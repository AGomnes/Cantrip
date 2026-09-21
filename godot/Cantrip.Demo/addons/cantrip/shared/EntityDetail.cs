#nullable enable
using System;
using System.Collections.Generic;
using Cantrip.Runtime;
using Cantrip.Syntax;

namespace Cantrip.GodotAdapter
{
    /// <summary>One registered <c>on ...:</c> block, as an inspector shows it.</summary>
    public sealed class ListenerView
    {
        private ListenerView(int id, string eventName, string scope, string phase, string limit, int priority, string text, string file, int line, int column)
        {
            Id = id;
            Event = eventName;
            Scope = scope;
            Phase = phase;
            Limit = limit;
            Priority = priority;
            Text = text;
            File = file;
            Line = line;
            Column = column;
        }

        public int Id { get; }

        /// <summary>The event it hears, without its phase or scope: <c>damaged</c>.</summary>
        public string Event { get; }

        /// <summary>The dotted prefix, as in <c>owner</c> of <c>owner.damaged</c>. Empty when unscoped.</summary>
        public string Scope { get; }

        /// <summary>before, instead or after.</summary>
        public string Phase { get; }

        /// <summary>none, turn, battle, run or chain.</summary>
        public string Limit { get; }

        public int Priority { get; }

        /// <summary>How it reads in content, filter and all: <c>on owner.damaged(tag:fire)</c>.</summary>
        public string Text { get; }

        public string File { get; }

        public int Line { get; }

        public int Column { get; }

        public static ListenerView Of(Listener listener)
        {
            if (listener == null) throw new ArgumentNullException(nameof(listener));

            ListenerNode syntax = listener.Syntax;
            string filter = syntax.Filter == null ? string.Empty : "(" + AstPrinter.Print(syntax.Filter) + ")";
            string limit = syntax.Limit.ToString().ToLowerInvariant();
            string once = syntax.Limit == LimitScope.None ? string.Empty : " once per " + limit;

            return new ListenerView(
                listener.Id,
                listener.EventName,
                listener.Scope ?? string.Empty,
                listener.Phase.ToString().ToLowerInvariant(),
                limit,
                listener.Priority,
                "on " + syntax.EventName + filter + once,
                syntax.Span.File ?? string.Empty,
                syntax.Span.Line,
                syntax.Span.Column);
        }

        public override string ToString() => Text;
    }

    /// <summary>One active <c>modify</c> line, as an inspector shows it.</summary>
    public sealed class ModifierView
    {
        private ModifierView(int id, string channel, string layer, string scope, string amount, string text, string file, int line, int column)
        {
            Id = id;
            Channel = channel;
            Layer = layer;
            Scope = scope;
            Amount = amount;
            Text = text;
            File = file;
            Line = line;
            Column = column;
        }

        public int Id { get; }

        /// <summary>damage, damage_taken, block, cost, or any stat.</summary>
        public string Channel { get; }

        /// <summary>add, multiply, clamp or override: which pass of the pipeline it belongs to.</summary>
        public string Layer { get; }

        /// <summary>What it applies to, when the line says <c>of ...</c>. Empty for the default scope.</summary>
        public string Scope { get; }

        public string Amount { get; }

        /// <summary>How it reads in content: <c>modify damage where tag:fire: x1.5</c>.</summary>
        public string Text { get; }

        public string File { get; }

        public int Line { get; }

        public int Column { get; }

        public static ModifierView Of(Modifier modifier)
        {
            if (modifier == null) throw new ArgumentNullException(nameof(modifier));

            ModifyNode syntax = modifier.Syntax;
            string scope = syntax.Scope == null ? string.Empty : AstPrinter.Print(syntax.Scope);
            string filter = syntax.Filter == null ? string.Empty : " where " + AstPrinter.Print(syntax.Filter);
            string amount = AstPrinter.Print(syntax.Amount);

            return new ModifierView(
                modifier.Id,
                modifier.Channel,
                modifier.Layer.ToString().ToLowerInvariant(),
                scope,
                amount,
                "modify " + modifier.Channel + (scope.Length == 0 ? string.Empty : " of " + scope) + filter + ": " + amount,
                syntax.Span.File ?? string.Empty,
                syntax.Span.Line,
                syntax.Span.Column);
        }

        public override string ToString() => Text;
    }

    /// <summary>
    /// Everything about one live entity: what it is, what it is worth now against what it started
    /// with, and every rule currently attached to it.
    /// </summary>
    /// <remarks>
    /// The point of showing base beside current is that a modifier is invisible in a single number.
    /// An enemy with 6 attack tells a designer nothing; 4 becoming 6, next to the <c>modify</c> line
    /// doing it and the file it lives in, answers the question they actually had.
    /// </remarks>
    public sealed class EntityDetail
    {
        private static readonly IReadOnlyList<ListenerView> NoListeners = new ListenerView[0];
        private static readonly IReadOnlyList<ModifierView> NoModifiers = new ModifierView[0];

        private EntityDetail(
            EntityView entity,
            bool active,
            string definition,
            IReadOnlyDictionary<string, int> baseStats,
            IReadOnlyList<ListenerView> listeners,
            IReadOnlyList<ModifierView> modifiers)
        {
            Entity = entity;
            Active = active;
            Definition = definition;
            BaseStats = baseStats;
            Listeners = listeners;
            Modifiers = modifiers;
        }

        public EntityView Entity { get; }

        /// <summary>
        /// Whether its rules are live. An inactive entity keeps its listeners in content but hears
        /// nothing: a card in the draw pile, a status on a dead actor.
        /// </summary>
        public bool Active { get; }

        /// <summary>The content it came from, as <c>card "Ember"</c>, or empty for the player.</summary>
        public string Definition { get; }

        /// <summary>Stats before modifiers, against <see cref="EntityView.Stats"/> after them.</summary>
        public IReadOnlyDictionary<string, int> BaseStats { get; }

        public IReadOnlyList<ListenerView> Listeners { get; }

        public IReadOnlyList<ModifierView> Modifiers { get; }

        public static EntityDetail Of(Entity entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            GameState state = entity.State;

            var baseStats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string stat in entity.StatNames) baseStats[stat] = entity.GetBase(stat).ToInt();

            List<ListenerView>? listeners = null;
            foreach (Listener listener in state.Events.OwnedBy(entity))
            {
                listeners ??= new List<ListenerView>();
                listeners.Add(ListenerView.Of(listener));
            }

            List<ModifierView>? modifiers = null;
            foreach (Modifier modifier in state.Modifiers.OwnedBy(entity))
            {
                modifiers ??= new List<ModifierView>();
                modifiers.Add(ModifierView.Of(modifier));
            }

            return new EntityDetail(
                EntityView.Of(entity),
                state.IsActive(entity),
                entity.Definition == null ? string.Empty : entity.Definition.ToString(),
                baseStats,
                listeners ?? NoListeners,
                modifiers ?? NoModifiers);
        }

        public override string ToString() =>
            $"{Entity.Name}#{Entity.Id}: {Listeners.Count} listener(s), {Modifiers.Count} modifier(s)";
    }
}
