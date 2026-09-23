using System;
using System.Collections.Generic;
using System.Linq;

namespace Cantrip.Content
{
    /// <summary>
    /// The vocabulary of a <c>scenario</c> body: the verbs it may use and the measurements its
    /// <c>expect</c> lines may name. The linter checks scenarios against these lists, and whatever
    /// runs a scenario registers the same verbs, so a word cannot be legal in one place and unknown
    /// in the other.
    /// </summary>
    public static class Scenario
    {
        /// <summary>
        /// Verbs a scenario shares with a <c>test</c>, which set the game up and mean the same thing
        /// in both. Every one of them is also in <see cref="Testing.DslTestRunner.TestVerbs"/>.
        /// </summary>
        public static IReadOnlyCollection<string> SetupVerbs { get; } = new[]
        {
            "player", "deck", "hand", "discard_pile", "relic", "grant", "seed", "answer",
        };

        /// <summary>
        /// Every verb a scenario body may use: the setup verbs, plus <c>battle</c>, <c>runs</c> and
        /// its own <c>expect</c>. The verbs that play a turn by hand — <c>play</c>, <c>cast</c>,
        /// <c>end turn</c>, <c>tick</c> — are deliberately absent: a bot plays a scenario, and a
        /// hand-written line would fight it.
        /// </summary>
        public static IReadOnlyCollection<string> Verbs { get; } =
            SetupVerbs.Concat(new[] { "battle", "runs", "expect" }).ToArray();

        /// <summary>
        /// What a scenario's <c>expect</c> can check. Each is measured over every run of the
        /// scenario, not inside one game: <c>expect no stalls</c>, <c>expect wins &gt;= 55%</c>.
        /// </summary>
        public static IReadOnlyCollection<string> Metrics { get; } = new[]
        {
            "stalls", "errors", "wins", "hp_left", "turns",
        };

        internal static bool IsVerb(string word) => Verbs.Contains(word, StringComparer.OrdinalIgnoreCase);

        internal static bool IsMetric(string word) => Metrics.Contains(word, StringComparer.OrdinalIgnoreCase);
    }
}
