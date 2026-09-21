using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;

namespace Cantrip.Sim
{
    public enum FloorKind { Battle, EliteOrRest, Boss }

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
        public const int RestHealPercent = 30;

        private readonly ContentLibrary _content;
        private readonly Func<ulong, IBot> _makeBot;

        public RunSimulator(ContentLibrary content, Func<ulong, IBot> makeBot)
        {
            _content = content;
            _makeBot = makeBot;
        }

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

            Entity player = runtime.CreatePlayer(hp: PlayerHp, maxEnergy: PlayerEnergy);
            runtime.AddDeck(StarterDeck);

            try
            {
                for (int i = 0; i < Tower.Count; i++)
                {
                    Floor floor = Tower[i];
                    bool elite = false;

                    if (floor.Kind == FloorKind.EliteOrRest)
                    {
                        elite = bot.FightElite(runtime);
                        result.TookElite = elite;
                        if (!elite)
                        {
                            int heal = player.GetInt("max_hp") * RestHealPercent / 100;
                            runtime.Execute("heal " + heal, player);
                            Say($"Floor {i + 1}: rests, heals {heal} to {player.GetInt("hp")}");
                            result.FloorsCleared++;
                            continue;
                        }
                    }

                    BattleResult battle = Fight(runtime, bot, floor, i + 1);
                    result.Battles.Add(battle);
                    if (battle.Won != true)
                    {
                        result.DiedTo = floor.Label + (battle.Won == null ? " (turn limit)" : "");
                        break;
                    }

                    result.FloorsCleared++;
                    if (floor.Kind == FloorKind.Boss) break;

                    string? pick = OfferCards(runtime, bot, runRng);
                    if (pick != null) result.Picks.Add(pick);
                    if (elite)
                    {
                        string? relic = OfferRelic(runtime, runRng);
                        if (relic != null) result.Relics.Add(relic);
                    }
                }
            }
            catch (Exception error)
            {
                result.Error = error.GetType().Name + ": " + error.Message;
            }

            result.Won = result.Error == null && result.FloorsCleared == Tower.Count && result.DiedTo == null;
            result.FinalHp = player.GetInt("hp");
            return result;
        }

        private BattleResult Fight(CardRuntime runtime, IBot bot, Floor floor, int number)
        {
            Entity player = runtime.Player!;
            int hpBefore = player.GetInt("hp");
            foreach (string enemy in floor.Enemies) runtime.SpawnEnemy(enemy);
            runtime.StartBattle();
            Say($"Floor {number}: {floor.Label}, at {hpBefore} hp");

            int turns = 0;
            while (runtime.Won == null && turns < TurnLimit)
            {
                turns++;
                if (Log != null) Say("  turn " + turns + ": " + Board(runtime));
                bot.PlayTurn(runtime, Log == null ? null : (Action<string>)(line => Say("    " + line)));
                if (runtime.Won != null) break;
                runtime.EndTurn();
            }

            int hpAfter = runtime.Player!.GetInt("hp");
            Say($"  {(runtime.Won == true ? "won" : runtime.Won == false ? "lost" : "turn limit")} after {turns} turn(s), {hpAfter} hp left");
            return new BattleResult { Encounter = floor.Label, Won = runtime.Won, Turns = turns, HpLost = Math.Max(0, hpBefore - hpAfter) };
        }

        private string? OfferCards(CardRuntime runtime, IBot bot, Rng rng)
        {
            List<string> pool = _content.Pool("card").Where(d => d.HasTag("reward")).Select(d => d.Name).ToList();
            rng.Shuffle(pool);
            List<string> offer = pool.Take(RewardChoices).ToList();
            if (offer.Count == 0) return null;

            string pick = offer[bot.PickReward(offer, runtime)];
            runtime.AddCard(pick);
            Say($"  offered {string.Join(", ", offer)}; took {pick}");
            return pick;
        }

        private string? OfferRelic(CardRuntime runtime, Rng rng)
        {
            var owned = new HashSet<string>(runtime.State.ZoneOf(runtime.Player, Zones.Relics).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            List<string> pool = _content.Pool("relic").Select(d => d.Name).Where(n => !owned.Contains(n)).ToList();
            if (pool.Count == 0) return null;

            string relic = pool[rng.NextInt(0, pool.Count - 1)];
            runtime.AddRelic(relic);
            Say("  relic: " + relic);
            return relic;
        }

        private void Say(string line) => Log?.Invoke(line);

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
