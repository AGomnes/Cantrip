using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Cantrip.Syntax;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Review
{
    /// <summary>
    /// A qualifier's value in quotes, as in <c>card:"Fire Bolt"</c> and <c>name:"Strike+"</c>, for a
    /// name that is not one word. Before, only one word could follow the colon, so such a card could
    /// not be named in a qualifier at all.
    /// </summary>
    public sealed class QuotedQualifierTests
    {
        private const string Cards = """
            card "Fire Bolt"
              cost 1
              target enemy
              tags attack
              effect:
                deal 3 to target

            card "Strike+"
              cost 1
              target enemy
              tags attack
              effect:
                deal 9 to target

            card Strike
              cost 1
              target enemy
              tags attack
              effect:
                deal 6 to target

            """;

        private static QualifiedExpr Qualifier(string expression)
        {
            ContentLibrary content = ContentLibrary.FromText("card Probe\n  cost 0\n  effect:\n    log " + expression + "\n", "probe.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            return Assert.IsType<QualifiedExpr>(content.Find("Probe", "card")!.Effect!.Statements.OfType<CommandNode>().Single().Arguments.Single());
        }

        [Theory]
        [InlineData("card:\"Fire Bolt\"", "card", "Fire Bolt")]
        [InlineData("name:\"Strike+\"", "name", "Strike+")]
        [InlineData("name:'Strike+'", "name", "Strike+")]
        [InlineData("tag:\"on fire\"", "tag", "on fire")]
        [InlineData("card:Strike", "card", "Strike")]
        public void A_quoted_value_is_read_whole(string written, string qualifier, string name)
        {
            QualifiedExpr read = Qualifier(written);

            Assert.Equal(qualifier, read.Qualifier);
            Assert.Equal(name, read.Name);
        }

        [Theory]
        [InlineData("card:\"Fire Bolt\"")]
        [InlineData("name:\"Strike+\"")]
        [InlineData("card:Strike")]
        public void A_qualifier_prints_back_as_it_can_be_read(string written)
        {
            Assert.Equal(written, AstPrinter.Print(Qualifier(written)));
        }

        [Fact]
        public void Quoted_names_match_at_runtime()
        {
            ContentLibrary content = ContentLibrary.FromText(Cards + """
                relic Kindler
                  on card_played(card:"Fire Bolt"):
                    gain 1 heat

                card Hone
                  cost 0
                  effect:
                    choose 1 from hand where name:"Strike+" as picked
                    destroy picked

                test "a listener filter names a card with a space in its name"
                  enemy hp 30
                  relic Kindler
                  play "Fire Bolt" on enemy
                  play "Strike+" on enemy
                  play Strike on enemy
                  expect player.heat == 1

                test "a where filter names a card with a symbol in its name"
                  enemy hp 30
                  hand "Strike+", Strike, "Strike+"
                  expect count(hand where name:"Strike+") == 2
                  expect count(hand where card:Strike) == 1
                  play Hone
                  expect count(hand where name:"Strike+") == 1
                  expect count(hand) == 2
                """, "quoted.cantrip");

            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            IReadOnlyList<DslTestResult> results = new DslTestRunner(content).RunAll();
            Assert.Equal(2, results.Count);
            foreach (DslTestResult result in results) Assert.True(result.Passed, result.ToString());
        }

        [Fact]
        public void The_linter_checks_a_quoted_card_name()
        {
            ContentLibrary content = ContentLibrary.FromText(Cards + """
                relic Kindler
                  on card_played(card:"Fire Bolt"):
                    gain 1 heat
                  on card_played(card:"Fire Blot"):
                    gain 1 heat
                """, "quoted.cantrip");

            Diagnostic unknown = Assert.Single(Linter.Lint(content), d => d.Code == Linter.UnknownName);
            Assert.Equal(DiagnosticSeverity.Error, unknown.Severity);
            Assert.Contains("`Fire Blot`", unknown.Message);
            Assert.Equal("Fire Bolt", unknown.Suggestion);
        }

        /// <summary>
        /// A qualifier never starts a line, so a property written with no space before its quoted
        /// value keeps working as the property it is.
        /// </summary>
        [Fact]
        public void A_property_written_without_a_space_is_still_a_property()
        {
            ContentLibrary content = ContentLibrary.FromText("""
                card Gem
                  cost 0
                  target:"enemy"
                  rarity:"rare"
                  effect:
                    deal 1 to target
                """, "property.cantrip");

            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            EntityDefinition gem = content.Find("Gem", "card")!;
            Assert.Equal("enemy", gem.Word("target"));
            Assert.Equal("rare", gem.Word("rarity"));
        }
    }
}
