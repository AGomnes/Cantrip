using System;
using System.Collections.Generic;
using System.Text;
using Cantrip.Descriptions;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// One run of a description: either words, or a number the rules produced.
    /// </summary>
    /// <remarks>
    /// The printed value and the current one are both kept, which is the whole reason descriptions
    /// are segmented rather than rendered to a string. A card that reads "deal 6" while a relic
    /// makes it deal 9 has to show the 9, and a player who cannot see that something changed it
    /// will think the relic does nothing.
    /// </remarks>
    public sealed class SegmentView
    {
        private SegmentView(
            string kind,
            string text,
            string placeholder,
            bool hasNumber,
            double baseValue,
            double current,
            string baseText,
            bool changed,
            string trend,
            bool lowerIsBetter)
        {
            Kind = kind;
            Text = text;
            Placeholder = placeholder;
            HasNumber = hasNumber;
            Base = baseValue;
            Current = current;
            BaseText = baseText;
            Changed = changed;
            Trend = trend;
            LowerIsBetter = lowerIsBetter;
        }

        /// <summary>"text" or "value".</summary>
        public string Kind { get; }

        /// <summary>What to show: the words, or the current value already formatted.</summary>
        public string Text { get; }

        /// <summary>The effect value this is linked to (<c>damage</c>, <c>cost</c>...), or empty.</summary>
        public string Placeholder { get; }

        /// <summary>False for symbolic values such as <c>X</c>, which have only their text.</summary>
        public bool HasNumber { get; }

        /// <summary>The printed value, before any modifier.</summary>
        public double Base { get; }

        /// <summary>The value the rules would use now.</summary>
        public double Current { get; }

        /// <summary>The printed value formatted, for a "~~6~~ 9" display.</summary>
        public string BaseText { get; }

        public bool Changed { get; }

        /// <summary>"unchanged", "buffed" or "debuffed", from the player's point of view.</summary>
        public string Trend { get; }

        /// <summary>True for costs, where a smaller number is the good direction.</summary>
        public bool LowerIsBetter { get; }

        /// <param name="forOpponent">
        /// True when the effect belongs to the other side, as an enemy's intent does. The rules
        /// compute a trend for whoever owns the effect, so an enemy hitting harder is "buffed" to
        /// the enemy; a player reading their intent panel needs to see that as worse for them.
        /// </param>
        public static SegmentView Of(DescriptionSegment segment, bool forOpponent = false)
        {
            if (segment == null) throw new ArgumentNullException(nameof(segment));

            return new SegmentView(
                segment.Kind == SegmentKind.Value ? "value" : "text",
                segment.Text,
                segment.Placeholder ?? string.Empty,
                segment.HasNumber,
                segment.Base.ToDouble(),
                segment.Current.ToDouble(),
                segment.BaseText,
                segment.IsChanged,
                DescriptionView.TrendName(segment.Trend, forOpponent),
                segment.LowerIsBetter);
        }

        public override string ToString() => Text;
    }

    /// <summary>A keyword the text leans on, explained in its own right.</summary>
    public sealed class TooltipView
    {
        private TooltipView(string name, string plain, string bbcode)
        {
            Name = name;
            Plain = plain;
            BBCode = bbcode;
        }

        public string Name { get; }

        public string Plain { get; }

        public string BBCode { get; }

        public static TooltipView Of(KeywordTooltip tooltip)
        {
            if (tooltip == null) throw new ArgumentNullException(nameof(tooltip));

            return new TooltipView(
                tooltip.Name,
                tooltip.Description.ToPlainText(),
                DescriptionView.ToBBCode(tooltip.Description.Segments));
        }

        public override string ToString() => Name + ": " + Plain;
    }

    /// <summary>
    /// A description ready for a label: the plain text, the same text as BBCode, the segments
    /// behind it, and the keywords it relies on.
    /// </summary>
    /// <remarks>
    /// The BBCode is generated here, in the engine-free layer, because it is only string building
    /// and this is where it can be tested. A game that wants its own colours reads
    /// <see cref="Segments"/> instead and renders them itself.
    /// </remarks>
    public sealed class DescriptionView
    {
        /// <summary>Green for better than printed, red for worse: the usual card-game convention.</summary>
        public const string BuffedColour = "#6fcf6f";

        public const string DebuffedColour = "#e06c6c";

        private static readonly IReadOnlyList<SegmentView> NoSegments = new SegmentView[0];
        private static readonly IReadOnlyList<TooltipView> NoTooltips = new TooltipView[0];

        private DescriptionView(
            string name,
            string level,
            string plain,
            string bbcode,
            string flavour,
            SegmentView? cost,
            IReadOnlyList<SegmentView> segments,
            IReadOnlyList<TooltipView> tooltips)
        {
            Name = name;
            Level = level;
            Plain = plain;
            BBCode = bbcode;
            Flavour = flavour;
            Cost = cost;
            Segments = segments;
            Tooltips = tooltips;
        }

        public string Name { get; }

        /// <summary>"auto", "custom" or "override": where the words came from.</summary>
        public string Level { get; }

        public string Plain { get; }

        /// <summary>The text with changed values struck through and coloured, for a RichTextLabel.</summary>
        public string BBCode { get; }

        /// <summary>The flavour line, never mixed into the rules text. Empty when there is none.</summary>
        public string Flavour { get; }

        /// <summary>The card's cost as a live value, or null when the entity has no cost.</summary>
        public SegmentView? Cost { get; }

        public IReadOnlyList<SegmentView> Segments { get; }

        public IReadOnlyList<TooltipView> Tooltips { get; }

        /// <summary>True when there is nothing to show, as for an intent before it has been rolled.</summary>
        public bool IsEmpty => Plain.Length == 0;

        /// <param name="forOpponent">
        /// True for an effect the other side owns, such as an enemy's intent: the buffed and
        /// debuffed senses are inverted, because a bigger number coming at you is not an improvement.
        /// </param>
        public static DescriptionView Of(Description description, bool forOpponent = false)
        {
            if (description == null) throw new ArgumentNullException(nameof(description));

            IReadOnlyList<DescriptionSegment> source = description.Segments;
            var segments = new List<SegmentView>(source.Count);
            for (int i = 0; i < source.Count; i++) segments.Add(SegmentView.Of(source[i], forOpponent));

            IReadOnlyList<KeywordTooltip> keywords = description.Tooltips;
            List<TooltipView>? tooltips = null;
            for (int i = 0; i < keywords.Count; i++)
            {
                tooltips ??= new List<TooltipView>(keywords.Count);
                tooltips.Add(TooltipView.Of(keywords[i]));
            }

            return new DescriptionView(
                description.Name,
                description.Level.ToString().ToLowerInvariant(),
                description.ToPlainText(),
                ToBBCode(source, forOpponent),
                description.Flavour ?? string.Empty,
                description.Cost == null ? null : SegmentView.Of(description.Cost, forOpponent),
                segments.Count == 0 ? NoSegments : segments,
                tooltips ?? NoTooltips);
        }

        /// <summary>
        /// Renders segments as BBCode: a changed value is shown against the printed one, so a
        /// player can see both what the card says and what it will actually do.
        /// </summary>
        internal static string ToBBCode(IReadOnlyList<DescriptionSegment> segments, bool forOpponent = false)
        {
            if (segments == null || segments.Count == 0) return string.Empty;

            var text = new StringBuilder();
            for (int i = 0; i < segments.Count; i++)
            {
                DescriptionSegment segment = segments[i];
                if (!segment.IsChanged)
                {
                    text.Append(Escape(segment.Text));
                    continue;
                }

                string colour = TrendName(segment.Trend, forOpponent) == "debuffed" ? DebuffedColour : BuffedColour;
                text.Append("[s]").Append(Escape(segment.BaseText)).Append("[/s] ");
                text.Append("[color=").Append(colour).Append(']').Append(Escape(segment.Text)).Append("[/color]");
            }
            return text.ToString();
        }

        /// <summary>
        /// The trend as the reader experiences it. The rules answer for whoever owns the effect,
        /// which is right for a card in your hand and exactly backwards for an enemy's intent.
        /// </summary>
        internal static string TrendName(ValueTrend trend, bool forOpponent)
        {
            if (trend == ValueTrend.Unchanged) return "unchanged";

            bool better = trend == ValueTrend.Buffed;
            if (forOpponent) better = !better;
            return better ? "buffed" : "debuffed";
        }

        /// <summary>
        /// Hides a literal bracket from the BBCode parser. Content is written by designers, and a
        /// card whose flavour text mentions "[see the manual]" must not silently lose it.
        /// </summary>
        private static string Escape(string text) =>
            text.IndexOf('[') < 0 ? text : text.Replace("[", "[lb]");

        public override string ToString() => Plain;
    }
}
