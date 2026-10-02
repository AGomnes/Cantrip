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

        /// <summary>The name a listener writes, without a phase prefix.</summary>
        public string Name { get; }

        /// <summary>What <c>event.target</c>, <c>event.source</c>, <c>event.amount</c> and friends carry.</summary>
        public string Description { get; }

        /// <summary>The event's name.</summary>
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
        /// <summary>A hit landed. <c>target</c> took it, <c>source</c> dealt it, and <c>amount</c> is hp actually lost <em>after</em> block, not what the card said.</summary>
        public const string Damaged = "damaged";

        /// <summary>Block absorbed part of a hit. <c>amount</c> is how much was blocked, not what got through.</summary>
        public const string Blocked = "blocked";

        /// <summary>A hit killed with damage to spare. <c>amount</c> is only the excess.</summary>
        public const string Overkill = "overkill";

        /// <summary>An actor is dying, and can still be saved: <c>instead_of_died</c> prevents it. The dying actor's own listeners still hear this.</summary>
        public const string Died = "died";

        /// <summary>An actor died and it is settled. <c>source</c> is the killer; <see cref="Died"/> is the one that can be refused.</summary>
        public const string Killed = "killed";

        /// <summary>An actor is being brought back. Raised only for one that really was dead, so a heal on the living never reaches it.</summary>
        public const string Revived = "revived";

        /// <summary><c>amount</c> is hp actually restored, so a heal at full hp raises this with 0 rather than not at all.</summary>
        public const string Healed = "healed";

        /// <summary><c>amount</c> is block actually gained, after any modifier.</summary>
        public const string GainedBlock = "gained_block";

        /// <summary>A card reached the hand. Its tags are the event's tags, so <c>on drawn(tag:curse)</c> works.</summary>
        public const string Drawn = "drawn";

        /// <summary>A discard pile went back into the draw pile. It fires on the automatic reshuffle mid-draw as well as on a written <c>shuffle</c>.</summary>
        public const string Shuffled = "shuffled";

        /// <summary>A card went to the discard pile, including one drawn into a hand that was already full.</summary>
        public const string Discarded = "discarded";

        /// <summary>A card left the battle for good. A shuffle will not bring it back.</summary>
        public const string Exhausted = "exhausted";

        /// <summary>
        /// Something changed where it is: a card its zone, or an actor its slot. <c>data.kind</c> says
        /// which, and the two carry different data. A row closing under <c>on_vacated close_ranks</c>
        /// raises it after the fact, so <c>before_moved</c> cannot refuse that one.
        /// </summary>
        public const string Moved = "moved";

        /// <summary>
        /// Something arrived: <c>create</c>, <c>copy</c>, or a spawn the host made. It is an
        /// announcement and not a gate (the thing is already in the game), and it is what an enemy's
        /// own arrival effect is written on.
        /// </summary>
        public const string Created = "created";

        /// <summary>Something was taken out of the game. Not the same as <see cref="Killed"/>, which is about an actor's hp.</summary>
        public const string Destroyed = "destroyed";

        /// <summary>
        /// Something became something else and kept its id, owner, side and place. Raised once, and the
        /// statuses it sheds raise nothing, which is why <c>until</c> refuses to hold a transform.
        /// </summary>
        public const string Transformed = "transformed";

        /// <summary>A status landed on <c>target</c>, which is its host. <c>amount</c> is stacks, and <c>data.status</c> is the definition at the before phase and the entity after it.</summary>
        public const string StatusApplied = "status_applied";

        /// <summary>A status did not land because the target was immune. Nothing was applied, so no <see cref="StatusApplied"/> follows.</summary>
        public const string StatusResisted = "status_resisted";

        /// <summary>A status left its host. <c>amount</c> is the stacks it had when it went.</summary>
        public const string StatusRemoved = "status_removed";

        /// <summary>
        /// A card was played. <c>source</c> is the member who performed it, which in a party is not
        /// necessarily the leader who paid for it; <c>amount</c> is the energy paid.
        /// </summary>
        public const string CardPlayed = "card_played";

        /// <summary>
        /// <c>target</c>'s turn began. An unscoped listener on a status or relic hears only its own
        /// controller's turn, not everybody's. It never fires in a real-time game.
        /// </summary>
        public const string TurnStart = "turn_start";

        /// <summary><c>target</c>'s turn is ending, with the same scoping as <see cref="TurnStart"/>. It never fires in a real-time game.</summary>
        public const string TurnEnd = "turn_end";

        /// <summary>A battle began. It fires in a real-time game too, unlike the turn events.</summary>
        public const string BattleStart = "battle_start";

        /// <summary>The battle ended. <c>target</c> is the player and <c>data.won</c> says which way it went.</summary>
        public const string BattleEnd = "battle_end";

        /// <summary>An enemy is performing a named move. <c>data.move</c> is which one. Not to be confused with <see cref="Moved"/>, which is about position.</summary>
        public const string Move = "move";

        /// <summary>An ability was used. <c>data.ability</c> is which one; this is the real-time counterpart of <see cref="CardPlayed"/>.</summary>
        public const string AbilityUsed = "ability_used";

        /// <summary><c>source</c> obtained <c>target</c>, a relic. It fires during setup as well as mid-run, since <c>CardRuntime.AddRelic(string, Entity)</c> raises it.</summary>
        public const string Obtained = "obtained";

        /// <summary>
        /// The interval of an <c>on every ...:</c> listener elapsed. Raised only for the listener whose
        /// time has come and never broadcast, so <c>on every</c> in one place cannot be heard in another.
        /// </summary>
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
            new BuiltinEvent(Revived, "target is being brought back from the dead by source; amount is the hp it comes back at. Raised only for an actor that is actually dead."),
            new BuiltinEvent(Healed, "target regained hp. amount is hp actually restored."),
            new BuiltinEvent(GainedBlock, "target gained block. amount is block actually gained."),

            // Cards
            new BuiltinEvent(Drawn, "target (and card) was drawn into hand. Tags are the card's tags."),
            new BuiltinEvent(Shuffled, "target's discard pile was shuffled into the draw pile."),
            new BuiltinEvent(Discarded, "target (a card) went to the discard pile, including a card drawn with a full hand; data from, to."),
            new BuiltinEvent(Exhausted, "target (a card) was exhausted; data from, to."),
            new BuiltinEvent(Moved, "target changed where it is. A card changed zone through `move` or `shuffle`: data kind \"card\", from, to (zone names). An actor changed slot through `who.rank = ...` or `who.lane = ...`, or because `on_vacated close_ranks` closed the row in front of it: data kind \"actor\", from, to (the places in words), from_lane, from_rank, to_lane, to_rank. `before_moved` refuses an actor's move; a row closing is reported once it has closed, so there is nothing left there to refuse."),
            new BuiltinEvent(Created, "target was created by `create`, `copy` or `shuffle <card>`; card is set when it is a card; data copy_of is the original when `copy` made it."),
            new BuiltinEvent(Destroyed, "target was taken out of the game."),
            new BuiltinEvent(Transformed, "target is becoming something else and keeps its id, owner, side and place; card is set when it is a card; data was, into (both definitions). Tags are the tags it had before. Raised once, and the statuses it sheds raise nothing."),
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
            ["copy"] = new[] { Created },
            ["transform"] = new[] { Transformed },
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
            ["revive"] = new[] { Revived },
            ["grant"] = NoEvents,
            ["log"] = NoEvents,

            // Runtime verbs. `play` and `replay` also raise whatever the card's own effect raises,
            // which is not knowable from the verb alone. `play` pays as well, so it can raise
            // `<resource>_changed`.
            ["play"] = new[] { CardPlayed, Exhausted, Discarded, Moved },
            ["replay"] = NoEvents,
            ["use"] = new[] { Move },
        };

        /// <summary>Verbs that go through the stat primitive and so can raise <c>&lt;stat&gt;_changed</c>.</summary>
        private static readonly HashSet<string> StatChangingVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "change", "apply", "add", "gain", "lose", "play",
        };

        /// <summary>Every built-in event, grouped: combat, cards, statuses, lifecycle.</summary>
        public static IReadOnlyList<BuiltinEvent> All => Events;

        /// <summary>Built-in event names, in the same order as <see cref="All"/>.</summary>
        public static IReadOnlyList<string> Names { get; } = Events.Select(e => e.Name).ToArray();

        /// <summary>
        /// Whether the engine owns this event name, ignoring case. It is the check behind CT322: a game
        /// raising an event of its own has to pick a name this answers false for.
        /// </summary>
        public static bool IsBuiltin(string name) => name != null && ByName.ContainsKey(name);

        /// <summary>
        /// The entry for a built-in event, or null for a custom one. Its <c>Description</c> is where the
        /// meaning of <c>event.target</c>, <c>event.source</c> and <c>event.amount</c> is written down
        /// for that event.
        /// </summary>
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

        /// <summary>
        /// The built-in verbs that raise <paramref name="eventName"/>, in the table's own order.
        /// Empty for a custom event, and for an event only the engine's own lifecycle raises.
        /// </summary>
        /// <remarks>
        /// This is <see cref="RaisedBy"/> read the other way round, and it exists so that a refusal
        /// can name the verb that really does the thing: "`damaged` is raised by `deal`" is a fix,
        /// where "do not emit that" is only a rule.
        /// </remarks>
        public static IReadOnlyList<string> VerbsThatRaise(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return NoEvents;
            if (!RaisedByVerb.TryGetValue(eventName, out List<string>? verbs)) return NoEvents;
            return verbs;
        }

        /// <summary>
        /// Why <c>emit damaged</c> is refused, worded once for the linter and the runtime.
        /// </summary>
        /// <remarks>
        /// Emitting a built-in name dispatched to every listener of it while nothing had happened:
        /// no hp moved, no history counter moved, and `on damaged` fired anyway. The event was
        /// forged and the record was not, so a "whenever you take damage" card and a "damage taken
        /// this turn" card disagreed about the same turn. `emit` raises a *custom* event.
        /// </remarks>
        internal static string CannotBeEmitted(string name)
        {
            IReadOnlyList<string> verbs = VerbsThatRaise(name);
            string fix = verbs.Count > 0
                ? "The verb that really does it raises it: " + string.Join(", ", verbs.Select(v => "`" + v + "`")) + "."
                : "The engine raises it itself, as part of the turn or the battle.";

            return $"`{name}` is a built-in event, and `emit` raises a custom one. Emitting it would tell every `on {name}` " +
                   $"listener something happened that did not: no history counter moves with it. {fix} " +
                   $"For an event of your own, pick a name the engine does not use, such as `my_{name}`.";
        }

        private static readonly Dictionary<string, List<string>> RaisedByVerb = BuildRaisedByVerb();

        private static Dictionary<string, List<string>> BuildRaisedByVerb()
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            // The table is a dictionary, so its order is not a contract. Walking the declared event
            // list on the outside gives one stated order, which is what a message can be tested on.
            foreach (BuiltinEvent known in Events)
            {
                var verbs = new List<string>();
                foreach (KeyValuePair<string, string[]> entry in VerbEvents.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    if (Array.IndexOf(entry.Value, known.Name) >= 0) verbs.Add(entry.Key);
                }
                if (verbs.Count > 0) map[known.Name] = verbs;
            }

            return map;
        }
    }
}
