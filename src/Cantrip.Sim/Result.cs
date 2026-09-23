using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;

namespace Cantrip.Sim
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
        /// What the meter said happened to each actor's hp in this run, against what the engine
        /// says its hp did. It is a check on the meter rather than something to report, so it is
        /// not part of the tool's surface; a test runs it over the samples.
        /// </summary>
        internal HpLedger? Ledger { get; set; }

        /// <summary>
        /// What this run came to: each battle's label, result, turns and hp lost, then whether the
        /// run was won, the hp left and anything it threw. Two runs with the same signature came
        /// out the same way, which is how "every run came out the same" is known.
        /// </summary>
        /// <remarks>
        /// It is the outcome that is compared and not the plays, so content that rolls a die which
        /// never changes the outcome counts as settled here. That is the claim the report makes,
        /// and it is weaker than "nothing rolls a die".
        /// </remarks>
        internal string Signature =>
            string.Join("|", Battles.Select(b => $"{b.Label}:{b.Won}:{b.Turns}:{b.HpLost}")) + $"|{Won}|{HpLeft}|{Error}";
    }

    /// <summary>
    /// What the engine saw over every run of every bot: which of each enemy's moves fired, and
    /// which cards ever became playable. These are facts about what the content allows, so a fact
    /// one bot reached is the content's whether or not the other bot reached it, and they are
    /// collected in one place for all of them.
    /// </summary>
    /// <remarks>
    /// Nothing a bot merely tried is in here. A play tried through <c>Capture</c> and
    /// <c>Restore</c> raises the same events as a real one, so the run stops recording while a
    /// trial is open; see <see cref="Trials"/>.
    /// </remarks>
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

    /// <summary>
    /// What one bot made of a scenario: every run it played, and what they came to. Everything in
    /// here is a fact about that bot as much as about the content, which is why it is kept apart
    /// from <see cref="ScenarioOutcome.Facts"/> and never merged with another bot's.
    /// </summary>
    public sealed class ScenarioResult
    {
        internal ScenarioResult(string bot, string description)
        {
            Bot = bot;
            BotDescription = description;
        }

        /// <summary>The name of the bot that played, for the report to say so.</summary>
        public string Bot { get; }

        /// <summary>One line saying what that bot does, which every report has to carry.</summary>
        public string BotDescription { get; }

        public IList<RunResult> Runs { get; } = new List<RunResult>();

        /// <summary>
        /// What the engine raised over this bot's runs: damage by what dealt it and by tag,
        /// healing, block, moves, statuses and cards. The amounts are the content's — they are the
        /// engine's own, and another bot making the same plays would reach the same ones — but
        /// which plays were made is this bot's, which is why there is one meter per bot.
        /// </summary>
        public Meter Meter { get; internal set; } = new Meter();

        /// <summary>How long this bot's runs took together.</summary>
        public TimeSpan Elapsed { get; internal set; }

        public int Errors => Runs.Count(r => r.Error != null);

        /// <summary>Battles that reached the turn limit without ending.</summary>
        public int Stalls => Runs.Sum(r => r.Battles.Count(b => b.Won == null));

        /// <summary>
        /// How many runs this bot finished, having won every battle the scenario names. It is a
        /// fact about the bot: another bot given the same content reaches another number.
        /// </summary>
        public int RunsWon => Runs.Count(r => r.Won);

        /// <summary>
        /// The same as a share of the runs, or null when nothing was played. A level, so the report
        /// prints it only with the warning that goes with it and never as a result.
        /// </summary>
        public double? Level => Runs.Count == 0 ? (double?)null : (double)RunsWon / Runs.Count;

        /// <summary>
        /// True when every run of this bot came out the same. Either nothing the scenario reaches
        /// rolls a die, or nothing it rolls changed the outcome; either way one run of this bot
        /// said everything its others did.
        /// </summary>
        public bool EveryRunIdentical => Runs.Count > 1 && Runs.Select(r => r.Signature).Distinct().Count() == 1;
    }

    /// <summary>
    /// One scenario, played by every bot that was asked for. The facts are one block for all of
    /// them, because a move that fired under either bot is a move the content reached; what each
    /// bot did with those facts is one block each, because that is all a level ever is.
    /// </summary>
    public sealed class ScenarioOutcome
    {
        internal ScenarioOutcome(ScenarioDefinition scenario, int turnLimit)
        {
            Scenario = scenario;
            TurnLimit = turnLimit;
        }

        public ScenarioDefinition Scenario { get; }

        public int TurnLimit { get; }

        /// <summary>What the content allowed, found by whichever bot found it.</summary>
        public ContentFacts Facts { get; } = new ContentFacts();

        /// <summary>One per bot, in the order they played.</summary>
        public IList<ScenarioResult> ByBot { get; } = new List<ScenarioResult>();

        public IList<ExpectResult> Expectations { get; } = new List<ExpectResult>();

        /// <summary>How many <c>battle</c> lines the scenario has.</summary>
        public int BattleLines { get; internal set; }

        /// <summary>How many other statements sit between them.</summary>
        public int OtherLines { get; internal set; }

        /// <summary>How many runs each bot played.</summary>
        public int RunsEach => ByBot.Count == 0 ? 0 : ByBot.Max(b => b.Runs.Count);

        /// <summary>Every run of every bot.</summary>
        public IEnumerable<RunResult> Runs => ByBot.SelectMany(b => b.Runs);

        public TimeSpan Elapsed => new TimeSpan(ByBot.Sum(b => b.Elapsed.Ticks));

        public int Errors => ByBot.Sum(b => b.Errors);

        /// <summary>Battles that reached the turn limit without ending, under any bot.</summary>
        public int Stalls => ByBot.Sum(b => b.Stalls);

        /// <summary>
        /// True when no bot found anything that changed an outcome: every run of every bot came out
        /// the same as that bot's others. Two bots still came out differently from each other.
        /// </summary>
        public bool EveryRunIdentical => ByBot.Count > 0 && ByBot.All(b => b.EveryRunIdentical);

        /// <summary>Whether this scenario is a reason to fail the command.</summary>
        public bool Failed => Errors > 0 || Stalls > 0 || Expectations.Any(e => e.Held == false);
    }
}
