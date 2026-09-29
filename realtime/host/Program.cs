using Cantrip;
using Cantrip.Content;
using Cantrip.Runtime;

namespace Cantrip.Realtime;

/// <summary>
/// Emberline, headless.
///
///   dotnet cantrip-realtime            plays the hold once and prints a transcript
///   dotnet cantrip-realtime --check    plays it and checks what CI needs to stay true
///   dotnet cantrip-realtime --seeds 8  plays eight seeds and prints the table
/// </summary>
public static class Program
{
    static int failures;

    public static int Main(string[] args)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "content");
        if (!Directory.Exists(folder)) folder = "realtime/content";
        ContentLibrary content = Emberline.Load(folder);

        if (args.Contains("--check")) return Check(content);

        int seeds = 1;
        int at = Array.IndexOf(args, "--seeds");
        if (at >= 0 && at + 1 < args.Length) seeds = int.Parse(args[at + 1]);

        if (seeds == 1) { Transcript(content, 1); return 0; }
        Console.WriteLine("seed   held   lasted   killed   keepers left");
        for (long seed = 1; seed <= seeds; seed++)
        {
            Emberline run = Play(content, seed, out int _);
            Console.WriteLine($"{seed,4}   {(run.Held ? "yes" : "no "),4}   {run.Second,5}s   {run.Killed,6}   {Describe(run.Party)}");
        }
        return 0;
    }

    /// <summary>
    /// The frame loop. A game engine calls this from a fixed timestep; here it runs as fast as
    /// the machine will go, because there is nothing to render.
    /// </summary>
    static Emberline Play(ContentLibrary content, long seed, out int steps)
    {
        var game = new Emberline(content);
        game.Begin(seed);
        steps = 0;
        while (!game.Over)
        {
            Keepers.Act(game);     // the player's hands, between ticks
            game.Step();           // one tick of the clock
            steps++;
        }
        return game;
    }

    static void Transcript(ContentLibrary content, long seed)
    {
        var game = new Emberline(content);
        game.Begin(seed);
        Console.WriteLine("Emberline -- a 45 second hold at 20 ticks a second, seed " + seed);
        Console.WriteLine();
        int lastSecond = -1;
        while (!game.Over)
        {
            Keepers.Act(game);
            game.Step();
            if (game.Second == lastSecond) continue;
            lastSecond = game.Second;
            Console.WriteLine($"  {game.Second,3}s  keepers {Describe(game.Party)}   line {Line(game)}");
        }
        Console.WriteLine();
        Console.WriteLine(game.Held
            ? $"The line held. {game.Killed} down, {game.Party.Count} keeper(s) standing."
            : $"The line broke at {game.Second}s.");
    }

    static string Describe(IReadOnlyList<Entity> party) =>
        party.Count == 0 ? "none" : string.Join(" ", party.Select(p => $"{p.Name}:{p.GetInt("hp")}"));

    static string Line(Emberline game) =>
        game.Enemies.Count == 0
            ? "clear"
            : string.Join(" ", game.Enemies.Select(e => $"{e.Name}({e.Lane},{e.Rank}):{e.GetInt("hp")}"));

    // ---------------------------------------------------------------------------------------

    static void Expect(string what, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what + (ok || detail.Length == 0 ? "" : "  (" + detail + ")"));
        if (!ok) failures++;
    }

    static int Check(ContentLibrary content)
    {
        // 1. The hold plays, and ends because the clock ran out rather than because something
        //    threw or the board happened to empty.
        Emberline game = Play(content, 1, out int steps);
        Expect("the hold runs to its end on the clock alone",
            game.Second >= Emberline.HoldSeconds, $"stopped at {game.Second}s after {steps} ticks");
        Expect("the keepers held the line", game.Held, $"{game.Party.Count} standing");
        Expect("things died on the way", game.Killed >= 6, game.Killed.ToString());
        Expect("no turn was ever taken", game.Runtime.State.Turn == 1, game.Runtime.State.Turn.ToString());

        // 2. The same seed plays the same hold.
        Emberline again = Play(content, 1, out _);
        Expect("the same seed plays the same hold, hash for hash",
            again.Runtime.State.ComputeHash() == game.Runtime.State.ComputeHash());

        // 3. Save and restore in the middle of a fight, with cooldowns running, a burn part way
        //    through a second and an `in 2s:` flare in the air.
        var live = new Emberline(content);
        live.Begin(3);
        for (int i = 0; i < Emberline.TicksPerSecond * 18; i++) { Keepers.Act(live); live.Step(); }
        Expect("something is on the board when the save is taken", live.Enemies.Count > 0);
        Expect("a save can be taken mid-fight", live.Runtime.CanCapture);
        GameSnapshot snapshot = live.Runtime.Capture();
        ulong hashBefore = live.Runtime.State.ComputeHash();

        var clockB = new TickClock(Emberline.TicksPerSecond);
        var restored = new CardRuntime(content, new RuntimeOptions { Clock = clockB, Seed = 3 });
        restored.Restore(snapshot);
        Expect("the restored game is the same game, hash for hash",
            restored.State.ComputeHash() == hashBefore,
            $"{restored.State.ComputeHash()} vs {hashBefore}");
        Expect("and the clock came back where it was",
            clockB.Now == live.Clock.Now, $"{clockB.Now} vs {live.Clock.Now}");
        Expect("so did the ids the host wrote down",
            restored.State.Find(live.Warden.Id)?.Name == "Warden");

        // Play both on for five more seconds with the same policy and compare.
        Emberline mirror = live.Adopt(restored, clockB);
        for (int i = 0; i < Emberline.TicksPerSecond * 5; i++)
        {
            Keepers.Act(live); live.Step();
            Keepers.Act(mirror); mirror.Step();
        }
        Expect("and five seconds later they are still the same game",
            mirror.Runtime.State.ComputeHash() == live.Runtime.State.ComputeHash(),
            $"{mirror.Runtime.State.ComputeHash()} vs {live.Runtime.State.ComputeHash()}");

        // 4. The cooldown read-out a UI needs.
        var fresh = new Emberline(content);
        fresh.Begin(5);
        Entity bolt = Emberline.AbilitiesOf(fresh.Runtime, fresh.Stoker).First(a => a.Name == "Ember Bolt");
        Expect("an ability is ready before it is ever used", fresh.Runtime.IsReady(bolt));
        Expect("and has no cooldown left to show", fresh.CooldownLeft(bolt) == 0);
        fresh.Runtime.UseAbility(bolt, fresh.Enemies[0]);
        Expect("using it starts a cooldown a sweep can draw",
            fresh.CooldownLeft(bolt) > 0.9 && fresh.CooldownLeft(bolt) <= 1.0,
            fresh.CooldownLeft(bolt).ToString("0.00"));
        for (int i = 0; i < Emberline.TicksPerSecond; i++) fresh.Step();
        Expect("and a second later it is back", fresh.Runtime.IsReady(bolt));

        // 5. What a real-time game has no business doing, and what happens when it does it anyway.
        //    None of these refuses; every one of them answers as though this were a turn game.
        Expect("CanAct answers on a tick runtime instead of refusing", fresh.Runtime.CanAct(fresh.Warden));
        Expect("ActiveMember answers too", fresh.Runtime.ActiveMember != null,
            fresh.Runtime.ActiveMember?.Name ?? "null");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "All checks passed." : failures + " check(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
