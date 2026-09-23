using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;

// This namespace is what `cantrip sim` runs: the scenario runner, its bot and its report. The
// older whole-run simulator beside it (Run.cs, Bots.cs, Program.cs) is a separate program with a
// hard-coded tower, and keeps its own `IBot` and `Plays`; the two are merged when it goes.
namespace Cantrip.Sim.Scenarios
{
    /// <summary>
    /// Plays the player's turns of a scenario. It is also the runtime's chooser of last resort, so
    /// the same policy answers what content asks for mid-effect: which card to discard, which card
    /// to discover.
    /// </summary>
    /// <remarks>
    /// The runner talks to a bot only through this, so a bot that weighs a play can replace the
    /// placeholder without the runner changing. Nothing the report calls a fact about the content
    /// is read from the bot.
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
        /// <param name="log">Given a line per play while a single run is being watched.</param>
        void PlayTurn(CardRuntime runtime, Action<string>? log);

        /// <summary>
        /// Answers a choice content asks for mid-effect, when no <c>answer</c> is queued. Give one
        /// that is an <see cref="IDefinitionChooser"/> as well, as <see cref="RandomChooser"/> is,
        /// or a <c>discover</c> takes the first option it is offered.
        /// </summary>
        IChoiceProvider Chooser { get; }
    }

    /// <summary>One thing the player could do now: play a card, or use an ability.</summary>
    internal readonly struct Option
    {
        public Option(int source, int target, bool ability)
        {
            Source = source;
            Target = target;
            Ability = ability;
        }

        /// <summary>The card in hand, or the ability attached to the player.</summary>
        public int Source { get; }

        /// <summary>Who it is aimed at, or 0 for a play that resolves its own target.</summary>
        public int Target { get; }

        public bool Ability { get; }
    }

    /// <summary>
    /// Everything the player could legally do this turn, asked of the engine rather than worked out
    /// again here, so a bot knows no card and no ability by name and new content needs no change.
    /// </summary>
    internal static class Options
    {
        public static List<Option> Legal(CardRuntime runtime)
        {
            var options = new List<Option>();
            Entity player = runtime.Player!;

            foreach (Entity card in runtime.State.ZoneOf(player, Zones.Hand).ToArray())
            {
                if (runtime.CanPlay(card)) Aim(runtime, card, ability: false, options);
            }

            // A fight with no cards in it is still a fight, so an ability off cooldown is a play too.
            foreach (Entity ability in player.Attached.ToArray())
            {
                if (ability.Kind == EntityKind.Ability && !ability.IsRemoved && runtime.IsReady(ability))
                    Aim(runtime, ability, ability: true, options);
            }

            return options;
        }

        /// <summary>
        /// Adds one option per legal target for something that needs one, or a single option for
        /// anything else, which resolves its own target as a self card does.
        /// </summary>
        private static void Aim(CardRuntime runtime, Entity source, bool ability, List<Option> options)
        {
            string mode = runtime.TargetMode(source);
            if (mode == "enemy" || mode == "ally")
            {
                foreach (Entity target in runtime.LegalTargets(source)) options.Add(new Option(source.Id, target.Id, ability));
            }
            else
            {
                options.Add(new Option(source.Id, 0, ability));
            }
        }

        public static bool Take(CardRuntime runtime, Option option)
        {
            Entity? source = runtime.State.Find(option.Source);
            if (source == null) return false;

            Entity? target = option.Target == 0 ? null : runtime.State.Find(option.Target);
            return option.Ability
                ? runtime.UseAbility(source, target)
                : runtime.Play(source, target) == PlayResult.Played;
        }

        public static string Describe(CardRuntime runtime, Option option)
        {
            string name = runtime.State.Find(option.Source)?.Name ?? "?";
            return option.Target == 0 ? name : name + " -> " + (runtime.State.Find(option.Target)?.Name ?? "?");
        }

        /// <summary>The names of the cards, not the abilities, among a turn's options.</summary>
        public static IEnumerable<string> CardNames(CardRuntime runtime, IEnumerable<Option> options) =>
            options.Where(o => !o.Ability).Select(o => runtime.State.Find(o.Source)?.Name).Where(n => n != null)!;
    }

    /// <summary>
    /// A placeholder. It plays the first thing it can, over and over, until nothing is left to do,
    /// and then ends the turn. It weighs nothing, looks nowhere ahead and knows no card by name.
    /// </summary>
    /// <remarks>
    /// It exists so that a scenario can be played at all: a run that throws, a battle that never
    /// ends and a card that is never playable show up under any bot, and those are the only things
    /// this release reports. How often it wins, and how long it takes, are facts about this bot.
    /// Bots that weigh a play come next; when they do, this one stays as the floor.
    /// </remarks>
    public sealed class FirstPlayableBot : IBot
    {
        /// <summary>A turn that plays this many times is a rules loop rather than a turn.</summary>
        private const int MaxPlaysPerTurn = 50;

        private readonly RandomChooser _chooser;

        /// <param name="seed">The run's seed. Its choices are forked from it, so a run repeats exactly.</param>
        public FirstPlayableBot(ulong seed) => _chooser = new RandomChooser(seed ^ 0xB07UL);

        public string Name => "placeholder";

        public string Description => "plays the first card or ability it can, in hand order, until it can play no more";

        public IChoiceProvider Chooser => _chooser;

        public void PlayTurn(CardRuntime runtime, Action<string>? log)
        {
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));

            for (int guard = 0; guard < MaxPlaysPerTurn && runtime.Won == null; guard++)
            {
                List<Option> options = Options.Legal(runtime);
                if (options.Count == 0) return;

                // The first option is the choice; the rest are only tried if it turns out content
                // refuses it while it resolves, which no check made in advance can see.
                bool played = false;
                foreach (Option option in options)
                {
                    string what = Options.Describe(runtime, option);
                    if (!Options.Take(runtime, option)) continue;

                    log?.Invoke(what);
                    played = true;
                    break;
                }

                if (!played) return;
            }
        }
    }
}
