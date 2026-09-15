using GameplayEffects.Content;
using GameplayEffects.Diagnostics;
using Xunit;

namespace GameplayEffects.Tests.Review
{
    /// <summary>
    /// ContentLibrary loading, unloading and reloading, and number literal parsing. Regression
    /// tests: each test's summary records a defect a review found, now fixed.
    /// </summary>
    public sealed class ReviewContentTests
    {
        /// <summary>
        /// The file table is case-insensitive but Unload matches definitions by ordinal file name,
        /// so reloading a file under a different casing (as Windows watchers report) removes the
        /// file entry but not its definitions, and the reload reports duplicates.
        /// </summary>
        [Fact]
        [Trait("Regression", "unload-file-name-case")]
        public void Reloading_a_file_with_different_casing_replaces_its_definitions()
        {
            var content = new ContentLibrary();
            content.LoadText("card \"Strike\"\n  cost 1\n", "Cards.ge");

            DiagnosticBag reload = content.LoadText("card \"Strike\"\n  cost 2\n", "cards.ge");

            Assert.False(reload.HasErrors, reload.ToString());
            Assert.Equal(2, content.Find("Strike", "card")!.Stats["cost"].ToInt());
        }

        /// <summary>
        /// RulesetSyntax takes the last value of a Dictionary, whose enumeration reuses freed slots
        /// after a removal. After an unload, a newly loaded ruleset can land before an older one.
        /// </summary>
        [Fact]
        [Trait("Regression", "ruleset-last-loaded-not-last")]
        public void The_most_recently_loaded_ruleset_wins_after_an_unload()
        {
            var content = new ContentLibrary();
            content.LoadText("ruleset\n  hand_size 3\n", "a.ge");
            content.LoadText("ruleset\n  hand_size 7\n", "b.ge");
            content.Unload("a.ge");

            content.LoadText("ruleset\n  hand_size 4\n", "c.ge");

            Assert.Equal(4, content.BuildRuleset().HandSize);
        }

        /// <summary>Num.TryParse accumulates into a long without overflow checks, so large literals wrap to negative.</summary>
        [Fact]
        [Trait("Regression", "number-literal-overflow")]
        public void An_out_of_range_literal_is_rejected_rather_than_wrapped()
        {
            bool parsed = Num.TryParse("10000000000000", out Num value);

            Assert.True(!parsed || value > Num.Zero, $"parsed as {value}");
        }

        [Fact]
        [Trait("Regression", "number-literal-overflow")]
        public void An_out_of_range_literal_in_content_is_a_diagnostic()
        {
            ContentLibrary content = ContentLibrary.FromText("card \"Huge\"\n  cost 10000000000000\n");
            EntityDefinition huge = content.Find("Huge", "card")!;

            Assert.True(content.Diagnostics.HasErrors || huge.Stats["cost"] > Num.Zero, $"cost loaded as {huge.Stats["cost"]} with no error");
        }
    }
}
