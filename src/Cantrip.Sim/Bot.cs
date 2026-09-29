using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;

// Everything `cantrip sim` runs: the scenario runner, its bots and the meter that records what the
// engine raised. It is a library, shipped inside the `cantrip` tool rather than installed on its own.
namespace Cantrip.Sim
{
    /// <summary>
    /// Plays the player's turns of a scenario. It is also the runtime's chooser of last resort, so
    /// the same policy answers what content asks for mid-effect: which card to discard, which card
    /// to discover.
    /// </summary>
    /// <remarks>
    /// The runner talks to a bot only through this, so a new bot is a new implementation and
    /// nothing else. Nothing the report calls a fact about the content is read from the bot.
    /// </remarks>
    public interface IBot
    {
        /// <summary>What the report calls this bot.</summary>
        string Name { get; }

        /// <summary>One line saying what this bot is, printed under every report.</summary>
        string Description { get; }

        /// <summary>
        /// Plays the player's turn and returns; the runner ends the turn afterwards. A bot that
        /// plays nothing is legal, and ends its turns doing nothing.
        /// </summary>
        /// <param name="runtime">The live game. Anything a player could do, a bot may do.</param>
        /// <param name="trials">
        /// How to try a play without making it. Everything a bot does outside a trial happened and
        /// is counted; everything inside one did not.
        /// </param>
        /// <param name="log">Given a line per play while a single run is being watched.</param>
        void PlayTurn(CardRuntime runtime, Trials trials, Action<string>? log);

        /// <summary>
        /// Answers a choice content asks for mid-effect, when no <c>answer</c> is queued. A chooser
        /// that does not override <see cref="IChoiceProvider.ChooseDefinition"/>, as
        /// <see cref="RandomChooser"/> does, lets a <c>discover</c> take the first option offered.
        /// </summary>
        IChoiceProvider Chooser { get; }
    }

    /// <summary>
    /// How a bot tries a play without making it: the game is captured, the play is made, what is
    /// left is scored, and everything is put back.
    /// </summary>
    /// <remarks>
    /// What happens inside a trial did not happen, and the run has to know it, or an enemy move
    /// made only in a trial is reported as one the content reached. So while a trial is open the
    /// run records nothing the engine raises and spends none of the scenario's <c>answer</c> lines,
    /// and the dice are reseeded, so that a bot cannot choose the play that wins a roll it has read
    /// in advance. The reseeding costs three lines and changes nothing on content that telegraphs
    /// what is coming, as Cantrip's does; it is there for the card that says <c>chance 50: deal 20</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// using (trials.Begin(fog))
    /// {
    ///     Options.Take(runtime, option);
    ///     score = Score(runtime);
    /// }
    /// </code>
    /// </example>
    public sealed class Trials
    {
        private readonly CardRuntime _runtime;
        private readonly Action<bool> _recording;
        private int _open;

        internal Trials(CardRuntime runtime, Action<bool> recording)
        {
            _runtime = runtime;
            _recording = recording;
        }

        /// <summary>True while a trial is open, which is to say while nothing is really happening.</summary>
        public bool Open => _open > 0;

        /// <summary>
        /// Whether a trial can be opened at all right now. It is false while an effect is still
        /// resolving, and while a block is waiting that a snapshot cannot hold. A bot that finds it
        /// false has to choose without looking, which is still better than passing the turn.
        /// </summary>
        public bool CanTry => _runtime.CanCapture;

        /// <summary>Opens one; disposing it puts the game back exactly as it was.</summary>
        /// <param name="fog">
        /// The dice the trial rolls instead of the run's own. Give every play of one decision the
        /// same fog and they are compared on the same luck, rather than on which of them happened
        /// to draw the kinder number.
        /// </param>
        public IDisposable Begin(ulong fog)
        {
            GameSnapshot before = _runtime.Capture();
            if (++_open == 1) _recording(false);
            _runtime.State.Rng.Reseed(fog);
            return new Scope(this, before);
        }

        private void End(GameSnapshot before)
        {
            try
            {
                _runtime.Restore(before);
            }
            finally
            {
                if (--_open == 0) _recording(true);
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly Trials _owner;
            private readonly GameSnapshot _before;
            private bool _closed;

            public Scope(Trials owner, GameSnapshot before)
            {
                _owner = owner;
                _before = before;
            }

            public void Dispose()
            {
                if (_closed) return;
                _closed = true;
                _owner.End(_before);
            }
        }
    }

    /// <summary>
    /// How a bot takes a turn with a party rather than with one hero. The whole of the difference
    /// between the two is here, so that a bot stays a scoring policy and nothing else.
    /// </summary>
    internal static class Party
    {
        /// <summary>A turn that asks this many members is a rules loop rather than a turn.</summary>
        private const int MaxMembersPerTurn = 64;

        /// <summary>
        /// Plays the turn with every member that has a step in it, passing each one it is done with.
        /// </summary>
        /// <remarks>
        /// A party of one is not passed at all: the runner's <c>EndTurn</c> ends its turn, exactly
        /// as it did before a party existed, so every call the engine sees on a one-hero game is
        /// the call it saw before. With more than one member, passing is how the turn moves on —
        /// under <c>turns: initiative</c> it is the only way the next member ever comes up.
        /// </remarks>
        public static void EachMember(CardRuntime runtime, Action<Entity> play)
        {
            if (runtime.Party.Count <= 1)
            {
                if (runtime.Party.Count == 1) play(runtime.Party[0]);
                return;
            }

            // The turn this is playing. Passing the last member ends it, and the next round's
            // members are immediately waiting again — so without this the loop would play a whole
            // battle inside one turn, and the runner would count it as one.
            int round = runtime.State.Turn;

            for (int step = 0; step < MaxMembersPerTurn && runtime.Won == null && runtime.State.Turn == round; step++)
            {
                List<Entity> waiting = Options.StillToAct(runtime);
                if (waiting.Count == 0) return;

                Entity member = waiting[0];
                play(member);
                if (runtime.Won != null || runtime.State.Turn != round) return;

                // It fell while acting, so there is nothing of its own left to give up.
                if (!runtime.CanAct(member)) continue;
                if (runtime.Pass(member) != ActionResult.Played) return;
            }
        }
    }

    /// <summary>One thing a member of the party could do now: play a card, or use an ability.</summary>
    internal readonly struct Option
    {
        public Option(int source, int target, bool ability, int by = 0)
        {
            Source = source;
            Target = target;
            Ability = ability;
            By = by;
        }

        /// <summary>The card in hand, or the ability attached to the member.</summary>
        public int Source { get; }

        /// <summary>Who it is aimed at, or 0 for a play that resolves its own target.</summary>
        public int Target { get; }

        public bool Ability { get; }

        /// <summary>
        /// The member performing it, or 0 for the card's own controller — which is what a party of
        /// one always is, and why nothing about a one-hero game reads differently here.
        /// </summary>
        public int By { get; }
    }

    /// <summary>
    /// Everything the player could legally do this turn, asked of the engine rather than worked out
    /// again here, so a bot knows no card and no ability by name and new content needs no change.
    /// </summary>
    internal static class Options
    {
        /// <param name="actor">
        /// The member whose options these are, or null for the leader. A member with a hand of its
        /// own plays from it; one without plays from the party's, which is the leader's, and the
        /// play names it as the performer. For a party of one both readings are the same actor,
        /// which is why a game with no <c>hero</c> in it cannot tell this parameter exists.
        /// </param>
        public static List<Option> Legal(CardRuntime runtime, Entity? actor = null)
        {
            var options = new List<Option>();
            Entity player = runtime.Player!;
            Entity who = actor ?? player;
            bool performing = !ReferenceEquals(who, player);

            Entity holder = runtime.State.ZoneOf(who, Zones.Hand).Count > 0 ? who : player;
            foreach (Entity card in runtime.State.ZoneOf(holder, Zones.Hand).ToArray())
            {
                if (runtime.CanPlay(card)) Aim(runtime, card, ability: false, options, performing ? who.Id : 0);
            }

            // A fight with no cards in it is still a fight, so an ability off cooldown is a play too.
            foreach (Entity ability in who.Attached.ToArray())
            {
                if (ability.Kind == EntityKind.Ability && !ability.IsRemoved && runtime.IsReady(ability))
                    Aim(runtime, ability, ability: true, options, 0);
            }

            return options;
        }

        /// <summary>
        /// The members that still have a step this turn, in the order the engine offers them. For a
        /// party of one that is the leader, until it has passed.
        /// </summary>
        public static List<Entity> StillToAct(CardRuntime runtime) =>
            runtime.Party.Where(runtime.CanAct).ToList();

        /// <summary>
        /// Adds one option per legal target for something that needs one, or a single option for
        /// anything else, which resolves its own target as a self card does.
        /// </summary>
        private static void Aim(CardRuntime runtime, Entity source, bool ability, List<Option> options, int by)
        {
            string mode = runtime.TargetMode(source);
            if (mode == "enemy" || mode == "ally")
            {
                foreach (Entity target in runtime.LegalTargets(source)) options.Add(new Option(source.Id, target.Id, ability, by));
            }
            else
            {
                options.Add(new Option(source.Id, 0, ability, by));
            }
        }

        public static bool Take(CardRuntime runtime, Option option)
        {
            Entity? source = runtime.State.Find(option.Source);
            if (source == null) return false;

            Entity? target = option.Target == 0 ? null : runtime.State.Find(option.Target);
            Entity? by = option.By == 0 ? null : runtime.State.Find(option.By);
            return (option.Ability
                ? runtime.UseAbility(source, target)
                : runtime.Play(source, target, by)) == ActionResult.Played;
        }

        public static string Describe(CardRuntime runtime, Option option)
        {
            string name = runtime.State.Find(option.Source)?.Name ?? "?";
            return option.Target == 0 ? name : name + " -> " + (runtime.State.Find(option.Target)?.Name ?? "?");
        }

        /// <summary>The names of the cards, not the abilities, among a turn's options.</summary>
        public static IEnumerable<string> CardNames(CardRuntime runtime, IEnumerable<Option> options) =>
            options.Where(o => !o.Ability).Select(o => runtime.State.Find(o.Source)?.Name).Where(n => n != null)!;

        /// <summary>
        /// Plays the first option the engine accepts and says whether anything was played. It is
        /// what the random bot does once it has shuffled its options, and what a bot that cannot
        /// look ahead falls back on.
        /// </summary>
        public static bool TakeAny(CardRuntime runtime, IEnumerable<Option> options, Action<string>? log)
        {
            foreach (Option option in options)
            {
                // What can be played is asked in advance; content can still refuse a play while it
                // resolves, and no check made beforehand sees that, so the next option is tried.
                string what = Describe(runtime, option);
                if (!Take(runtime, option)) continue;

                log?.Invoke(what);
                return true;
            }
            return false;
        }
    }
}
