using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GameplayEffects.Content;
using GameplayEffects.Runtime;
using GameplayEffects.Testing;
using Xunit;

namespace GameplayEffects.Tests.Linting
{
    /// <summary>Keeps the event and verb catalogues the linter relies on in step with the code.</summary>
    public sealed class BuiltinEventsTests
    {
        [Fact]
        public void Every_event_the_core_raises_by_name_is_listed()
        {
            string core = Path.Combine(LintTestPaths.RepositoryRoot(), "src", "GameplayEffects.Core");
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
            string core = Path.Combine(LintTestPaths.RepositoryRoot(), "src", "GameplayEffects.Core");
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

        [Fact]
        public void Test_verbs_do_not_shadow_rule_verbs()
        {
            var runtime = new CardRuntime(new ContentLibrary());
            var clashes = DslTestRunner.TestVerbs.Where(v => runtime.Interpreter.IsVerb(v)).ToList();
            Assert.True(clashes.Count == 0, "test verbs that hide real verbs: " + string.Join(", ", clashes));
        }
    }
}
