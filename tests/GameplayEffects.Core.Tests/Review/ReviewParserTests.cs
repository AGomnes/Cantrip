using System;
using System.Text;
using System.Threading.Tasks;
using GameplayEffects.Content;
using Xunit;

namespace GameplayEffects.Tests.Review
{
    /// <summary>
    /// Robustness of the front end and the sandbox against hostile or broken content. Regression
    /// tests: each test's summary records a defect a review found, now fixed.
    /// </summary>
    public sealed class ReviewParserTests
    {
        private static readonly string[] Vocabulary =
        {
            "card", "status", "relic", "enemy", "ability", "verb", "ruleset", "test", "resource", "\"X\"", "Y", "effect", "move",
            "on", "modify", "damage", "cost", "hp", "stacking", "decay", "max_stacks", "pattern", "tags", "flags", "immune",
            "events", "loops", "ordering", "where", "of", "to", "from", "for", "as", "by", "into", "tag:fire", "source:self",
            "target", "self", "owner", "event", "if", "else", "repeat", "each", "in", "chance", "next", "turn", "until", "once",
            "let", "phase", "when",
            "per", "priority", "random", "lowest", "highest", "all", "other", "within", "deal", "apply", "remove", "stacks",
            "not", "and", "or", "has", "is", "6", "1.5", "40%", "3s", "x1.5", "clamp", "set", "0..10", ":", ",", ".", "..",
            "(", ")", "[", "]", "+", "-", "*", "/", "%", "=", "==", "!=", "<", "<=", ">", ">=", "+=", "-=", "*=", "->", "!",
            "\"open", "'q'", "#note", "\t", "\n", "\n", "\n  ", "\n  ", "\n    ", "\n      ", "\n   ",
        };

        /// <summary>
        /// Random token soup through the full load path (lexer, parser, definition building,
        /// ruleset validation). Every input must terminate and report problems as diagnostics.
        /// The seed is fixed so a failure is reproducible.
        /// </summary>
        [Fact]
        public async Task Loading_random_token_soup_terminates_without_exceptions()
        {
            var rng = new Rng(20260911);
            string current = string.Empty;
            Exception? failure = null;

            Task run = Task.Run(() =>
            {
                for (int i = 0; i < 4000 && failure == null; i++)
                {
                    current = RandomSource(rng);
                    try
                    {
                        ContentLibrary.FromText(current, "fuzz.ge");
                    }
                    catch (Exception e)
                    {
                        failure = e;
                    }
                }
            });

            Task finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(90)));
            Assert.True(finished == run, "loading did not terminate on: " + Escape(current));
            Assert.True(failure == null, $"loading threw {failure?.GetType().Name} on: {Escape(current)}\n{failure}");
        }

        /// <summary>
        /// The step limit is meant to turn runaway content into an error, but a recursive content
        /// verb exhausts the C# stack long before 100,000 steps and kills the process.
        /// </summary>
        [Fact]
        [Trait("Regression", "recursive-verb-stack-overflow")]
        public void Runaway_verb_recursion_fails_the_test_instead_of_crashing()
        {
            (int exitCode, string output) = ReviewCli.Run("test", "verb echo(t):\n  echo t\n\ntest \"runaway recursion\"\n  echo 1\n");

            Assert.True(exitCode == 1 && output.Contains("FAIL"), $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
        }

        /// <summary>The recursive-descent parser has no depth limit, so deeply nested input overflows the stack.</summary>
        [Fact]
        [Trait("Regression", "parser-stack-overflow-on-deep-nesting")]
        public void Deeply_nested_expression_is_a_diagnostic_not_a_crash()
        {
            string dsl = "card \"X\"\n  cost " + new string('(', 20000) + "1\n";

            (int exitCode, string output) = ReviewCli.Run("validate", dsl);

            Assert.True(exitCode == 0 || exitCode == 1, $"exit code {exitCode}:\n{ReviewCli.Head(output)}");
        }

        private static string RandomSource(Rng rng)
        {
            var text = new StringBuilder();
            int count = rng.NextInt(1, 80);
            for (int i = 0; i < count; i++)
            {
                text.Append(Vocabulary[rng.NextInt(0, Vocabulary.Length - 1)]);
                if (rng.NextInt(0, 3) > 0) text.Append(' ');
            }
            return text.ToString();
        }

        private static string Escape(string text) => text.Replace("\n", "\\n").Replace("\t", "\\t");
    }
}
