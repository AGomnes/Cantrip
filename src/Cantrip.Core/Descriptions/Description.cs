using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cantrip.Content;

namespace Cantrip.Descriptions
{
    /// <summary>Which of the three description levels from section 3.12 produced the text.</summary>
    public enum DescriptionLevel
    {
        /// <summary>Generated from the effect tree, listeners and modifiers. Nothing to write.</summary>
        Auto,

        /// <summary>The writer's <c>text:</c>, with <c>{placeholders}</c> still linked to the effect.</summary>
        Custom,

        /// <summary><c>text_override:</c>, shown verbatim with no live values.</summary>
        Override,
    }

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

        public SegmentKind Kind { get; }

        /// <summary>What is shown: the words, or the current value formatted for display.</summary>
        public string Text { get; }

        /// <summary>The placeholder this value is linked to (<c>damage</c>, <c>Poison</c>...), if any.</summary>
        public string? Placeholder { get; }

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

        public bool IsChanged => HasNumber && Base != Current;

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

        public EntityDefinition Definition { get; }

        public string Name => Description.Name;

        /// <summary>
        /// The keyword's own description. Its <see cref="Descriptions.Description.Tooltips"/> is empty:
        /// keywords it mentions in turn are flattened into the top-level tooltip list instead, so a
        /// UI shows each keyword once however deeply they reference each other.
        /// </summary>
        public Description Description { get; }

        public override string ToString() => Name + ": " + Description.ToPlainText();
    }

    /// <summary>
    /// The rules text of one entity (section 3.12): segments of plain text and linked values, the
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
            DescriptionSegment? cost)
        {
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

        public EntityDefinition? Definition { get; }

        public DescriptionLevel Level { get; }

        public IReadOnlyList<DescriptionSegment> Segments { get; }

        /// <summary>The flavour line. Never mixed into the rules text.</summary>
        public string? Flavour { get; }

        public IReadOnlyList<KeywordTooltip> Tooltips { get; }

        /// <summary>
        /// The card's cost as a live value, for the corner of the card frame, or null when the
        /// entity has no cost. It is not part of <see cref="Segments"/>.
        /// </summary>
        public DescriptionSegment? Cost { get; }

        public IEnumerable<DescriptionSegment> Values => Segments.Where(s => s.Kind == SegmentKind.Value);

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

        public override string ToString() => ToPlainText();
    }
}
