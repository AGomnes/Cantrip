using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Cantrip;
using Cantrip.Content;
using Cantrip.Descriptions;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Cantrip.Runtime;
using Cantrip.Sim;
using Cantrip.Testing;

namespace Cantrip.Cli
{
    internal static class Program
    {
        private const string Usage = @"cantrip - tools for the Cantrip DSL

usage:
  cantrip validate <path>... [options] load content and report its errors, including unknown verbs and names
  cantrip lint <path>... [options]    load content and run static checks
  cantrip test <path>... [options]    run the `test` blocks in content
  cantrip sim <path>... [options]     play the `scenario` blocks in content many times with a bot
  cantrip describe <path>... [--name <name>]
                                    print generated descriptions (all definitions, or one)
  cantrip repl <path>...              load content and run DSL statements interactively
  cantrip --version                   print the version and the commit it was built from

paths may be files or folders (folders load every *.cantrip file, recursively).

validate, lint and sim options:
  --suppress <codes>  comma-separated diagnostic codes to leave out, e.g. CT306,CT310, or CT301
                      for verbs your game registers in C#. Errors from loading always show
  --warnings-as-errors
                      lint only: exit with 1 when there are warnings, as for errors

test options:
  --filter <text>    only run tests whose name contains <text>
  --trace            print the causality trace, with any log output, for failing tests

sim options:
  --name <text>      only scenarios whose name contains <text>
  --runs N           play each scenario N times, whatever its `runs` line says
  --seed S           the first seed (default 1); runs use S, S+1, ...
  --bot <name>       cautious, patient, random, or both (default both, which is cautious and
                     patient; two bots take about twice as long as one)
  --turn-limit N     turns one battle may take before the run counts as a stall (default 50)
  --watch SEED       play one run of one scenario with the first bot, printing every turn, play
                     and statement
  --against <path>   also play the scenarios in <path> with the same bots and the same seeds, and
                     report the difference seed by seed: content before and after a change. May be
                     given more than once, like a path
  --against-name <text>
                     which scenario in --against to compare with, when it is not called the same
                     thing. With --name it pairs two decks stated in one folder

exit codes: 0 success, 1 content errors, failing tests, a run that threw, a battle that hit the
turn limit or a failed expectation (or lint warnings, with --warnings-as-errors), 2 bad usage";

        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

            if (args[0] is "--version" or "version")
            {
                // The informational version carries the commit after a +, so a bug report can name the exact build.
                Console.WriteLine("cantrip " + (typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"));
                return 0;
            }

            string command = args[0];
            var paths = new List<string>();
            string? filter = null;
            string? bot = null;
            var against = new List<string>();
            string? againstName = null;
            bool trace = false;
            bool warningsAsErrors = false;
            var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var numbers = new Dictionary<string, string>(StringComparer.Ordinal);

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--filter" when i + 1 < args.Length: filter = args[++i]; break;
                    case "--name" when i + 1 < args.Length: filter = args[++i]; break;
                    case "--bot" when i + 1 < args.Length: bot = args[++i]; break;
                    case "--against" when i + 1 < args.Length: against.Add(args[++i]); break;
                    case "--against-name" when i + 1 < args.Length: againstName = args[++i]; break;
                    case "--trace": trace = true; break;
                    case "--warnings-as-errors": warningsAsErrors = true; break;
                    case "--runs" when i + 1 < args.Length:
                    case "--seed" when i + 1 < args.Length:
                    case "--turn-limit" when i + 1 < args.Length:
                    case "--watch" when i + 1 < args.Length:
                        numbers[args[i]] = args[++i];
                        break;
                    case "--suppress" when i + 1 < args.Length:
                        suppressed.UnionWith(args[++i].Split(',').Select(c => c.Trim()).Where(c => c.Length > 0));
                        break;
                    default:
                        if (args[i].StartsWith("--", StringComparison.Ordinal))
                        {
                            Console.Error.WriteLine($"unknown option {args[i]}");
                            return 2;
                        }
                        paths.Add(args[i]);
                        break;
                }
            }

            if (paths.Count == 0)
            {
                Console.Error.WriteLine("no content paths given\n\n" + Usage);
                return 2;
            }

            // Only `lint` has warnings to fail on. Accepted by another command, the option would look
            // like a check that is not being made.
            if (warningsAsErrors && command != "lint")
            {
                Console.Error.WriteLine("--warnings-as-errors only applies to `lint`");
                return 2;
            }

            // Only `sim` plays scenarios. Accepted by another command these would read as settings
            // that were being used, which is worse than being refused.
            List<string> forSim = numbers.Keys
                .Concat(bot == null ? Enumerable.Empty<string>() : new[] { "--bot" })
                .Concat(against.Count == 0 ? Enumerable.Empty<string>() : new[] { "--against" })
                .Concat(againstName == null ? Enumerable.Empty<string>() : new[] { "--against-name" })
                .OrderBy(o => o, StringComparer.Ordinal)
                .ToList();
            if (forSim.Count > 0 && command != "sim")
            {
                Console.Error.WriteLine($"{string.Join(", ", forSim)} only applies to `sim`");
                return 2;
            }

            ContentLibrary? content = Load(paths);
            if (content == null) return 2;

            switch (command)
            {
                case "validate": return Validate(content, suppressed);
                case "lint": return Lint(content, suppressed, warningsAsErrors);
                case "test": return Test(content, filter, trace);
                case "sim": return Sim(content, filter, bot, suppressed, numbers, against, againstName);
                case "describe": return Describe(content, filter);
                case "repl": return Repl(content);
                default:
                    Console.Error.WriteLine($"unknown command `{command}`\n\n" + Usage);
                    return 2;
            }
        }

        private static ContentLibrary? Load(IEnumerable<string> paths)
        {
            var content = new ContentLibrary();
            foreach (string path in paths)
            {
                if (Directory.Exists(path)) content.LoadFolder(path);
                else if (File.Exists(path)) content.LoadFile(path);
                else
                {
                    Console.Error.WriteLine($"not found: {path}");
                    return null;
                }
            }
            return content;
        }

        private static bool Report(ContentLibrary content)
        {
            DiagnosticBag diagnostics = content.Diagnostics;
            Print(diagnostics);
            return !diagnostics.HasErrors;
        }

        /// <summary>
        /// What loading reported, less the warnings and notes that <c>--suppress</c> names. Its errors
        /// always stay: content that did not load cannot be checked around them.
        /// </summary>
        private static List<Diagnostic> Loading(ContentLibrary content, ISet<string> suppressed) =>
            content.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error || !suppressed.Contains(d.Code)).ToList();

        private static void Print(IEnumerable<Diagnostic> diagnostics)
        {
            foreach (Diagnostic diagnostic in diagnostics)
            {
                Console.ForegroundColor = diagnostic.Severity switch
                {
                    DiagnosticSeverity.Error => ConsoleColor.Red,
                    DiagnosticSeverity.Warning => ConsoleColor.Yellow,
                    _ => ConsoleColor.Gray,
                };
                Console.WriteLine(diagnostic);
                Console.ResetColor();
            }
        }

        private static int Lint(ContentLibrary content, ISet<string> suppressed, bool warningsAsErrors)
        {
            List<Diagnostic> loading = Loading(content, suppressed);
            Print(loading);

            IReadOnlyList<Diagnostic> findings = Linter.Lint(content, Options(suppressed));
            Print(findings);

            List<Diagnostic> all = loading.Concat(findings).ToList();
            int errors = all.Count(d => d.Severity == DiagnosticSeverity.Error);
            int warnings = all.Count(d => d.Severity == DiagnosticSeverity.Warning);
            int infos = all.Count(d => d.Severity == DiagnosticSeverity.Info);
            Console.WriteLine($"{errors} error(s), {warnings} warning(s), {infos} note(s)");

            if (errors > 0) return 1;
            if (warningsAsErrors && warnings > 0)
            {
                Console.WriteLine("failed, because --warnings-as-errors counts each warning as an error");
                return 1;
            }
            return 0;
        }

        private static LintOptions Options(IEnumerable<string> suppressed)
        {
            var options = new LintOptions();
            foreach (string code in suppressed) options.Suppressed.Add(code);
            return options;
        }

        private static int Validate(ContentLibrary content, ISet<string> suppressed)
        {
            List<Diagnostic> loading = Loading(content, suppressed);
            Print(loading);
            bool ok = !content.Diagnostics.HasErrors;

            // Loading catches what cannot be parsed; a misspelled verb such as `aply` parses fine and
            // only fails when a card runs it. Those are the linter's errors, so validate reports them
            // too, and leaves the linter's warnings and notes to `lint`.
            List<Diagnostic> broken = Linter.Lint(content, Options(suppressed)).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Print(broken);

            int definitions = content.Definitions.Count();
            DiagnosticBag diagnostics = content.Diagnostics;
            Console.WriteLine(
                $"{content.Files.Count()} file(s), {definitions} definition(s), {content.Verbs.Count()} verb(s), " +
                $"{content.Tests.Count} test(s), {content.Scenarios.Count} scenario(s): " +
                $"{diagnostics.Errors.Count() + broken.Count} error(s), {loading.Count(d => d.Severity == DiagnosticSeverity.Warning)} warning(s)");
            return ok && broken.Count == 0 ? 0 : 1;
        }

        private static int Test(ContentLibrary content, string? filter, bool trace)
        {
            if (!Report(content)) return 1;

            var runner = new DslTestRunner(content) { Trace = trace };
            IReadOnlyList<DslTestResult> results = runner.RunAll(filter);

            foreach (DslTestResult result in results)
            {
                if (result.Passed)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("  PASS ");
                    Console.ResetColor();
                    Console.WriteLine(result.Name);
                    continue;
                }

                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("  FAIL ");
                Console.ResetColor();
                Console.WriteLine(result.Name);
                Console.WriteLine($"       {result.FailureSpan}: {result.Failure}");
                if (trace && !string.IsNullOrEmpty(result.Trace))
                {
                    foreach (string line in result.Trace!.Split('\n')) Console.WriteLine("       | " + line.TrimEnd('\r'));
                }
            }

            int failed = results.Count(r => !r.Passed);
            Console.WriteLine();
            Console.WriteLine($"{results.Count - failed} passed, {failed} failed");
            return failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// Plays the <c>scenario</c> blocks many times and reports what happened. It refuses to
        /// start on a content error, the linter's errors included, so a scenario that names an
        /// enemy nothing defines fails in milliseconds instead of after a hundred runs.
        /// </summary>
        private static int Sim(
            ContentLibrary content,
            string? name,
            string? bot,
            ISet<string> suppressed,
            IReadOnlyDictionary<string, string> given,
            IReadOnlyList<string> against,
            string? againstName)
        {
            // The options first: bad usage is bad usage, whatever the content turns out to be.
            var options = new ScenarioOptions();
            ulong? watch = null;

            if (bot != null)
            {
                bool both = string.Equals(bot, Bots.Both, StringComparison.OrdinalIgnoreCase);
                if (!both && !Bots.Exists(bot))
                {
                    Console.Error.WriteLine($"--bot takes {string.Join(", ", Bots.Names)} or {Bots.Both}, not \"{bot}\"");
                    return 2;
                }

                options.MakeBots.Clear();
                foreach (string chosen in both ? Bots.Pair : new[] { bot }) options.MakeBots.Add(Bots.Make(chosen));
            }
            foreach (string option in given.Keys.OrderBy(o => o, StringComparer.Ordinal))
            {
                // A count of nothing is not a count; a seed of 0 is a seed like any other. A count
                // past int.MaxValue is refused rather than quietly cut down to it, which would run
                // a different number of times from the one asked for and say nothing.
                bool counted = option == "--runs" || option == "--turn-limit";
                if (!ulong.TryParse(given[option], NumberStyles.None, CultureInfo.InvariantCulture, out ulong value)
                    || (counted && (value < 1 || value > int.MaxValue)))
                {
                    Console.Error.WriteLine(
                        $"{option} takes a whole number{(counted ? $" from 1 to {int.MaxValue}" : string.Empty)}, not \"{given[option]}\"");
                    return 2;
                }

                switch (option)
                {
                    case "--runs": options.Runs = (int)value; break;
                    case "--turn-limit": options.TurnLimit = (int)value; break;
                    case "--seed": options.FirstSeed = value; break;
                    default: watch = value; break;
                }
            }

            List<Diagnostic> loading = Loading(content, suppressed);
            Print(loading);
            List<Diagnostic> broken = Linter.Lint(content, Options(suppressed)).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Print(broken);
            if (content.Diagnostics.HasErrors || broken.Count > 0) return 1;

            // A bot plays a scenario by taking turns, and this content has none. It used to load
            // such a folder without a murmur and play it by calling EndTurn fifty times: the clock
            // was a TickClock and nothing ever ticked it, so every `on every` listener was silent,
            // every ability used once never came back, no battle could end, and the report said
            // "hp lost 0.0" against enemies dealing seventeen damage every three seconds, at exit 0.
            if (content.BuildRuleset(new DiagnosticBag()).Clock == ClockKind.Ticks)
            {
                Console.Error.WriteLine(
                    "`sim` cannot play this content: its ruleset says `clock ticks`, and a scenario is played by taking turns.\n" +
                    "When to act in continuous time is the game's own frame loop, not a bot's. Cover a real-time game with `test`\n" +
                    "blocks instead: `realtime <rate>` gives the test a tick clock and `tick <n>` is how time passes in it.");
                return 2;
            }

            // The baseline, loaded and checked before anything is played, so that a typo in
            // `--against` costs a second rather than the length of two simulations.
            ContentLibrary? baseline = null;
            string baselineWhere = string.Join(", ", against);
            if (against.Count > 0)
            {
                if (watch != null)
                {
                    Console.Error.WriteLine("--watch prints one run of one scenario, and --against compares two sets of many. Use one or the other.");
                    return 2;
                }

                baseline = Load(against);
                if (baseline == null) return 2;

                Print(Loading(baseline, suppressed));
                List<Diagnostic> wrong = Linter.Lint(baseline, Options(suppressed)).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                Print(wrong);
                if (baseline.Diagnostics.HasErrors || wrong.Count > 0)
                {
                    Console.Error.WriteLine($"the baseline at {baselineWhere} has errors, so nothing can be compared with it.");
                    return 1;
                }

                if (baseline.BuildRuleset(new DiagnosticBag()).Clock == ClockKind.Ticks)
                {
                    Console.Error.WriteLine($"the baseline at {baselineWhere} says `clock ticks`, and `sim` plays a scenario by taking turns.");
                    return 2;
                }

                string? theirName = againstName ?? name;
                if (!baseline.Scenarios.Any(s => theirName == null || s.Name.IndexOf(theirName, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    Console.Error.WriteLine(theirName == null
                        ? $"the baseline at {baselineWhere} has no `scenario` blocks, so there is nothing to compare with."
                        : $"no scenario in the baseline at {baselineWhere} has a name containing \"{theirName}\".");
                    return 2;
                }
            }
            else if (againstName != null)
            {
                Console.Error.WriteLine("--against-name names a scenario in the baseline, and there is no --against to look in.");
                return 2;
            }

            List<ScenarioDefinition> scenarios = content.Scenarios
                .Where(s => name == null || s.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (scenarios.Count == 0)
            {
                Console.Error.WriteLine(name == null
                    ? "no `scenario` blocks are loaded. A scenario states a deck, the fights in order and what to measure."
                    : $"no scenario's name contains \"{name}\"");
                return 1;
            }

            var runner = new ScenarioRunner(content, options);

            if (watch != null)
            {
                if (scenarios.Count > 1)
                {
                    Console.Error.WriteLine($"--watch plays one run of one scenario, and {scenarios.Count} are loaded. Pick one with --name <text>.");
                    return 2;
                }

                RunResult one = runner.Replay(scenarios[0], watch.Value, Console.WriteLine);
                return one.Error == null && !one.Stalled ? 0 : 1;
            }

            var results = new List<ScenarioOutcome>();
            foreach (ScenarioDefinition scenario in scenarios)
            {
                ScenarioOutcome outcome = runner.Run(scenario);
                SimReport.Print(outcome);
                results.Add(outcome);
            }

            SimReport.Summary(results);

            if (baseline != null && !Compare(baseline, baselineWhere, results, options, againstName ?? name)) return 2;

            return results.Any(r => r.Failed) ? 1 : 0;
        }

        /// <summary>
        /// Plays the baseline content with the same options and sets the two against each other,
        /// scenario by scenario. It never changes the exit code: the comparison is a reading of
        /// what a change did, and what fails <c>sim</c> is still what the content the command was
        /// pointed at did on its own.
        /// </summary>
        /// <remarks>
        /// Both sides are played here rather than in two commands so that they cannot disagree
        /// about anything but the content: one <see cref="ScenarioOptions"/>, so the same bots in
        /// the same order, the same first seed, the same turn limit and the same run count, and
        /// run <c>n</c> is seeded identically on both sides. Two invocations of <c>sim</c> could
        /// differ in any of those and the difference would land in the numbers unannounced.
        /// </remarks>
        private static bool Compare(
            ContentLibrary baseline,
            string baselineWhere,
            IReadOnlyList<ScenarioOutcome> subject,
            ScenarioOptions options,
            string? name)
        {
            List<ScenarioDefinition> theirs = baseline.Scenarios
                .Where(s => name == null || s.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            // Emptiness was refused before either side was played; reaching here with none would
            // mean the content changed underneath the command.
            if (theirs.Count == 0) return false;

            var runner = new ScenarioRunner(baseline, options);
            var played = new Dictionary<string, ScenarioOutcome>(StringComparer.OrdinalIgnoreCase);
            foreach (ScenarioDefinition scenario in theirs)
            {
                if (!played.ContainsKey(scenario.Name)) played[scenario.Name] = runner.Run(scenario);
            }

            var comparisons = new List<ScenarioComparison>();
            var unmatched = new List<string>();
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Two scenarios with different names pair when each side has been narrowed to exactly
            // one, which is how two decks in the same folder are compared: `--name "Wide" and
            // `--against-name "Tall"`. Anything else pairs by name, because a comparison of two
            // scenarios that are not the same scenario is a number nobody can read.
            bool oneToOne = subject.Count == 1 && played.Count == 1
                && !played.ContainsKey(subject[0].Scenario.Name);

            foreach (ScenarioOutcome ours in subject)
            {
                ScenarioOutcome? theirsOutcome = oneToOne
                    ? played.Values.First()
                    : played.TryGetValue(ours.Scenario.Name, out ScenarioOutcome? found) ? found : null;

                if (theirsOutcome == null) { unmatched.Add(ours.Scenario.Name); continue; }

                matched.Add(theirsOutcome.Scenario.Name);
                ScenarioComparison comparison = Comparisons.Of(theirsOutcome, ours);
                comparisons.Add(comparison);
                SimCompareReport.Print(comparison, baselineWhere);
            }

            List<string> extra = played.Keys.Where(n => !matched.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            SimCompareReport.Summary(comparisons, unmatched, extra);
            return true;
        }

        private static int Describe(ContentLibrary content, string? name)
        {
            if (!Report(content)) return 1;

            var builder = new DescriptionBuilder(content);
            var definitions = content.Definitions
                .Where(d => d.IsThing)
                .Where(d => name == null || string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.KindName, StringComparer.Ordinal)
                .ThenBy(d => d.Name, StringComparer.Ordinal)
                .ToList();

            if (definitions.Count == 0)
            {
                Console.Error.WriteLine(name == null ? "no definitions loaded" : $"nothing called \"{name}\" is loaded");
                return 1;
            }

            foreach (EntityDefinition definition in definitions)
            {
                Description description = builder.Describe(definition);
                string cost = description.Cost == null ? string.Empty : $" ({description.Cost.Text})";
                Console.WriteLine($"{description.Name} [{definition.KindName}]{cost}");
                if (!description.IsEmpty) Console.WriteLine("  " + description.ToPlainText());
                if (description.Flavour != null) Console.WriteLine($"  \"{description.Flavour}\"");
                foreach (KeywordTooltip tooltip in description.Tooltips) Console.WriteLine("    " + tooltip);
                Console.WriteLine();
            }

            return 0;
        }

        /// <summary>The REPL: run DSL lines against a live game.</summary>
        private static int Repl(ContentLibrary content)
        {
            if (!Report(content)) return 1;

            var runtime = new CardRuntime(content, new RuntimeOptions { Trace = true });
            Entity player = runtime.CreatePlayer();
            Entity dummy = runtime.State.Spawn("Dummy", EntityKind.Actor, null, Team.Enemy, Zones.Board);
            dummy.SetBase("max_hp", 100);
            dummy.SetBase("hp", 100);
            dummy.SetBase("block", 0);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.Interpreter.Logged += message => Console.WriteLine("log: " + message);

            Console.WriteLine("cantrip repl. Statements run as the player, targeting the Dummy.");
            Console.WriteLine("Commands: :state  :trace  :quit");

            while (true)
            {
                Console.Write("> ");
                string? line = Console.ReadLine();
                if (line == null || line.Trim() == ":quit") return 0;
                if (line.Trim().Length == 0) continue;

                if (line.Trim() == ":state")
                {
                    PrintState(runtime);
                    continue;
                }

                if (line.Trim() == ":trace")
                {
                    Console.Write(runtime.State.Trace.FormatTree());
                    runtime.State.Trace.Clear();
                    continue;
                }

                try
                {
                    runtime.Execute(line, player, dummy.IsAlive ? dummy : null);
                    PrintState(runtime);
                }
                catch (Exception e) when (e is RuntimeError || e is DslException)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(e.Message);
                    Console.ResetColor();
                }
            }
        }

        private static void PrintState(CardRuntime runtime)
        {
            foreach (Entity actor in runtime.State.Entities.Where(e => e.Kind == EntityKind.Actor && !e.IsRemoved))
            {
                string statuses = string.Join(", ", actor.Attached.Where(a => !a.IsRemoved).Select(a => $"{a.Name} {a.GetInt("stacks")}"));
                Console.WriteLine(
                    $"  {actor.Name,-12} hp {actor.GetInt("hp")}/{actor.GetInt("max_hp")}  block {actor.GetInt("block")}" +
                    (actor.HasStat("energy") ? $"  energy {actor.GetInt("energy")}" : string.Empty) +
                    (actor.IsDead ? "  DEAD" : string.Empty) +
                    (statuses.Length > 0 ? $"  [{statuses}]" : string.Empty));
            }
        }
    }
}
