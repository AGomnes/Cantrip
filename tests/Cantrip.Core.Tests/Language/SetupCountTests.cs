using System.Collections.Generic;
using System.Linq;
using Cantrip;
using Cantrip.Content;
using Cantrip.Runtime;
using Cantrip.Syntax;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Language
{
    /// <summary>
    /// A number before a name in a setup line repeats it: <c>deck 4 Zap, 4 Ward</c> is eight cards.
    /// Before this, the number was dropped without a word from anywhere, so the line put in one of
    /// each and quietly measured a different deck from the one written.
    /// </summary>
    public sealed class SetupCountTests
    {
        private const string Cards = @"
card Zap
  cost 1
  target enemy
  effect:
    deal 6 to target

card Ward
  cost 1
  effect:
    block 5
";

        [Theory]
        [InlineData("deck 4 Zap", Zones.Draw, 4)]
        [InlineData("hand 3 Zap", Zones.Hand, 3)]
        [InlineData("discard_pile 2 Zap", Zones.Discard, 2)]
        [InlineData("deck Zap", Zones.Draw, 1)]
        public void A_count_before_a_card_repeats_it(string line, string zone, int expected)
        {
            CardRuntime runtime = Setup(line);
            Assert.Equal(expected, runtime.State.ZoneOf(runtime.Player, zone).Count);
        }

        [Fact]
        public void Each_name_on_a_line_keeps_its_own_count()
        {
            CardRuntime runtime = Setup("deck 4 Zap, 4 Ward");

            List<string> draw = runtime.State.ZoneOf(runtime.Player, Zones.Draw).Select(c => c.Name).ToList();
            Assert.Equal(8, draw.Count);
            Assert.Equal(4, draw.Count(n => n == "Zap"));
            Assert.Equal(4, draw.Count(n => n == "Ward"));
        }

        /// <summary>
        /// The parser puts what follows a comma into a clause rather than an argument, so the count
        /// for the second name arrives apart from the name it belongs to. Only the spans still know
        /// the order, and the order is the whole meaning of the line.
        /// </summary>
        [Fact]
        public void A_count_belongs_to_the_name_written_after_it()
        {
            CardRuntime runtime = Setup("deck 2 Zap, Ward, 3 Zap");

            List<string> draw = runtime.State.ZoneOf(runtime.Player, Zones.Draw).Select(c => c.Name).ToList();
            Assert.Equal(5, draw.Count(n => n == "Zap"));
            Assert.Equal(1, draw.Count(n => n == "Ward"));
        }

        [Theory]
        [InlineData("deck 0 Zap")]
        [InlineData("deck 2.5 Zap")]
        [InlineData("deck 2 3 Zap")]
        [InlineData("deck 5000 Zap")]
        public void A_number_that_is_not_a_count_fails_the_line(string line)
        {
            DslTestResult result = Fails(line);
            Assert.False(result.Passed);
            Assert.Contains("is not a count", result.Failure);
        }

        [Fact]
        public void A_count_with_nothing_after_it_fails_the_line()
        {
            DslTestResult result = Fails("deck 4");
            Assert.False(result.Passed);
            Assert.Contains("`4` has no name after it to repeat", result.Failure);
        }

        [Fact]
        public void A_count_works_the_same_in_a_scenario_and_in_a_test()
        {
            DslTestResult result = Run(@"
test ""counted deck""
  deck 4 Zap, 4 Ward
  expect count(draw) == 8
");
            Assert.True(result.Passed, result.Failure);
        }

        /// <summary>Other setup lines that list names read a count the same way.</summary>
        [Fact]
        public void A_relic_line_repeats_too()
        {
            ContentLibrary content = Load(Cards + @"
relic ""Ember Charm""
  on battle_start:
    block 1
");
            var runtime = new CardRuntime(content);
            var session = new SetupSession(runtime, new ScriptedChooser());
            foreach (StatementNode statement in CardRuntime.ParseStatements(@"relic 2 ""Ember Charm""").Statements) session.Run(statement);

            Assert.Equal(2, runtime.State.ZoneOf(runtime.Player, Zones.Relics).Count);
        }

        private static CardRuntime Setup(string line)
        {
            var runtime = new CardRuntime(Load(Cards));
            var session = new SetupSession(runtime, new ScriptedChooser());
            foreach (StatementNode statement in CardRuntime.ParseStatements(line).Statements) session.Run(statement);
            return runtime;
        }

        private static DslTestResult Fails(string line) => Run("test \"counted\"\n  " + line + "\n");

        private static DslTestResult Run(string test)
        {
            ContentLibrary content = Load(Cards + test);
            return new DslTestRunner(content).RunAll().Single();
        }

        private static ContentLibrary Load(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl);
            content.Diagnostics.ThrowIfErrors();
            return content;
        }
    }
}
