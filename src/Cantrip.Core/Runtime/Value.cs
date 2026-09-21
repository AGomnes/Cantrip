using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;

namespace Cantrip.Runtime
{
    public enum ValueKind
    {
        // Saved games store these as numbers: never renumber or reuse one, only add.
        None = 0,
        Number = 1,
        Bool = 2,
        Text = 3,
        Entity = 4,
        List = 5,

        /// <summary>A reference to loaded content by name, such as <c>Poison</c> in <c>apply Poison 3</c>.</summary>
        Definition = 6,

        /// <summary>A <c>tag:fire</c> style predicate.</summary>
        Qualified = 7,

        /// <summary>An unrolled <c>3..6</c>.</summary>
        Range = 8,
    }

    /// <summary>
    /// The dynamically typed value the interpreter passes around. Numbers are always
    /// <see cref="Num"/> so that evaluation stays deterministic.
    /// </summary>
    public readonly struct Value
    {
        private static readonly IReadOnlyList<Entity> EmptyEntities = new Entity[0];

        private readonly object? _ref;

        private Value(ValueKind kind, Num number, object? reference, string? unit = null)
        {
            Kind = kind;
            Number = number;
            _ref = reference;
            Unit = unit;
        }

        public static readonly Value None = new Value(ValueKind.None, Num.Zero, null);
        public static readonly Value True = new Value(ValueKind.Bool, Num.One, null);
        public static readonly Value False = new Value(ValueKind.Bool, Num.Zero, null);

        public ValueKind Kind { get; }

        /// <summary>The numeric payload for numbers, and 1/0 for booleans.</summary>
        public Num Number { get; }

        /// <summary>The unit a number literal was written with (<c>%</c>, <c>s</c>, <c>turns</c>...).</summary>
        public string? Unit { get; }

        public static Value FromNumber(Num value, string? unit = null) => new Value(ValueKind.Number, value, null, unit);
        public static Value FromBool(bool value) => value ? True : False;
        public static Value FromText(string text) => new Value(ValueKind.Text, Num.Zero, text ?? string.Empty);
        public static Value FromEntity(Entity? entity) => entity == null ? None : new Value(ValueKind.Entity, Num.Zero, entity);
        public static Value FromEntities(IReadOnlyList<Entity> entities) => new Value(ValueKind.List, Num.Zero, entities ?? EmptyEntities);
        public static Value FromDefinition(EntityDefinition definition) => new Value(ValueKind.Definition, Num.Zero, definition);
        public static Value FromQualified(string qualifier, string name) => new Value(ValueKind.Qualified, Num.Zero, new QualifiedName(qualifier, name));
        public static Value FromRange(Num low, Num high) => new Value(ValueKind.Range, low, high);

        public bool IsNone => Kind == ValueKind.None;

        public string? Text => _ref as string;
        public Entity? Entity => _ref as Entity;
        public EntityDefinition? Definition => _ref as EntityDefinition;
        public QualifiedName? Qualified => _ref as QualifiedName;

        /// <summary>Upper bound of a <see cref="ValueKind.Range"/>; the lower bound is <see cref="Number"/>.</summary>
        public Num RangeHigh => _ref is Num high ? high : Number;

        /// <summary>
        /// Every entity this value denotes: one for an entity, all of them for a list, none otherwise.
        /// Selectors return lists, so verbs call this rather than branching on the kind.
        /// </summary>
        public IReadOnlyList<Entity> AsEntities()
        {
            switch (Kind)
            {
                case ValueKind.Entity: return new[] { (Entity)_ref! };
                case ValueKind.List: return (IReadOnlyList<Entity>)_ref!;
                default: return EmptyEntities;
            }
        }

        /// <summary>Truthiness: nonzero numbers, non-empty text and lists, and live entities are true.</summary>
        public bool AsBool()
        {
            switch (Kind)
            {
                case ValueKind.Number:
                case ValueKind.Bool:
                    return !Number.IsZero;
                case ValueKind.Text:
                    return !string.IsNullOrEmpty(Text);
                case ValueKind.Entity:
                    return Entity != null && !Entity.IsRemoved;
                case ValueKind.List:
                    return AsEntities().Count > 0;
                case ValueKind.Definition:
                case ValueKind.Qualified:
                case ValueKind.Range:
                    return true;
                default:
                    return false;
            }
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case ValueKind.None: return "none";
                case ValueKind.Number: return Number + (Unit ?? string.Empty);
                case ValueKind.Bool: return Number.IsZero ? "false" : "true";
                case ValueKind.Text: return "\"" + Text + "\"";
                case ValueKind.Entity: return Entity!.ToString();
                case ValueKind.List: return "[" + string.Join(", ", AsEntities().Select(e => e.ToString())) + "]";
                case ValueKind.Definition: return Definition!.Name;
                case ValueKind.Qualified: return Qualified!.ToString();
                case ValueKind.Range: return Number + ".." + RangeHigh;
                default: return Kind.ToString();
            }
        }
    }

    /// <summary>A <c>qualifier:name</c> pair such as <c>tag:fire</c> or <c>source:self</c>.</summary>
    public sealed class QualifiedName
    {
        public QualifiedName(string qualifier, string name)
        {
            Qualifier = qualifier;
            Name = name;
        }

        public string Qualifier { get; }
        public string Name { get; }

        public override string ToString() => Qualifier + ":" + Name;
    }

    /// <summary>Raised for errors in content at runtime, carrying the offending source location.</summary>
    public sealed class RuntimeError : Exception
    {
        public RuntimeError(string message, Diagnostics.SourceSpan span)
            : base(span.IsNone ? message : $"{span}: {message}")
        {
            Span = span;
            Detail = message;
        }

        public Diagnostics.SourceSpan Span { get; }

        public string Detail { get; }
    }
}
