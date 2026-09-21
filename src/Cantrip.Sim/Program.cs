using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Cantrip.Content;

namespace Cantrip.Sim
{
    public static class Program
    {
        private const string Usage = @"cantrip-sim - play whole runs of the slice with a bot and report how they went

usage:
  cantrip-sim [--content path] [--runs N] [--seed S] [--bot greedy|random]
  cantrip-sim --watch SEED [--content path] [--bot greedy|random]

  --content  content folder (default samples/slice)
  --runs     how many runs (default 500)
  --seed     first seed; runs use S, S+1, ... (default 1)
  --bot      greedy looks one card ahead through the engine; random plays anything (default greedy)
  --watch    play one run and print every turn, play and reward";

        public static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            string content = "samples/slice";
            int runs = 500;
            ulong seed = 1;
            string bot = "greedy";
            ulong? watch = null;

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string value = i + 1 < args.Length ? args[i + 1] : "";
                    switch (args[i])
                    {
                        case "--content": content = value; i++; break;
                        case "--runs": runs = int.Parse(value, CultureInfo.InvariantCulture); i++; break;
                        case "--seed": seed = ulong.Parse(value, CultureInfo.InvariantCulture); i++; break;
                        case "--bot": bot = value; i++; break;
                        case "--watch": watch = ulong.Parse(value, CultureInfo.InvariantCulture); i++; break;
                        case "-h": case "--help": Console.WriteLine(Usage); return 0;
                        default: throw new FormatException("unknown option " + args[i]);
                    }
                }
                if (bot != "greedy" && bot != "random") throw new FormatException("--bot is greedy or random");
            }
            catch (FormatException error)
            {
                Console.Error.WriteLine(error.Message);
                Console.Error.WriteLine(Usage);
                return 2;
            }

            var library = new ContentLibrary();
            library.LoadFolder(content);
            if (library.Diagnostics.HasErrors)
            {
                foreach (var diagnostic in library.Diagnostics.Where(d => d.Severity == Cantrip.Diagnostics.DiagnosticSeverity.Error))
                    Console.Error.WriteLine(diagnostic);
                return 1;
            }

            Func<ulong, IBot> makeBot = bot == "random" ? (Func<ulong, IBot>)(s => new RandomBot(s)) : s => new GreedyBot(s);
            var simulator = new RunSimulator(library, makeBot);

            if (watch.HasValue)
            {
                simulator.Log = Console.WriteLine;
                RunResult one = simulator.Play(watch.Value);
                Console.WriteLine();
                Console.WriteLine(one.Won ? "WON" : "LOST" + (one.DiedTo != null ? " to " + one.DiedTo : "") + (one.Error != null ? ": " + one.Error : ""));
                return 0;
            }

            var clock = Stopwatch.StartNew();
            var results = new List<RunResult>();
            for (int i = 0; i < runs; i++) results.Add(simulator.Play(seed + (ulong)i));
            clock.Stop();

            Report.Print(results, bot, clock.Elapsed);
            return results.Any(r => r.Error != null) ? 1 : 0;
        }
    }

    internal static class Report
    {
        public static void Print(IReadOnlyList<RunResult> results, string bot, TimeSpan elapsed)
        {
            int total = results.Count;
            int wins = results.Count(r => r.Won);
            Console.WriteLine($"{total} runs, {bot} bot, seeds {results[0].Seed}-{results[total - 1].Seed}, {elapsed.TotalSeconds:0.0}s ({elapsed.TotalMilliseconds / total:0} ms/run)");
            Console.WriteLine();
            Console.WriteLine($"Win rate           {Percent(wins, total)}  ({wins}/{total})");
            Console.WriteLine($"Floors cleared     {results.Average(r => r.FloorsCleared):0.00} of {RunSimulator.Tower.Count} on average");
            List<RunResult> winners = results.Where(r => r.Won).ToList();
            if (winners.Count > 0)
                Console.WriteLine($"Hp left on a win   {winners.Average(r => r.FinalHp):0} of {RunSimulator.PlayerHp} on average");
            int elites = results.Count(r => r.TookElite);
            Console.WriteLine($"Fought the elite   {Percent(elites, total)}; won the run {Percent(results.Count(r => r.TookElite && r.Won), elites)} when they did, {Percent(results.Count(r => !r.TookElite && r.Won && r.FloorsCleared > 2), results.Count(r => !r.TookElite && r.FloorsCleared > 2))} when they rested");

            Console.WriteLine();
            Console.WriteLine("Where runs end");
            foreach (var group in results.Where(r => !r.Won && r.Error == null).GroupBy(r => r.DiedTo ?? "?").OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {group.Key,-34} {group.Count(),5}  {Percent(group.Count(), total)}");

            Console.WriteLine();
            Console.WriteLine($"  {"Encounter",-32} {"fought",6} {"lost",6} {"turns",6} {"hp lost",8}");
            foreach (var group in results.SelectMany(r => r.Battles).GroupBy(b => b.Encounter))
            {
                int fought = group.Count();
                int lost = group.Count(b => b.Won != true);
                Console.WriteLine($"  {group.Key,-32} {fought,6} {Percent(lost, fought),6} {group.Average(b => b.Turns),6:0.0} {group.Average(b => b.HpLost),8:0.0}");
            }

            Console.WriteLine();
            Console.WriteLine($"  {"Card picked",-32} {"runs",6} {"won",6} {"vs not",7}");
            foreach (var group in results.SelectMany(r => r.Picks.Distinct().Select(p => (Pick: p, Run: r))).GroupBy(x => x.Pick).OrderByDescending(g => WinRate(g.Select(x => x.Run))))
            {
                List<RunResult> with = group.Select(x => x.Run).ToList();
                List<RunResult> without = results.Where(r => !r.Picks.Contains(group.Key)).ToList();
                double delta = WinRate(with) - WinRate(without);
                Console.WriteLine($"  {group.Key,-32} {with.Count,6} {WinRate(with),6:0%} {delta,+7:+0%;-0%;0%}");
            }

            List<RunResult> relicRuns = results.Where(r => r.Relics.Count > 0).ToList();
            if (relicRuns.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  {"Relic",-32} {"runs",6} {"won",6}");
                foreach (var group in relicRuns.SelectMany(r => r.Relics.Select(x => (Relic: x, Run: r))).GroupBy(x => x.Relic).OrderBy(g => g.Key))
                    Console.WriteLine($"  {group.Key,-32} {group.Count(),6} {WinRate(group.Select(x => x.Run)),6:0%}");
            }

            List<RunResult> errors = results.Where(r => r.Error != null).ToList();
            Console.WriteLine();
            if (errors.Count == 0)
            {
                Console.WriteLine("No run threw.");
            }
            else
            {
                Console.WriteLine($"{errors.Count} run(s) threw. Replay one with --watch SEED:");
                foreach (var group in errors.GroupBy(e => e.Error))
                    Console.WriteLine($"  seeds {string.Join(", ", group.Take(5).Select(e => e.Seed))}{(group.Count() > 5 ? ", ..." : "")}: {group.Key}");
            }
        }

        private static double WinRate(IEnumerable<RunResult> runs)
        {
            List<RunResult> list = runs.ToList();
            return list.Count == 0 ? 0 : (double)list.Count(r => r.Won) / list.Count;
        }

        private static string Percent(int part, int whole) => whole == 0 ? "-" : ((double)part / whole).ToString("0%", CultureInfo.InvariantCulture);
    }
}
