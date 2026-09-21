using System;
using System.Collections.Generic;

namespace Cantrip
{
    /// <summary>
    /// Deterministic random number generator (xoshiro256** seeded through splitmix64).
    /// </summary>
    /// <remarks>
    /// <see cref="System.Random"/> is explicitly not usable here: its algorithm is not
    /// guaranteed stable across .NET versions, so a replay recorded on one build could diverge
    /// on the next. This implementation is pure integer arithmetic with a small, fully
    /// serializable state, so <c>seed + input log</c> reproduces any bug exactly.
    /// </remarks>
    public sealed class Rng
    {
        private ulong _s0, _s1, _s2, _s3;

        public Rng(ulong seed) => Reseed(seed);

        /// <summary>Restores a generator from a previously captured <see cref="GetState"/>.</summary>
        public Rng(ulong s0, ulong s1, ulong s2, ulong s3)
        {
            _s0 = s0; _s1 = s1; _s2 = s2; _s3 = s3;
            if ((s0 | s1 | s2 | s3) == 0) Reseed(0);
        }

        public ulong Seed { get; private set; }

        public void Reseed(ulong seed)
        {
            Seed = seed;
            ulong x = seed;
            _s0 = SplitMix64(ref x);
            _s1 = SplitMix64(ref x);
            _s2 = SplitMix64(ref x);
            _s3 = SplitMix64(ref x);
            if ((_s0 | _s1 | _s2 | _s3) == 0) _s0 = 0x9E3779B97F4A7C15UL;
        }

        /// <summary>Snapshot of the full generator state, for save games and replays.</summary>
        public (ulong S0, ulong S1, ulong S2, ulong S3) GetState() => (_s0, _s1, _s2, _s3);

        public void SetState((ulong S0, ulong S1, ulong S2, ulong S3) state)
        {
            _s0 = state.S0; _s1 = state.S1; _s2 = state.S2; _s3 = state.S3;
        }

        /// <summary>
        /// Creates an independent stream derived from this generator, so that (for example)
        /// card shuffles and enemy AI rolls cannot perturb each other's sequences.
        /// </summary>
        public Rng Fork(ulong salt) => new Rng(NextUInt64() ^ salt);

        public ulong NextUInt64()
        {
            ulong result = RotateLeft(_s1 * 5UL, 7) * 9UL;
            ulong t = _s1 << 17;

            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = RotateLeft(_s3, 45);

            return result;
        }

        /// <summary>Uniform integer in <c>[minInclusive, maxInclusive]</c>.</summary>
        public int NextInt(int minInclusive, int maxInclusive)
        {
            if (maxInclusive <= minInclusive) return minInclusive;
            ulong range = (ulong)((long)maxInclusive - minInclusive) + 1UL;
            return minInclusive + (int)BoundedUInt64(range);
        }

        /// <summary>Uniform value in <c>[min, max]</c>, inclusive of both ends.</summary>
        public Num NextNum(Num min, Num max)
        {
            if (max <= min) return min;
            ulong range = (ulong)(max.Raw - min.Raw) + 1UL;
            return Num.FromRaw(min.Raw + (long)BoundedUInt64(range));
        }

        /// <summary>Rolls against a percentage, so <c>Chance(30)</c> is true 30% of the time.</summary>
        public bool Chance(Num percent)
        {
            if (percent.Raw <= 0) return false;
            if (percent >= Num.FromInt(100)) return true;
            // Compare against a uniform draw in [0, 100) at full fixed-point resolution.
            long threshold = percent.Raw;
            long roll = (long)BoundedUInt64((ulong)(100L * Num.Scale));
            return roll < threshold;
        }

        /// <summary>In-place Fisher-Yates. Deterministic for a given state and list order.</summary>
        public void Shuffle<T>(IList<T> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = (int)BoundedUInt64((ulong)(i + 1));
                if (i == j) continue;
                T swap = items[i];
                items[i] = items[j];
                items[j] = swap;
            }
        }

        public T Pick<T>(IReadOnlyList<T> items)
        {
            if (items == null || items.Count == 0) throw new ArgumentException("Cannot pick from an empty list.", nameof(items));
            return items[(int)BoundedUInt64((ulong)items.Count)];
        }

        /// <summary>Picks an index proportionally to <paramref name="weights"/>. Returns -1 if every weight is zero.</summary>
        public int PickWeighted(IReadOnlyList<Num> weights)
        {
            if (weights == null) throw new ArgumentNullException(nameof(weights));

            long total = 0;
            for (int i = 0; i < weights.Count; i++)
            {
                if (weights[i].Raw > 0) total += weights[i].Raw;
            }
            if (total <= 0) return -1;

            long roll = (long)BoundedUInt64((ulong)total);
            for (int i = 0; i < weights.Count; i++)
            {
                long weight = weights[i].Raw;
                if (weight <= 0) continue;
                if (roll < weight) return i;
                roll -= weight;
            }
            return weights.Count - 1;
        }

        /// <summary>
        /// Uniform value in <c>[0, bound)</c> using Lemire's rejection method, so the result has
        /// no modulo bias and stays identical on every platform.
        /// </summary>
        private ulong BoundedUInt64(ulong bound)
        {
            if (bound <= 1) return 0;

            ulong threshold = (0UL - bound) % bound;
            while (true)
            {
                ulong draw = NextUInt64();
                if (draw >= threshold) return draw % bound;
            }
        }

        private static ulong SplitMix64(ref ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        private static ulong RotateLeft(ulong value, int count) => (value << count) | (value >> (64 - count));
    }
}
