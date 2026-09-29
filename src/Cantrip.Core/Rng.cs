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

        /// <summary>
        /// A generator at the start of the stream a seed names. Two generators built from the same seed
        /// give the same numbers for ever, on every machine and every build.
        /// </summary>
        public Rng(ulong seed) => Reseed(seed);

        /// <summary>Restores a generator from a previously captured <see cref="GetState"/>.</summary>
        public Rng(ulong s0, ulong s1, ulong s2, ulong s3)
        {
            _s0 = s0; _s1 = s1; _s2 = s2; _s3 = s3;
            if ((s0 | s1 | s2 | s3) == 0) Reseed(0);
        }

        /// <summary>
        /// The seed this generator was last started from — a label, not its position. It does not move
        /// as numbers are drawn and <see cref="SetState"/> does not change it, so
        /// <c>new Rng(saved.Seed)</c> rewinds to the beginning of the run rather than restoring where
        /// that generator had got to. <see cref="GetState"/> is what restores a generator.
        /// </summary>
        public ulong Seed { get; private set; }

        /// <summary>
        /// Throws away the current position and starts the stream this seed names, as the constructor
        /// does. It is for starting a new run, not for restoring one: see <see cref="SetState"/>.
        /// </summary>
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

        /// <summary>
        /// Snapshot of the full generator state, for save games and replays: four words, in the same
        /// order and shape <see cref="Cantrip.Runtime.GameSnapshot.Rng"/> writes them.
        /// </summary>
        /// <remarks>
        /// An array rather than a tuple because a tuple's arity is part of its type: a generator
        /// with a fifth word would be a breaking change to every caller, and a save already carries
        /// this state as a list of words alongside the generator's name.
        /// </remarks>
        public ulong[] GetState() => new[] { _s0, _s1, _s2, _s3 };

        /// <summary>Restores what <see cref="GetState"/> gave. Anything but four words is refused.</summary>
        public void SetState(ulong[] state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Length != StateWords)
                throw new ArgumentException($"This generator's state is {StateWords} words, not {state.Length}.", nameof(state));

            _s0 = state[0]; _s1 = state[1]; _s2 = state[2]; _s3 = state[3];
        }

        /// <summary>How many words <see cref="GetState"/> gives and <see cref="SetState"/> wants.</summary>
        public const int StateWords = 4;

        /// <summary>
        /// Creates an independent stream derived from this generator, so that (for example)
        /// card shuffles and enemy AI rolls cannot perturb each other's sequences.
        /// </summary>
        public Rng Fork(ulong salt) => new Rng(NextUInt64() ^ salt);

        /// <summary>
        /// The raw draw every other method is built on. Calling it advances the same stream the game's
        /// shuffles and rolls come out of, so a host that borrows a number here changes every later
        /// shuffle — <see cref="Fork"/> is the way to take numbers without disturbing the game.
        /// </summary>
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

        /// <summary>
        /// One item, uniformly. An empty list throws rather than answering <c>default</c>, because a
        /// caller that picks from nothing has a bug one line earlier and a silent null is a worse place
        /// to find it.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="items"/> is null or empty.</exception>
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
