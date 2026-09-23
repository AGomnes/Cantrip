using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cantrip.Sim.Scenarios;

namespace Cantrip.Cli
{
    /// <summary>
    /// Writes what <c>sim</c> measured. The first block is what the content allows, which holds
    /// whichever bot played; the second is what this bot did with it, and says so.
    /// </summary>
    internal static class SimReport
    {
        private const int Indent = 4;

        public static void Print(ScenarioResult result)
        {
            var scenario = result.Scenario;
            string where = $"{scenario.File}:{scenario.Syntax.Span.Line}";
            int runs = result.Runs.Count;
            ulong first = result.Runs.Count == 0 ? 0 : result.Runs[0].Seed;
            ulong last = result.Runs.Count == 0 ? 0 : result.Runs[runs - 1].Seed;

            Console.WriteLine();
            Console.WriteLine(Pad(scenario.Name, 52) + where);
            Console.WriteLine($"  {runs} run(s), seeds {first}-{last}, turn limit {result.TurnLimit}, {result.Bot} bot, {Seconds(result.Elapsed)}");
            Console.WriteLine($"  {result.BattleLines} battle(s) and {result.OtherLines} other statement(s)");

            Console.WriteLine();
            Console.WriteLine("  What the content allows, whatever the bot did");
            Threw(result);
            Stalled(result);
            Cards(result);
            Moves(result);
            if (result.EveryRunIdentical)
                Note($"every run went the same way, so {runs} runs said no more than 1 would");

            Battles(result);
            Expectations(result);
        }

        // The facts ---------------------------------------------------------------------------

        private static void Threw(ScenarioResult result)
        {
            List<RunResult> threw = result.Runs.Where(r => r.Error != null).ToList();
            if (threw.Count == 0)
            {
                Note("nothing threw");
                return;
            }

            Bad($"{threw.Count} run(s) threw");
            foreach (var group in threw.GroupBy(r => r.Error, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
            {
                RunResult example = group.First();
                string statement = example.Statement == null ? string.Empty : $", `{example.Statement}`";
                Line(Indent + 2, $"seed {example.Seed} at {example.At}{statement}" + Also(group.Count() - 1));
                Line(Indent + 4, group.Key!);
            }
        }

        private static void Stalled(ScenarioResult result)
        {
            var stalls = result.Runs
                .SelectMany(r => r.Battles.Where(b => b.Won == null).Select(b => (Battle: b, r.Seed)))
                .ToList();

            if (stalls.Count == 0)
            {
                Note($"no battle reached the turn limit of {result.TurnLimit}");
                return;
            }

            Bad($"{stalls.Count} battle(s) reached the turn limit of {result.TurnLimit} and never ended");
            foreach (var group in stalls.GroupBy(s => s.Battle.Label, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
            {
                IEnumerable<ulong> seeds = group.Select(s => s.Seed).Take(5);
                Line(Indent + 2, $"{group.Key}, seeds {string.Join(", ", seeds)}{(group.Count() > 5 ? ", ..." : string.Empty)}");
            }
        }

        private static void Cards(ScenarioResult result)
        {
            // A fight with no cards in it is a real thing, and has nothing to say here.
            if (result.Facts.CardCount == 0) return;

            IReadOnlyList<string> never = result.Facts.NeverPlayable;
            IReadOnlyList<string> undrawn = result.Facts.NeverHeld;

            if (never.Count == 0) Note("every card the player held was playable at least once");
            else Bad($"{never.Count} card(s) were held but never playable: {string.Join(", ", never)}");

            if (undrawn.Count > 0) Bad($"{undrawn.Count} card(s) never reached a hand: {string.Join(", ", undrawn)}");
        }

        private static void Moves(ScenarioResult result)
        {
            IReadOnlyList<(string Enemy, string Move)> never = result.Facts.NeverFired;
            if (result.Facts.MoveCount == 0) return;

            if (never.Count == 0)
            {
                Note("every move of every enemy fought fired");
                return;
            }

            Bad($"{never.Count} enemy move(s) never fired");
            foreach (var group in never.GroupBy(m => m.Enemy, StringComparer.Ordinal))
                Line(Indent + 2, $"{group.Key}: {string.Join(", ", group.Select(m => m.Move))}");
        }

        // What the bot did --------------------------------------------------------------------

        private static void Battles(ScenarioResult result)
        {
            var battles = result.Runs.SelectMany(r => r.Battles).GroupBy(b => (b.Index, b.Label)).OrderBy(g => g.Key.Index).ToList();
            if (battles.Count == 0) return;

            int width = Math.Max(24, battles.Max(g => g.Key.Label.Length) + 2);
            Console.WriteLine();
            Console.WriteLine($"  What the {result.Bot} bot did with it");
            Console.WriteLine("    " + Pad("battle", width) + Right("fought", 8) + Right("turns", 8) + Right("hp lost", 9));
            foreach (var group in battles)
            {
                Console.WriteLine(
                    "    " + Pad(group.Key.Label, width) +
                    Right(group.Count().ToString(CultureInfo.InvariantCulture), 8) +
                    Right(group.Average(b => b.Turns).ToString("0.0", CultureInfo.InvariantCulture), 8) +
                    Right(group.Average(b => b.HpLost).ToString("0.0", CultureInfo.InvariantCulture), 9));
            }
        }

        private static void Expectations(ScenarioResult result)
        {
            if (result.Expectations.Count == 0) return;

            Console.WriteLine();
            int width = Math.Max(24, result.Expectations.Max(e => e.Text.Length) + 2);
            foreach (ExpectResult expectation in result.Expectations)
            {
                Console.Write("  " + Pad(expectation.Text, width));
                if (expectation.Held == true)
                {
                    In(ConsoleColor.Green, "ok");
                    Console.WriteLine();
                    continue;
                }

                if (expectation.Held == false) In(ConsoleColor.Red, "failed");
                else In(ConsoleColor.Yellow, "-");
                Console.WriteLine(expectation.Detail.Length == 0 ? string.Empty : "   " + expectation.Detail);
            }
        }

        /// <summary>The last lines: what every scenario came to, and what the bot's part in it was.</summary>
        public static void Summary(IReadOnlyList<ScenarioResult> results)
        {
            int runs = results.Sum(r => r.Runs.Count);
            int errors = results.Sum(r => r.Errors);
            int stalls = results.Sum(r => r.Stalls);
            int failed = results.Sum(r => r.Expectations.Count(e => e.Held == false));
            int notChecked = results.Sum(r => r.Expectations.Count(e => e.Held == null));

            Console.WriteLine();
            Console.WriteLine(
                $"{results.Count} scenario(s), {runs} run(s), {errors} error(s), {stalls} stall(s), " +
                $"{failed} failed expectation(s)" + (notChecked == 0 ? "." : $", {notChecked} not checked."));

            if (results.Count == 0) return;

            Console.WriteLine();
            Console.WriteLine($"The {results[0].Bot} bot {results[0].BotDescription}.");
            Console.WriteLine("How far a run got, how long it took and what it cost are facts about that bot.");
            Console.WriteLine("Above each table, what happened at least once happened in your content, whoever plays.");
            Console.WriteLine("What never happened may instead be something this bot never reached.");
        }

        // Writing -----------------------------------------------------------------------------

        private static void Note(string text) => Line(Indent, text);

        private static void Bad(string text)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Line(Indent, text);
            Console.ResetColor();
        }

        private static void In(ConsoleColor colour, string text)
        {
            Console.ForegroundColor = colour;
            Console.Write(text);
            Console.ResetColor();
        }

        private static void Line(int indent, string text) => Console.WriteLine(new string(' ', indent) + text);

        private static string Also(int more) => more == 0 ? string.Empty : $" and {more} more";

        private static string Pad(string text, int width) => text.Length >= width ? text + "  " : text.PadRight(width);

        private static string Right(string text, int width) => text.PadLeft(width);

        private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
    }
}
