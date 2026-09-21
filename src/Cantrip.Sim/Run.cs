using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;

namespace Cantrip.Sim
{
    public enum FloorKind { Battle, EliteOrRest, Boss }

    /// <summary>How a reward card is chosen from the offer.</summary>
    public enum RewardPolicy
    {
        /// <summary>Any of the offered cards, at random. Clean for comparing cards with each other.</summary>
        Random,

        /// <summary>
        /// Plays out the rest of the run once per offered card, on a copy of the game with fresh
        /// dice, and takes the card that did best. Slower, but it is how a card's value shows up.
        /// </summary>
        Rollout,
    }

    /// <summary>Whether floor 3 is fought or rested on.</summary>
    public enum ElitePolicy
    {
        /// <summary>Fight when the bot says so: the greedy bot fights above 60% hp.</summary>
        Auto,
        Always,
        Never,

        /// <summary>Plays out the rest of the run both ways on copies of the game and takes the better.</summary>
        Rollout,
    }

    public sealed class Floor
    {
        public Floor(FloorKind kind, params string[] enemies)
        {
            Kind = kind;
            Enemies = enemies;
        }

        public FloorKind Kind { get; }
        public IReadOnlyList<string> Enemies { get; }
        public string Label => string.Join(" + ", Enemies);
    }

    /// <summary>
    /// What happened in one battle. <see cref="Won"/> is null when the turn limit stopped it, which
    /// counts as a loss for the run but is reported apart, because it usually means a rules loop or
    /// an enemy nothing in the deck can get through.
    /// </summary>
    public sealed class BattleResult
    {
        public string Encounter { get; set; } = "";
        public bool? Won { get; set; }
        public int Turns { get; set; }
        public int HpLost { get; set; }
    }

    public sealed class RunResult
    {
        public ulong Seed { get; set; }
        public bool Won { get; set; }
        public int FloorsCleared { get; set; }
        public string? DiedTo { get; set; }
        public string? Error { get; set; }
        public int FinalHp { get; set; }
        public bool TookElite { get; set; }
        public List<BattleResult> Battles { get; } = new List<BattleResult>();
        public List<string> Offered { get; } = new List<string>();
        public List<string> Picks { get; } = new List<string>();
        public List<string> Relics { get; } = new List<string>();
    }

    /// <summary>
    /// The run layer the language does not have yet (gap 17 in docs/coverage.md): hp, deck and
    /// relics carried between battles, a reward after each, and one choice on the map. It is host
    /// code on purpose, so the friction of writing it here is what decides whether it belongs in
    /// content later.
    /// </summary>
    public sealed class RunSimulator
    {
        public static readonly IReadOnlyList<Floor> Tower = new[]
        {
            new Floor(FloorKind.Battle, "Cinder Imp"),
            new Floor(FloorKind.Battle, "Frost Wisp", "Cinder Imp"),
            new Floor(FloorKind.EliteOrRest, "Stone Golem"),
            new Floor(FloorKind.Battle, "Tower Guard", "Frost Wisp"),
            new Floor(FloorKind.Boss, "Archmage"),
        };

        public static readonly string[] StarterDeck =
        {
            "Zap", "Zap", "Zap", "Zap", "Ward", "Ward", "Ward", "Ward", "Kindle", "Rime",
        };

        public const int PlayerHp = 60;
        public const int PlayerEnergy = 3;
        public const int RewardChoices = 3;
        public const int TurnLimit = 50;
        public const int RestHealPercent = 20;

        private readonly ContentLibrary _content;
        private readonly Func<ulong, IBot> _makeBot;

        public RunSimulator(ContentLibrary content, Func<ulong, IBot> makeBot)
        {
            _content = content;
            _makeBot = makeBot;
        }

        public RewardPolicy Rewards { get; set; } = RewardPolicy.Random;
        public ElitePolicy Elite { get; set; } = ElitePolicy.Auto;

        /// <summary>How many playouts judge each offered card under <see cref="RewardPolicy.Rollout"/>.</summary>
        public int Rollouts { get; set; } = 2;

        /// <summary>How many times floor 3 was decided by rollout, and how many of those chose to fight.</summary>
        public int EliteDecisions { get; private set; }
        public int EliteFights { get; private set; }

        /// <summary>Called with a line of commentary when a single run is being watched.</summary>
        public Action<string>? Log { get; set; }

        public RunResult Play(ulong seed)
        {
            var result = new RunResult { Seed = seed };
            IBot bot = _makeBot(seed);
            var runtime = new CardRuntime(_content, new RuntimeOptions { Seed = seed, Chooser = bot });
            // Map and reward rolls come from their own stream, so a different bot choice in one
            // battle cannot change which cards the next reward offers.
            var runRng = new Rng(seed ^ 0x5EEDF00DUL);

            runtime.CreatePlayer(hp: PlayerHp, maxEnergy: PlayerEnergy);
            runtime.AddDeck(StarterDeck);

            try
            {
                PlayFloors(runtime, bot, 0, runRng, result, Rewards, Log, nested: false);
            }
            catch (Exception error)
            {
                result.Error = error.GetType().Name + ": " + error.Message;
                result.Won = false;
            }

            result.FinalHp = runtime.Player!.GetInt("hp");
            return result;
        }

        /// <summary>
        /// Plays the tower from <paramref name="from"/> to the end, or until the player dies. The
        /// real run and every rollout go through here, so a rollout is the same game, not a model of it.
        /// </summary>
        private void PlayFloors(CardRuntime runtime, IBot bot, int from, Rng rng, RunResult result, RewardPolicy rewards, Action<string>? log, bool nested, bool? eliteChoice = null)
        {
            Entity player = runtime.Player!;
            for (int i = from; i < Tower.Count; i++)
            {
                Floor floor = Tower[i];
                bool elite = false;

                if (floor.Kind == FloorKind.EliteOrRest)
                {
                    elite = eliteChoice ?? DecideElite(runtime, bot, i, result.Seed, nested, log);
                    result.TookElite = elite;
                    if (!elite)
                    {
                        int heal = player.GetInt("max_hp") * RestHealPercent / 100;
                        runtime.Execute("heal " + heal, player);
                        log?.Invoke($"Floor {i + 1}: rests, heals {heal} to {player.GetInt("hp")}");
                        result.FloorsCleared++;
                        continue;
                    }
                }

                BattleResult battle = Fight(runtime, bot, floor, i + 1, log);
                result.Battles.Add(battle);
                if (battle.Won != true)
                {
                    result.DiedTo = floor.Label + (battle.Won == null ? " (turn limit)" : "");
                    break;
                }

                result.FloorsCleared++;
                if (floor.Kind == FloorKind.Boss) break;

                OfferCards(runtime, bot, rng, result, rewards, i + 1, log);
                if (elite)
                {
                    string? relic = OfferRelic(runtime, rng, log);
                    if (relic != null) result.Relics.Add(relic);
                }
            }

            result.Won = result.FloorsCleared == Tower.Count && result.DiedTo == null;
        }

        private static BattleResult Fight(CardRuntime runtime, IBot bot, Floor floor, int number, Action<string>? log)
        {
            Entity player = runtime.Player!;
            int hpBefore = player.GetInt("hp");
            foreach (string enemy in floor.Enemies) runtime.SpawnEnemy(enemy);
            runtime.StartBattle();
            log?.Invoke($"Floor {number}: {floor.Label}, at {hpBefore} hp");

            int turns = 0;
            while (runtime.Won == null && turns < TurnLimit)
            {
                turns++;
                log?.Invoke("  turn " + turns + ": " + Board(runtime));
                bot.PlayTurn(runtime, log == null ? null : (Action<string>)(line => log("    " + line)));
                if (runtime.Won != null) break;
                runtime.EndTurn();
            }

            int hpAfter = player.GetInt("hp");
            log?.Invoke($"  {(runtime.Won == true ? "won" : runtime.Won == false ? "lost" : "turn limit")} after {turns} turn(s), {hpAfter} hp left");
            return new BattleResult { Encounter = floor.Label, Won = runtime.Won, Turns = turns, HpLost = Math.Max(0, hpBefore - hpAfter) };
        }

        private void OfferCards(CardRuntime runtime, IBot bot, Rng rng, RunResult result, RewardPolicy rewards, int nextFloor, Action<string>? log)
        {
            List<string> pool = _content.Pool("card").Where(d => d.HasTag("reward")).Select(d => d.Name).ToList();
            rng.Shuffle(pool);
            List<string> offer = pool.Take(RewardChoices).ToList();
            if (offer.Count == 0) return;

            string pick;
            if (rewards == RewardPolicy.Rollout)
            {
                double[] scores = offer.Select(card => Playout(runtime, r => r.AddCard(card), nextFloor, Salt(result.Seed, nextFloor, card), null)).ToArray();
                pick = offer[Array.IndexOf(scores, scores.Max())];
                log?.Invoke($"  offered {string.Join(", ", offer.Select((c, n) => $"{c} ({scores[n]:0})"))}; took {pick}");
            }
            else
            {
                pick = offer[bot.PickReward(offer, runtime)];
                log?.Invoke($"  offered {string.Join(", ", offer)}; took {pick}");
            }

            runtime.AddCard(pick);
            result.Offered.AddRange(offer);
            result.Picks.Add(pick);
        }

        private bool DecideElite(CardRuntime runtime, IBot bot, int floor, ulong seed, bool nested, Action<string>? log)
        {
            switch (Elite)
            {
                case ElitePolicy.Always: return true;
                case ElitePolicy.Never: return false;
                case ElitePolicy.Rollout when !nested:
                    double fight = Playout(runtime, null, floor, Salt(seed, floor, "elite"), true);
                    double rest = Playout(runtime, null, floor, Salt(seed, floor, "elite"), false);
                    EliteDecisions++;
                    if (fight > rest) EliteFights++;
                    log?.Invoke($"Floor {floor + 1}: fight scores {fight:0}, rest scores {rest:0}");
                    return fight > rest;
                default:
                    return bot.FightElite(runtime);
            }
        }

        /// <summary>
        /// Plays the rest of the run on a copy of the game, after <paramref name="prepare"/> and with
        /// floor 3 decided by <paramref name="elite"/> if given, using a greedy bot and fresh dice so
        /// the judgement cannot peek at the real run's future. Scores favour winning, then getting
        /// further, then hp left; averaged over <see cref="Rollouts"/> playouts.
        /// </summary>
        private double Playout(CardRuntime runtime, Action<CardRuntime>? prepare, int fromFloor, ulong salt, bool? elite)
        {
            GameSnapshot before = runtime.Capture();
            IChoiceProvider chooser = runtime.Chooser;
            double total = 0;
            try
            {
                for (int n = 0; n < Rollouts; n++)
                {
                    ulong seed = salt + (ulong)n;
                    runtime.Restore(before);
                    prepare?.Invoke(runtime);
                    runtime.State.Rng.Reseed(seed);
                    var rolloutBot = new GreedyBot(seed);
                    runtime.Chooser = rolloutBot;

                    var scratch = new RunResult();
                    PlayFloors(runtime, rolloutBot, fromFloor, new Rng(seed ^ 0xA11UL), scratch, RewardPolicy.Random, null, nested: true, eliteChoice: elite);
                    total += (scratch.Won ? 1000 : 0) + 100 * scratch.FloorsCleared + runtime.Player!.GetInt("hp");
                }
            }
            finally
            {
                runtime.Chooser = chooser;
                runtime.Restore(before);
            }
            return total / Rollouts;
        }

        /// <summary>Dice for a rollout, from the run's seed, the floor and the card, so judging never draws on the run's own reward stream.</summary>
        private static ulong Salt(ulong seed, int floor, string card)
        {
            ulong hash = 14695981039346656037UL ^ seed ^ ((ulong)floor << 48);
            foreach (char c in card) hash = (hash ^ c) * 1099511628211UL;
            return hash;
        }

        private string? OfferRelic(CardRuntime runtime, Rng rng, Action<string>? log)
        {
            var owned = new HashSet<string>(runtime.State.ZoneOf(runtime.Player, Zones.Relics).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            List<string> pool = _content.Pool("relic").Select(d => d.Name).Where(n => !owned.Contains(n)).ToList();
            if (pool.Count == 0) return null;

            string relic = pool[rng.NextInt(0, pool.Count - 1)];
            runtime.AddRelic(relic);
            log?.Invoke("  relic: " + relic);
            return relic;
        }

        public static string Board(CardRuntime runtime)
        {
            Entity player = runtime.Player!;
            IEnumerable<string> enemies = runtime.State.Actors(Team.Enemy).Select(e =>
                $"{e.Name} {e.GetInt("hp")}hp{Statuses(e)} -> {e.Intent}");
            return $"you {player.GetInt("hp")}hp{Statuses(player)} | " + string.Join(" | ", enemies);
        }

        private static string Statuses(Entity entity)
        {
            List<string> parts = entity.Attached
                .Where(s => s.Kind == EntityKind.Status)
                .Select(s => $"{s.Name}{s.GetInt("stacks")}")
                .ToList();
            int block = entity.GetInt("block");
            if (block > 0) parts.Insert(0, "block" + block);
            return parts.Count == 0 ? "" : " [" + string.Join(" ", parts) + "]";
        }
    }
}
