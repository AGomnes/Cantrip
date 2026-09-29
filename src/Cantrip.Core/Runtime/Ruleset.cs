using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Diagnostics;
using Cantrip.Syntax;

namespace Cantrip.Runtime
{
    public enum LoopProtection
    {
        /// <summary>Only the depth cap applies.</summary>
        DepthOnly,

        /// <summary>
        /// A listener can never be re-triggered by its own consequences. Fan-out is still allowed:
        /// "whenever an enemy dies, deal 1 to all" may fire for each death in a chain reaction,
        /// but a listener whose effect re-raises its own trigger stops after one pass.
        /// </summary>
        OncePerChain,
    }

    public enum ListenerOrdering
    {
        Priority,
        PlayOrder,
        ActivePlayer,
    }

    /// <summary>
    /// Which clock content is written for. A game with turns measures time in turns; a real-time
    /// game measures it in seconds, milliseconds and ticks, and the two sets of units do not convert
    /// into each other.
    /// </summary>
    /// <remarks>
    /// Before this setting existed, content that disagreed with the clock it was run on failed in
    /// two different ways: <c>on every 1s:</c> registered nothing at all, silently, and
    /// <c>apply Weak 1 for 3s</c> was a runtime error when the line ran. Saying which clock the
    /// content is for makes both of them one error, before anything runs.
    /// </remarks>
    public enum ClockKind
    {
        /// <summary>
        /// Content has not said. Nothing is checked, and a runtime with no clock of its own still
        /// uses turns, which is what every game written before the setting existed gets.
        /// </summary>
        Unstated,

        /// <summary>One unit per turn. <c>3s</c> is then a length this game cannot measure.</summary>
        Turns,

        /// <summary>One unit per fixed-timestep tick. <c>2 turns</c> is then a length this game cannot measure.</summary>
        Ticks,
    }

    /// <summary>
    /// Whether a listener that comes into play during an event hears that event.
    /// </summary>
    /// <remarks>
    /// The two answers are both defensible, which is why this is a setting rather than a fix. A
    /// minion summoned by "whenever you summon a minion" hearing its own summoning is a bug in most
    /// games, and the workaround is a filter on every listener of that shape (<c>not target:self</c>,
    /// <c>not card:Reverb</c>). But "when this enters play, it also triggers on the thing that put it
    /// there" is a real card in others, and the sample roguelite's Chill is written against it.
    /// </remarks>
    public enum NewListeners
    {
        /// <summary>Today's behaviour, and the default: it hears the event that brought it into play.</summary>
        HearTheEvent,

        /// <summary>It hears nothing until the next event: what was already here hears this one.</summary>
        MissTheEvent,
    }

    public enum TriggerResolution
    {
        /// <summary>After-phase listeners wait until the current action finishes. Slay the Spire style.</summary>
        Queued,

        /// <summary>After-phase listeners run the moment their event is raised.</summary>
        Immediate,
    }

    /// <summary>
    /// Whether the party takes one turn between them or every combatant takes its own step in one
    /// interleaved order.
    /// </summary>
    public enum TurnMode
    {
        /// <summary>
        /// The default, and what every game written before a party existed gets: the party acts,
        /// then the enemies. Members act in whatever order the game likes within the party's turn.
        /// </summary>
        Sides,

        /// <summary>
        /// Every combatant takes its step in one order across both sides, so a hero acts between two
        /// enemies. Each one's <c>turn_start</c> and <c>turn_end</c> fire at its own step, which is
        /// the natural reading of "at the start of your turn"; the round ends when every living
        /// combatant has taken one, and only then does <c>State.Turn</c> move.
        /// </summary>
        Initiative,
    }

    /// <summary>What ends a battle that the party is still standing in.</summary>
    /// <remarks>
    /// A battle has always ended the instant the board was empty, which is right for a fight the
    /// engine lays out once and wrong for every fight that arrives in waves. In a wave game the
    /// board is empty every few seconds by design — the keepers clear one wave and the next is two
    /// seconds away — so the first run of a survival mode ends at two seconds, having been won.
    /// The only way to keep such a fight open was an enemy that is always there and can never be
    /// pointed at, propping the battle open while every count and every <c>deal to enemies</c> in
    /// the game had to remember it was there.
    /// </remarks>
    public enum BattleEnd
    {
        /// <summary>
        /// The default, and what every game written before this setting existed gets: the battle is
        /// won the moment the last enemy that was in it is gone.
        /// </summary>
        LastEnemy,

        /// <summary>
        /// The game says when. An empty board is just an empty board; the battle runs until the
        /// party falls or something calls <see cref="Cantrip.CardRuntime.EndBattle"/>. What a wave,
        /// horde, survival or endless-arena game needs, and nothing else changes.
        /// </summary>
        Called,
    }

    /// <summary>The order the engine offers a party's members in when it runs the side itself.</summary>
    /// <summary>The order the engine offers a party's members in when it runs the side itself.</summary>
    public enum PartyOrder
    {
        /// <summary>
        /// Where they stand: <c>(lane, rank)</c>, which is what <c>GameState.Actors</c> already
        /// gives and so cannot move an existing game.
        /// </summary>
        Position,

        /// <summary>
        /// By the <c>speed</c> stat, descending; ties broken by where they stand and then by id, so
        /// the order is total and two runs agree. An actor with no <c>speed</c> reads 0, which is
        /// why a game that never writes the stat gets position order under another name.
        /// </summary>
        Speed,
    }

    /// <summary>
    /// Rules that content is written against. These change results, so they live in
    /// content where mod authors can see them, unlike execution speed which lives in code.
    /// </summary>
    public sealed class Ruleset
    {
        /// <summary>
        /// A new ruleset with every default in place. It is a method rather than a property because
        /// each call allocates: setting a property on one changes that one and nothing else.
        /// </summary>
        public static Ruleset CreateDefault() => new Ruleset();

        public bool BeforeEvents { get; set; } = true;
        public bool InsteadEvents { get; set; } = true;
        public bool AfterEvents { get; set; } = true;

        public LoopProtection Loops { get; set; } = LoopProtection.OncePerChain;

        /// <summary>Maximum depth of a causal chain before it is cut off with a warning trace.</summary>
        public int MaxDepth { get; set; } = 50;

        public IReadOnlyList<ListenerOrdering> Ordering { get; set; } =
            new[] { ListenerOrdering.Priority, ListenerOrdering.PlayOrder, ListenerOrdering.ActivePlayer };

        public IReadOnlyList<ModifierLayer> ModifierLayers { get; set; } =
            new[] { ModifierLayer.Add, ModifierLayer.Multiply, ModifierLayer.Clamp, ModifierLayer.Override };

        public TriggerResolution Triggers { get; set; } = TriggerResolution.Queued;

        /// <summary>
        /// The clock this content is written for. <see cref="ClockKind.Unstated"/> by default, which
        /// checks nothing and runs on turns, as content written before the setting existed does.
        /// </summary>
        public ClockKind Clock { get; set; } = ClockKind.Unstated;

        /// <summary>
        /// Whether a listener that comes into play during an event hears that event.
        /// <see cref="NewListeners.HearTheEvent"/> by default, which is what content written before
        /// the setting existed expects.
        /// </summary>
        public NewListeners NewListeners { get; set; } = NewListeners.HearTheEvent;

        /// <summary>
        /// Whether the party acts as a side or in one interleaved order. <see cref="TurnMode.Sides"/>
        /// by default, which is what every game written before a party existed gets. One round is
        /// one turn in either mode: <c>State.Turn</c> is the round number, because
        /// <c>on every N turns</c>, <c>once per turn</c>, the history counters, the saved turn and
        /// the simulator's stall limit all key off it.
        /// </summary>
        public TurnMode Turns { get; set; } = TurnMode.Sides;

        /// <summary>
        /// The order the party's members are offered in. <see cref="PartyOrder.Position"/> by
        /// default: where they stand, which is the order the board already keeps them in.
        /// </summary>
        public PartyOrder Order { get; set; } = PartyOrder.Position;

        /// <summary>
        /// What ends a battle the party is still standing in.
        /// <see cref="BattleEnd.LastEnemy"/> by default, which is what every game written before
        /// this setting existed gets.
        /// </summary>
        public BattleEnd Ends { get; set; } = BattleEnd.LastEnemy;

        /// <summary>Cards drawn at the start of each player turn.</summary>
        public int HandSize { get; set; } = 5;

        /// <summary>Cards beyond this are discarded instead of drawn.</summary>
        public int MaxHandSize { get; set; } = 10;

        /// <summary>
        /// Interpreter steps allowed per top-level action. This is the mod sandbox's step limit: a
        /// runaway loop in downloaded content becomes an error, not a frozen game.
        /// </summary>
        public int MaxStepsPerAction { get; set; } = 100_000;

        /// <summary>
        /// How deeply content verbs may call each other. Recursion exhausts the process stack long
        /// before the step limit is reached, so it needs its own, much smaller limit.
        /// </summary>
        public int MaxCallDepth { get; set; } = 64;

        /// <summary>
        /// True when two rulesets say exactly the same thing. Hot reload uses it to tell a designer
        /// that a ruleset edit needs a new game, since a running one is already resolving against
        /// the old rules.
        /// </summary>
        public bool SameAs(Ruleset other) =>
            other != null
            && BeforeEvents == other.BeforeEvents
            && InsteadEvents == other.InsteadEvents
            && AfterEvents == other.AfterEvents
            && Loops == other.Loops
            && MaxDepth == other.MaxDepth
            && Triggers == other.Triggers
            && Clock == other.Clock
            && Turns == other.Turns
            && Ends == other.Ends
            && Order == other.Order
            && NewListeners == other.NewListeners
            && HandSize == other.HandSize
            && MaxHandSize == other.MaxHandSize
            && MaxStepsPerAction == other.MaxStepsPerAction
            && MaxCallDepth == other.MaxCallDepth
            && Ordering.SequenceEqual(other.Ordering)
            && ModifierLayers.SequenceEqual(other.ModifierLayers);

        /// <summary>Applies the settings from a <c>ruleset</c> block over the defaults.</summary>
        public static Ruleset FromSyntax(RulesetDeclNode syntax, DiagnosticBag diagnostics)
        {
            var rules = new Ruleset();
            if (syntax == null) return rules;

            foreach (PropertyNode setting in syntax.Settings)
            {
                List<string> words = setting.Values.SelectMany(Content.EntityDefinition.ReadWords).Select(w => w.ToLowerInvariant()).ToList();

                switch (setting.Name)
                {
                    case "events":
                        rules.BeforeEvents = words.Contains("before");
                        rules.InsteadEvents = words.Contains("instead") || words.Contains("instead_of");
                        rules.AfterEvents = words.Contains("after");
                        break;

                    case "loops":
                        rules.Loops = words.Contains("once_per_chain") ? LoopProtection.OncePerChain : LoopProtection.DepthOnly;
                        int? depth = NumberAfter(setting, "max_depth");
                        if (depth.HasValue) rules.MaxDepth = Math.Max(1, depth.Value);
                        break;

                    case "ordering":
                        rules.Ordering = ParseEnumList<ListenerOrdering>(setting, words, diagnostics, new Dictionary<string, ListenerOrdering>
                        {
                            ["priority"] = ListenerOrdering.Priority,
                            ["play_order"] = ListenerOrdering.PlayOrder,
                            ["active_player"] = ListenerOrdering.ActivePlayer,
                        });
                        break;

                    case "modifier_layers":
                        rules.ModifierLayers = ParseEnumList<ModifierLayer>(setting, words, diagnostics, new Dictionary<string, ModifierLayer>
                        {
                            ["add"] = ModifierLayer.Add,
                            ["multiply"] = ModifierLayer.Multiply,
                            ["clamp"] = ModifierLayer.Clamp,
                            ["override"] = ModifierLayer.Override,
                        });
                        break;

                    case "triggers":
                        rules.Triggers = words.Contains("immediate") ? TriggerResolution.Immediate : TriggerResolution.Queued;
                        break;

                    case "new_listeners":
                        if (words.Contains("miss_the_event") || words.Contains("miss")) rules.NewListeners = NewListeners.MissTheEvent;
                        else if (words.Contains("hear_the_event") || words.Contains("hear")) rules.NewListeners = NewListeners.HearTheEvent;
                        else
                            diagnostics.Error("CT0202", $"Unknown value `{string.Join(" ", words)}` for `new_listeners`. Write `hear_the_event` or `miss_the_event`.", setting.Span,
                                Suggest.Closest(words.FirstOrDefault() ?? string.Empty, new[] { "hear_the_event", "miss_the_event" }));
                        break;

                    case "clock":
                        if (words.Contains("ticks") || words.Contains("tick") || words.Contains("real_time")) rules.Clock = ClockKind.Ticks;
                        else if (words.Contains("turns") || words.Contains("turn")) rules.Clock = ClockKind.Turns;
                        else
                            diagnostics.Error("CT0202", $"Unknown value `{string.Join(" ", words)}` for `clock`. Write `clock turns` or `clock ticks`.", setting.Span,
                                Suggest.Closest(words.FirstOrDefault() ?? string.Empty, new[] { "turns", "ticks" }));
                        break;

                    case "turns":
                        if (words.Contains("sides") || words.Contains("side")) rules.Turns = TurnMode.Sides;
                        else if (words.Contains("initiative")) rules.Turns = TurnMode.Initiative;
                        else
                            diagnostics.Error("CT0202", $"Unknown value `{string.Join(" ", words)}` for `turns`. Write `turns: sides` or `turns: initiative`.", setting.Span,
                                Suggest.Closest(words.FirstOrDefault() ?? string.Empty, new[] { "sides", "initiative" }));
                        break;

                    case "order":
                        if (words.Contains("position") || words.Contains("rank")) rules.Order = PartyOrder.Position;
                        else if (words.Contains("speed")) rules.Order = PartyOrder.Speed;
                        else
                            diagnostics.Error("CT0202", $"Unknown value `{string.Join(" ", words)}` for `order`. Write `order: position` or `order: speed`.", setting.Span,
                                Suggest.Closest(words.FirstOrDefault() ?? string.Empty, new[] { "position", "speed" }));
                        break;

                    case "ends":
                        if (words.Contains("last_enemy") || words.Contains("empty_board")) rules.Ends = BattleEnd.LastEnemy;
                        else if (words.Contains("called") || words.Contains("game")) rules.Ends = BattleEnd.Called;
                        else
                            diagnostics.Error("CT0202", $"Unknown value `{string.Join(" ", words)}` for `ends`. Write `ends: last_enemy` or `ends: called`.", setting.Span,
                                Suggest.Closest(words.FirstOrDefault() ?? string.Empty, new[] { "last_enemy", "called" }));
                        break;

                    case "hand_size":
                        rules.HandSize = FirstNumber(setting) ?? rules.HandSize;
                        break;

                    case "max_hand_size":
                        rules.MaxHandSize = FirstNumber(setting) ?? rules.MaxHandSize;
                        break;

                    case "max_steps":
                        rules.MaxStepsPerAction = FirstNumber(setting) ?? rules.MaxStepsPerAction;
                        break;

                    case "max_call_depth":
                        rules.MaxCallDepth = Math.Max(1, FirstNumber(setting) ?? rules.MaxCallDepth);
                        break;

                    default:
                        diagnostics.Warn(
                            "CT0201",
                            $"Unknown ruleset setting `{setting.Name}`.",
                            setting.Span,
                            Suggest.Closest(setting.Name, new[]
                            {
                                "events", "loops", "ordering", "modifier_layers", "triggers", "clock", "new_listeners", "turns", "order", "ends", "hand_size", "max_hand_size", "max_steps", "max_call_depth",
                            }));
                        break;
                }
            }

            return rules;
        }

        private static int? FirstNumber(PropertyNode setting) =>
            setting.Values.OfType<NumberExpr>().Select(n => (int?)n.Value.ToInt()).FirstOrDefault();

        private static int? NumberAfter(PropertyNode setting, string name)
        {
            for (int i = 0; i + 1 < setting.Values.Count; i++)
            {
                if (setting.Values[i] is NameExpr n && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase) && setting.Values[i + 1] is NumberExpr number)
                    return number.Value.ToInt();
            }
            return null;
        }

        private static IReadOnlyList<T> ParseEnumList<T>(
            PropertyNode setting,
            List<string> words,
            DiagnosticBag diagnostics,
            Dictionary<string, T> names)
        {
            var result = new List<T>();
            foreach (string word in words)
            {
                if (names.TryGetValue(word, out T value))
                {
                    if (!result.Contains(value)) result.Add(value);
                }
                else
                {
                    diagnostics.Error("CT0202", $"Unknown value `{word}` for `{setting.Name}`.", setting.Span, Suggest.Closest(word, names.Keys));
                }
            }

            // Anything left out keeps its default relative position at the end, so a partial list
            // still yields a total order and results stay deterministic.
            foreach (T value in names.Values)
            {
                if (!result.Contains(value)) result.Add(value);
            }

            return result;
        }
    }
}
