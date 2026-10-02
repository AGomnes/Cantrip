using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;

namespace Cantrip.Runtime
{
    /// <summary>
    /// What a <see cref="Value"/> is holding. Worth branching on rather than guessing: several kinds
    /// carry a number and only <see cref="Number"/> is one.
    /// </summary>
    public enum ValueKind
    {
        // Saved games store these as numbers: never renumber or reuse one, only add.
        /// <summary>Nothing at all: a name that resolved to no entity, a selector that matched none. It is what <c>default(Value)</c> is.</summary>
        None = 0,

        /// <summary>A number, in <see cref="Cantrip.Num"/>, possibly with a unit such as <c>%</c> or <c>s</c>.</summary>
        Number = 1,

        /// <summary>True or false, carried as 1 or 0 in <see cref="Value.Number"/>. Not the same kind as a number, though it reads as one.</summary>
        Bool = 2,

        /// <summary>A string literal from content.</summary>
        Text = 3,

        /// <summary>One entity in the game. A selector that found exactly one still gives a <see cref="List"/>, so use <c>Value.AsEntities</c> rather than branching.</summary>
        Entity = 4,

        /// <summary>A list of entities: what every selector produces, however many it matched, including none.</summary>
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

        /// <summary>
        /// The absent value, and what <c>default(Value)</c> is. It is false, it denotes no entities, and
        /// it is what a name that resolved to nothing gives back.
        /// </summary>
        public static readonly Value None = new Value(ValueKind.None, Num.Zero, null);

        /// <summary>True. It is a <see cref="ValueKind.Bool"/>, not a number, though its payload is 1.</summary>
        public static readonly Value True = new Value(ValueKind.Bool, Num.One, null);

        /// <summary>False. Distinct from <see cref="None"/>, which is also falsey but means "nothing here" rather than "no".</summary>
        public static readonly Value False = new Value(ValueKind.Bool, Num.Zero, null);

        /// <summary>
        /// What this is holding. Every accessor below answers null or zero for the wrong kind rather
        /// than throwing, so a caller that does not check gets a quiet default.
        /// </summary>
        public ValueKind Kind { get; }

        /// <summary>The numeric payload for numbers, and 1/0 for booleans.</summary>
        public Num Number { get; }

        /// <summary>The unit a number literal was written with (<c>%</c>, <c>s</c>, <c>turns</c>...).</summary>
        public string? Unit { get; }

        /// <summary>
        /// A number, with the unit it was written with. The unit is carried, not converted: a clock is
        /// what turns <c>3s</c> into ticks, and it only does so where a duration is expected.
        /// </summary>
        public static Value FromNumber(Num value, string? unit = null) => new Value(ValueKind.Number, value, null, unit);

        /// <summary><see cref="True"/> or <see cref="False"/>.</summary>
        public static Value FromBool(bool value) => value ? True : False;

        /// <summary>A text value. Null becomes the empty string, which is falsey, rather than <see cref="None"/>.</summary>
        public static Value FromText(string text) => new Value(ValueKind.Text, Num.Zero, text ?? string.Empty);

        /// <summary>One entity, or <see cref="None"/> when it is null, so a lookup that failed needs no separate branch.</summary>
        public static Value FromEntity(Entity? entity) => entity == null ? None : new Value(ValueKind.Entity, Num.Zero, entity);

        /// <summary>
        /// A list of entities. The list is taken as it is and not copied, so a caller that goes on
        /// mutating it changes what the value denotes.
        /// </summary>
        public static Value FromEntities(IReadOnlyList<Entity> entities) => new Value(ValueKind.List, Num.Zero, entities ?? EmptyEntities);

        /// <summary>A reference to loaded content by name, as <c>Poison</c> is in <c>apply Poison 3</c>. Nothing has been made from it.</summary>
        public static Value FromDefinition(EntityDefinition definition) => new Value(ValueKind.Definition, Num.Zero, definition);

        /// <summary>A <c>qualifier:name</c> predicate such as <c>tag:fire</c>. Building one does not test anything; the interpreter does that.</summary>
        public static Value FromQualified(string qualifier, string name) => new Value(ValueKind.Qualified, Num.Zero, new QualifiedName(qualifier, name));

        /// <summary>
        /// An unrolled <c>3..6</c>: both ends, with nothing chosen yet. Whoever consumes it rolls it, so
        /// the same range in two places gives two numbers.
        /// </summary>
        public static Value FromRange(Num low, Num high) => new Value(ValueKind.Range, low, high);

        /// <summary>
        /// Whether this is the absent value. It is not "is this falsey": <see cref="False"/>, zero and
        /// the empty string are all present. <c>AsBool</c> is the other question.
        /// </summary>
        public bool IsNone => Kind == ValueKind.None;

        /// <summary>The text, or null for every other kind.</summary>
        public string? Text => _ref as string;

        /// <summary>
        /// The entity, or null. It is null for a <see cref="ValueKind.List"/> of exactly one too, which is
        /// what most selectors give. <c>AsEntities</c> covers both.
        /// </summary>
        public Entity? Entity => _ref as Entity;

        /// <summary>The definition this names, or null for every other kind.</summary>
        public EntityDefinition? Definition => _ref as EntityDefinition;

        /// <summary>The <c>qualifier:name</c> pair, or null for every other kind.</summary>
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

        /// <summary>
        /// A readable form for traces and test failures: <c>none</c>, <c>true</c>, a quoted string,
        /// <c>Name#id</c> for an entity, a bracketed list. It is for people, not for parsing.
        /// </summary>
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
        /// <summary>Builds a <c>qualifier:name</c> pair. Neither half is validated: the interpreter decides what a qualifier means when it tests one.</summary>
        public QualifiedName(string qualifier, string name)
        {
            Qualifier = qualifier;
            Name = name;
        }

        /// <summary>The part before the colon (<c>tag</c>, <c>source</c>, <c>card</c>), which says what kind of test this is.</summary>
        public string Qualifier { get; }

        /// <summary>The part after the colon: what the qualifier is being tested against.</summary>
        public string Name { get; }

        /// <summary>The form content writes: <c>tag:fire</c>.</summary>
        public override string ToString() => Qualifier + ":" + Name;
    }

    /// <summary>Raised for errors in content at runtime, carrying the offending source location.</summary>
    public sealed class RuntimeError : Exception
    {
        /// <summary>
        /// An error in content, caught while it runs. It is thrown at whoever drove the action, so a
        /// game that runs content from a UI handler catches it there; <c>CardRuntime</c> does not
        /// swallow it.
        /// </summary>
        /// <param name="message">What went wrong, without the location.</param>
        /// <param name="span">The line of content, which is prefixed onto <see cref="Exception.Message"/> when there is one.</param>
        public RuntimeError(string message, Diagnostics.SourceSpan span)
            : base(span.IsNone ? message : $"{span}: {message}")
        {
            Span = span;
            Detail = message;
        }

        /// <summary>The line of content behind it, so a tool can jump there. <see cref="Diagnostics.SourceSpan.None"/> when the engine raised it outside any line.</summary>
        public Diagnostics.SourceSpan Span { get; }

        /// <summary>The message without the location in front of it, for a UI that shows the two separately.</summary>
        public string Detail { get; }
    }
}
