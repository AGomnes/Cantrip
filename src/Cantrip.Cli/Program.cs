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
using Cantrip.Sim.Scenarios;
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
  --turn-limit N     turns one battle may take before the run counts as a stall (default 50)
  --watch SEED       play one run of one scenario and print every turn, play and statement

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
            if (numbers.Count > 0 && command != "sim")
            {
                Console.Error.WriteLine($"{string.Join(", ", numbers.Keys.OrderBy(o => o, StringComparer.Ordinal))} only applies to `sim`");
                return 2;
            }

            ContentLibrary? content = Load(paths);
            if (content == null) return 2;

            switch (command)
            {
                case "validate": return Validate(content, suppressed);
                case "lint": return Lint(content, suppressed, warningsAsErrors);
                case "test": return Test(content, filter, trace);
                case "sim": return Sim(content, filter, suppressed, numbers);
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
        private static int Sim(ContentLibrary content, string? name, ISet<string> suppressed, IReadOnlyDictionary<string, string> given)
        {
            // The options first: bad usage is bad usage, whatever the content turns out to be.
            var options = new ScenarioOptions();
            ulong? watch = null;
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

            var results = new List<ScenarioResult>();
            foreach (ScenarioDefinition scenario in scenarios)
            {
                ScenarioResult result = runner.Run(scenario);
                SimReport.Print(result);
                results.Add(result);
            }

            SimReport.Summary(results);
            return results.Any(r => r.Failed) ? 1 : 0;
        }

        private static int Describe(ContentLibrary content, string? name)
        {
            if (!Report(content)) return 1;

            var builder = new DescriptionBuilder(content);
            var definitions = content.Definitions
                .Where(d => d.KindName != "resource")
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
