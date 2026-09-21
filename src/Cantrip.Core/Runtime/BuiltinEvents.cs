using System;
using System.Collections.Generic;
using System.Linq;

namespace Cantrip.Runtime
{
    /// <summary>One event the engine raises on its own, with what its fields mean to a listener.</summary>
    public sealed class BuiltinEvent
    {
        internal BuiltinEvent(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }

        /// <summary>What <c>event.target</c>, <c>event.source</c>, <c>event.amount</c> and friends carry.</summary>
        public string Description { get; }

        public override string ToString() => Name;
    }

    /// <summary>
    /// The canonical list of events the runtime raises without content asking for them, and which
    /// built-in verbs can raise which. Tools read this instead of keeping their own copies, so
    /// "is <c>damagd</c> a real event?" has exactly one answer. A unit test scans the Core sources
    /// for <c>new GameEvent("...")</c> and fails if an event is missing here.
    /// </summary>
    /// <remarks>
    /// Two families are open-ended and therefore not listed by name: <c>&lt;stat&gt;_changed</c>,
    /// raised whenever a stat changes through <c>change</c>, <c>gain</c>, <c>lose</c>, an assignment
    /// or a resource reset (and only when something listens), and custom events raised by
    /// <c>emit</c>.
    /// </remarks>
    public static class BuiltinEvents
    {
        public const string Damaged = "damaged";
        public const string Blocked = "blocked";
        public const string Overkill = "overkill";
        public const string Died = "died";
        public const string Killed = "killed";
        public const string Healed = "healed";
        public const string GainedBlock = "gained_block";
        public const string Drawn = "drawn";
        public const string Shuffled = "shuffled";
        public const string Discarded = "discarded";
        public const string Exhausted = "exhausted";
        public const string Moved = "moved";
        public const string Created = "created";
        public const string Destroyed = "destroyed";
        public const string StatusApplied = "status_applied";
        public const string StatusResisted = "status_resisted";
        public const string StatusRemoved = "status_removed";
        public const string CardPlayed = "card_played";
        public const string TurnStart = "turn_start";
        public const string TurnEnd = "turn_end";
        public const string BattleStart = "battle_start";
        public const string BattleEnd = "battle_end";
        public const string Move = "move";
        public const string AbilityUsed = "ability_used";
        public const string Obtained = "obtained";
        public const string Every = "every";

        /// <summary>Suffix of the per-stat events such as <c>energy_changed</c>.</summary>
        public const string StatChangedSuffix = "_changed";

        private static readonly BuiltinEvent[] Events =
        {
            // Combat
            new BuiltinEvent(Damaged, "target took a hit. source dealt it, card is the card that caused it, amount is hp actually lost after block; data base, total, blocked, overkill. Tags are the damage type."),
            new BuiltinEvent(Blocked, "block absorbed part of a hit on target. amount is how much was blocked."),
            new BuiltinEvent(Overkill, "a hit killed target with damage to spare. amount is the excess."),
            new BuiltinEvent(Died, "target is dying. `on instead_of_died` prevents the death; the dying actor's own listeners still hear it."),
            new BuiltinEvent(Killed, "target died. source is the killer."),
            new BuiltinEvent(Healed, "target regained hp. amount is hp actually restored."),
            new BuiltinEvent(GainedBlock, "target gained block. amount is block actually gained."),

            // Cards
            new BuiltinEvent(Drawn, "target (and card) was drawn into hand. Tags are the card's tags."),
            new BuiltinEvent(Shuffled, "target's discard pile was shuffled into the draw pile."),
            new BuiltinEvent(Discarded, "target (a card) went to the discard pile, including a card drawn with a full hand; data from, to."),
            new BuiltinEvent(Exhausted, "target (a card) was exhausted; data from, to."),
            new BuiltinEvent(Moved, "target (a card) changed zone through `move` or `shuffle`; data from, to."),
            new BuiltinEvent(Created, "target was created by `create` or `shuffle <card>`; card is set when it is a card."),
            new BuiltinEvent(Destroyed, "target was taken out of the game."),
            new BuiltinEvent(CardPlayed, "card was played by source (the player) on target; amount is the energy paid. Tags are the card's tags."),

            // Statuses
            new BuiltinEvent(StatusApplied, "a status is applied to target (its host); amount is stacks; data status (the definition before it exists, the status afterwards), status_name. Tags are the status's tags."),
            new BuiltinEvent(StatusResisted, "target was immune to a status; data status. Tags are the status's tags."),
            new BuiltinEvent(StatusRemoved, "a status left target (its host); amount is its last stacks; data status, status_name. Tags are the status's tags."),

            // Lifecycle
            new BuiltinEvent(TurnStart, "target's turn began. Unscoped listeners on statuses and relics hear only their own controller's turn."),
            new BuiltinEvent(TurnEnd, "target's turn is ending. Unscoped listeners on statuses and relics hear only their own controller's turn."),
            new BuiltinEvent(BattleStart, "a battle began; source is the player."),
            new BuiltinEvent(BattleEnd, "the battle ended; target is the player; data won."),
            new BuiltinEvent(Move, "enemy source performs a move against target; data move."),
            new BuiltinEvent(AbilityUsed, "source used an ability on target; data ability."),
            new BuiltinEvent(Obtained, "source obtained target (a relic)."),
            new BuiltinEvent(Every, "the interval of an `on every ...:` listener elapsed; target and source are the listening entity. Raised only for the listener whose time has come, never broadcast."),
        };

        private static readonly Dictionary<string, BuiltinEvent> ByName =
            Events.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        private static readonly string[] NoEvents = new string[0];

        /// <summary>
        /// Events each built-in verb can raise directly or through the chain it always starts
        /// (<c>deal</c> can kill, so it can raise <c>died</c> and <c>killed</c>). Verbs that raise
        /// nothing are listed too, so "unknown verb" and "raises nothing" stay distinguishable.
        /// </summary>
        private static readonly Dictionary<string, string[]> VerbEvents = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["change"] = new[] { Died, Killed, StatusRemoved },
            ["move"] = new[] { Moved },
            ["create"] = new[] { Created },
            ["destroy"] = new[] { Destroyed, StatusRemoved },
            ["apply"] = new[] { StatusApplied, StatusResisted },
            ["remove"] = new[] { StatusRemoved, Destroyed },
            ["emit"] = NoEvents,
            ["deal"] = new[] { Damaged, Blocked, Overkill, Died, Killed },
            ["damage"] = new[] { Damaged, Blocked, Overkill, Died, Killed },
            ["attack"] = new[] { Damaged, Blocked, Overkill, Died, Killed },
            ["heal"] = new[] { Healed },
            ["block"] = new[] { GainedBlock },
            ["gain_block"] = new[] { GainedBlock },
            ["draw"] = new[] { Drawn, Shuffled, Discarded },
            ["discard"] = new[] { Discarded },
            ["exhaust"] = new[] { Exhausted },
            ["shuffle"] = new[] { Shuffled, Created, Moved },
            ["gain"] = new[] { StatusApplied, StatusResisted, StatusRemoved, Died, Killed },
            ["lose"] = new[] { StatusApplied, StatusResisted, StatusRemoved, Died, Killed },
            ["add"] = new[] { StatusApplied, StatusResisted },
            ["choose"] = NoEvents,
            ["discover"] = NoEvents,
            ["cancel"] = NoEvents,
            ["kill"] = new[] { Died, Killed },
            ["log"] = NoEvents,

            // Runtime verbs. `replay` also raises whatever the replayed effect raises, which is
            // not knowable from the verb alone.
            ["replay"] = NoEvents,
            ["use"] = new[] { Move },
        };

        /// <summary>Verbs that go through the stat primitive and so can raise <c>&lt;stat&gt;_changed</c>.</summary>
        private static readonly HashSet<string> StatChangingVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "change", "apply", "add", "gain", "lose",
        };

        /// <summary>Every built-in event, grouped as in section 3.3: combat, cards, statuses, lifecycle.</summary>
        public static IReadOnlyList<BuiltinEvent> All => Events;

        /// <summary>Built-in event names, in the same order as <see cref="All"/>.</summary>
        public static IReadOnlyList<string> Names { get; } = Events.Select(e => e.Name).ToArray();

        public static bool IsBuiltin(string name) => name != null && ByName.ContainsKey(name);

        public static BuiltinEvent? Find(string name) =>
            name != null && ByName.TryGetValue(name, out BuiltinEvent? found) ? found : null;

        /// <summary>The event raised when <paramref name="stat"/> changes: <c>energy</c> gives <c>energy_changed</c>.</summary>
        public static string StatChanged(string stat) => stat.ToLowerInvariant() + StatChangedSuffix;

        /// <summary>Splits <c>energy_changed</c> into <c>energy</c>. False for anything else.</summary>
        public static bool TryParseStatChanged(string eventName, out string stat)
        {
            stat = string.Empty;
            if (string.IsNullOrEmpty(eventName) || eventName.Length <= StatChangedSuffix.Length) return false;
            if (!eventName.EndsWith(StatChangedSuffix, StringComparison.OrdinalIgnoreCase)) return false;
            stat = eventName.Substring(0, eventName.Length - StatChangedSuffix.Length).ToLowerInvariant();
            return true;
        }

        /// <summary>True for the verbs this table describes: every built-in and runtime verb.</summary>
        public static bool IsKnownVerb(string verb) => verb != null && VerbEvents.ContainsKey(verb);

        /// <summary>
        /// Built-in events a built-in verb can raise, not counting <c>&lt;stat&gt;_changed</c>
        /// (see <see cref="CanChangeStats"/>) or the custom event of <c>emit</c>. Empty for verbs
        /// this table does not know, such as content verbs or verbs a host registers.
        /// </summary>
        public static IReadOnlyList<string> RaisedBy(string verb) =>
            verb != null && VerbEvents.TryGetValue(verb, out string[]? events) ? events : NoEvents;

        /// <summary>True when a built-in verb can raise <c>&lt;stat&gt;_changed</c>.</summary>
        public static bool CanChangeStats(string verb) => verb != null && StatChangingVerbs.Contains(verb);
    }
}
