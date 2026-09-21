using Cantrip.Content;
using Cantrip.Diagnostics;
using Xunit;

namespace Cantrip.Tests.Review
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
            content.LoadText("card \"Strike\"\n  cost 1\n", "Cards.cantrip");

            DiagnosticBag reload = content.LoadText("card \"Strike\"\n  cost 2\n", "cards.cantrip");

            Assert.False(reload.HasErrors, reload.ToString());
            Assert.Equal(2, content.Find("Strike", "card")!.Stats["cost"].ToInt());
        }

        /// <summary>
        /// On Windows <c>LoadFolder</c> records <c>content\cards.cantrip</c>, while a game reloading
        /// the file naturally writes <c>content/cards.cantrip</c>. File names compared only by case,
        /// so the reload did not replace the file: it reported every definition as a duplicate and
        /// the running game kept the old rules.
        /// </summary>
        [Fact]
        [Trait("Regression", "unload-file-name-slashes")]
        public void Reloading_a_file_with_the_other_slash_replaces_its_definitions()
        {
            var content = new ContentLibrary();
            content.LoadText("card \"Strike\"\n  cost 1\n", "content\\cards.cantrip");

            DiagnosticBag reload = content.LoadText("card \"Strike\"\n  cost 2\n", "content/cards.cantrip");

            Assert.False(reload.HasErrors, reload.ToString());
            Assert.Equal(2, content.Find("Strike", "card")!.Stats["cost"].ToInt());
            Assert.Single(content.Files);
        }

        /// <summary>
        /// The parser accepts <c>event</c> and <c>encounter</c> as declaration keywords, but nothing
        /// consumes them, so they loaded as inert definitions and content that used them looked
        /// finished while nothing ever ran. They are refused until they mean something.
        /// </summary>
        [Theory]
        [Trait("Regression", "reserved-declarations-silently-inert")]
        [InlineData("event")]
        [InlineData("encounter")]
        public void Reserved_declarations_are_an_error_rather_than_silently_inert(string keyword)
        {
            var content = new ContentLibrary();

            DiagnosticBag diagnostics = content.LoadText(keyword + " \"Ambush\"\n  on battle_start:\n    log 1\n", "run.cantrip");

            Assert.True(diagnostics.HasErrors);
            Assert.Contains("CT0113", diagnostics.ToString());
            Assert.Null(content.Find("Ambush"));
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
            content.LoadText("ruleset\n  hand_size 3\n", "a.cantrip");
            content.LoadText("ruleset\n  hand_size 7\n", "b.cantrip");
            content.Unload("a.cantrip");

            content.LoadText("ruleset\n  hand_size 4\n", "c.cantrip");

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
