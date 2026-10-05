using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cantrip.Content;

namespace Cantrip.Descriptions
{
    /// <summary>Which of the three description levels (automatic, custom, override) produced the text.</summary>
    public enum DescriptionLevel
    {
        /// <summary>Generated from the effect tree, listeners and modifiers. Nothing to write.</summary>
        Auto,

        /// <summary>The writer's <c>text:</c>, with <c>{placeholders}</c> still linked to the effect.</summary>
        Custom,

        /// <summary><c>text_override:</c>, shown verbatim with no live values.</summary>
        Override,
    }

    /// <summary>
    /// Whether a run of a description is fixed words or a number the rules worked out. It is the
    /// distinction a UI needs to colour one and not the other.
    /// </summary>
    public enum SegmentKind
    {
        /// <summary>Words, punctuation and anything else that never changes.</summary>
        Text,

        /// <summary>A number (or a stand-in such as <c>X</c>) that comes from the rules.</summary>
        Value,
    }

    /// <summary>How the modifier pipeline moved a value, from the point of view of whoever uses the entity.</summary>
    public enum ValueTrend
    {
        /// <summary>No modifier touched this value, so the printed number is the real one.</summary>
        Unchanged,

        /// <summary>Better than printed: more damage, or a lower cost.</summary>
        Buffed,

        /// <summary>Worse than printed: less damage, or a higher cost.</summary>
        Debuffed,
    }

    /// <summary>
    /// One run of a description. Values are kept apart from the words around them so a UI can
    /// colour buffed numbers, show a breakdown on hover, or strike through the printed value.
    /// </summary>
    public sealed class DescriptionSegment
    {
        private DescriptionSegment(
            SegmentKind kind,
            string text,
            string? placeholder,
            bool hasNumber,
            Num baseValue,
            Num current,
            string? unit,
            bool lowerIsBetter)
        {
            Kind = kind;
            Text = text;
            Placeholder = placeholder;
            HasNumber = hasNumber;
            Base = baseValue;
            Current = current;
            Unit = unit;
            LowerIsBetter = lowerIsBetter;
        }

        /// <summary>
        /// A run of fixed words. Games build these when they assemble text of their own around a
        /// description; everything inside a description is built by the description builder.
        /// </summary>
        public static DescriptionSegment Plain(string text) =>
            new DescriptionSegment(SegmentKind.Text, text ?? string.Empty, null, false, Num.Zero, Num.Zero, null, false);

        /// <summary>A computed value. <paramref name="baseValue"/> is what it is before any modifier applies.</summary>
        public static DescriptionSegment Number(Num baseValue, Num current, string? placeholder = null, string? unit = null, bool lowerIsBetter = false) =>
            new DescriptionSegment(SegmentKind.Value, Format(current, unit), placeholder, true, baseValue, current, unit, lowerIsBetter);

        /// <summary>
        /// A value that cannot be known until the effect runs, such as the stacks of a status that
        /// is described on its own or the energy spent on an X-cost card. Shown as its phrase.
        /// </summary>
        public static DescriptionSegment Symbol(string text, string? placeholder = null) =>
            new DescriptionSegment(SegmentKind.Value, text ?? string.Empty, placeholder, false, Num.Zero, Num.Zero, null, false);

        /// <summary>
        /// Whether this run is words or a value. Note that a value is not always a number: see
        /// <see cref="HasNumber"/>, which is false for symbolic ones such as <c>X</c>.
        /// </summary>
        public SegmentKind Kind { get; }

        /// <summary>What is shown: the words, or the current value formatted for display.</summary>
        public string Text { get; }

        /// <summary>The placeholder this value is linked to (<c>damage</c>, <c>Poison</c>...), if any.</summary>
        public string? Placeholder { get; }

        /// <summary>
        /// Whether this value is tied to a named part of the effect, and can therefore be looked up with
        /// <see cref="Description.Find"/>. Automatic text links everything it generates; a writer's
        /// <c>text:</c> links whatever its <c>{placeholders}</c> name.
        /// </summary>
        public bool IsLinked => Placeholder != null;

        /// <summary>False for symbolic values such as <c>X</c>, which carry only their <see cref="Text"/>.</summary>
        public bool HasNumber { get; }

        /// <summary>The value before the modifier pipeline: the printed number.</summary>
        public Num Base { get; }

        /// <summary>The value after every active modifier, exactly as the rules would use it now.</summary>
        public Num Current { get; }

        /// <summary>The unit written in content, such as <c>%</c>. Only <c>%</c> is shown.</summary>
        public string? Unit { get; }

        /// <summary>True for costs, where a smaller number is the good direction.</summary>
        public bool LowerIsBetter { get; }

        /// <summary>
        /// Whether a modifier has moved this number away from the printed one, which is what
        /// <see cref="Description.ToMarkup"/> strikes through. Always false for a symbolic value, which
        /// has no number to compare.
        /// </summary>
        public bool IsChanged => HasNumber && Base != Current;

        /// <summary>
        /// Which way the change went for whoever holds the entity, with costs already accounted for: a
        /// cost that went down is <see cref="ValueTrend.Buffed"/>, not decreased. Colour from this
        /// rather than from comparing <see cref="Current"/> with <see cref="Base"/>, which gets a cost
        /// backwards.
        /// </summary>
        public ValueTrend Trend
        {
            get
            {
                if (!IsChanged) return ValueTrend.Unchanged;
                bool higher = Current > Base;
                return higher != LowerIsBetter ? ValueTrend.Buffed : ValueTrend.Debuffed;
            }
        }

        /// <summary>The printed value, for "~~6~~ 9" style displays.</summary>
        public string BaseText => HasNumber ? Format(Base, Unit) : Text;

        /// <summary>What is shown, which for a value is the current number and not the printed one.</summary>
        public override string ToString() => Text;

        internal static string Format(Num value, string? unit) =>
            value.ToString() + (unit == "%" ? "%" : string.Empty);
    }

    /// <summary>A generated explanation of a status or keyword the described entity refers to.</summary>
    public sealed class KeywordTooltip
    {
        internal KeywordTooltip(EntityDefinition definition, Description description)
        {
            Definition = definition;
            Description = description;
        }

        /// <summary>The keyword or status this explains, so a UI can key an icon or a lookup off it.</summary>
        public EntityDefinition Definition { get; }

        /// <summary>The keyword's display name, localized. This is what the text being explained says.</summary>
        public string Name => Description.Name;

        /// <summary>
        /// The keyword's own description. Its <see cref="Descriptions.Description.Tooltips"/> is empty:
        /// keywords it mentions in turn are flattened into the top-level tooltip list instead, so a
        /// UI shows each keyword once however deeply they reference each other.
        /// </summary>
        public Description Description { get; }

        /// <summary>The tooltip on one line, for a log or a test. A UI wants <see cref="Description"/>, which keeps the values apart.</summary>
        public override string ToString() => Name + ": " + Description.ToPlainText();
    }

    /// <summary>
    /// The rules text of one entity: segments of plain text and linked values, the
    /// flavour line kept apart, and tooltips for every keyword the text relies on.
    /// </summary>
    public sealed class Description
    {
        internal Description(
            string name,
            EntityDefinition? definition,
            DescriptionLevel level,
            IReadOnlyList<DescriptionSegment> segments,
            string? flavour,
            IReadOnlyList<KeywordTooltip> tooltips,
            DescriptionSegment? cost,
            string? against = null)
        {
            Against = against;
            Name = name;
            Definition = definition;
            Level = level;
            Segments = segments;
            Flavour = flavour;
            Tooltips = tooltips;
            Cost = cost;
        }

        /// <summary>Display name, localized when the localizer supplies one.</summary>
        public string Name { get; }

        /// <summary>
        /// Who this is aimed at, by name, for a description that has a target of its own: the member
        /// an enemy is telegraphing against. Null for everything else, including an intent that has
        /// not been rolled.
        /// </summary>
        /// <remarks>
        /// It is a name rather than an entity because a description is data a UI keeps across a
        /// frame, and an entity is not. A game that wants the id asks the runtime.
        /// </remarks>
        public string? Against { get; }

        /// <summary>
        /// What this describes, or null for a description built from something other than a definition,
        /// such as an enemy's rolled intent.
        /// </summary>
        public EntityDefinition? Definition { get; }

        /// <summary>
        /// Which of the three levels produced this text. Worth checking before offering a designer's
        /// tooling: at <see cref="DescriptionLevel.Override"/> the values are not live, so nothing in
        /// <see cref="Values"/> will change however the game goes.
        /// </summary>
        public DescriptionLevel Level { get; }

        /// <summary>
        /// The text in order, split so that values can be drawn differently from the words around them.
        /// Concatenating their <see cref="DescriptionSegment.Text"/> is <see cref="ToPlainText"/>.
        /// </summary>
        public IReadOnlyList<DescriptionSegment> Segments { get; }

        /// <summary>The flavour line. Never mixed into the rules text.</summary>
        public string? Flavour { get; }

        /// <summary>
        /// One entry for every keyword or status the text relies on, flattened: a keyword that mentions
        /// another appears once here rather than nested, so a UI can show them as a flat list without
        /// walking a tree or guarding against a cycle.
        /// </summary>
        public IReadOnlyList<KeywordTooltip> Tooltips { get; }

        /// <summary>
        /// The card's cost as a live value, for the corner of the card frame, or null when the
        /// entity has no cost. It is not part of <see cref="Segments"/>.
        /// </summary>
        public DescriptionSegment? Cost { get; }

        /// <summary>
        /// Just the values, in order: the numbers a tooltip or a comparison view wants without the
        /// prose. Evaluated on each enumeration.
        /// </summary>
        public IEnumerable<DescriptionSegment> Values => Segments.Where(s => s.Kind == SegmentKind.Value);

        /// <summary>
        /// Whether there is anything to show. True for a definition with no effect, no listeners and no
        /// modifiers (a vanilla card), so a UI can leave the rules box out rather than draw an empty one.
        /// </summary>
        public bool IsEmpty => Segments.All(s => s.Text.Length == 0);

        /// <summary>The first value in the text linked to <paramref name="placeholder"/>, if any.</summary>
        public DescriptionSegment? Find(string placeholder) =>
            Segments.FirstOrDefault(s => s.Placeholder != null && string.Equals(s.Placeholder, placeholder, StringComparison.OrdinalIgnoreCase));

        /// <summary>The text with current values, for logs, tests and plain labels.</summary>
        public string ToPlainText()
        {
            var text = new StringBuilder();
            foreach (DescriptionSegment segment in Segments) text.Append(segment.Text);
            return text.ToString();
        }

        /// <summary>
        /// The text with changed values shown against their printed value, as in "~~6~~ 9".
        /// Unchanged values print once.
        /// </summary>
        public string ToMarkup()
        {
            var text = new StringBuilder();
            foreach (DescriptionSegment segment in Segments)
            {
                if (segment.IsChanged) text.Append("~~").Append(segment.BaseText).Append("~~ ");
                text.Append(segment.Text);
            }
            return text.ToString();
        }

        /// <summary>
        /// The whole thing on one line, as an intent panel shows it: "Cutthroat -> Vestal: Deal 8
        /// damage and apply 2 Bleeding." The name and the target are left out when there are none,
        /// so a description with neither is just its text.
        /// </summary>
        public string ToLine()
        {
            string body = ToPlainText();
            var text = new StringBuilder(Name);

            if (!string.IsNullOrEmpty(Against)) text.Append(" -> ").Append(Against);
            if (body.Length > 0) text.Append(text.Length > 0 ? ": " : string.Empty).Append(body);
            return text.ToString();
        }

        /// <summary>The same as <see cref="ToPlainText"/>: the current values, no markup, no name and no target.</summary>
        public override string ToString() => ToPlainText();
    }
}
