using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cantrip.Sim;

namespace Cantrip.Cli
{
    /// <summary>
    /// Writes what <c>--against</c> measured: one scenario played twice, by the same bots, from
    /// the same seeds, over two versions of the content.
    /// </summary>
    /// <remarks>
    /// The order of the blocks is the order they are worth reading. What the content allows comes
    /// first, because a card that stopped being playable is true whoever plays and is usually the
    /// thing that was not meant. The seeds that changed hands come second, because that is the
    /// only honest form the levels take. The two percentages are last, small, and carry the same
    /// warning they carry in a single report.
    /// </remarks>
    internal static class SimCompareReport
    {
        private const int Indent = 4;

        public static void Print(ScenarioComparison comparison, string baselineWhere)
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 78));
            Console.WriteLine(Title(comparison));
            Console.WriteLine($"  against {baselineWhere}");
            Console.WriteLine();
            Console.WriteLine("  Same bots, same seeds, same turn limit; only the content differs.");

            if (comparison.Identical)
            {
                Console.WriteLine();
                Line(Indent, "Nothing moved: every seed came out the same way, in the same number of turns,");
                Line(Indent, "for the same hp, and the two sides reach the same cards and the same moves.");
                Line(Indent, "Whatever changed, these runs did not touch it.");
                return;
            }

            Facts(comparison);

            foreach (BotComparison bot in comparison.ByBot) Bot(bot);

            Console.WriteLine();
            Line(Indent, "What this is: the same bot, playing run n from seed n on both sides, so the");
            Line(Indent, "difference between the two is your change and not the bot's taste or the dice.");
            Line(Indent, "What it is not: evidence that the change is good. Every bot here weighs the");
            Line(Indent, "player's hp against the enemies', so it is wrong in a known direction about a");
            Line(Indent, "card that draws and about anything that pays off several turns later. A change");
            Line(Indent, "these numbers like can still be the change that made the game duller.");
            Line(Indent, "The block above them is the half that is true whoever plays.");
        }

        private static string Title(ScenarioComparison comparison) =>
            string.Equals(comparison.Name, comparison.BaselineName, StringComparison.Ordinal)
                ? comparison.Name
                : comparison.Name + "  vs  " + comparison.BaselineName;

        /// <summary>
        /// The half that holds whoever played. It is printed first and printed even when it is
        /// empty, because "nothing the content reaches changed" is itself an answer.
        /// </summary>
        private static void Facts(ScenarioComparison comparison)
        {
            Console.WriteLine();
            Console.WriteLine("  What the content allows, whatever the bot did");

            bool anything = false;
            anything |= List("card(s) that were playable and are not any more", comparison.StoppedBeingPlayable, bad: true);
            anything |= List("card(s) that were never playable and now are", comparison.BecamePlayable, bad: false);
            anything |= List("enemy move(s) that fired and do not any more", comparison.StoppedFiring, bad: true);
            anything |= List("enemy move(s) that never fired and now do", comparison.StartedFiring, bad: false);

            if (!anything) Line(Indent, "the same cards are reachable and the same enemy moves fire");
        }

        private static bool List(string what, IReadOnlyList<string> names, bool bad)
        {
            if (names.Count == 0) return false;

            string text = $"{names.Count} {what}: {string.Join(", ", names)}";
            if (bad)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Line(Indent, text);
                Console.ResetColor();
            }
            else Line(Indent, text);

            return true;
        }

        private static void Bot(BotComparison bot)
        {
            Console.WriteLine();
            Console.WriteLine($"  {bot.Bot} bot, {bot.Paired} seed(s) played by both");

            if (bot.OnlyBefore > 0 || bot.OnlyAfter > 0)
            {
                Line(Indent, $"{bot.OnlyBefore} seed(s) only the baseline played and {bot.OnlyAfter} only this side did, " +
                             "and neither is compared. Give `--runs N` to make both sides play the same ones.");
            }

            if (bot.Paired == 0)
            {
                Line(Indent, "No seed was played by both sides, so there is nothing to compare.");
                return;
            }

            Line(Indent, Pad("seeds that changed hands", 32) + Right(bot.Changed.ToString(CultureInfo.InvariantCulture), 8));
            Line(Indent + 2, Pad("lost before, won now", 30) + Right(bot.Gained.ToString(CultureInfo.InvariantCulture), 8));
            Line(Indent + 2, Pad("won before, lost now", 30) + Right(bot.Lost.ToString(CultureInfo.InvariantCulture), 8));
            Line(Indent + 2, Pad("came out the same way", 30) + Right(bot.Unchanged.ToString(CultureInfo.InvariantCulture), 8));

            Console.WriteLine();
            foreach (string line in Coin(bot)) Line(Indent, line);

            Console.WriteLine();
            Line(Indent, Pad(string.Empty, 32) + Right("before", 10) + Right("after", 10) + Right("change", 10));
            Row("turns, all runs", bot.TurnsBefore, bot.TurnsAfter);
            Row("hp lost, all runs", bot.HpLostBefore, bot.HpLostAfter);
            if (bot.StallsBefore > 0 || bot.StallsAfter > 0) Row("runs that stalled", bot.StallsBefore, bot.StallsAfter);
            if (bot.ErrorsBefore > 0 || bot.ErrorsAfter > 0) Row("runs that threw", bot.ErrorsBefore, bot.ErrorsAfter);

            Console.WriteLine();
            Line(Indent, Pad("runs finished", 32) +
                 Right(Percent(bot.WonBefore, bot.Paired), 10) +
                 Right(Percent(bot.WonAfter, bot.Paired), 10) +
                 Right(Points(bot), 10) + "   a level, for reference only");
        }

        private static void Row(string what, long before, long after)
        {
            long change = after - before;
            Line(Indent, Pad(what, 32) +
                Right(before.ToString(CultureInfo.InvariantCulture), 10) +
                Right(after.ToString(CultureInfo.InvariantCulture), 10) +
                Right((change > 0 ? "+" : string.Empty) + change.ToString(CultureInfo.InvariantCulture), 10));
        }

        /// <summary>
        /// The one line that says whether the seeds that changed hands are a difference or a
        /// shrug. It is stated as how often a change that did nothing would look like this,
        /// because "one run in forty thousand" is a thing a person can weigh and a p-value is not.
        /// </summary>
        private static IReadOnlyList<string> Coin(BotComparison bot)
        {
            if (bot.Changed == 0)
            {
                return new[] { "No seed changed hands: this change decided not one run of this scenario." };
            }

            if (bot.Gained == bot.Lost)
            {
                return new[]
                {
                    $"The {bot.Changed} split evenly, which is what a change that decided nothing does.",
                    "It moved runs about; it did not move the outcome.",
                };
            }

            string way = bot.Gained > bot.Lost
                ? $"{bot.Gained} of the {bot.Changed} went your way"
                : $"{bot.Lost} of the {bot.Changed} went the other way";

            double coincidence = bot.Coincidence;
            if (coincidence >= 0.05)
            {
                return new[]
                {
                    $"{way}. A change that decided nothing would split {bot.Changed} seeds at least this",
                    $"unevenly {Percent(coincidence)} of the time, so this is not yet a difference: play more seeds.",
                };
            }

            double once = 1 / coincidence;
            string odds = once >= 1_000_000
                ? "more than a million"
                : Math.Round(once).ToString("#,0", CultureInfo.InvariantCulture);

            return new[]
            {
                $"{way}. A change that decided nothing would split {bot.Changed} seeds at least this",
                $"unevenly about once in {odds} tries.",
            };
        }

        private static string Points(BotComparison bot)
        {
            if (bot.Paired == 0) return "-";

            double change = ((double)bot.WonAfter - bot.WonBefore) / bot.Paired * 100;
            return (change > 0 ? "+" : string.Empty) + change.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>The last lines of a comparison, after every scenario in it.</summary>
        public static void Summary(IReadOnlyList<ScenarioComparison> comparisons, IReadOnlyList<string> unmatched, IReadOnlyList<string> extra)
        {
            Console.WriteLine();
            Console.WriteLine($"{comparisons.Count} scenario(s) compared.");

            foreach (string name in unmatched)
                Console.WriteLine($"  \"{name}\" is in this content and not in the baseline, so it was played but not compared.");
            foreach (string name in extra)
                Console.WriteLine($"  \"{name}\" is in the baseline and not in this content, so it was not played.");

            if (comparisons.Count == 0 && (unmatched.Count > 0 || extra.Count > 0))
            {
                Console.WriteLine();
                Console.WriteLine("Nothing paired. Scenarios pair by name; two scenarios called different things pair");
                Console.WriteLine("when `--name` and `--against-name` pick exactly one on each side.");
            }

            Console.WriteLine();
            Console.WriteLine("A comparison never changes the exit code: it is a reading, not a check. What fails");
            Console.WriteLine("this command is what always failed it: a run that threw, a battle that hit the turn");
            Console.WriteLine("limit, an expectation that did not hold, in the content the command was pointed at.");
        }

        private static void Line(int indent, string text) => Console.WriteLine(new string(' ', indent) + text);

        private static string Pad(string text, int width) => text.Length >= width ? text + "  " : text.PadRight(width);

        private static string Right(string text, int width) => text.PadLeft(width);

        private static string Percent(int part, int whole) =>
            whole == 0 ? "-" : ((double)part / whole * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        private static string Percent(double share) => (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }
}
