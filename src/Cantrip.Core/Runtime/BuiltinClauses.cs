using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Syntax;

namespace Cantrip.Runtime
{
    /// <summary>
    /// Which named clauses each built-in verb reads. The parser knows the clause words but attaches
    /// no meaning to any of them, so before this table a clause a verb did not read was simply
    /// dropped: <c>block 8 for 2 turns</c> gave ordinary block, <c>apply Poison 3 at target</c>
    /// ignored the <c>at</c>, and <c>deal 5 against enemy2</c> hit the card's own target.
    /// </summary>
    /// <remarks>
    /// Only built-in verbs are listed. A verb a game registers in C#, or one content declares, may
    /// read any clause it likes — which is the whole reason the grammar knows words no built-in verb
    /// reads — so the check that uses this table skips them. A flag after a comma
    /// (<c>, ignore block</c>) is a different thing and is not a clause at all.
    /// </remarks>
    public static class BuiltinClauses
    {
        private static readonly string[] None = new string[0];

        /// <summary>
        /// The clauses each built-in verb reads, in the order its own implementation prefers them.
        /// A verb listed with nothing reads no clause; a verb not listed at all is not built in.
        /// </summary>
        private static readonly Dictionary<string, string[]> ByVerb = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Core verbs
            ["change"] = new[] { "to", "of", "by" },
            ["move"] = new[] { "to", "into", "onto" },
            ["create"] = new[] { "into", "to", "onto" },
            ["copy"] = new[] { "into", "to", "onto" },
            ["transform"] = new[] { "into", "to" },
            ["destroy"] = None,
            ["apply"] = new[] { "to", "for" },
            ["remove"] = new[] { "from" },
            ["emit"] = new[] { "to" },

            // Macros
            ["deal"] = new[] { "to", "as", "into" },
            ["damage"] = new[] { "to", "as", "into" },
            ["attack"] = new[] { "to", "with", "as", "into" },
            ["heal"] = new[] { "to", "into" },
            ["block"] = new[] { "to", "into" },
            ["gain_block"] = new[] { "to", "into" },
            ["draw"] = new[] { "to" },
            ["discard"] = None,
            ["exhaust"] = None,
            ["shuffle"] = new[] { "into", "to" },
            ["gain"] = new[] { "to" },
            ["lose"] = new[] { "to" },
            ["add"] = new[] { "to" },
            ["choose"] = new[] { "from", "as" },
            ["discover"] = new[] { "as" },
            ["cancel"] = None,
            ["kill"] = new[] { "to" },
            ["revive"] = new[] { "to" },
            ["grant"] = new[] { "to" },
            ["log"] = None,

            // Runtime verbs. `play card on target` and `replay card on target` aim with the `on`
            // operator inside the argument, not with a clause, and `, free` is a flag.
            ["play"] = None,
            ["replay"] = None,
            ["use"] = None,
        };

        /// <summary>
        /// The clause a writer probably meant, for each word a built-in verb might be handed. The
        /// first candidate the verb actually reads is what the message suggests.
        /// </summary>
        /// <remarks>
        /// These are not synonyms the language accepts. <c>at</c>, <c>over</c>, <c>against</c> and
        /// <c>using</c> are read by no built-in verb at all, and stay that way: three spellings of
        /// one clause is language to learn and content to keep consistent, for nothing. They are
        /// words the grammar knows so that a verb a game registers can use them, and so that this
        /// message can name the word that works instead of guessing at a spelling.
        /// </remarks>
        private static readonly Dictionary<string, string[]> Meant = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["at"] = new[] { "to", "into" },
            ["against"] = new[] { "to" },
            ["using"] = new[] { "with", "from" },
            ["over"] = new[] { "for" },
            ["onto"] = new[] { "into", "to" },
            ["of"] = new[] { "to" },
            ["into"] = new[] { "to" },
            ["to"] = new[] { "into", "onto", "as" },
            ["from"] = new[] { "to" },
            ["with"] = new[] { "from", "to" },
            ["as"] = new[] { "to" },
            ["by"] = new[] { "to" },
            ["for"] = new[] { "to" },
        };

        /// <summary>
        /// What to say where the clause is not a slip of the pen but a thing the engine does not do,
        /// keyed <c>verb:clause</c>. Without these the message would suggest the nearest clause and
        /// the author would keep looking for the feature.
        /// </summary>
        private static readonly Dictionary<string, string> Advice = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["block:for"] =
                "Block is not timed: it lasts until it is spent or the holder's turn starts. " +
                "Write `block 8`, or give a status that grants block each turn.",
            ["gain_block:for"] =
                "Block is not timed: it lasts until it is spent or the holder's turn starts. " +
                "Write `gain_block 8`, or give a status that grants block each turn.",
            ["heal:for"] =
                "A heal happens once. For healing over time, apply a status that heals on `turn_start` and give that status the duration.",
            ["deal:for"] =
                "Damage happens once. For damage over time, apply a status that deals it on `turn_start` and give that status the duration.",
            ["damage:for"] =
                "Damage happens once. For damage over time, apply a status that deals it on `turn_start` and give that status the duration.",
            ["draw:into"] =
                "`draw` binds nothing. Read the hand afterwards, or use `choose` if the cards themselves are wanted.",
        };

        /// <summary>Every built-in verb this table describes, in name order.</summary>
        public static IReadOnlyList<string> Verbs { get; } =
            ByVerb.Keys.OrderBy(v => v, StringComparer.Ordinal).ToArray();

        /// <summary>True for a verb built into the engine, which is the only kind this table covers.</summary>
        public static bool IsKnownVerb(string verb) => verb != null && ByVerb.ContainsKey(verb);

        /// <summary>The clauses a built-in verb reads. Empty for one that reads none, and for an unknown verb.</summary>
        public static IReadOnlyList<string> ReadBy(string verb) =>
            verb != null && ByVerb.TryGetValue(verb, out string[]? clauses) ? clauses : None;

        /// <summary>True when a built-in verb reads that clause.</summary>
        public static bool Reads(string verb, string clause) =>
            verb != null && clause != null && ByVerb.TryGetValue(verb, out string[]? clauses)
            && clauses.Contains(clause, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The clauses of a command that its verb does not read, in the order they were written.
        /// Empty for a verb this table does not know, so a game's own verb is never reported.
        /// </summary>
        /// <remarks>
        /// A flag after a comma is not a clause: it is a bare word the parser files beside them, and
        /// verbs a game registers depend on reading their own. Only the grammar's clause words are
        /// considered here, so <c>, ignore block</c> and <c>, free</c> pass untouched.
        /// </remarks>
        public static IReadOnlyList<ClauseNode> Unread(CommandNode command)
        {
            if (command == null || command.Clauses.Count == 0 || !ByVerb.ContainsKey(command.Verb)) return Array.Empty<ClauseNode>();

            List<ClauseNode>? found = null;
            foreach (ClauseNode clause in command.Clauses)
            {
                if (!Parser.IsClauseWord(clause.Keyword)) continue;   // a flag, not a clause
                if (Reads(command.Verb, clause.Keyword)) continue;

                (found ??= new List<ClauseNode>()).Add(clause);
            }

            return (IReadOnlyList<ClauseNode>?)found ?? Array.Empty<ClauseNode>();
        }

        /// <summary>Why the clause was refused, and what to write instead. One wording for the linter and the runtime.</summary>
        internal static string NotRead(string verb, string clause)
        {
            string lower = verb.ToLowerInvariant();
            var message = new System.Text.StringBuilder();
            message.Append('`').Append(clause.ToLowerInvariant()).Append("` is not a clause `").Append(lower).Append("` reads, so it did nothing. ");

            if (Advice.TryGetValue(lower + ":" + clause.ToLowerInvariant(), out string? advice))
            {
                message.Append(advice).Append(' ');
            }
            else if (Meant.TryGetValue(clause, out string[]? candidates)
                     && candidates.FirstOrDefault(c => Reads(verb, c)) is string better)
            {
                message.Append("Write `").Append(better).Append("` instead. ");
            }

            IReadOnlyList<string> reads = ReadBy(verb);
            message.Append(reads.Count == 0
                ? $"`{lower}` reads no clauses."
                : $"`{lower}` reads {Join(reads)}.");

            return message.ToString();
        }

        private static string Join(IReadOnlyList<string> words)
        {
            var quoted = words.Select(w => "`" + w + "`").ToList();
            if (quoted.Count == 1) return quoted[0];
            return string.Join(", ", quoted.Take(quoted.Count - 1)) + " and " + quoted[quoted.Count - 1];
        }
    }
}
