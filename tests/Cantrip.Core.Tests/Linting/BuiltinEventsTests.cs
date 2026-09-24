using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Cantrip.Content;
using Cantrip.Runtime;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>Keeps the event and verb catalogues the linter relies on in step with the code.</summary>
    public sealed class BuiltinEventsTests
    {
        [Fact]
        public void Every_event_the_core_raises_by_name_is_listed()
        {
            string core = Path.Combine(LintTestPaths.RepositoryRoot(), "src", "Cantrip.Core");
            var raised = Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories)
                .SelectMany(file => Regex.Matches(File.ReadAllText(file), "new GameEvent\\(\"([a-z_]+)\"\\)").Select(m => m.Groups[1].Value))
                .Distinct()
                .OrderBy(name => name)
                .ToList();

            Assert.NotEmpty(raised);
            var missing = raised.Where(name => !BuiltinEvents.IsBuiltin(name)).ToList();
            Assert.True(missing.Count == 0, "not in BuiltinEvents: " + string.Join(", ", missing));
        }

        [Fact]
        public void Every_listed_event_is_actually_raised()
        {
            string core = Path.Combine(LintTestPaths.RepositoryRoot(), "src", "Cantrip.Core");
            string sources = string.Join("\n", Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.EndsWith("BuiltinEvents.cs"))
                .Select(File.ReadAllText));

            // Card flow events reach `new GameEvent(eventName)` through MoveCard, so any use of the
            // name as a string literal counts.
            var stale = BuiltinEvents.Names.Where(name => !sources.Contains($"\"{name}\"")).ToList();
            Assert.True(stale.Count == 0, "listed but never raised: " + string.Join(", ", stale));
        }

        [Fact]
        public void The_verb_catalogue_matches_the_registered_verbs()
        {
            var runtime = new CardRuntime(new ContentLibrary());
            var registered = runtime.Interpreter.VerbNames.OrderBy(v => v).ToList();

            var unlisted = registered.Where(v => !BuiltinEvents.IsKnownVerb(v)).ToList();
            Assert.True(unlisted.Count == 0, "registered but not in BuiltinEvents: " + string.Join(", ", unlisted));
        }

        /// <summary>
        /// Exactly one word means two things, and it is resolved by where the line is written: a
        /// <c>play</c> in a test's own body is the test's, and a <c>play</c> anywhere else is the
        /// rules'. The allow-list is this test, so a second overlap still fails here.
        /// </summary>
        [Fact]
        public void Test_verbs_do_not_shadow_rule_verbs()
        {
            var allowed = new[] { "play" };

            var runtime = new CardRuntime(new ContentLibrary());
            var clashes = DslTestRunner.TestVerbs
                .Where(v => runtime.Interpreter.IsVerb(v))
                .Where(v => !allowed.Contains(v, StringComparer.OrdinalIgnoreCase))
                .ToList();
            Assert.True(clashes.Count == 0, "test verbs that hide real verbs: " + string.Join(", ", clashes));

            // And the allowed one really is both, or the allowance is hiding nothing.
            foreach (string verb in allowed) Assert.True(runtime.Interpreter.IsVerb(verb), verb + " is no longer a rule verb");
        }
    }
}
