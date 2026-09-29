using Cantrip;
using Cantrip.Content;
using Cantrip.Runtime;

namespace Cantrip.Realtime;

/// <summary>
/// Emberline: a real-time hold, played headlessly.
///
/// Everything in this file that is not a call into Cantrip is a thing a real-time game has to
/// write that a turn game gets for free. There is a lot of it, and realtime/FINDINGS.md counts
/// the cost. In order down the file: a clock and a fixed timestep, a wave schedule, placing what
/// spawns, keeping the battle alive between waves, a cooldown read-out, and deciding when the
/// fight is over.
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

    /// <summary>
    /// Sets up the hold. `RuntimeOptions.Clock` with a `TickClock` is the whole of "this is a
    /// real-time game" from C#; nothing in docs/csharp.md says so, and the constructor's argument
    /// is the tick rate that every `cooldown 1s` in the content converts through. FINDINGS #1.
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

        // A battle is over the instant no enemy is left standing, so a wave game ends in the gap
        // between two waves. The Dark is on the board before the battle starts and never leaves
        // it, which is the only way found to keep a real-time fight running. FINDINGS #7.
        Dark = Runtime.SpawnEnemy("The Dark");

        Runtime.StartBattle(false, false);   // nothing to shuffle, no hand to draw: there are no cards

        // The engine fills a board in single file down lane 0. Three keepers abreast on the front
        // rank is a string of content executed by the host, because nothing on CardRuntime places
        // an actor. FINDINGS #5.
        Runtime.Execute("loom self", Dark, null);
        Place(Warden, 0);
        Place(Stoker, 1);
        Place(Lantern, 2);

        ReleaseWaves();                      // the first wave is due at second 0
    }

    void Place(Entity who, int lane) => Runtime.Execute($"hold self {lane}", who, null);

    public Entity Dark { get; private set; } = null!;

    public Entity Spawn(string what, int lane)
    {
        Entity e = Runtime.SpawnEnemy(what);
        Runtime.Execute($"arrive self {lane}", e, null);
        return e;
    }

    public IReadOnlyList<Entity> Party => Runtime.Party;

    /// <summary>What is on the line. The Dark is scenery, so it is not in this list.</summary>
    public IReadOnlyList<Entity> Enemies =>
        Runtime.State.Actors(Team.Enemy).Where(e => e.Name != "The Dark").ToList();
    public int Second => (int)(Clock.Now / TicksPerSecond);
    public bool Standing => Runtime.Party.Count > 0;

    /// <summary>Every ability in play for a member, which is what a row of buttons draws.</summary>
    public static IReadOnlyList<Entity> AbilitiesOf(CardRuntime runtime, Entity who) =>
        runtime.State.ZoneOf(who, Zones.Attached).Where(e => e.Kind == EntityKind.Ability).ToList();

    /// <summary>
    /// Seconds until an ability comes back, for a cooldown sweep. `ready_at` is an undocumented
    /// stat that only exists once the ability has been used at all. FINDINGS #9.
    /// </summary>
    public double CooldownLeft(Entity ability)
    {
        if (Runtime.IsReady(ability)) return 0;
        if (!ability.HasStat("ready_at")) return 0;
        long left = ability.GetInt("ready_at") - Clock.Now;
        return left <= 0 ? 0 : (double)left / TicksPerSecond;
    }

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
    /// it on. The wave schedule is the host's, so it has to be carried across by hand: a Cantrip
    /// save knows nothing above the fight. FINDINGS #12.
    /// </summary>
    public Emberline Adopt(CardRuntime other, TickClock otherClock)
    {
        var twin = new Emberline(content)
        {
            Runtime = other,
            Clock = otherClock,
            nextWave = nextWave,
            Stoker = other.Player,
            Dark = other.State.Find(Dark.Id)!,
            Warden = other.State.Find(Warden.Id)!,
            Lantern = other.State.Find(Lantern.Id)!,
        };
        return twin;
    }

    public bool Over => Runtime.Won != null || Second >= HoldSeconds;

    /// <summary>
    /// Whether the hold was held. `Runtime.Won` is not the question a wave game asks -- it turns
    /// true the moment the board happens to be empty between waves -- so the host owns the
    /// ending. FINDINGS #7.
    /// </summary>
    public bool Held => Standing && Second >= HoldSeconds;
}

/// <summary>Records what happened, for a transcript. Never calls back into the runtime.</summary>
public sealed class EventLog : EffectHostBase
{
    public List<GameEvent> All { get; } = new List<GameEvent>();
    public override void OnEvent(GameEvent gameEvent) => All.Add(gameEvent);
}
