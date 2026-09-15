using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameplayEffects;
using GameplayEffects.Content;
using GameplayEffects.Descriptions;
using GameplayEffects.Diagnostics;
using GameplayEffects.Linting;
using GameplayEffects.Runtime;
using GameplayEffects.Testing;

namespace GameplayEffects.Cli
{
    internal static class Program
    {
        private const string Usage = @"gedsl - tools for the gameplay effects DSL

usage:
  gedsl validate <path>...          parse and load content, report problems
  gedsl lint <path>... [options]    load content and run static checks
  gedsl test <path>... [options]    run the `test` blocks in content
  gedsl describe <path>... [--name <name>]
                                    print generated descriptions (all definitions, or one)
  gedsl repl <path>...              load content and run DSL statements interactively

paths may be files or folders (folders load every *.ge file, recursively).

lint options:
  --suppress <codes>  comma-separated diagnostic codes to leave out, e.g. GE306,GE310

test options:
  --filter <text>    only run tests whose name contains <text>
  --trace            print the causality trace for failing tests

exit codes: 0 success, 1 content errors or failing tests, 2 bad usage";

        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

            string command = args[0];
            var paths = new List<string>();
            string? filter = null;
            bool trace = false;
            var suppressed = new List<string>();

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--filter" when i + 1 < args.Length: filter = args[++i]; break;
                    case "--name" when i + 1 < args.Length: filter = args[++i]; break;
                    case "--trace": trace = true; break;
                    case "--suppress" when i + 1 < args.Length:
                        suppressed.AddRange(args[++i].Split(',').Select(c => c.Trim()).Where(c => c.Length > 0));
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

            ContentLibrary? content = Load(paths);
            if (content == null) return 2;

            switch (command)
            {
                case "validate": return Validate(content);
                case "lint": return Lint(content, suppressed);
                case "test": return Test(content, filter, trace);
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

        private static int Lint(ContentLibrary content, IEnumerable<string> suppressed)
        {
            bool loaded = Report(content);

            var options = new LintOptions();
            foreach (string code in suppressed) options.Suppressed.Add(code);

            IReadOnlyList<Diagnostic> findings = Linter.Lint(content, options);
            Print(findings);

            int errors = findings.Count(d => d.Severity == DiagnosticSeverity.Error) + content.Diagnostics.Errors.Count();
            int warnings = findings.Count(d => d.Severity == DiagnosticSeverity.Warning) + content.Diagnostics.Warnings.Count();
            int infos = findings.Count(d => d.Severity == DiagnosticSeverity.Info);
            Console.WriteLine($"{errors} error(s), {warnings} warning(s), {infos} note(s)");

            return loaded && errors == 0 ? 0 : 1;
        }

        private static int Validate(ContentLibrary content)
        {
            bool ok = Report(content);
            int definitions = content.Definitions.Count();
            DiagnosticBag diagnostics = content.Diagnostics;
            Console.WriteLine(
                $"{content.Files.Count()} file(s), {definitions} definition(s), {content.Verbs.Count()} verb(s), {content.Tests.Count} test(s): " +
                $"{diagnostics.Errors.Count()} error(s), {diagnostics.Warnings.Count()} warning(s)");
            return ok ? 0 : 1;
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

        /// <summary>The REPL from section 5: run DSL lines against a live game.</summary>
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

            Console.WriteLine("gedsl repl. Statements run as the player, targeting the Dummy.");
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
