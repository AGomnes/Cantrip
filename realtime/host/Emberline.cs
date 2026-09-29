using Cantrip;
using Cantrip.Content;
using Cantrip.Runtime;

namespace Cantrip.Realtime;

/// <summary>
/// Emberline: a real-time hold, played headlessly.
///
/// Everything in this file that is not a call into Cantrip is a thing a real-time game has to
/// write that a turn game gets for free, and realtime/FINDINGS.md counts what that used to be.
/// What is left is three things, and all three are the game's own: a clock and a fixed timestep,
/// a wave schedule, and deciding when the hold is over.
/// </summary>
public sealed class Emberline
{
    public const int TicksPerSecond = 20;
    public const int HoldSeconds = 45;

    /// <summary>A wave: the second it arrives, what arrives, and which lane it walks down.</summary>
    public readonly record struct Wave(int AtSecond, string Enemy, int Lane);

    // The whole encounter. Cantrip has no idea a wave exists -- a `scenario` names fights, not
    // arrivals in time -- so this list is the host's, and so is everything that reads it.
    public static readonly Wave[] Schedule =
    {
        new(0,  "Hollow",  1),
        new(2,  "Hollow",  0),
        new(4,  "Hollow",  2),
        new(6,  "Wisp",    2),
        new(8,  "Hollow",  1),
        new(10, "Hollow",  0),
        new(12, "Breaker", 1),
        new(13, "Hollow",  2),
        new(16, "Hollow",  0),
        new(18, "Wisp",    1),
        new(20, "Wisp",    0),
        new(21, "Hollow",  2),
        new(24, "Hollow",  1),
        new(25, "Hollow",  0),
        new(26, "Breaker", 0),
        new(28, "Hollow",  2),
        new(30, "Hollow",  1),
        new(32, "Wisp",    2),
        new(34, "Wisp",    1),
        new(35, "Hollow",  0),
        new(38, "Hollow",  2),
        new(39, "Breaker", 1),
        new(40, "Breaker", 2),
        new(42, "Hollow",  0),
        new(43, "Hollow",  1),
    };

    readonly ContentLibrary content;
    public TickClock Clock { get; private set; } = null!;
    public CardRuntime Runtime { get; private set; } = null!;
    public EventLog Events { get; } = new EventLog();

    public Entity Stoker { get; private set; } = null!;
    public Entity Warden { get; private set; } = null!;
    public Entity Lantern { get; private set; } = null!;

    int nextWave;
    public int Killed => Events.All.Count(e => e.Name == "killed" && e.Target != null && e.Target.Team == Team.Enemy);
    public int Leaked { get; private set; }

    public Emberline(ContentLibrary content) => this.content = content;

    public static ContentLibrary Load(string folder)
    {
        var library = new ContentLibrary();
        library.LoadFolder(folder);
        library.Diagnostics.ThrowIfErrors();
        return library;
    }

    /// <summary>The rank a wave walks in at. Everything hostile comes out of the dark here.</summary>
    public const int ArriveRank = 3;

    /// <summary>
    /// Sets up the hold. `RuntimeOptions.Clock` with a `TickClock` is the whole of "this is a
    /// real-time game" from C#, and the constructor's argument is the tick rate that every
    /// `cooldown 1s` in the content converts through -- so the same content at 20 and at 60 is the
    /// same game, and that is why the rate lives beside the content it belongs to.
    /// </summary>
    public void Begin(long seed = 1)
    {
        Clock = new TickClock(TicksPerSecond);
        Runtime = new CardRuntime(content, new RuntimeOptions { Clock = Clock, Seed = seed, Host = Events });

        Stoker = Runtime.CreatePlayer("Stoker", hp: 30, maxEnergy: 3);
        Runtime.GrantAbility("Ember Bolt", Stoker);
        Runtime.GrantAbility("Backdraft", Stoker);
        Warden = Runtime.AddHero("Warden");
        Lantern = Runtime.AddHero("Lantern");

        // The content says `ends: called`, so an empty board between two waves is just an empty
        // board and the hold runs on the clock. Nothing has to stand on the board to keep the
        // fight open, and this game decides its own ending in Finish().
        Runtime.StartBattle(false, false);   // nothing to shuffle, no hand to draw: there are no cards

        // The engine fills a board in single file down lane 0. Three keepers abreast on the front
        // rank is three calls: where somebody stands is a rule, and Place is that rule from C#.
        Runtime.Place(Warden, 0, 0);
        Runtime.Place(Stoker, 1, 0);
        Runtime.Place(Lantern, 2, 0);

        ReleaseWaves();                      // the first wave is due at second 0
    }

    public Entity Spawn(string what, int lane)
    {
        Entity e = Runtime.SpawnEnemy(what);   // raises `created`, so content can meet an arrival
        Runtime.Place(e, lane, ArriveRank);
        return e;
    }

    public IReadOnlyList<Entity> Party => Runtime.Party;

    /// <summary>What is on the line.</summary>
    public IReadOnlyList<Entity> Enemies => Runtime.State.Actors(Team.Enemy);
    public int Second => (int)(Clock.Now / TicksPerSecond);
    public bool Standing => Runtime.Party.Count > 0;

    /// <summary>Every ability in play for a member, which is what a row of buttons draws.</summary>
    public static IReadOnlyList<Entity> AbilitiesOf(CardRuntime runtime, Entity who) => runtime.AbilitiesOf(who);

    /// <summary>Seconds until an ability comes back, for a cooldown sweep.</summary>
    public double CooldownLeft(Entity ability) => (double)Runtime.ReadyIn(ability) / TicksPerSecond;

    /// <summary>
    /// One fixed step. This is the whole of the host's clock: `Tick` is the only thing that makes
    /// time pass, and `EndTurn` is never called anywhere in this game.
    /// </summary>
    public void Step()
    {
        Runtime.Tick(1);
        ReleaseWaves();
    }

    void ReleaseWaves()
    {
        while (nextWave < Schedule.Length && Schedule[nextWave].AtSecond <= Second)
        {
            Wave wave = Schedule[nextWave++];
            Spawn(wave.Enemy, wave.Lane);
        }
    }

    /// <summary>
    /// Wraps a runtime restored from a save back up as an Emberline, so the same policy can play
    /// it on. The clock comes back in the save; the wave schedule does not, because a Cantrip save
    /// knows nothing above the fight, so it is carried across by hand.
    /// </summary>
    public Emberline Adopt(CardRuntime other, TickClock otherClock)
    {
        var twin = new Emberline(content)
        {
            Runtime = other,
            Clock = otherClock,
            nextWave = nextWave,
            Stoker = other.Player,
            Warden = other.State.Find(Warden.Id)!,
            Lantern = other.State.Find(Lantern.Id)!,
        };
        return twin;
    }

    public bool Over => Runtime.Won != null || Second >= HoldSeconds;

    /// <summary>
    /// Says the fight is over, which under `ends: called` is the game's own decision and nobody
    /// else's: the keepers win by still being there when the forty-five seconds run out.
    /// </summary>
    /// <remarks>
    /// The rules end it themselves when the party falls, which is the losing half and is the same
    /// rule in every game. What `ends: called` turns off is the other half, the one a wave game
    /// cannot live with: winning because the board happened to be empty between two waves.
    /// </remarks>
    public void Finish()
    {
        if (Runtime.State.InBattle) Runtime.EndBattle(won: Standing && Second >= HoldSeconds);
    }

    /// <summary>Whether the hold was held.</summary>
    public bool Held => Runtime.Won == true;
}

/// <summary>Records what happened, for a transcript. Never calls back into the runtime.</summary>
public sealed class EventLog : EffectHostBase
{
    public List<GameEvent> All { get; } = new List<GameEvent>();
    public override void OnEvent(GameEvent gameEvent) => All.Add(gameEvent);
}
