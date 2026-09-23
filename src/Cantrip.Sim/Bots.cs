using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Runtime;

namespace Cantrip.Sim
{
    /// <summary>
    /// The bots <c>--bot</c> knows, and how a run makes one. A bot is made afresh for every run,
    /// from that run's seed, so the same command plays the same runs on any machine.
    /// </summary>
    /// <remarks>
    /// All of them weigh a position the same way — the player's hp against the enemies' — so they
    /// are wrong in the same direction about a card that draws, and about anything that pays off
    /// several turns later. Two of them agreeing is therefore not evidence that either is right.
    /// </remarks>
    public static class Bots
    {
        /// <summary>One turn of lookahead. The default, with <see cref="Patient"/> beside it.</summary>
        public const string Cautious = "cautious";

        /// <summary>The same, two turns out. Its job is to disagree with <see cref="Cautious"/>.</summary>
        public const string Patient = "patient";

        /// <summary>The floor, and the fastest way to fuzz content.</summary>
        public const string Random = "random";

        /// <summary>What <c>--bot both</c> is called on the command line.</summary>
        public const string Both = "both";

        /// <summary>Every bot's name, in the order a report prints them.</summary>
        public static IReadOnlyList<string> Names { get; } = new[] { Cautious, Patient, Random };

        /// <summary>
        /// The two bots that play when nobody says otherwise. A level one bot reaches is a fact
        /// about that bot, and the second one is there to say so out loud.
        /// </summary>
        public static IReadOnlyList<string> Pair { get; } = new[] { Cautious, Patient };

        /// <summary>Whether <paramref name="name"/> is one of <see cref="Names"/>, in any case.</summary>
        public static bool Exists(string name) =>
            Names.Any(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>How a run makes the named bot from its seed.</summary>
        /// <exception cref="ArgumentException">Nothing is called that.</exception>
        public static Func<ulong, IBot> Make(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));

            if (string.Equals(name, Cautious, StringComparison.OrdinalIgnoreCase))
                return seed => new LookaheadBot(Cautious, turns: 1, seed);
            if (string.Equals(name, Patient, StringComparison.OrdinalIgnoreCase))
                return seed => new LookaheadBot(Patient, turns: 2, seed);
            if (string.Equals(name, Random, StringComparison.OrdinalIgnoreCase))
                return seed => new RandomBot(seed);

            throw new ArgumentException($"No bot is called `{name}`. The bots are {string.Join(", ", Names)}.", nameof(name));
        }
    }

    /// <summary>
    /// Looks ahead through the engine itself. For every legal play it captures the game, makes the
    /// play, ends the turn so that the enemies answer, scores what is left and puts it all back; it
    /// then makes the best play it found. It stops when ending the turn scores as well as anything
    /// it could do, which is how it decides to hold a card.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It knows no card, ability, status or enemy by name, so it needs no change for new content,
    /// and it scores a position on hp alone: 1.5 times the player's, less the enemies'. Block,
    /// burn and the rest are not scored, because they are already in the hp: the score is taken
    /// after the enemies have answered, so a block that stopped a hit shows up as hp the player
    /// still has. A status worth naming in one game is worth nothing in another, and a bot that
    /// knew the names in one sample would be measuring itself.
    /// </para>
    /// <para>
    /// Every play it tries is fogged (see <see cref="Trials"/>), so it cannot read a roll before it
    /// chooses, and nothing it tries is recorded as having happened.
    /// </para>
    /// </remarks>
    internal sealed class LookaheadBot : IBot
    {
        /// <summary>A turn that plays this many times is a rules loop rather than a turn.</summary>
        private const int MaxPlaysPerTurn = 50;

        /// <summary>Winning is worth more than any position, and losing less than any.</summary>
        private const double Won = 1e9;

        private readonly RandomChooser _chooser;
        private readonly Rng _rng;
        private readonly int _turns;

        /// <param name="name">What the report calls it.</param>
        /// <param name="turns">How many turns are played out before a position is scored.</param>
        /// <param name="seed">The run's seed. Its choices and its fog are forked from it, so a run repeats exactly.</param>
        public LookaheadBot(string name, int turns, ulong seed)
        {
            Name = name;
            _turns = turns;
            _chooser = new RandomChooser(seed ^ 0xB07UL);
            _rng = new Rng(seed ^ 0x6EEDUL);
        }

        public string Name { get; }

        public string Description => _turns == 1
            ? "tries every legal play, ends the turn so the enemies answer, and keeps the one that leaves it with the most hp and the enemies with the least"
            : "does the same as the cautious bot but scores two turns out, so it will take a hit now for a card that pays off next turn";

        public IChoiceProvider Chooser => _chooser;

        public void PlayTurn(CardRuntime runtime, Trials trials, Action<string>? log)
        {
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));
            if (trials == null) throw new ArgumentNullException(nameof(trials));

            for (int guard = 0; guard < MaxPlaysPerTurn && runtime.Won == null; guard++)
            {
                List<Option> options = Options.Legal(runtime);
                if (options.Count == 0) return;

                // Nothing can be tried while an effect is resolving or a block is waiting that a
                // snapshot cannot hold. Playing something beats passing the turn, so it plays.
                if (!trials.CanTry)
                {
                    if (!Options.TakeAny(runtime, options, log)) return;
                    continue;
                }

                // One roll for the whole decision, so every play is judged against the same luck.
                ulong fog = _rng.NextUInt64();

                // What ending the turn now is worth. A play has to beat that, and ties go to
                // playing: a card whose worth only shows later is worth at least nothing.
                double best = Try(runtime, trials, fog, null);
                Option? choice = null;

                foreach (Option option in options)
                {
                    double score = Try(runtime, trials, fog, option);
                    if (double.IsNaN(score)) continue;
                    if (score >= best)
                    {
                        best = score;
                        choice = option;
                    }
                }

                if (choice == null) return;

                string what = Options.Describe(runtime, choice.Value);

                // Refused for real although the trial took it: the turn ends rather than the same
                // play being chosen again, which would be a loop.
                if (!Options.Take(runtime, choice.Value)) return;
                log?.Invoke(what);
            }
        }

        /// <summary>
        /// What the board is worth after <paramref name="option"/>, or after doing nothing when it
        /// is null. Not a number when content refused the play as it resolved.
        /// </summary>
        private double Try(CardRuntime runtime, Trials trials, ulong fog, Option? option)
        {
            using (trials.Begin(fog))
            {
                if (option != null && !Options.Take(runtime, option.Value)) return double.NaN;
                return Score(runtime);
            }
        }

        private double Score(CardRuntime runtime)
        {
            for (int turn = 0; turn < _turns && runtime.Won == null; turn++) runtime.EndTurn();

            if (runtime.Won == true) return Won;
            if (runtime.Won == false) return -Won;

            double score = 1.5 * runtime.Player!.GetInt("hp");
            foreach (Entity enemy in runtime.State.Actors(Team.Enemy)) score -= enemy.GetInt("hp");
            return score;
        }
    }

    /// <summary>
    /// Plays legal cards and abilities at random until it can play no more. It is the floor every
    /// other bot has to beat, and the fastest way to fuzz content, because it tries nothing first.
    /// </summary>
    internal sealed class RandomBot : IBot
    {
        private const int MaxPlaysPerTurn = 50;

        private readonly RandomChooser _chooser;
        private readonly Rng _rng;

        /// <param name="seed">The run's seed. Its plays are forked from it, so a run repeats exactly.</param>
        public RandomBot(ulong seed)
        {
            _chooser = new RandomChooser(seed ^ 0xB07UL);
            _rng = new Rng(seed ^ 0x5A17UL);
        }

        public string Name => Bots.Random;

        public string Description => "plays legal cards and abilities at random, in a random order, until it can play no more";

        public IChoiceProvider Chooser => _chooser;

        public void PlayTurn(CardRuntime runtime, Trials trials, Action<string>? log)
        {
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));

            for (int guard = 0; guard < MaxPlaysPerTurn && runtime.Won == null; guard++)
            {
                List<Option> options = Options.Legal(runtime);
                if (options.Count == 0) return;

                _rng.Shuffle(options);
                if (!Options.TakeAny(runtime, options, log)) return;
            }
        }
    }
}
