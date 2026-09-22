using System.Linq;
using Cantrip.Content;
using Cantrip.Descriptions;
using Cantrip.Linting;
using Cantrip.Syntax;
using Xunit;

namespace Cantrip.Tests.Descriptions
{
    /// <summary>
    /// How generated rules text writes a length with a unit, and an enemy move that only one phase
    /// uses.
    /// </summary>
    public sealed class UnitAndPhaseTextTests
    {
        private const string Nova = """
            status Weak
              stacking duration

            status Slow
              stacking intensity

            ability Nova
              cooldown 2 turns
              effect:
                apply Weak 3 for 2 turns to enemies
                apply Slow 1 for 3 seconds to enemies
                apply Slow 1 for 1 turn to enemies
                apply Slow 1 for 250ms to enemies
                apply Slow 1 for 3s to enemies
            """;

        private static string Describe(string dsl, string name, string kind)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl, "text.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            return new DescriptionBuilder(content).Describe(content.Find(name, kind)!).ToPlainText();
        }

        [Fact]
        public void A_unit_that_is_a_word_is_written_after_a_space()
        {
            string text = Describe(Nova, "Nova", "ability");

            Assert.Equal(
                "Apply 3 Weak to ALL enemies for 2 turns. Apply 1 Slow to ALL enemies for 3 seconds. " +
                "Apply 1 Slow to ALL enemies for 1 turn. Apply 1 Slow to ALL enemies for 250ms. " +
                "Apply 1 Slow to ALL enemies for 3s. Cooldown 2 turns.",
                text);
        }

        [Fact]
        public void A_printed_length_parses_back_to_the_same_number()
        {
            ContentLibrary content = ContentLibrary.FromText(Nova, "text.cantrip");
            var lengths = content.Find("Nova", "ability")!.Effect!.Statements
                .OfType<CommandNode>()
                .Select(c => (NumberExpr)c.Clause("for")!)
                .ToList();

            foreach (NumberExpr length in lengths)
            {
                string printed = AstPrinter.Print(length);
                ContentLibrary again = ContentLibrary.FromText(Nova.Replace("apply Weak 3 for 2 turns", "apply Weak 3 for " + printed), "again.cantrip");
                var reread = (NumberExpr)again.Find("Nova", "ability")!.Effect!.Statements.OfType<CommandNode>().First().Clause("for")!;

                Assert.Equal(length.Value, reread.Value);
                Assert.Equal(length.Unit, reread.Unit);
            }
        }

        /// <summary>
        /// The fingerprint behind <c>text_checked</c> is still taken from the spelling it always was,
        /// so text checked by an earlier version is not reported as stale. The hash is the one
        /// 0.1.0-preview.2 records for this definition.
        /// </summary>
        [Fact]
        public void Spacing_a_unit_does_not_change_the_text_checked_hash()
        {
            ContentLibrary content = ContentLibrary.FromText("""
                status Weak
                  stacking duration

                ability Nova
                  cooldown 2 turns
                  effect:
                    apply Weak 3 for 2 turns to enemies
                    in 2 turns:
                      deal 1 to enemies
                  text: "Weaken every enemy."
                  text_checked "f644df2bf37cf01d"
                """, "hash.cantrip");

            Assert.Equal("f644df2bf37cf01d", DescriptionBuilder.EffectHash(content.Find("Nova", "ability")!));
            Assert.DoesNotContain(Linter.Lint(content), d => d.Code == DescriptionBuilder.StaleText);
        }

        [Fact]
        public void A_move_limited_to_a_phase_says_so()
        {
            string text = Describe("""
                enemy "Slime King"
                  hp 60
                  phase Broken when hp <= max_hp / 2
                  move "Chomp":
                    deal 11 to player
                  move "Split" phase Broken:
                    deal 5 to player
                  pattern cycle Chomp, Split
                """, "Slime King", "enemy");

            Assert.Equal("Chomp: Deal 11 damage to the player. Split (Broken phase only): Deal 5 damage to the player.", text);
        }
    }
}
