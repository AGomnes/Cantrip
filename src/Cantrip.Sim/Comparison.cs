using System;
using System.Collections.Generic;
using System.Linq;

namespace Cantrip.Sim
{
    /// <summary>
    /// Two runs of one scenario set against each other, seed by seed: the same bot, the same
    /// seeds, the same turn limit, two versions of the content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A level is a fact about the bot, which is why <c>sim</c> refuses to let one be quoted. A
    /// <em>difference</em> between two levels is not the same thing, as long as everything except
    /// the content is held still: the bot is the same bot, run <c>n</c> is seeded the same way on
    /// both sides, and the two runs are compared to each other rather than to a number. That is
    /// what this does, and it is the only comparison the tool makes.
    /// </para>
    /// <para>
    /// The comparison that matters is not the two percentages but the seeds that <em>changed
    /// hands</em>: five hundred runs that end 70.8% and 74.0% might be sixteen seeds that went one
    /// way, or two hundred that went one way and a hundred and eighty-four that went the other.
    /// Those are different facts about a change, and only the paired counts tell them apart. That
    /// is why <see cref="Gained"/> and <see cref="Lost"/> are here and a delta is not.
    /// </para>
    /// </remarks>
    public sealed class BotComparison
    {
        internal BotComparison(string bot, string description)
        {
            Bot = bot;
            BotDescription = description;
        }

        /// <summary>The bot that played both sides. It is the same bot, or there is no comparison.</summary>
        public string Bot { get; }

        /// <summary>What that bot does, which the report carries wherever the bot is named.</summary>
        public string BotDescription { get; }

        /// <summary>How many seeds both sides played. A seed only one side played is not compared.</summary>
        public int Paired { get; internal set; }

        /// <summary>Seeds only the baseline played, which the report names rather than hides.</summary>
        public int OnlyBefore { get; internal set; }

        /// <summary>Seeds only the subject played.</summary>
        public int OnlyAfter { get; internal set; }

        public int WonBefore { get; internal set; }
        public int WonAfter { get; internal set; }

        /// <summary>Seeds the baseline lost and the subject won.</summary>
        public int Gained { get; internal set; }

        /// <summary>Seeds the baseline won and the subject lost.</summary>
        public int Lost { get; internal set; }

        /// <summary>Seeds that came out the same way on both sides.</summary>
        public int Unchanged => Paired - Gained - Lost;

        /// <summary>Seeds that changed hands either way: the only ones the change is visible in.</summary>
        public int Changed => Gained + Lost;

        public long TurnsBefore { get; internal set; }
        public long TurnsAfter { get; internal set; }
        public long HpLostBefore { get; internal set; }
        public long HpLostAfter { get; internal set; }
        public int StallsBefore { get; internal set; }
        public int StallsAfter { get; internal set; }
        public int ErrorsBefore { get; internal set; }
        public int ErrorsAfter { get; internal set; }

        /// <summary>
        /// How often a change that did nothing at all would split the seeds that changed hands at
        /// least this unevenly: the two-sided tail of a fair coin tossed <see cref="Changed"/>
        /// times. 1 when nothing changed hands.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It is here because the alternative is a reader deciding for themselves that 71% against
        /// 74% is an improvement, which over sixteen changed seeds it is not. Every seed is an
        /// independent game, so the arithmetic is exact rather than approximate, and it is the only
        /// statistic in this tool.
        /// </para>
        /// <para>
        /// What it is not: evidence that the change is <em>good</em>. It says this bot finished
        /// more runs, and a bot that weighs the player's hp against the enemies' is wrong in a
        /// known direction about a card that draws and about anything that pays off several turns
        /// later. A change this number likes may be a change that made the game duller.
        /// </para>
        /// </remarks>
        public double Coincidence => Coin(Changed, Gained);

        /// <summary>
        /// The two-sided exact tail of <paramref name="heads"/> heads in <paramref name="tosses"/>
        /// tosses of a fair coin. Worked in logs, because the counts run to thousands and
        /// <c>C(n, k)</c> does not fit in anything.
        /// </summary>
        internal static double Coin(int tosses, int heads)
        {
            if (tosses <= 0) return 1;

            int side = Math.Min(heads, tosses - heads);
            var logFactorial = new double[tosses + 1];
            for (int i = 2; i <= tosses; i++) logFactorial[i] = logFactorial[i - 1] + Math.Log(i);

            double log2 = Math.Log(2);
            double tail = 0;
            for (int i = 0; i <= side; i++)
            {
                tail += Math.Exp(logFactorial[tosses] - logFactorial[i] - logFactorial[tosses - i] - (tosses * log2));
            }

            return Math.Min(1, 2 * tail);
        }
    }

    /// <summary>One scenario played on both sides, and what the two came to.</summary>
    /// <remarks>
    /// The content facts are kept apart from the bots' numbers for the same reason <c>sim</c> keeps
    /// them apart in one report: a card that is no longer ever playable is a fact about the content
    /// and is true whoever plays, where a run finished is a fact about the bot. In a comparison the
    /// facts are usually the part worth acting on first.
    /// </remarks>
    public sealed class ScenarioComparison
    {
        internal ScenarioComparison(string name, string baselineName, ScenarioOutcome before, ScenarioOutcome after)
        {
            Name = name;
            BaselineName = baselineName;
            Before = before;
            After = after;
        }

        /// <summary>The subject scenario's name.</summary>
        public string Name { get; }

        /// <summary>The baseline scenario's name, which is the same one unless they were paired by hand.</summary>
        public string BaselineName { get; }

        public ScenarioOutcome Before { get; }
        public ScenarioOutcome After { get; }

        public IList<BotComparison> ByBot { get; } = new List<BotComparison>();

        /// <summary>Cards that were playable at some point before and never are now.</summary>
        public IReadOnlyList<string> StoppedBeingPlayable { get; internal set; } = Array.Empty<string>();

        /// <summary>Cards that were never playable before and are now.</summary>
        public IReadOnlyList<string> BecamePlayable { get; internal set; } = Array.Empty<string>();

        /// <summary>Enemy moves that fired before and fire no longer.</summary>
        public IReadOnlyList<string> StoppedFiring { get; internal set; } = Array.Empty<string>();

        /// <summary>Enemy moves that never fired before and fire now.</summary>
        public IReadOnlyList<string> StartedFiring { get; internal set; } = Array.Empty<string>();

        /// <summary>
        /// True when nothing the comparison measures moved at all. False when no bot played both
        /// sides, because "nothing moved" is a finding and "nothing was compared" is not.
        /// </summary>
        public bool Identical =>
            ByBot.Count > 0
            && ByBot.All(b => b.Paired > 0
                && b.Changed == 0
                && b.TurnsBefore == b.TurnsAfter
                && b.HpLostBefore == b.HpLostAfter
                && b.StallsBefore == b.StallsAfter
                && b.ErrorsBefore == b.ErrorsAfter)
            && StoppedBeingPlayable.Count == 0 && BecamePlayable.Count == 0
            && StoppedFiring.Count == 0 && StartedFiring.Count == 0;
    }

    /// <summary>Pairs two sets of outcomes, seed by seed and bot by bot.</summary>
    public static class Comparisons
    {
        /// <summary>
        /// Sets one scenario's outcome against another's. Both must have been played with the same
        /// bots, in the same order, from the same first seed, or nothing here means anything; the
        /// command that calls it plays both sides itself, with one set of options, for that reason.
        /// </summary>
        /// <param name="before">The baseline: the older content, or the other deck.</param>
        /// <param name="after">The subject: the content the command was pointed at.</param>
        public static ScenarioComparison Of(ScenarioOutcome before, ScenarioOutcome after)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (after == null) throw new ArgumentNullException(nameof(after));

            var comparison = new ScenarioComparison(after.Scenario.Name, before.Scenario.Name, before, after);

            foreach (ScenarioResult subject in after.ByBot)
            {
                ScenarioResult? baseline = before.ByBot.FirstOrDefault(b =>
                    string.Equals(b.Bot, subject.Bot, StringComparison.OrdinalIgnoreCase));
                if (baseline == null) continue;

                comparison.ByBot.Add(Pair(baseline, subject));
            }

            // Each list is one side's "never" minus the other's, which is why they read backwards:
            // a card that is in the subject's never-playable list and not in the baseline's is a
            // card that stopped being playable.
            comparison.StoppedBeingPlayable = NewlyIn(NeverPlayable(before), NeverPlayable(after));
            comparison.BecamePlayable = NewlyIn(NeverPlayable(after), NeverPlayable(before));
            comparison.StoppedFiring = NewlyIn(NeverFired(before), NeverFired(after));
            comparison.StartedFiring = NewlyIn(NeverFired(after), NeverFired(before));

            return comparison;
        }

        private static BotComparison Pair(ScenarioResult before, ScenarioResult after)
        {
            var comparison = new BotComparison(after.Bot, after.BotDescription);

            Dictionary<ulong, RunResult> baseline = Bind(before);
            Dictionary<ulong, RunResult> subject = Bind(after);

            comparison.OnlyBefore = baseline.Keys.Count(seed => !subject.ContainsKey(seed));
            comparison.OnlyAfter = subject.Keys.Count(seed => !baseline.ContainsKey(seed));

            foreach (ulong seed in baseline.Keys.Where(subject.ContainsKey).OrderBy(s => s))
            {
                RunResult was = baseline[seed];
                RunResult now = subject[seed];

                comparison.Paired++;
                if (was.Won) comparison.WonBefore++;
                if (now.Won) comparison.WonAfter++;
                if (!was.Won && now.Won) comparison.Gained++;
                if (was.Won && !now.Won) comparison.Lost++;

                comparison.TurnsBefore += was.Battles.Sum(b => b.Turns);
                comparison.TurnsAfter += now.Battles.Sum(b => b.Turns);
                comparison.HpLostBefore += was.Battles.Sum(b => b.HpLost);
                comparison.HpLostAfter += now.Battles.Sum(b => b.HpLost);
                if (was.Stalled) comparison.StallsBefore++;
                if (now.Stalled) comparison.StallsAfter++;
                if (was.Error != null) comparison.ErrorsBefore++;
                if (now.Error != null) comparison.ErrorsAfter++;
            }

            return comparison;
        }

        /// <summary>
        /// One run per seed. A seed that somehow ran twice keeps its first run, so the pairing is
        /// one to one whatever it is handed.
        /// </summary>
        private static Dictionary<ulong, RunResult> Bind(ScenarioResult result)
        {
            var runs = new Dictionary<ulong, RunResult>();
            foreach (RunResult run in result.Runs)
            {
                if (!runs.ContainsKey(run.Seed)) runs[run.Seed] = run;
            }

            return runs;
        }

        private static IEnumerable<string> NeverPlayable(ScenarioOutcome outcome) =>
            outcome.Facts.NeverPlayable;

        private static IEnumerable<string> NeverFired(ScenarioOutcome outcome) =>
            outcome.Facts.NeverFired.Select(m => m.Enemy + " " + m.Move);

        /// <summary>What is in <paramref name="to"/> and not in <paramref name="from"/>, in name order.</summary>
        private static IReadOnlyList<string> NewlyIn(IEnumerable<string> from, IEnumerable<string> to) =>
            to.Except(from, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
}
