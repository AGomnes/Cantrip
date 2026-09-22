using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// CT304, a listener on an event nothing raises, suggested an event only for a misspelling
    /// such as <c>turn_strat</c>. An event written in other words, as it is in other engines, got
    /// no suggestion, although every other diagnostic offers the nearest name.
    /// </summary>
    public sealed class EventSuggestionTests
    {
        private static Diagnostic UnknownEvent(string listener, string extra = "")
        {
            ContentLibrary content = ContentLibrary.FromText($"relic Probe\n  mana 0\n  on {listener}:\n    draw 1\n{extra}", "events.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            return Assert.Single(Linter.Lint(content), d => d.Code == Linter.UnknownEvent);
        }

        [Theory]
        [Trait("Regression", "ct304-no-suggestion")]
        [InlineData("start_of_turn", "turn_start")]
        [InlineData("end_of_turn", "turn_end")]
        [InlineData("start_turn", "turn_start")]
        [InlineData("owner.start_of_turn", "turn_start")]
        [InlineData("before_start_of_turn", "turn_start")]
        [InlineData("battle_begin", "battle_start")]
        [InlineData("beginning_of_battle", "battle_start")]
        [InlineData("play_card", "card_played")]
        [InlineData("block_gained", "gained_block")]
        [InlineData("applied_status", "status_applied")]
        [InlineData("card_drawn", "drawn")]
        [InlineData("enemy_died", "died")]
        [InlineData("damage_taken", "damaged")]
        [InlineData("turn_strat", "turn_start")]
        [InlineData("card_play", "card_played")]
        [InlineData("mana_change", "mana_changed")]
        public void An_unknown_event_suggests_the_event_it_looks_like(string written, string suggestion)
        {
            Diagnostic warning = UnknownEvent(written);

            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.Equal(suggestion, warning.Suggestion);
        }

        [Fact]
        public void An_event_content_emits_is_suggested_too()
        {
            Diagnostic warning = UnknownEvent("spark_fired", "\ncard Flint\n  cost 0\n  effect:\n    emit sparked\n");

            Assert.Equal("sparked", warning.Suggestion);
        }

        [Theory]
        [InlineData("flurb")]
        [InlineData("my_custom_thing")]
        public void An_event_nothing_resembles_gets_no_suggestion(string written)
        {
            Assert.Null(UnknownEvent(written).Suggestion);
        }
    }
}
