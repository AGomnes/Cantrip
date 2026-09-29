using Cantrip;
using Cantrip.Content;
using Cantrip.Runtime;

namespace Cantrip.Reference;

/// <summary>
/// The run above the battle: the part Cantrip deliberately does not model. Seven floors, the
/// rewards between them, a shrine, a shop, and a save that has to hold two things at once.
/// </summary>
public sealed class Chapel
{
    public const int Floors = 7;

    /// <summary>The last floor, where the Antiphonary is: the one fight in the nave.</summary>
    private const int LastFloor = Floors - 1;

    private static readonly string[][] Encounters =
    {
        new[] { "Drowned Acolyte" },
        new[] { "Drowned Acolyte", "Choir of Teeth" },
        Array.Empty<string>(),                                     // the shrine
        new[] { "Bell Warden", "Tidewalker" },
        Array.Empty<string>(),                                     // the shop
        new[] { "Bell Warden", "Drowned Acolyte", "Choir of Teeth" },
        new[] { "The Antiphonary" },
    };

    private static readonly string[] StarterDeck =
    {
        "Censer", "Censer", "Censer", "Litany", "Litany", "Litany", "Wade", "Wade", "Pike", "Grace",
    };

    private readonly ContentLibrary content;
    private readonly Action<string> say;

    private CardRuntime runtime;
    private RunState run = new();
    private RunRandom rng = new(1);

    public Chapel(ContentLibrary content, Action<string> say)
    {
        this.content = content;
        this.say = say;
        runtime = new CardRuntime(content, new RuntimeOptions { Seed = 1 });
    }

    public CardRuntime Runtime => runtime;
    public RunState State => run;
    public bool Alive { get; private set; } = true;
    public bool Won { get; private set; }

    // --- starting a run -------------------------------------------------------------------

    public void Begin(int seed)
    {
        runtime.Dispose();
        runtime = new CardRuntime(content, new RuntimeOptions { Seed = seed });
        run = new RunState { Seed = seed, Floor = 0, RunRng = (ulong)(uint)seed * 2654435761ul + 1ul };
        rng = new RunRandom(run.RunRng);
        Alive = true;
        Won = false;

        Entity leader = runtime.CreatePlayer("Acolyte", hp: 40, maxEnergy: 3);

        // The leader is the one party member no declaration describes, so the speed that decides
        // the whole initiative order is written onto it afterwards. SetStat is the same thing
        // content's `speed = 6` does, with the resource's bounds and its event.
        runtime.SetStat(leader, "speed", 6);
        runtime.Execute("grant Invocation");

        Remember(leader, "Acolyte");
        Remember(runtime.AddHero("Warden"), "Warden");
        Remember(runtime.AddHero("Cantor"), "Cantor");

        runtime.AddDeck(StarterDeck);
        say($"A seed of {seed}. The Acolyte, the Warden and the Cantor go down into the water.");
    }

    private void Remember(Entity member, string name) =>
        run.Roster.Add(new RunState.RosterEntry { Id = member.Id, Name = name });

    // --- the descent ----------------------------------------------------------------------

    /// <summary>Plays floors from where the run stands to the end of it, or to a wipe.</summary>
    public void Descend(int stopBefore = Floors)
    {
        while (Alive && !Won && run.Floor < Math.Min(stopBefore, Floors))
        {
            PlayFloor(run.Floor);
            run.Floor++;
        }

        if (Alive && run.Floor >= Floors) Won = true;
    }

    private void PlayFloor(int floor)
    {
        string[] encounter = Encounters[floor];
        if (encounter.Length == 0)
        {
            if (floor == 2) Shrine();
            else Shop();
            return;
        }

        Fight(floor, encounter);
    }

    private void Fight(int floor, string[] encounter)
    {
        foreach (string name in encounter) runtime.SpawnEnemy(name);
        // The Antiphonary sits in the nave, which is a rank deeper than the rest of the chapel.
        // The Godot half of this game says the same thing with StartBattleOn.
        runtime.StartBattle(board: floor == LastFloor ? "Nave" : "Chapel");
        say($"Floor {floor + 1}: {string.Join(", ", encounter)}.");

        bool won = new Tactician(runtime, say).FightToTheEnd();
        if (!won)
        {
            Alive = false;
            say($"  The party falls on floor {floor + 1}.");
            return;
        }

        // The purse is the run's, but `gold` is a stat on the leader, so the run pays into the
        // rules rather than keeping a number of its own. That is the one place where the part
        // Cantrip does not model and the part it does share a field.
        runtime.ChangeStat(Leader, "gold", 30);
        say($"  Won on turn {runtime.State.Turn}. {PartyLine()}  gold {Gold()}");
        Reward();
        BuryTheFallen();
    }

    /// <summary>
    /// A reward screen. Cantrip hands over the candidates -- content.Pool("card") reads the
    /// loaded definitions -- and the rest is the game's: which are eligible, how many to offer,
    /// which the player took, and putting it in the deck.
    /// </summary>
    private void Reward()
    {
        var pool = content.Pool("card")
            .Where(d => d.HasTag("reward") && !d.HasTag("upgraded"))
            .Select(d => d.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (pool.Count == 0) return;

        var offer = new List<string>();
        for (int i = 0; i < 3 && pool.Count > 0; i++) offer.Add(rng.Take(pool));

        string taken = offer[rng.Next(offer.Count)];
        runtime.AddCard(taken);
        say($"  Offered {string.Join(", ", offer)}; took {taken}.");
    }

    // --- the shrine -------------------------------------------------------------------------

    /// <summary>
    /// Rest, recruit or raise. Content can spell a raise too -- Last Rites is `revive fallen.first
    /// 8` -- so this is the run offering one rather than the only way to get one.
    /// </summary>
    private void Shrine()
    {
        say("Floor 3: a shrine above the water line.");

        Entity? fallen = runtime.Fallen.FirstOrDefault();
        if (fallen != null)
        {
            runtime.Revive(fallen, hp: Math.Max(1, fallen.GetInt("max_hp") / 2));
            say($"  {fallen.Name} is raised at {fallen.GetInt("hp")} hp.");
            return;
        }

        if (!run.Roster.Any(r => r.Name == "Ferryman"))
        {
            Remember(runtime.AddHero("Ferryman"), "Ferryman");
            say("  The Ferryman joins the party.");
            return;
        }

        runtime.Execute("heal 12 to party");
        say($"  The party rests. {PartyLine()}");
    }

    // --- the shop ---------------------------------------------------------------------------

    private static readonly (string Relic, int Price)[] Stock =
    {
        ("Drowned Coin", 20),
        ("Tideglass", 35),
        ("Pilgrim's Token", 40),
        ("Choirmaster's Baton", 45),
        ("Bell Rope", 55),
        ("Lantern of Ebb", 60),
    };

    private void Shop()
    {
        say($"Floor 5: the vestry. {Gold()} gold.");

        // A curse costs 15 to lift. RemoveCard is how a game takes a card out of the deck, and it
        // is the same call the Godot node has had all along.
        while (Gold() >= 15 && FirstInDeck("Brine") is { } brine)
        {
            runtime.RemoveCard(brine);
            Spend(15);
            say("  A Brine is lifted out of the book.");
        }

        // An upgrade is a definition of its own, swapped in. Nothing in Cantrip does this: the
        // deck is the draw pile between battles, and the swap is two calls and a price.
        foreach ((string from, string to, int price) in new[]
                 { ("Censer", "Censer+", 30), ("Pike", "Pike+", 30), ("Grace", "Grace+", 30) })
        {
            if (Gold() < price) break;
            if (FirstInDeck(from) is not { } card) continue;
            runtime.RemoveCard(card);
            runtime.AddCard(to);
            Spend(price);
            say($"  {from} is rewritten as {to}.");
        }

        foreach ((string relic, int price) in Stock)
        {
            if (Gold() < price) continue;
            if (runtime.State.ZoneOf(Leader, Zones.Relics).Any(r => r.Name == relic)) continue;
            runtime.AddRelic(relic);
            Spend(price);
            say($"  Bought {relic} for {price}.");
        }
    }

    private Entity Leader => runtime.Player;

    private int Gold() => Leader.GetInt("gold");

    // Gold is a stat on the leader, so the shop spends it through the rules rather than keeping a
    // purse of its own -- and it saves and restores with the battle for nothing.
    private void Spend(int amount) => runtime.ChangeStat(Leader, "gold", -amount);

    private Entity? FirstInDeck(string name) =>
        runtime.State.ZoneOf(Leader, Zones.Draw).FirstOrDefault(c => c.Name == name);

    // --- the party between battles -----------------------------------------------------------

    private void BuryTheFallen()
    {
        foreach (Entity member in runtime.Fallen) say($"  {member.Name} did not get up.");
    }

    private string PartyLine()
    {
        var parts = new List<string>();
        foreach (RunState.RosterEntry entry in run.Roster)
        {
            Entity? member = runtime.State.Find(entry.Id);
            if (member == null) continue;
            parts.Add(member.IsDead
                ? $"{entry.Name} fallen"
                : $"{entry.Name} {member.GetInt("hp")}/{member.GetInt("max_hp")}");
        }

        return string.Join("  ", parts);
    }

    // --- saving ------------------------------------------------------------------------------

    /// <summary>
    /// Two halves in one file: the battle snapshot Cantrip captures, and the run state it knows
    /// nothing about. They have to be written together, or a reload puts a floor-3 party on
    /// floor 1 with floor-3 gold.
    /// </summary>
    public ChapelSave Save()
    {
        if (!runtime.CanCapture) throw new InvalidOperationException("The rules cannot be saved here.");
        run.RunRng = rng.State;
        return new ChapelSave
        {
            Fingerprint = content.Fingerprint,
            Run = run,
            Game = runtime.Capture(),
        };
    }

    public void Load(ChapelSave save)
    {
        if (save.Fingerprint != content.Fingerprint)
            say("  The content has changed since this save was written; trying it anyway.");

        runtime.Restore(save.Game);
        run = save.Run;
        rng = new RunRandom(run.RunRng);
        Alive = true;
        Won = false;
    }

    // The state hash is how two runs are compared, and csharp.md names it three times without ever
    // saying what to call. The Godot node does `State.ComputeHash().ToString("x16")`, which is where
    // this came from: the addon source, not the guide.
    public string Hash() => runtime.State.ComputeHash().ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
}
