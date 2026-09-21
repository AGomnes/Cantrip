using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;

namespace Cantrip.Sim
{
    /// <summary>
    /// Plays for the player. It is also the runtime's chooser, so the same policy answers the
    /// decisions content asks for mid-effect: which card to discard, which card to discover.
    /// </summary>
    public interface IBot : IChoiceProvider, IDefinitionChooser
    {
        void PlayTurn(CardRuntime runtime, Action<string>? log);
        int PickReward(IReadOnlyList<string> offer, CardRuntime runtime);
        bool FightElite(CardRuntime runtime);
    }

    internal static class Plays
    {
        /// <summary>Every card in hand that can be paid for now, with each target it may be aimed at.</summary>
        public static List<(int Card, int Target)> Legal(CardRuntime runtime)
        {
            var plays = new List<(int, int)>();
            foreach (Entity card in runtime.State.ZoneOf(runtime.Player, Zones.Hand).ToArray())
            {
                if (!runtime.CanPlay(card)) continue;

                // An enemy or ally card is aimed at each legal target in turn; anything else is
                // played at nothing and resolves its own target, as a self card does.
                string mode = runtime.TargetMode(card);
                if (mode == "enemy" || mode == "ally")
                {
                    foreach (Entity target in runtime.LegalTargets(card)) plays.Add((card.Id, target.Id));
                }
                else
                {
                    plays.Add((card.Id, 0));
                }
            }
            return plays;
        }

        public static PlayResult Play(CardRuntime runtime, (int Card, int Target) play) =>
            runtime.Play(runtime.State.Find(play.Card)!, play.Target == 0 ? null : runtime.State.Find(play.Target));

        public static string Describe(CardRuntime runtime, (int Card, int Target) play)
        {
            string card = runtime.State.Find(play.Card)?.Name ?? "?";
            return play.Target == 0 ? card : card + " -> " + runtime.State.Find(play.Target)?.Name;
        }
    }

    /// <summary>Plays random affordable cards at random targets until nothing is affordable.</summary>
    public sealed class RandomBot : IBot
    {
        private readonly Rng _rng;

        public RandomBot(ulong seed) => _rng = new Rng(seed ^ 0xB07UL);

        public void PlayTurn(CardRuntime runtime, Action<string>? log)
        {
            for (int guard = 0; guard < 30 && runtime.Won == null; guard++)
            {
                List<(int, int)> plays = Plays.Legal(runtime);
                if (plays.Count == 0) return;
                (int, int) play = plays[_rng.NextInt(0, plays.Count - 1)];
                log?.Invoke(Plays.Describe(runtime, play));
                Plays.Play(runtime, play);
            }
        }

        public int PickReward(IReadOnlyList<string> offer, CardRuntime runtime) => _rng.NextInt(0, offer.Count - 1);

        public bool FightElite(CardRuntime runtime) => _rng.NextInt(0, 1) == 1;

        public IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state)
        {
            var options = request.Options.ToList();
            _rng.Shuffle(options);
            return options.Take(Math.Max(request.Min, Math.Min(request.Max, options.Count))).ToList();
        }

        public EntityDefinition? ChooseDefinition(DefinitionChoice request, GameState state) =>
            request.Options.Count == 0 ? null : request.Options[_rng.NextInt(0, request.Options.Count - 1)];
    }

    /// <summary>
    /// One-card lookahead through the engine itself: for every legal play it snapshots the game,
    /// plays the card, ends the turn so the enemies answer, scores what is left, and rolls back.
    /// It then makes the best play, and stops when ending the turn now scores as well as anything.
    /// It knows no card by name, so new content needs no bot changes. It does see the outcome of
    /// the next few random rolls, which a player would not; treat its numbers as a skilled player's.
    /// </summary>
    public sealed class GreedyBot : IBot
    {
        private readonly Rng _rng;

        public GreedyBot(ulong seed) => _rng = new Rng(seed ^ 0x6EEDUL);

        public void PlayTurn(CardRuntime runtime, Action<string>? log)
        {
            for (int guard = 0; guard < 30 && runtime.Won == null; guard++)
            {
                List<(int, int)> plays = Plays.Legal(runtime);
                if (plays.Count == 0) return;

                GameSnapshot before = runtime.Capture();
                double best = ScoreEndingTurn(runtime);
                runtime.Restore(before);
                (int, int)? choice = null;

                foreach ((int, int) play in plays)
                {
                    if (Plays.Play(runtime, play) == PlayResult.Played)
                    {
                        double score = runtime.Won == true ? double.MaxValue : ScoreEndingTurn(runtime);
                        // Ties go to playing: a card whose value only shows next turn (a draw, an
                        // energy gain) is worth at least nothing.
                        if (score >= best)
                        {
                            best = score;
                            choice = play;
                        }
                    }
                    runtime.Restore(before);
                }

                if (choice == null) return;
                log?.Invoke(Plays.Describe(runtime, choice.Value));
                Plays.Play(runtime, choice.Value);
            }
        }

        private static double ScoreEndingTurn(CardRuntime runtime)
        {
            if (runtime.Won == null) runtime.EndTurn();
            if (runtime.Won == true) return 100000;
            if (runtime.Won == false) return -100000;

            Entity player = runtime.Player!;
            double score = 1.5 * player.GetInt("hp");
            foreach (Entity enemy in runtime.State.Actors(Team.Enemy))
            {
                score -= enemy.GetInt("hp");
                // Damage still to come, at a discount, and hits already softened.
                score += 0.8 * enemy.CounterOf("Burn");
                score += 0.5 * enemy.CounterOf("Chill");
                score -= 1.0 * enemy.CounterOf("Strength");
            }
            return score;
        }

        public int PickReward(IReadOnlyList<string> offer, CardRuntime runtime) => _rng.NextInt(0, offer.Count - 1);

        public bool FightElite(CardRuntime runtime)
        {
            Entity player = runtime.Player!;
            return player.GetInt("hp") * 100 >= player.GetInt("max_hp") * 60;
        }

        public IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state) =>
            request.Options.Take(Math.Max(request.Min, Math.Min(request.Max, request.Options.Count))).ToList();

        public EntityDefinition? ChooseDefinition(DefinitionChoice request, GameState state) =>
            request.Options.Count == 0 ? null : request.Options[0];
    }
}
