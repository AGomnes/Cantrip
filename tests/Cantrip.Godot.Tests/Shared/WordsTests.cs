using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Descriptions;
using Cantrip.Diagnostics;
using Cantrip.Syntax;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// The words a game's script compares against. Each table is written out here as well as in
    /// <see cref="Words"/>, on purpose: a table that made its words from the member's name would
    /// agree with any renaming, and these are frozen at 1.0. Each test also asserts that the table
    /// covers the enum exactly, so a member added later fails here rather than reaching a game as
    /// a word nobody chose.
    /// </summary>
    public sealed class WordsTests
    {
        [Fact]
        public void Every_severity_has_its_word()
        {
            Covers(
                new Dictionary<DiagnosticSeverity, string>
                {
                    [DiagnosticSeverity.Info] = "info",
                    [DiagnosticSeverity.Warning] = "warning",
                    [DiagnosticSeverity.Error] = "error",
                },
                Words.SeverityName);
        }

        [Fact]
        public void Every_entity_kind_has_its_word()
        {
            Covers(
                new Dictionary<EntityKind, string>
                {
                    [EntityKind.Actor] = "actor",
                    [EntityKind.Card] = "card",
                    [EntityKind.Status] = "status",
                    [EntityKind.Relic] = "relic",
                    [EntityKind.Ability] = "ability",
                    [EntityKind.Keyword] = "keyword",
                    [EntityKind.Item] = "item",
                    [EntityKind.Global] = "global",
                },
                Words.KindName);
        }

        [Fact]
        public void Every_team_has_its_word()
        {
            Covers(
                new Dictionary<Team, string>
                {
                    [Team.Neutral] = "neutral",
                    [Team.Player] = "player",
                    [Team.Enemy] = "enemy",
                },
                Words.TeamName);
        }

        [Fact]
        public void Every_description_level_has_its_word()
        {
            Covers(
                new Dictionary<DescriptionLevel, string>
                {
                    [DescriptionLevel.Auto] = "auto",
                    [DescriptionLevel.Custom] = "custom",
                    [DescriptionLevel.Override] = "override",
                },
                Words.LevelName);
        }

        [Fact]
        public void Every_phase_has_its_word()
        {
            Covers(
                new Dictionary<EventPhase, string>
                {
                    [EventPhase.Before] = "before",
                    [EventPhase.Instead] = "instead",
                    [EventPhase.After] = "after",
                },
                Words.PhaseName);
        }

        [Fact]
        public void Every_limit_scope_has_its_word()
        {
            Covers(
                new Dictionary<LimitScope, string>
                {
                    [LimitScope.None] = "none",
                    [LimitScope.Turn] = "turn",
                    [LimitScope.Battle] = "battle",
                    [LimitScope.Run] = "run",
                    [LimitScope.Chain] = "chain",
                },
                Words.LimitName);
        }

        [Fact]
        public void Every_modifier_layer_has_its_word()
        {
            Covers(
                new Dictionary<ModifierLayer, string>
                {
                    [ModifierLayer.Add] = "add",
                    [ModifierLayer.Multiply] = "multiply",
                    [ModifierLayer.Clamp] = "clamp",
                    [ModifierLayer.Override] = "override",
                },
                Words.LayerName);
        }

        /// <summary>
        /// The one table a fallback used to sit under. <c>NotReady</c> is why it mattered: lowering
        /// the member's name would have given a game "notready", which is neither snake_case nor
        /// a word anyone decided on.
        /// </summary>
        [Fact]
        public void Every_action_result_has_its_word()
        {
            Covers(
                new Dictionary<ActionResult, string>
                {
                    [ActionResult.Played] = "played",
                    [ActionResult.ChoicePending] = "pending",
                    [ActionResult.NotACard] = "not_a_card",
                    [ActionResult.NotInHand] = "not_in_hand",
                    [ActionResult.Unplayable] = "unplayable",
                    [ActionResult.NotEnoughEnergy] = "not_enough_energy",
                    [ActionResult.InvalidTarget] = "invalid_target",
                    [ActionResult.Cancelled] = "cancelled",
                    [ActionResult.NotReady] = "not_ready",
                },
                Words.ActionName);
        }

        /// <summary>
        /// Every word is snake_case: lower case, digits and underscores, never run-together words.
        /// This is the shape a game matches on, and one table drifting from it would be found by a
        /// user rather than here.
        /// </summary>
        [Fact]
        public void Every_word_is_snake_case()
        {
            var words = new List<string>();
            words.AddRange(All<DiagnosticSeverity>(Words.SeverityName));
            words.AddRange(All<EntityKind>(Words.KindName));
            words.AddRange(All<Team>(Words.TeamName));
            words.AddRange(All<DescriptionLevel>(Words.LevelName));
            words.AddRange(All<EventPhase>(Words.PhaseName));
            words.AddRange(All<LimitScope>(Words.LimitName));
            words.AddRange(All<ModifierLayer>(Words.LayerName));
            words.AddRange(All<ActionResult>(Words.ActionName));
            words.AddRange(Enum.GetValues<ChoiceRejection>().Select(ChoiceAnswer.NameOf));
            words.AddRange(Enum.GetValues<SaveRejection>().Select(SaveCheck.NameOf));

            List<string> wrong = words.FindAll(word => !System.Text.RegularExpressions.Regex.IsMatch(word, "^[a-z][a-z0-9_]*$"));
            Assert.True(wrong.Count == 0, "not snake_case: " + string.Join(", ", wrong));
        }

        [Fact]
        public void A_member_with_no_word_is_refused_rather_than_guessed_at()
        {
            // The shape of a member added to the enum and not to the table. It must not answer.
            Assert.Throws<ArgumentOutOfRangeException>(() => Words.ActionName((ActionResult)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => Words.KindName((EntityKind)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => Words.PhaseName((EventPhase)99));
        }

        private static IEnumerable<string> All<TEnum>(Func<TEnum, string> word)
            where TEnum : struct, Enum => Enum.GetValues<TEnum>().Select(word);

        /// <summary>Checks a table against the enum: same members, and the same word for each.</summary>
        private static void Covers<TEnum>(Dictionary<TEnum, string> expected, Func<TEnum, string> word)
            where TEnum : struct, Enum
        {
            Assert.Equal(Enum.GetValues<TEnum>().OrderBy(member => member), expected.Keys.OrderBy(member => member));
            foreach (KeyValuePair<TEnum, string> pair in expected) Assert.Equal(pair.Value, word(pair.Key));
        }
    }
}
