using System;
using System.Globalization;

namespace Cantrip
{
    /// <summary>
    /// Fixed-point number used for every value the rules engine computes.
    /// </summary>
    /// <remarks>
    /// The core never uses <see cref="float"/> or <see cref="double"/>. Replays, tests, saves and
    /// headless balance runs rely on the same content, seed and inputs producing the same game on
    /// every machine, within one version of Cantrip, and IEEE floating point does not promise that
    /// once a JIT is allowed to contract or reorder operations.
    /// <para>
    /// Values are stored as a <see cref="long"/> scaled by <see cref="Scale"/> (one millionth),
    /// which makes percentages and the usual 0.5 / 1.5 / 0.25 multipliers exact. Multiplication
    /// and division are written to avoid 64-bit overflow for any value in the +/- 1,000,000
    /// range, which is far beyond what game numbers need.
    /// </para>
    /// </remarks>
    public readonly struct Num : IEquatable<Num>, IComparable<Num>, IFormattable
    {
        /// <summary>Number of raw units in 1.0.</summary>
        public const long Scale = 1_000_000L;

        /// <summary>The raw scaled representation. Serialize this, not the decimal form.</summary>
        public long Raw { get; }

        private Num(long raw) => Raw = raw;

        /// <summary>
        /// Zero, and what <c>default(Num)</c> is: a field or array element that has never been assigned
        /// already holds this, so nothing has to initialise a <see cref="Num"/> to be valid.
        /// </summary>
        public static readonly Num Zero = new Num(0);

        /// <summary>
        /// 1.0. Worth naming because a multiplier that should leave a value alone is this and not
        /// <c>Num.FromInt(1)</c> spelled out at every call site; the two are the same value.
        /// </summary>
        public static readonly Num One = new Num(Scale);

        /// <summary>
        /// The most negative value this type carries. It is half of what a <see cref="long"/> would
        /// hold, which is deliberate: see <see cref="MaxValue"/>.
        /// </summary>
        public static readonly Num MinValue = new Num(long.MinValue / 2);

        /// <summary>
        /// The largest value this type carries: half of what its <see cref="long"/> could hold, so that
        /// adding or subtracting any two values in range cannot overflow the underlying integer.
        /// </summary>
        /// <remarks>
        /// It is not a limit game numbers meet: this is roughly 4.6 trillion, and damage is a
        /// two-digit number. It is the headroom that lets <c>+</c> and <c>-</c> stay unchecked, which is
        /// what keeps arithmetic identical on every platform rather than throwing on one and not another.
        /// </remarks>
        public static readonly Num MaxValue = new Num(long.MaxValue / 2);

        /// <summary>
        /// Rebuilds a value from its <see cref="Raw"/> form, for loading a save. It does not scale:
        /// <c>FromRaw(3)</c> is three millionths, and the whole number 3 is <see cref="FromInt"/> or the
        /// implicit conversion from <see cref="int"/>.
        /// </summary>
        public static Num FromRaw(long raw) => new Num(raw);

        /// <summary>
        /// A whole number as a <see cref="Num"/>. The parameter is a <see cref="long"/> for the
        /// convenience of callers that already hold one, such as the clock, but the value must fit
        /// this type: anything past <see cref="MaxValue"/> throws rather than wrapping round.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="value"/> is outside the range a <see cref="Num"/> can hold, which every
        /// <see cref="int"/> is inside.
        /// </exception>
        public static Num FromInt(long value)
        {
            const long Limit = long.MaxValue / 2 / Scale;
            if (value > Limit || value < -Limit)
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"A Num holds whole numbers from {-Limit} to {Limit}; anything further would wrap round to a wrong value.");

            return new Num(value * Scale);
        }

        /// <summary>Builds a value from a percentage, so <c>Percent(40)</c> is 0.4.</summary>
        public static Num Percent(Num percent) => percent / FromInt(100);

        /// <summary>
        /// Exactly zero. It is an integer comparison, not a tolerance, so unlike a <see cref="double"/>
        /// this can be trusted after arithmetic: a value that should have cancelled out has.
        /// </summary>
        public bool IsZero => Raw == 0;

        /// <summary>
        /// Strictly below zero. Zero itself is neither negative nor positive, which matters for the
        /// usual "did this heal do anything" check: use <see cref="IsZero"/> for that.
        /// </summary>
        public bool IsNegative => Raw < 0;

        /// <summary>
        /// Whole numbers convert on their own, so <c>entity.SetBase("hp", 40)</c> compiles. A
        /// <see cref="long"/> does not, and a <see cref="double"/> never will: both would have to
        /// decide what to do with a value this type cannot hold, and silently doing something is what
        /// fixed point exists to avoid. Use <see cref="FromInt"/> and <see cref="Parse"/> for those.
        /// </summary>
        public static implicit operator Num(int value) => FromInt(value);

        /// <summary>
        /// Addition. Unchecked, which is safe for anything in range because <see cref="MaxValue"/>
        /// leaves a bit of headroom for exactly this; two values near the limit will wrap rather than
        /// throw, and no game number comes near it.
        /// </summary>
        public static Num operator +(Num a, Num b) => new Num(a.Raw + b.Raw);

        /// <summary>Subtraction, unchecked on the same terms as addition.</summary>
        public static Num operator -(Num a, Num b) => new Num(a.Raw - b.Raw);

        /// <summary>Negation. Exact and always in range, since <see cref="MinValue"/> is the negative of <see cref="MaxValue"/>.</summary>
        public static Num operator -(Num a) => new Num(-a.Raw);

        /// <summary>
        /// Multiplication, truncated towards zero at the sixth decimal place rather than rounded. That
        /// is what makes <c>x50%</c> on an odd number land where a designer expects and land there on
        /// every machine; a game that wants the other half of the point rounds the result itself with
        /// <see cref="Round"/> or <see cref="Ceiling"/>.
        /// </summary>
        public static Num operator *(Num a, Num b)
        {
            // Split both operands into integer and fractional halves so that no intermediate
            // product can overflow: (ai + af/S) * (bi + bf/S) scaled back up by S is
            // ai*bi*S + ai*bf + af*bi + af*bf/S.
            long sign = 1;
            long ar = a.Raw, br = b.Raw;
            if (ar < 0) { ar = -ar; sign = -sign; }
            if (br < 0) { br = -br; sign = -sign; }

            long ai = ar / Scale, af = ar % Scale;
            long bi = br / Scale, bf = br % Scale;

            long result = ai * bi * Scale
                        + ai * bf
                        + af * bi
                        + af * bf / Scale;

            return new Num(sign < 0 ? -result : result);
        }

        /// <summary>
        /// Division. Dividing by zero yields <see cref="Zero"/> rather than throwing, because a
        /// content author's typo should surface as a linter diagnostic, not as a crash in the
        /// middle of someone's battle.
        /// </summary>
        public static Num operator /(Num a, Num b)
        {
            if (b.Raw == 0) return Zero;

            long sign = 1;
            long ar = a.Raw, br = b.Raw;
            if (ar < 0) { ar = -ar; sign = -sign; }
            if (br < 0) { br = -br; sign = -sign; }

            long quotient = ar / br;
            long remainder = ar % br;
            long result = quotient * Scale + remainder * Scale / br;

            return new Num(sign < 0 ? -result : result);
        }

        /// <summary>
        /// Exact equality, with no epsilon and none needed: two values built the same way from the same
        /// inputs have the same bits on every platform. This is the whole reason the rules do not use
        /// <see cref="double"/>.
        /// </summary>
        public static bool operator ==(Num a, Num b) => a.Raw == b.Raw;

        /// <summary>The negation of equality, and exact for the same reason.</summary>
        public static bool operator !=(Num a, Num b) => a.Raw != b.Raw;
        public static bool operator <(Num a, Num b) => a.Raw < b.Raw;
        public static bool operator >(Num a, Num b) => a.Raw > b.Raw;
        public static bool operator <=(Num a, Num b) => a.Raw <= b.Raw;
        public static bool operator >=(Num a, Num b) => a.Raw >= b.Raw;

        /// <summary>
        /// The smaller of two values. Not to be confused with <see cref="MinValue"/>, which is the
        /// extreme this type can hold.
        /// </summary>
        public static Num Min(Num a, Num b) => a.Raw <= b.Raw ? a : b;
        /// <summary>
        /// The larger of two values. Not to be confused with <see cref="MaxValue"/>, which is the
        /// extreme this type can hold.
        /// </summary>
        public static Num Max(Num a, Num b) => a.Raw >= b.Raw ? a : b;

        /// <summary>Magnitude without the sign. Total and exact: there is no value whose absolute value is out of range.</summary>
        public static Num Abs(Num a) => a.Raw < 0 ? new Num(-a.Raw) : a;

        /// <summary>
        /// Holds a value between two bounds. It does not check that they are the right way round: given
        /// a <paramref name="min"/> above <paramref name="max"/> it answers <paramref name="min"/> for
        /// everything, rather than throwing, because a resource whose bounds a modifier has crossed
        /// should pin to a number and not stop the battle.
        /// </summary>
        public static Num Clamp(Num value, Num min, Num max)
        {
            if (value.Raw < min.Raw) return min;
            if (value.Raw > max.Raw) return max;
            return value;
        }

        /// <summary>Truncates towards negative infinity.</summary>
        public Num Floor()
        {
            long units = Raw / Scale;
            if (Raw < 0 && Raw % Scale != 0) units--;
            return FromInt(units);
        }

        /// <summary>Rounds towards positive infinity.</summary>
        public Num Ceiling()
        {
            long units = Raw / Scale;
            if (Raw > 0 && Raw % Scale != 0) units++;
            return FromInt(units);
        }

        /// <summary>Rounds half away from zero, which is what players expect from damage numbers.</summary>
        public Num Round()
        {
            long fraction = Raw % Scale;
            long units = Raw / Scale;
            if (fraction >= Scale / 2) units++;
            else if (fraction <= -Scale / 2) units--;
            return FromInt(units);
        }

        /// <summary>Rounds half away from zero and returns the result as an <see cref="int"/>.</summary>
        public int ToInt()
        {
            long units = Round().Raw / Scale;
            if (units > int.MaxValue) return int.MaxValue;
            if (units < int.MinValue) return int.MinValue;
            return (int)units;
        }

        /// <summary>
        /// Rounds half away from zero and returns the result as a <see cref="long"/>, for the
        /// values that are counts of clock units rather than damage.
        /// </summary>
        /// <remarks>
        /// A cast does not work and cannot be made to: <c>(long)entity.Get("ready_at")</c> is
        /// <c>CS0030</c>, because a <see cref="Num"/> is a fixed-point number and not a
        /// <see cref="long"/> in disguise. <see cref="ToInt"/> is the usual answer and
        /// <see cref="ToDouble"/> keeps the fraction; this is the one in between, for a tick count
        /// that outgrows an <see cref="int"/>.
        /// </remarks>
        public long ToLong() => Round().Raw / Scale;

        /// <summary>
        /// The value as a <see cref="double"/>, for drawing a bar or a sweep. It is a one-way door:
        /// arithmetic done on the result is no longer arithmetic the rules would agree with, so nothing
        /// that feeds back into the game should go through it. <see cref="ToInt"/> and
        /// <see cref="ToLong"/> are the ones that stay exact.
        /// </summary>
        public double ToDouble() => (double)Raw / Scale;

        /// <summary>
        /// Reads the form <see cref="ToString()"/> writes, plus a leading sign and <c>_</c> as a digit
        /// separator. False for anything else, with <paramref name="value"/> left at
        /// <see cref="Zero"/>, including for a number too large to scale, which is refused rather than
        /// wrapped round to a negative one nobody would think to look for.
        /// </summary>
        /// <remarks>
        /// Decimal digits past the sixth are dropped, not rounded and not rejected, because
        /// <see cref="Scale"/> cannot hold them: <c>0.1234567</c> parses, as <c>0.123456</c>.
        /// </remarks>
        public static bool TryParse(string text, out Num value)
        {
            value = Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;

            text = text.Trim();
            bool negative = false;
            int index = 0;
            if (text[0] == '-') { negative = true; index = 1; }
            else if (text[0] == '+') { index = 1; }
            if (index >= text.Length) return false;

            // Anything larger cannot be scaled into a long; reject it rather than wrap to a
            // negative number that a designer would never think to look for.
            const long MaxWhole = long.MaxValue / Scale;

            long whole = 0;
            bool sawDigit = false;
            for (; index < text.Length && text[index] != '.'; index++)
            {
                char c = text[index];
                if (c == '_') continue;
                if (c < '0' || c > '9') return false;
                int digit = c - '0';
                if (whole > (MaxWhole - digit) / 10) return false;
                whole = whole * 10 + digit;
                sawDigit = true;
            }

            long fraction = 0;
            long divisor = 1;
            if (index < text.Length && text[index] == '.')
            {
                index++;
                for (; index < text.Length; index++)
                {
                    char c = text[index];
                    if (c == '_') continue;
                    if (c < '0' || c > '9') return false;
                    // Digits past the sixth cannot be represented, so they are discarded.
                    if (divisor < Scale)
                    {
                        fraction = fraction * 10 + (c - '0');
                        divisor *= 10;
                    }
                    sawDigit = true;
                }
            }

            if (!sawDigit) return false;

            long scaledFraction = fraction * (Scale / divisor);
            if (whole == MaxWhole && scaledFraction > long.MaxValue - whole * Scale) return false;

            long raw = whole * Scale + scaledFraction;
            value = new Num(negative ? -raw : raw);
            return true;
        }

        /// <summary>
        /// <see cref="TryParse"/> for callers that would rather not check, such as a loader that has
        /// already validated its input.
        /// </summary>
        /// <exception cref="FormatException">The text is not a number this type can hold.</exception>
        public static Num Parse(string text) =>
            TryParse(text, out Num value) ? value : throw new FormatException($"'{text}' is not a number.");

        public int CompareTo(Num other) => Raw.CompareTo(other.Raw);
        public bool Equals(Num other) => Raw == other.Raw;
        public override bool Equals(object? obj) => obj is Num other && Equals(other);
        public override int GetHashCode() => Raw.GetHashCode();

        /// <summary>
        /// The value in invariant culture, with trailing zeros trimmed, so <c>1.5</c> prints as
        /// <c>1.5</c> and not <c>1.500000</c>. Always invariant, whatever the thread is set to: this
        /// form round-trips through <see cref="Parse"/> and ends up in traces, saves and test failures,
        /// where a comma for a decimal point would be a bug.
        /// </summary>
        public override string ToString() => ToString(null, CultureInfo.InvariantCulture);

        /// <summary>
        /// The <see cref="IFormattable"/> form. With no format, or <c>"G"</c>, it is the invariant,
        /// trailing-zero-trimmed text <see cref="ToString()"/> writes and <see cref="Parse"/> reads
        /// back. Any other standard or custom numeric format string is honoured, so
        /// <c>$"{damage:F2}"</c> gives <c>5.00</c> and <c>$"{chance:P0}"</c> gives a percentage.
        /// </summary>
        /// <remarks>
        /// A format is applied to the exact value as a <see cref="decimal"/>, never through
        /// <see cref="ToDouble"/>: six decimal places in 64 bits fit a decimal exactly, so the text
        /// is the number rather than a rounding of it, and it is the same text on every machine.
        /// This is presentation only (nothing in the rules formats a number), so it is outside the
        /// determinism promise's reach either way.
        /// </remarks>
        /// <exception cref="FormatException"><paramref name="format"/> is not a valid numeric format string.</exception>
        public string ToString(string? format, IFormatProvider? formatProvider)
        {
            formatProvider ??= CultureInfo.InvariantCulture;

            // "G" is this type's own form rather than the framework's general format, because that
            // form is what Parse reads back and what a trace, a save and a test failure all hold.
            if (!string.IsNullOrEmpty(format) && format != "G" && format != "g")
                return ((decimal)Raw / Scale).ToString(format, formatProvider);

            if (Raw % Scale == 0) return (Raw / Scale).ToString(formatProvider);

            // Trim trailing zeros so 1.500000 prints as 1.5.
            long sign = Raw < 0 ? -1 : 1;
            long magnitude = Raw * sign;
            string fraction = (magnitude % Scale).ToString("D6", formatProvider).TrimEnd('0');
            string whole = (magnitude / Scale).ToString(formatProvider);
            return (sign < 0 ? "-" : string.Empty) + whole + "." + fraction;
        }
    }
}
