#nullable enable
using System;
using Cantrip.Descriptions;
using Cantrip.Diagnostics;
using Cantrip.Syntax;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// The word script is given for each member of a core enum.
    /// </summary>
    /// <remarks>
    /// Written out rather than made by lower-casing the member's name, for the same reason as
    /// <see cref="ChoiceAnswer.NameOf"/> and <see cref="SaveCheck.NameOf"/>: a game's script
    /// compares against these words, so renaming a C# member must not quietly change one, and a
    /// member of two words must not arrive run together. Every table refuses a member it has no
    /// word for, because at 1.0 a wrong word is worse than a loud failure: a new member added
    /// later is a decision about the published surface, not something a fallback should make.
    /// </remarks>
    public static class Words
    {
        /// <summary>How bad a diagnostic is, as <c>dotnet cantrip lint</c> also prints it.</summary>
        public static string SeverityName(DiagnosticSeverity severity) => severity switch
        {
            DiagnosticSeverity.Info => "info",
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Error => "error",
            _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "This severity has no word for script yet."),
        };

        /// <summary>What an entity is. Not the same vocabulary as a definition's kind; see docs/godot.md.</summary>
        public static string KindName(EntityKind kind) => kind switch
        {
            EntityKind.Actor => "actor",
            EntityKind.Card => "card",
            EntityKind.Status => "status",
            EntityKind.Relic => "relic",
            EntityKind.Ability => "ability",
            EntityKind.Keyword => "keyword",
            EntityKind.Item => "item",
            EntityKind.Global => "global",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "This entity kind has no word for script yet."),
        };

        /// <summary>Which side an entity is on.</summary>
        public static string TeamName(Team team) => team switch
        {
            Team.Neutral => "neutral",
            Team.Player => "player",
            Team.Enemy => "enemy",
            _ => throw new ArgumentOutOfRangeException(nameof(team), team, "This team has no word for script yet."),
        };

        /// <summary>Where a description's words came from.</summary>
        public static string LevelName(DescriptionLevel level) => level switch
        {
            DescriptionLevel.Auto => "auto",
            DescriptionLevel.Custom => "custom",
            DescriptionLevel.Override => "override",
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "This description level has no word for script yet."),
        };

        /// <summary>When a listener ran relative to the thing it heard.</summary>
        public static string PhaseName(EventPhase phase) => phase switch
        {
            EventPhase.Before => "before",
            EventPhase.Instead => "instead",
            EventPhase.After => "after",
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "This phase has no word for script yet."),
        };

        /// <summary>How often a <c>once per</c> listener may fire.</summary>
        public static string LimitName(LimitScope limit) => limit switch
        {
            LimitScope.None => "none",
            LimitScope.Turn => "turn",
            LimitScope.Battle => "battle",
            LimitScope.Run => "run",
            LimitScope.Chain => "chain",
            _ => throw new ArgumentOutOfRangeException(nameof(limit), limit, "This limit scope has no word for script yet."),
        };

        /// <summary>Which pass of the modifier pipeline a <c>modify</c> line belongs to.</summary>
        public static string LayerName(ModifierLayer layer) => layer switch
        {
            ModifierLayer.Add => "add",
            ModifierLayer.Multiply => "multiply",
            ModifierLayer.Clamp => "clamp",
            ModifierLayer.Override => "override",
            _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "This modifier layer has no word for script yet."),
        };

        /// <summary>
        /// How an action ended, as <c>Play</c>, <c>PlayNamed</c>, <c>UseAbility</c> and
        /// <c>AnswerChoice</c> answer. "pending" for <see cref="ActionResult.ChoicePending"/>,
        /// because what a game does with it is wait for the question, not read the member's name.
        /// </summary>
        public static string ActionName(ActionResult result) => result switch
        {
            ActionResult.Played => "played",
            ActionResult.ChoicePending => "pending",
            ActionResult.NotACard => "not_a_card",
            ActionResult.NotInHand => "not_in_hand",
            ActionResult.Unplayable => "unplayable",
            ActionResult.NotEnoughEnergy => "not_enough_energy",
            ActionResult.InvalidTarget => "invalid_target",
            ActionResult.Cancelled => "cancelled",
            ActionResult.NotReady => "not_ready",
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, "This action result has no word for script yet."),
        };
    }
}
