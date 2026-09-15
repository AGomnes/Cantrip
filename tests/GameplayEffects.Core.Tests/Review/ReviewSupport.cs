using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameplayEffects.Content;
using GameplayEffects.Runtime;

namespace GameplayEffects.Tests.Review
{
    /// <summary>
    /// Shared fixtures for the adversarial review tests. Every test builds its own runtime from
    /// inline content so that a defect in one area cannot leak state into another test.
    /// </summary>
    internal static class ReviewSupport
    {
        /// <summary>Loads content, throwing on any content error, and creates the default player.</summary>
        public static CardRuntime NewRuntime(string dsl, RuntimeOptions? options = null)
        {
            CardRuntime runtime = CardRuntime.FromText(dsl, options);
            runtime.CreatePlayer();
            return runtime;
        }

        /// <summary>A plain enemy with no definition, the same shape the DSL runner's <c>enemy hp N</c> builds.</summary>
        public static Entity Enemy(CardRuntime runtime, int hp = 20, string name = "Enemy")
        {
            Entity enemy = runtime.State.Spawn(name, EntityKind.Actor, null, Team.Enemy, Zones.Board);
            enemy.SetBase("max_hp", hp);
            enemy.SetBase("hp", hp);
            enemy.SetBase("block", 0);
            return enemy;
        }

        /// <summary>Loads content for the DSL test runner, throwing on content errors.</summary>
        public static ContentLibrary LoadContent(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl);
            content.Diagnostics.ThrowIfErrors();
            return content;
        }

        public static int HandCount(CardRuntime runtime) => runtime.State.ZoneOf(runtime.Player, Zones.Hand).Count;
    }

    /// <summary>
    /// A chooser that breaks the contract by answering null, as a half-wired UI binding might. The
    /// interpreter already defends against this; the card runtime's target prompt does not.
    /// </summary>
    internal sealed class ReviewNullChooser : IChoiceProvider
    {
        public IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state) => null!;
    }

    /// <summary>
    /// Runs the gedsl CLI in a child process. Defects that overflow the stack kill the whole
    /// process, so they can only be observed from outside it without taking the test host down.
    /// </summary>
    internal static class ReviewCli
    {
        public static (int ExitCode, string Output) Run(string command, string dsl)
        {
            string cli = FindCli();
            string folder = Path.Combine(Path.GetTempPath(), "ge-review-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, "content.ge"), dsl);

                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                start.ArgumentList.Add(cli);
                start.ArgumentList.Add(command);
                start.ArgumentList.Add(folder);

                using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start gedsl.");
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120_000))
                {
                    process.Kill(true);
                    throw new TimeoutException("gedsl did not finish within two minutes.");
                }
                process.WaitForExit();
                return (process.ExitCode, stdout.Result + stderr.Result);
            }
            finally
            {
                try { Directory.Delete(folder, true); }
                catch (Exception) { /* best effort cleanup of a temp folder */ }
            }
        }

        /// <summary>First part of a process transcript, so a stack overflow dump does not flood the test log.</summary>
        public static string Head(string output) => output.Length <= 1500 ? output : output.Substring(0, 1500) + "...";

        private static string FindCli()
        {
            for (DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (!File.Exists(Path.Combine(dir.FullName, "GameplayEffects.sln"))) continue;

                string bin = Path.Combine(dir.FullName, "src", "GameplayEffects.Cli", "bin");
                string? cli = Directory.Exists(bin)
                    ? Directory.GetFiles(bin, "gedsl.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                    : null;
                return cli ?? throw new InvalidOperationException("gedsl.dll not found; build the solution before running the review tests.");
            }
            throw new InvalidOperationException("Could not find GameplayEffects.sln above the test output folder.");
        }
    }
}
