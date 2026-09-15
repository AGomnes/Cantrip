using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Xunit;

namespace GameplayEffects.Tests.Foundation
{
    /// <summary>
    /// <see cref="Num"/> is the only number type the rules engine computes with, so its rounding
    /// and overflow behaviour is effectively part of the content language: a designer's
    /// <c>x1.5</c> has to produce the same integer damage on every machine and in every replay.
    /// </summary>
    public sealed class NumTests
    {
        private static Num N(string text) => Num.Parse(text);

        // ---------------------------------------------------------------------------------------
        // Construction
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Constants_use_a_one_millionth_scale()
        {
            Assert.Equal(1_000_000L, Num.Scale);
            Assert.Equal(0L, Num.Zero.Raw);
            Assert.Equal(Num.Scale, Num.One.Raw);
        }

        [Fact]
        public void Implicit_int_conversion_scales_the_value()
        {
            Num seven = 7;
            Num minusThree = -3;

            Assert.Equal(7_000_000L, seven.Raw);
            Assert.Equal(-3_000_000L, minusThree.Raw);
            Assert.Equal(Num.FromInt(7), seven);
        }

        [Fact]
        public void FromRaw_keeps_the_raw_value_untouched()
        {
            Assert.Equal(1L, Num.FromRaw(1).Raw);
            Assert.Equal(-1_500_000L, Num.FromRaw(-1_500_000).Raw);
            Assert.Equal(N("-1.5"), Num.FromRaw(-1_500_000));
        }

        [Fact]
        public void IsZero_and_IsNegative_follow_the_raw_sign()
        {
            Assert.True(Num.Zero.IsZero);
            Assert.False(Num.FromRaw(1).IsZero);
            Assert.True(Num.FromRaw(-1).IsNegative);
            Assert.False(Num.Zero.IsNegative);
            Assert.False(Num.One.IsNegative);
        }

        // ---------------------------------------------------------------------------------------
        // Arithmetic
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("1.5", "2.25", "3.75")]
        [InlineData("-1.5", "2.25", "0.75")]
        [InlineData("-1.5", "-2.25", "-3.75")]
        [InlineData("0.000001", "0.000001", "0.000002")]
        [InlineData("1000000", "-1000000", "0")]
        public void Addition(string a, string b, string expected) =>
            Assert.Equal(N(expected), N(a) + N(b));

        [Theory]
        [InlineData("1", "2.5", "-1.5")]
        [InlineData("-1", "-1", "0")]
        [InlineData("0.5", "0.25", "0.25")]
        [InlineData("-0.5", "0.25", "-0.75")]
        public void Subtraction(string a, string b, string expected) =>
            Assert.Equal(N(expected), N(a) - N(b));

        [Fact]
        public void Negation_flips_the_sign()
        {
            Assert.Equal(N("-1.5"), -N("1.5"));
            Assert.Equal(N("1.5"), -N("-1.5"));
            Assert.Equal(Num.Zero, -Num.Zero);
        }

        [Theory]
        [InlineData("1.5", "2", "3")]
        [InlineData("-1.5", "2", "-3")]
        [InlineData("1.5", "-2", "-3")]
        [InlineData("-1.5", "-2", "3")]
        [InlineData("6", "0.75", "4.5")]
        [InlineData("6", "1.5", "9")]
        [InlineData("0.5", "0.5", "0.25")]
        [InlineData("0", "-7", "0")]
        [InlineData("1000000", "1000000", "1000000000000")]
        [InlineData("-1000000", "1000000", "-1000000000000")]
        [InlineData("-1000000", "-1000000", "1000000000000")]
        [InlineData("999999.999999", "999999.999999", "999999999998")]
        public void Multiplication(string a, string b, string expected) =>
            Assert.Equal(N(expected), N(a) * N(b));

        [Fact]
        public void Multiplication_truncates_sub_resolution_results_towards_zero()
        {
            // 0.000001 * 0.5 = 0.0000005, which is below the resolution. Both signs must truncate
            // to zero, otherwise the sign of an operand would bias rounding.
            Assert.Equal(Num.Zero, Num.FromRaw(1) * N("0.5"));
            Assert.Equal(Num.Zero, Num.FromRaw(-1) * N("0.5"));
            Assert.Equal(N("0.333333"), N("0.333333") * Num.One);
        }

        [Theory]
        [InlineData("7", "2", "3.5")]
        [InlineData("-7", "2", "-3.5")]
        [InlineData("7", "-2", "-3.5")]
        [InlineData("-7", "-2", "3.5")]
        [InlineData("1", "3", "0.333333")]
        [InlineData("-1", "3", "-0.333333")]
        [InlineData("2", "3", "0.666666")]
        [InlineData("10", "0.5", "20")]
        [InlineData("0", "5", "0")]
        [InlineData("1000000", "0.000001", "1000000000000")]
        [InlineData("-1000000", "0.000001", "-1000000000000")]
        [InlineData("1000000", "3", "333333.333333")]
        [InlineData("0.000001", "1000000", "0")]
        [InlineData("1000000", "1000000", "1")]
        public void Division(string a, string b, string expected) =>
            Assert.Equal(N(expected), N(a) / N(b));

        [Theory]
        [InlineData("5")]
        [InlineData("-5")]
        [InlineData("0")]
        [InlineData("0.000001")]
        [InlineData("1000000")]
        public void Division_by_zero_yields_zero_instead_of_throwing(string dividend) =>
            Assert.Equal(Num.Zero, N(dividend) / Num.Zero);

        [Fact]
        public void Multiplication_matches_exact_arithmetic_across_the_supported_range()
        {
            // The split-halves algorithm must agree with arbitrary-precision truncation for every
            // operand in +/- 1,000,000, including tiny fractions and mixed signs.
            var rng = new Rng(20260911);
            for (int i = 0; i < 2000; i++)
            {
                Num a = RandomOperand(rng);
                Num b = RandomOperand(rng);

                BigInteger exact = BigInteger.Divide((BigInteger)a.Raw * b.Raw, Num.Scale);
                Assert.True((long)exact == (a * b).Raw, $"{a} * {b}: expected raw {exact}, got {(a * b).Raw}");
            }
        }

        [Fact]
        public void Division_matches_exact_arithmetic_across_the_supported_range()
        {
            var rng = new Rng(19);
            for (int i = 0; i < 2000; i++)
            {
                Num a = RandomOperand(rng);
                Num b = RandomOperand(rng);
                if (b.IsZero) continue;

                BigInteger exact = BigInteger.Divide((BigInteger)a.Raw * Num.Scale, b.Raw);
                Assert.True((long)exact == (a / b).Raw, $"{a} / {b}: expected raw {exact}, got {(a / b).Raw}");
            }
        }

        [Fact]
        public void Multiplication_and_division_are_sign_symmetric()
        {
            var rng = new Rng(7);
            for (int i = 0; i < 500; i++)
            {
                Num a = RandomOperand(rng);
                Num b = RandomOperand(rng);

                Assert.Equal(-(a * b), (-a) * b);
                Assert.Equal(a * b, (-a) * (-b));
                if (!b.IsZero) Assert.Equal(-(a / b), (-a) / b);
            }
        }

        [Theory]
        [InlineData(40, "0.4")]
        [InlineData(150, "1.5")]
        [InlineData(-25, "-0.25")]
        [InlineData(0, "0")]
        [InlineData(1, "0.01")]
        [InlineData(100, "1")]
        public void Percent_divides_by_one_hundred(int percent, string expected) =>
            Assert.Equal(N(expected), Num.Percent(percent));

        [Fact]
        public void Percent_accepts_fractional_percentages()
        {
            Assert.Equal(N("0.125"), Num.Percent(N("12.5")));
            Assert.Equal(Num.FromRaw(10), Num.Percent(N("0.001")));
        }

        // ---------------------------------------------------------------------------------------
        // Rounding
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("1.5", "1")]
        [InlineData("-1.5", "-2")]
        [InlineData("2", "2")]
        [InlineData("-2", "-2")]
        [InlineData("0", "0")]
        [InlineData("0.000001", "0")]
        [InlineData("-0.000001", "-1")]
        [InlineData("0.999999", "0")]
        public void Floor_rounds_towards_negative_infinity(string value, string expected) =>
            Assert.Equal(N(expected), N(value).Floor());

        [Theory]
        [InlineData("1.5", "2")]
        [InlineData("-1.5", "-1")]
        [InlineData("2", "2")]
        [InlineData("-2", "-2")]
        [InlineData("0", "0")]
        [InlineData("0.000001", "1")]
        [InlineData("-0.000001", "0")]
        [InlineData("-0.999999", "0")]
        public void Ceiling_rounds_towards_positive_infinity(string value, string expected) =>
            Assert.Equal(N(expected), N(value).Ceiling());

        [Theory]
        [InlineData("2.5", "3")]
        [InlineData("-2.5", "-3")]
        [InlineData("0.5", "1")]
        [InlineData("-0.5", "-1")]
        [InlineData("2.499999", "2")]
        [InlineData("-2.499999", "-2")]
        [InlineData("1.4", "1")]
        [InlineData("-1.6", "-2")]
        [InlineData("3", "3")]
        [InlineData("-3", "-3")]
        [InlineData("0", "0")]
        public void Round_goes_half_away_from_zero(string value, string expected) =>
            Assert.Equal(N(expected), N(value).Round());

        [Theory]
        [InlineData("2.5", 3)]
        [InlineData("-2.5", -3)]
        [InlineData("7", 7)]
        [InlineData("0.4", 0)]
        [InlineData("-0.4", 0)]
        [InlineData("4.5", 5)]
        public void ToInt_rounds_half_away_from_zero(string value, int expected) =>
            Assert.Equal(expected, N(value).ToInt());

        [Fact]
        public void ToInt_saturates_instead_of_wrapping()
        {
            Assert.Equal(int.MaxValue, Num.FromInt(3_000_000_000L).ToInt());
            Assert.Equal(int.MinValue, Num.FromInt(-3_000_000_000L).ToInt());
            Assert.Equal(int.MaxValue, Num.FromInt(int.MaxValue).ToInt());
            Assert.Equal(int.MinValue, Num.FromInt(int.MinValue).ToInt());
            Assert.Equal(int.MaxValue, Num.MaxValue.ToInt());
            Assert.Equal(int.MinValue, Num.MinValue.ToInt());
        }

        // ---------------------------------------------------------------------------------------
        // Parsing
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("0", 0L)]
        [InlineData("42", 42_000_000L)]
        [InlineData("-42", -42_000_000L)]
        [InlineData("+2", 2_000_000L)]
        [InlineData("1.5", 1_500_000L)]
        [InlineData("-1.5", -1_500_000L)]
        [InlineData("  3  ", 3_000_000L)]
        [InlineData("\t7\n", 7_000_000L)]
        [InlineData("007", 7_000_000L)]
        [InlineData("1_000", 1_000_000_000L)]
        [InlineData("0.1_5", 150_000L)]
        [InlineData(".5", 500_000L)]
        [InlineData("-.5", -500_000L)]
        [InlineData("5.", 5_000_000L)]
        [InlineData("0.000001", 1L)]
        [InlineData("1000000", 1_000_000_000_000L)]
        [InlineData("-1000000", -1_000_000_000_000L)]
        public void TryParse_accepts_valid_numbers(string text, long expectedRaw)
        {
            Assert.True(Num.TryParse(text, out Num value));
            Assert.Equal(expectedRaw, value.Raw);
        }

        [Theory]
        [InlineData("1.2345678", 1_234_567L)]
        [InlineData("-1.9999999", -1_999_999L)]
        [InlineData("0.0000009", 0L)]
        public void TryParse_discards_digits_below_the_resolution(string text, long expectedRaw)
        {
            Assert.True(Num.TryParse(text, out Num value));
            Assert.Equal(expectedRaw, value.Raw);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("-")]
        [InlineData("+")]
        [InlineData(".")]
        [InlineData("-.")]
        [InlineData("_")]
        [InlineData("abc")]
        [InlineData("1a")]
        [InlineData("1.2.3")]
        [InlineData("1e5")]
        [InlineData("--1")]
        [InlineData("+-1")]
        [InlineData("1,5")]
        [InlineData("1 000")]
        [InlineData("0x10")]
        [InlineData("٣")]
        public void TryParse_rejects_malformed_text_and_yields_zero(string text)
        {
            Assert.False(Num.TryParse(text, out Num value));
            Assert.Equal(Num.Zero, value);
        }

        [Fact]
        public void TryParse_rejects_null()
        {
            Assert.False(Num.TryParse(null!, out Num value));
            Assert.Equal(Num.Zero, value);
        }

        [Fact]
        public void Parse_throws_a_FormatException_naming_the_text()
        {
            FormatException error = Assert.Throws<FormatException>(() => Num.Parse("abc"));
            Assert.Contains("abc", error.Message);
        }

        [Fact]
        [Trait("Regression", "num-parse-overflow")]
        public void TryParse_does_not_silently_wrap_numbers_beyond_the_representable_range()
        {
            // 10,000,000,000,000 needs a raw value of 1e19, which does not fit in a long. The
            // parser must either reject it or saturate; wrapping turns it into a large negative.
            bool ok = Num.TryParse("10000000000000", out Num value);
            Assert.True(!ok || value == Num.MaxValue, $"Parsed as {value} (raw {value.Raw}).");
        }

        // ---------------------------------------------------------------------------------------
        // Formatting
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData(0L, "0")]
        [InlineData(3_000_000L, "3")]
        [InlineData(-3_000_000L, "-3")]
        [InlineData(100_000_000L, "100")]
        [InlineData(1_500_000L, "1.5")]
        [InlineData(-1_500_000L, "-1.5")]
        [InlineData(-500_000L, "-0.5")]
        [InlineData(1L, "0.000001")]
        [InlineData(-1L, "-0.000001")]
        [InlineData(1_230_000L, "1.23")]
        [InlineData(10_050_000L, "10.05")]
        [InlineData(123_456_789L, "123.456789")]
        public void ToString_trims_trailing_zeros(long raw, string expected) =>
            Assert.Equal(expected, Num.FromRaw(raw).ToString());

        [Fact]
        public void ToString_round_trips_through_Parse()
        {
            var rng = new Rng(3);
            for (int i = 0; i < 1000; i++)
            {
                Num value = RandomOperand(rng);
                Assert.Equal(value, Num.Parse(value.ToString()));
            }
        }

        [Fact]
        public void Parsing_and_formatting_ignore_the_current_culture()
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                // German uses a decimal comma and Swedish a Unicode minus sign. Content files must
                // mean the same thing regardless of the machine they are loaded on.
                foreach (string name in new[] { "de-DE", "sv-SE", "fr-FR" })
                {
                    CultureInfo.CurrentCulture = new CultureInfo(name);

                    Assert.Equal(1_500_000L, Num.Parse("1.5").Raw);
                    Assert.Equal("1.5", N("1.5").ToString());
                    Assert.Equal("-1.5", N("-1.5").ToString());
                    Assert.Equal("-3", N("-3").ToString());
                    Assert.Equal("-1.5", $"{N("-1.5")}");
                    Assert.Equal("1000000", string.Format("{0}", N("1000000")));
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        // ---------------------------------------------------------------------------------------
        // Comparison and helpers
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Comparison_operators_follow_the_numeric_order()
        {
            Num small = N("-1.5");
            Num large = N("2");

            Assert.True(small < large);
            Assert.True(small <= large);
            Assert.True(large > small);
            Assert.True(large >= small);
            Assert.True(small <= N("-1.5"));
            Assert.True(small >= N("-1.5"));
            Assert.True(small == N("-1.5"));
            Assert.True(small != large);
            Assert.False(small == large);
            Assert.True(small.CompareTo(large) < 0);
            Assert.True(large.CompareTo(small) > 0);
            Assert.Equal(0, small.CompareTo(N("-1.5")));
        }

        [Fact]
        public void Equality_and_hash_code_agree()
        {
            Assert.True(N("1.5").Equals(Num.FromRaw(1_500_000)));
            Assert.True(N("1.5").Equals((object)Num.FromRaw(1_500_000)));
            Assert.False(N("1.5").Equals((object)1.5m));
            Assert.False(N("1.5").Equals(null));
            Assert.Equal(N("1.5").GetHashCode(), Num.FromRaw(1_500_000).GetHashCode());
        }

        [Fact]
        public void Sorting_uses_the_numeric_order()
        {
            var values = new List<Num> { N("3"), N("-1"), N("0.5"), N("-1.5"), Num.Zero };
            values.Sort();
            Assert.Equal(new[] { N("-1.5"), N("-1"), Num.Zero, N("0.5"), N("3") }, values);
        }

        [Fact]
        public void Min_Max_Abs_and_Clamp()
        {
            Assert.Equal(N("-1"), Num.Min(N("-1"), N("2")));
            Assert.Equal(N("2"), Num.Max(N("-1"), N("2")));
            Assert.Equal(N("1.5"), Num.Abs(N("-1.5")));
            Assert.Equal(N("1.5"), Num.Abs(N("1.5")));
            Assert.Equal(Num.Zero, Num.Abs(Num.Zero));

            Assert.Equal(N("0"), Num.Clamp(N("-5"), N("0"), N("10")));
            Assert.Equal(N("10"), Num.Clamp(N("15"), N("0"), N("10")));
            Assert.Equal(N("7.5"), Num.Clamp(N("7.5"), N("0"), N("10")));
        }

        /// <summary>
        /// Draws an operand in +/- 1,000,000, mixing whole numbers, small fractions and full-range
        /// raw values so that every branch of the split arithmetic is exercised.
        /// </summary>
        private static Num RandomOperand(Rng rng)
        {
            const long limit = 1_000_000L * Num.Scale;
            switch (rng.NextInt(0, 3))
            {
                case 0: return Num.FromInt(rng.NextInt(-1_000_000, 1_000_000));
                case 1: return Num.FromRaw(rng.NextInt(-2_000_000, 2_000_000));
                case 2: return Num.FromRaw(rng.NextNum(Num.FromRaw(-1000), Num.FromRaw(1000)).Raw);
                default: return Num.FromRaw(rng.NextNum(Num.FromRaw(-limit), Num.FromRaw(limit)).Raw);
            }
        }
    }
}
