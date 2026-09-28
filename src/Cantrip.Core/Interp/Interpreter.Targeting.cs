using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Syntax;

namespace Cantrip.Runtime
{
    /// <summary>
    /// What one action asks to be aimed at, read from its <c>target</c> line:
    /// <c>target enemy</c>, <c>target ally where it.position &lt;= 1</c>, and so on. Cards, abilities
    /// and the things with no <c>target</c> line at all — an enemy move, the <c>attack</c> verb —
    /// all describe themselves with one of these, so that every site that points at somebody asks
    /// <see cref="Interpreter.LegalTargets(TargetRule, Entity, Entity, System.Collections.Generic.IReadOnlyList{Entity})"/>
    /// the same question.
    /// </summary>
    internal sealed class TargetRule
    {
        private static readonly ExprNode[] NoFilters = new ExprNode[0];

        /// <summary>No <c>target</c> line: the action settles its own target, or wants none.</summary>
        internal static readonly TargetRule None = new TargetRule("none", NoFilters);

        /// <summary>
        /// Anybody living, either side, with no filter of its own. What an enemy move and the
        /// <c>attack</c> verb point at: they are handed their candidates and only ask whether each
        /// one may be aimed at.
        /// </summary>
        internal static readonly TargetRule Any = new TargetRule("any", NoFilters);

        private TargetRule(string mode, IReadOnlyList<ExprNode> filters)
        {
            Mode = mode;
            Filters = filters;
        }

        /// <summary><c>enemy</c>, <c>ally</c>, <c>self</c>, <c>any</c>, or <c>none</c>.</summary>
        internal string Mode { get; }

        /// <summary>
        /// The predicates of <c>target enemy where …</c>, with <c>it</c> the candidate. Empty when
        /// the <c>target</c> line names a side and nothing more, which is the usual case.
        /// </summary>
        internal IReadOnlyList<ExprNode> Filters { get; }

        /// <summary>
        /// True for the modes that need somebody: a card whose every candidate is out cannot be
        /// played, where a <c>target any</c> card may still be played at nobody.
        /// </summary>
        internal bool NeedsSomeone => Mode == "enemy" || Mode == "ally";

        /// <summary>
        /// Reads an action's <c>target</c> line. <c>target enemy</c> is a bare word;
        /// <c>target enemy where it.position &lt;= 1</c> parses as that word wrapped in a
        /// <see cref="WhereExpr"/>, which is why the word alone cannot be read with
        /// <c>Word("target")</c> any more — it would answer <c>none</c> and the card would quietly
        /// stop asking for a target at all.
        /// </summary>
        internal static TargetRule Of(EntityDefinition? definition)
        {
            PropertyNode? property = definition?.Property("target");
            if (property == null || property.Values.Count == 0) return None;

            ExprNode value = property.Values[0];

            // `a where b where c` nests, so unwrap to the word and keep every predicate.
            List<ExprNode>? filters = null;
            while (value is WhereExpr where)
            {
                (filters ??= new List<ExprNode>()).Insert(0, where.Predicate);
                value = where.Source;
            }

            string? mode = EntityDefinition.ReadWords(value).FirstOrDefault();
            if (mode == null) return None;
            mode = mode.ToLowerInvariant();

            return filters == null && mode == "none"
                ? None
                : new TargetRule(mode, (IReadOnlyList<ExprNode>?)filters ?? NoFilters);
        }
    }

    public sealed partial class Interpreter
    {
        // Targeting ---------------------------------------------------------------------------
        //
        // One function decides what an action may be aimed at, and every site that aims at
        // somebody comes through it: a card's `LegalTargets`, `CanPlay` and target resolution, an
        // ability's, an enemy move's, and the `attack` verb's. Four filters, in this order, and a
        // candidate must pass all of them:
        //
        //   1. the side the `target` line names, and being alive
        //   2. reach — the seam `range` plugs into; see InReach, which is empty on purpose
        //   3. the action's own `target … where` filter, with `it` bound to the candidate
        //   4. the `targetable` channel, where a taunt and a stealth live
        //
        // Area and random effects are deliberately not here. `deal 3 to enemies` resolves through
        // the interpreter's own selectors and reaches a stealthed minion: a taunt constrains what
        // something may be pointed at, not what a blast covers.

        /// <summary>
        /// Everyone this action may be aimed at right now, in board order, after all four filters.
        /// Empty for an action that takes no target.
        /// </summary>
        internal IReadOnlyList<Entity> LegalTargets(TargetRule rule, Entity user, Entity? action)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));

            // `target self` is not a choice, so nothing is asked of it — not the channel, and not
            // the card's own filter. It is who the card is for, not who it is pointed at.
            if (rule.Mode == "self") return new[] { user };

            return LegalTargets(rule, user, action, Candidates(rule.Mode, user));
        }

        /// <summary>
        /// The same four filters over a list the caller already has: an enemy move's side, or the
        /// entities an <c>attack</c> was told to swing at.
        /// </summary>
        internal IReadOnlyList<Entity> LegalTargets(TargetRule rule, Entity user, Entity? action, IReadOnlyList<Entity> candidates)
        {
            if (candidates == null || candidates.Count == 0) return Array.Empty<Entity>();

            var allowed = new List<Entity>(candidates.Count);
            foreach (Entity candidate in candidates)
            {
                if (IsLegalTarget(rule, user, action, candidate)) allowed.Add(candidate);
            }
            return allowed;
        }

        /// <summary>Whether one candidate passes all four filters. The whole of the rule, in order.</summary>
        internal bool IsLegalTarget(TargetRule rule, Entity user, Entity? action, Entity? candidate)
        {
            // 1. Side, and alive. Only actors are ever aimed at; a card is not somebody.
            if (candidate == null || candidate.Kind != EntityKind.Actor || !candidate.IsAlive) return false;
            if (!OnSide(rule.Mode, user, candidate)) return false;
            if (rule.Mode == "self") return true;

            // 2. Reach.
            if (!InReach(rule, user, action, candidate)) return false;

            // 3. The action's own `target … where`.
            if (!MatchesFilter(rule, user, action, candidate)) return false;

            // 4. The `targetable` channel.
            return IsTargetable(candidate, user, action);
        }

        /// <summary>Everyone the <c>target</c> line's side word puts on the table, before any filter.</summary>
        private IReadOnlyList<Entity> Candidates(string mode, Entity user)
        {
            switch (mode)
            {
                case "enemy": return State.Actors(Opposing(user));
                case "ally": return State.Actors(user.Team);
                case "self": return new[] { user };
                case "any": return State.Actors();
                default: return Array.Empty<Entity>();
            }
        }

        private static bool OnSide(string mode, Entity user, Entity candidate)
        {
            switch (mode)
            {
                case "enemy": return candidate.Team == Opposing(user);
                case "ally": return candidate.Team == user.Team;
                case "self": return ReferenceEquals(candidate, user);
                case "any": return true;
                default: return false;
            }
        }

        /// <summary>The side an actor is against. There are two sides, so this is the other one.</summary>
        internal static Team Opposing(Entity actor) => actor.Team == Team.Enemy ? Team.Player : Team.Enemy;

        // Reach — the seam where `range` plugs in ------------------------------------------------

        /// <summary>
        /// Filter 2 of 4: whether <paramref name="candidate"/> is near enough for
        /// <paramref name="user"/> to reach with <paramref name="action"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the extension point for <c>range</c>, and it is empty on purpose.</b> Nothing
        /// in the language yet has a place: there are no lanes, no ranks and no
        /// <c>distance(user, candidate)</c> to measure, so there is no question to ask and every
        /// candidate is in reach. It does not approximate an answer and content cannot make it
        /// answer anything else; it is a named hole with the shape of the thing that goes in it.
        /// </para>
        /// <para>
        /// When the board lands, this is the whole of what changes: read the action's <c>range</c>,
        /// run it through the <c>range</c> modifier channel with <paramref name="action"/> as the
        /// subject and <paramref name="user"/> as the source, and compare the result against the
        /// distance between the two places. Nothing else in targeting moves, because every site
        /// that aims at somebody already comes through here.
        /// </para>
        /// <para>
        /// Nobody may reach around it. Reach is decided here and nowhere else, so that a reach rule
        /// cannot mean one thing for a card and another for an enemy's move.
        /// </para>
        /// </remarks>
        private bool InReach(TargetRule rule, Entity user, Entity? action, Entity candidate) => true;

        // The action's own filter ----------------------------------------------------------------

        /// <summary>
        /// Filter 3 of 4: the <c>where</c> on the action's own <c>target</c> line, evaluated once
        /// per candidate with <c>it</c> bound to that candidate.
        /// </summary>
        /// <remarks>
        /// This is an ordinary <c>where</c>, so a bare qualifier such as <c>tag:undead</c> tests the
        /// candidate, the way it does in <c>enemies where tag:undead</c> — and unlike the same words
        /// inside a <c>targetable</c> modifier, where a bare qualifier tests the card being played.
        /// <c>self</c> is the action, <c>source</c> is whoever is using it.
        /// </remarks>
        private bool MatchesFilter(TargetRule rule, Entity user, Entity? action, Entity candidate)
        {
            if (rule.Filters.Count == 0) return true;

            var context = new EvalContext(action ?? user)
            {
                Source = user,
                Card = action != null && action.Kind == EntityKind.Card ? action : null,
                It = candidate,
                ItIsFocus = true,
            };

            foreach (ExprNode filter in rule.Filters)
            {
                if (!EvaluateCondition(filter, context)) return false;
            }
            return true;
        }
    }
}
