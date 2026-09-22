using System.Linq;
using Cantrip.Content;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Review
{
    /// <summary>
    /// <c>cantrip lint --warnings-as-errors</c>, which lets CI fail on a new warning, and <c>log</c>
    /// output in a test, which used to be shown nowhere.
    /// </summary>
    public sealed class CliWarningsAndLogTests
    {
        /// <summary>Loads cleanly, with one lint warning (CT303, a tag nothing has) and one note (CT305).</summary>
        private const string OneWarning = """
            card Zap
              cost 0
              effect:
                draw count(hand where tag:frost)
                emit sparked
            """;

        [Fact]
        public void Lint_passes_with_warnings_unless_told_to_fail_on_them()
        {
            (int plain, string plainOutput) = ReviewCli.Run("lint", OneWarning);
            Assert.True(plain == 0, $"exit code {plain}:\n{ReviewCli.Head(plainOutput)}");
            Assert.Contains("0 error(s), 1 warning(s), 1 note(s)", plainOutput);

            (int strict, string strictOutput) = ReviewCli.Run("lint", OneWarning, "--warnings-as-errors");
            Assert.True(strict == 1, $"exit code {strict}:\n{ReviewCli.Head(strictOutput)}");
            Assert.Contains("warning CT303", strictOutput);
            Assert.Contains("--warnings-as-errors", strictOutput);
        }

        [Fact]
        public void Notes_do_not_fail_and_suppressed_warnings_do_not_count()
        {
            (int exitCode, string output) = ReviewCli.Run("lint", OneWarning, "--warnings-as-errors", "--suppress", "CT303");
            Assert.True(exitCode == 0, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("0 error(s), 0 warning(s), 1 note(s)", output);
        }

        [Fact]
        public void Suppress_also_leaves_out_a_warning_from_loading_but_never_an_error()
        {
            // CT0105: an unknown status flag, a warning found while loading.
            const string dsl = "status Glow\n  flags shiny\n";

            (int strict, string strictOutput) = ReviewCli.Run("lint", dsl, "--warnings-as-errors");
            Assert.True(strict == 1, $"exit code {strict}:\n{ReviewCli.Head(strictOutput)}");
            Assert.Contains("warning CT0105", strictOutput);

            (int suppressed, string suppressedOutput) = ReviewCli.Run("lint", dsl, "--warnings-as-errors", "--suppress", "CT0105");
            Assert.True(suppressed == 0, $"exit code {suppressed}:\n{ReviewCli.Head(suppressedOutput)}");
            Assert.DoesNotContain("CT0105", suppressedOutput);

            // CT0103, an unknown stacking mode, is an error, and still fails.
            (int error, string errorOutput) = ReviewCli.Run("lint", "status Glow\n  stacking sideways\n", "--suppress", "CT0103");
            Assert.True(error == 1, $"exit code {error}:\n{ReviewCli.Head(errorOutput)}");
            Assert.Contains("error CT0103", errorOutput);
        }

        [Fact]
        public void Warnings_as_errors_belongs_to_lint_alone()
        {
            (int exitCode, string output) = ReviewCli.Run("test", OneWarning, "--warnings-as-errors");
            Assert.True(exitCode == 2, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
        }

        private const string Logging = """
            test "a log line goes into the trace"
              enemy hp 30
              log "enemy hp is" enemy.hp
              expect enemy.hp == 29
            """;

        [Fact]
        public void A_traced_test_records_what_log_wrote()
        {
            ContentLibrary content = ContentLibrary.FromText(Logging, "log.cantrip");

            DslTestResult traced = new DslTestRunner(content) { Trace = true }.RunAll().Single();
            Assert.False(traced.Passed);
            Assert.Contains("[log] enemy hp is 30", traced.Trace);

            // The trace, and so the log line, is under the `log` statement that wrote it.
            string[] lines = traced.Trace!.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            int statement = System.Array.FindIndex(lines, l => l.StartsWith("[verb] log"));
            Assert.True(statement >= 0 && lines[statement + 1] == "  [log] enemy hp is 30", traced.Trace);

            DslTestResult plain = new DslTestRunner(content).RunAll().Single();
            Assert.Null(plain.Trace);
        }

        [Fact]
        public void Test_with_trace_prints_log_lines_of_a_failing_test()
        {
            (int exitCode, string output) = ReviewCli.Run("test", Logging, "--trace");
            Assert.True(exitCode == 1, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
            Assert.Contains("[log] enemy hp is 30", output);
        }
    }
}
