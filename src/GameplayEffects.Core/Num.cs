using System;
using System.Globalization;

namespace GameplayEffects
{
    /// <summary>
    /// Fixed-point number used for every value the rules engine computes.
    /// </summary>
    /// <remarks>
    /// The core never uses <see cref="float"/> or <see cref="double"/>: replays, lockstep
    /// multiplayer and headless balance runs all require that the same inputs produce
    /// bit-identical outputs on every machine, and IEEE floating point does not promise that
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

        public static readonly Num Zero = new Num(0);
        public static readonly Num One = new Num(Scale);
        public static readonly Num MinValue = new Num(long.MinValue / 2);
        public static readonly Num MaxValue = new Num(long.MaxValue / 2);

        public static Num FromRaw(long raw) => new Num(raw);

        public static Num FromInt(long value) => new Num(value * Scale);

        /// <summary>Builds a value from a percentage, so <c>Percent(40)</c> is 0.4.</summary>
        public static Num Percent(Num percent) => percent / FromInt(100);

        public bool IsZero => Raw == 0;
        public bool IsNegative => Raw < 0;

        public static implicit operator Num(int value) => FromInt(value);

        public static Num operator +(Num a, Num b) => new Num(a.Raw + b.Raw);
        public static Num operator -(Num a, Num b) => new Num(a.Raw - b.Raw);
        public static Num operator -(Num a) => new Num(-a.Raw);

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

        public static bool operator ==(Num a, Num b) => a.Raw == b.Raw;
        public static bool operator !=(Num a, Num b) => a.Raw != b.Raw;
        public static bool operator <(Num a, Num b) => a.Raw < b.Raw;
        public static bool operator >(Num a, Num b) => a.Raw > b.Raw;
        public static bool operator <=(Num a, Num b) => a.Raw <= b.Raw;
        public static bool operator >=(Num a, Num b) => a.Raw >= b.Raw;

        public static Num Min(Num a, Num b) => a.Raw <= b.Raw ? a : b;
        public static Num Max(Num a, Num b) => a.Raw >= b.Raw ? a : b;
        public static Num Abs(Num a) => a.Raw < 0 ? new Num(-a.Raw) : a;

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

        public double ToDouble() => (double)Raw / Scale;

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

        public static Num Parse(string text) =>
            TryParse(text, out Num value) ? value : throw new FormatException($"'{text}' is not a number.");

        public int CompareTo(Num other) => Raw.CompareTo(other.Raw);
        public bool Equals(Num other) => Raw == other.Raw;
        public override bool Equals(object? obj) => obj is Num other && Equals(other);
        public override int GetHashCode() => Raw.GetHashCode();

        public override string ToString() => ToString(null, CultureInfo.InvariantCulture);

        public string ToString(string? format, IFormatProvider? formatProvider)
        {
            formatProvider ??= CultureInfo.InvariantCulture;
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
