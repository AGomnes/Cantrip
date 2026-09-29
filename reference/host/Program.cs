using Cantrip.Content;
using Cantrip.Reference;

// The Drowned Chapel, played with nobody at the keyboard, so CI can run a whole descent.
//
//   dotnet run --project reference/host -- --seed 7
//   dotnet run --project reference/host -- --check        the run CI checks
//
// --check plays a fixed set of seeds, and for each one saves the run part-way down, restores it
// into a new runtime and plays the rest twice: once from the live game and once from the save.
// The two have to agree, hash for hash, or saving does not keep the promise 1.x makes.

int seed = 7;
bool check = false;
bool quiet = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i]); break;
        case "--check": check = true; break;
        case "--quiet": quiet = true; break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

string folder = Path.Combine(AppContext.BaseDirectory, "content");
if (!Directory.Exists(folder))
{
    // Run from the repository as `dotnet run --project reference/host`, the content is a sibling
    // of the project rather than of the binary.
    folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "content");
}

var content = new ContentLibrary();
content.LoadFolder(folder);
content.Diagnostics.ThrowIfErrors();

void Say(string line)
{
    if (!quiet) Console.WriteLine(line);
}

if (!check)
{
    var one = new Chapel(content, Say);
    one.Begin(seed);
    one.Descend();
    Console.WriteLine(one.Won ? "The chapel is silent. The party climbs out." : "The water closes over them.");
    return 0;
}

int failures = 0;
void Check(string what, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "ok   " : "FAIL ") + what + (ok || detail.Length == 0 ? "" : $" ({detail})"));
    if (!ok) failures++;
}

int wins = 0;
foreach (int s in new[] { 1, 2, 3, 5, 8, 13, 21, 34 })
{
    var live = new Chapel(content, _ => { });
    live.Begin(s);
    live.Descend(stopBefore: 4);            // the two fights, the shrine and the third fight

    if (!live.Alive)
    {
        Check($"seed {s}: the party is wiped before the save point", true);
        continue;
    }

    ChapelSave save = live.Save();
    string json = save.ToJson();

    // A second, entirely separate game, restored from the file. Its entities are new objects,
    // so the run's own roster has to find them again by the ids it stored.
    var restored = new Chapel(content, _ => { });
    restored.Begin(s);
    restored.Load(ChapelSave.FromJson(json));

    Check($"seed {s}: a restored game has the same state hash", live.Hash() == restored.Hash(),
        $"{live.Hash()} vs {restored.Hash()}");
    Check($"seed {s}: and the same floor", live.State.Floor == restored.State.Floor,
        $"{live.State.Floor} vs {restored.State.Floor}");

    live.Descend();
    restored.Descend();

    Check($"seed {s}: the rest of the run plays out identically", live.Hash() == restored.Hash(),
        $"{live.Hash()} vs {restored.Hash()}");
    Check($"seed {s}: and ends the same way", live.Won == restored.Won && live.Alive == restored.Alive);
    if (live.Won) wins++;
}

Console.WriteLine($"{wins} of 8 seeds reached the bottom.");
Check("at least one seed is winnable", wins > 0);
Check("the chapel is not a walkover", wins < 8);
Console.WriteLine(failures == 0 ? "The Drowned Chapel: all checks passed." : $"The Drowned Chapel: {failures} check(s) failed.");
return failures == 0 ? 0 : 1;
