using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;

namespace Cantrip.Sim.Scenarios
{
    /// <summary>One <c>battle</c> line of one run.</summary>
    public sealed class BattleResult
    {
        internal BattleResult(int index, string label)
        {
            Index = index;
            Label = label;
        }

        /// <summary>Which <c>battle</c> line this was, counting from 1 down the scenario.</summary>
        public int Index { get; }

        /// <summary>The enemies fought, as the line named them: <c>Frost Wisp + Cinder Imp</c>.</summary>
        public string Label { get; }

        /// <summary>True won, false lost, null the turn limit stopped it: a stall.</summary>
        public bool? Won { get; internal set; }

        public int Turns { get; internal set; }

        public int HpLost { get; internal set; }
    }

    /// <summary>One play-through of a scenario, from its first statement to its last.</summary>
    public sealed class RunResult
    {
        internal RunResult(ulong seed) => Seed = seed;

        /// <summary>The seed the whole run came from. <c>--watch</c> replays it exactly.</summary>
        public ulong Seed { get; }

        public IList<BattleResult> Battles { get; } = new List<BattleResult>();

        /// <summary>What the run threw, if it threw. The run stops there.</summary>
        public string? Error { get; internal set; }

        /// <summary>Where the run was when it threw, or when a battle stalled.</summary>
        public SourceSpan At { get; internal set; }

        /// <summary>The statement it was running, as written, for the report.</summary>
        public string? Statement { get; internal set; }

        /// <summary>A battle reached the turn limit without ending.</summary>
        public bool Stalled => Battles.Any(b => b.Won == null);

        /// <summary>Every battle the scenario named was fought and won, and nothing threw.</summary>
        public bool Won { get; internal set; }

        /// <summary>The player's hp when the run ended.</summary>
        public int HpLeft { get; internal set; }

        /// <summary>
        /// Everything about this run that does not depend on its seed. Two runs with the same
        /// signature went the same way, which is how "every run was identical" is known.
        /// </summary>
        internal string Signature =>
            string.Join("|", Battles.Select(b => $"{b.Label}:{b.Won}:{b.Turns}:{b.HpLost}")) + $"|{Won}|{HpLeft}|{Error}";
    }

    /// <summary>
    /// What the engine saw over every run, whatever the bot chose: which of each enemy's moves
    /// fired, and which cards ever became playable. These are facts about what the content allows.
    /// </summary>
    public sealed class ContentFacts
    {
        internal ContentFacts()
        {
        }

        /// <summary>Every <c>(enemy, move)</c> the content defines among the enemies that were fought.</summary>
        internal HashSet<(string Enemy, string Move)> MovesDefined { get; } = new HashSet<(string, string)>();

        /// <summary>Every <c>(enemy, move)</c> that actually ran.</summary>
        internal HashSet<(string Enemy, string Move)> MovesFired { get; } = new HashSet<(string, string)>();

        /// <summary>Every card that was the player's at any point in any run.</summary>
        internal HashSet<string> CardsOwned { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Every card that was in hand when a turn began.</summary>
        internal HashSet<string> CardsHeld { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Every card that was affordable, with somewhere to aim it, when a turn began.</summary>
        internal HashSet<string> CardsPlayable { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>How many moves the enemies that were fought have between them.</summary>
        public int MoveCount => MovesDefined.Count;

        /// <summary>How many different cards the player ever had. Zero in a fight with no cards in it.</summary>
        public int CardCount => CardsOwned.Count;

        /// <summary>Cards the player held that were never once playable, in name order.</summary>
        public IReadOnlyList<string> NeverPlayable =>
            CardsHeld.Except(CardsPlayable).OrderBy(c => c, StringComparer.Ordinal).ToList();

        /// <summary>Cards the player owned that never reached a hand, in name order.</summary>
        public IReadOnlyList<string> NeverHeld =>
            CardsOwned.Except(CardsHeld).OrderBy(c => c, StringComparer.Ordinal).ToList();

        /// <summary>Moves the enemies fought have that never ran, as <c>enemy, move</c> pairs in order.</summary>
        public IReadOnlyList<(string Enemy, string Move)> NeverFired =>
            MovesDefined.Except(MovesFired)
                .OrderBy(m => m.Enemy, StringComparer.Ordinal)
                .ThenBy(m => m.Move, StringComparer.Ordinal)
                .ToList();
    }

    /// <summary>An <c>expect</c> line and what the runs made of it.</summary>
    public sealed class ExpectResult
    {
        internal ExpectResult(string text, SourceSpan span)
        {
            Text = text;
            Span = span;
        }

        /// <summary>The line as written: <c>expect no stalls</c>.</summary>
        public string Text { get; }

        public SourceSpan Span { get; }

        /// <summary>False when the runs did not hold it. Null when this release cannot check it.</summary>
        public bool? Held { get; internal set; }

        /// <summary>What was measured, or why nothing was.</summary>
        public string Detail { get; internal set; } = string.Empty;
    }

    /// <summary>Every run of one scenario, and what they came to.</summary>
    public sealed class ScenarioResult
    {
        internal ScenarioResult(ScenarioDefinition scenario, IBot bot, int turnLimit)
        {
            Scenario = scenario;
            Bot = bot.Name;
            BotDescription = bot.Description;
            TurnLimit = turnLimit;
        }

        public ScenarioDefinition Scenario { get; }

        /// <summary>The name of the bot that played, for the report to say so.</summary>
        public string Bot { get; }

        /// <summary>One line saying what that bot does, which every report has to carry.</summary>
        public string BotDescription { get; }

        public int TurnLimit { get; }

        public IList<RunResult> Runs { get; } = new List<RunResult>();

        public ContentFacts Facts { get; } = new ContentFacts();

        public IList<ExpectResult> Expectations { get; } = new List<ExpectResult>();

        /// <summary>How long every run took together.</summary>
        public TimeSpan Elapsed { get; internal set; }

        /// <summary>How many <c>battle</c> lines the scenario has.</summary>
        public int BattleLines { get; internal set; }

        /// <summary>How many other statements sit between them.</summary>
        public int OtherLines { get; internal set; }

        public int Errors => Runs.Count(r => r.Error != null);

        /// <summary>Battles that reached the turn limit without ending.</summary>
        public int Stalls => Runs.Sum(r => r.Battles.Count(b => b.Won == null));

        /// <summary>
        /// True when every run went exactly the same way. Then the content has no randomness the
        /// scenario reaches, and one run said everything the others did.
        /// </summary>
        public bool EveryRunIdentical => Runs.Count > 1 && Runs.Select(r => r.Signature).Distinct().Count() == 1;

        /// <summary>Whether this scenario is a reason to fail the command.</summary>
        public bool Failed => Errors > 0 || Stalls > 0 || Expectations.Any(e => e.Held == false);
    }
}
