# Quickstart

> **Using Godot?** Start at [godot.md](godot.md) instead. Godot 4.6 is the engine Cantrip is built for: its guide installs the addon and plays this same battle from GDScript, in [Your first battle](godot.md#your-first-battle), and nothing on this page has to come first.

This page is the route for a game that is not in Godot. In about fifteen minutes it gets you from nothing to a small card battle you can play in a terminal: three cards, a status and an enemy written in Cantrip, checked by tests, and driven from C#. It assumes you know C# and the .NET command line, and nothing about Cantrip.

Writing cards rather than code? [writing-content.md](writing-content.md) starts from the content side; it needs no C#. Steps 2 and 3 below are worth reading whichever engine you are in, because the content and the `cantrip` command-line tool are the same everywhere.

## 1. Make a project

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download) or later. Create a console project, add the library, and install the `cantrip` command-line tool into the project:

<!-- smoke: run -->
```
dotnet new console -o HexDuel
cd HexDuel
dotnet add package Cantrip.Core
dotnet new tool-manifest
dotnet tool install Cantrip.Cli
mkdir content
```

The tool is installed for this project only, and runs as `dotnet cantrip`. Check it works:

<!-- smoke: run -->
```
dotnet cantrip --version
```

## 2. Write content

Content lives in `.cantrip` files. Create `content/game.cantrip`:

<!-- smoke: file content/game.cantrip -->
```
# A status: stacks add up, and at the end of its host's turn it deals that much damage and fades.
status Hex
  tags debuff
  stacking intensity
  on turn_end:
    deal stacks to owner, ignore block
    stacks -1

card Strike
  cost 1
  target enemy
  tags attack
  effect:
    deal 6 to target

card Defend
  cost 1
  effect:
    block 5

card Curse
  cost 1
  target enemy
  effect:
    apply Hex 3 to target
    if target.Hex >= 6:
      draw 1
  text: "Apply {Hex} Hex. If it now has 6 or more, draw {draw}."

enemy Ghoul
  hp 30
  move Claw:
    deal 7 to player
  move Howl:
    block 6
  pattern cycle Claw, Claw, Howl
```

A few things to notice. The first line is a comment: `#` starts one, and it runs to the end of the line. Indentation makes the blocks — this is Cantrip's own syntax, not YAML, so nothing here is a key and a value. A number on a property (`cost 1`, `hp 30`) becomes a stat. `target` is the enemy the card was played on, and `target.Hex` reads that enemy's stacks of Hex. The `text` line is optional; without it Cantrip writes serviceable rules text itself. [Files](language.md#files) in the language reference states these rules exactly.

## 3. Test it

Tests live in content too. Create `content/tests.cantrip`:

<!-- smoke: file content/tests.cantrip -->
```
test "Strike deals 6"
  enemy Ghoul
  play Strike on enemy
  expect enemy.hp == 24

test "Hex ticks at the end of the Ghoul's turn and fades by one"
  enemy Ghoul
  play Curse on enemy
  end turn
  expect enemy.hp == 27
  expect enemy.Hex == 2

test "A second Curse draws a card"
  enemy Ghoul
  deck Strike
  play Curse on enemy
  play Curse on enemy
  expect count(hand) == 1
```

Each test starts a fresh battle with a player on 80 hp and 3 energy. Run them, and the linter, which catches unknown names, events nothing raises and similar mistakes:

<!-- smoke: run -->
```
dotnet cantrip test content
dotnet cantrip lint content
```

Both should report no failures. Break something on purpose, say `deal 6 to targt`, and run them again to see what a mistake looks like: a file, a line, and a suggestion.

## 4. Play it from C#

Replace `Program.cs` with:

<!-- smoke: file Program.cs -->
```csharp
using Cantrip;
using Cantrip.Content;
using Cantrip.Descriptions;
using Cantrip.Runtime;

var content = new ContentLibrary();
content.LoadFolder("content");
content.Diagnostics.ThrowIfErrors();

var runtime = new CardRuntime(content, new RuntimeOptions { Seed = 42 });
var text = new DescriptionBuilder(content);
Entity player = runtime.CreatePlayer(hp: 40, maxEnergy: 3);
runtime.AddDeck("Strike", "Strike", "Strike", "Defend", "Defend", "Curse", "Curse");
Entity ghoul = runtime.SpawnEnemy("Ghoul");
runtime.StartBattle();

while (runtime.Won == null)
{
    Console.WriteLine();
    Console.WriteLine($"You: {player.GetInt("hp")} hp, {player.GetInt("block")} block, {player.GetInt("energy")} energy");
    Console.WriteLine($"Ghoul: {ghoul.GetInt("hp")} hp, {ghoul.CounterOf("Hex")} Hex");
    Console.WriteLine($"  Next move, {ghoul.Intent}: {text.DescribeIntent(ghoul, runtime).ToPlainText()}");

    var hand = runtime.State.ZoneOf(player, Zones.Hand).ToList();
    for (int i = 0; i < hand.Count; i++)
    {
        string mark = runtime.CanPlay(hand[i]) ? " " : "x";
        Console.WriteLine($" {mark} {i + 1}. {hand[i].Name} ({runtime.CostOf(hand[i])}): {text.Describe(hand[i], runtime, ghoul).ToPlainText()}");
    }

    Console.Write("Play a card by number, or press Enter to end the turn: ");
    string? input = Console.ReadLine();
    if (input == null) break;
    if (int.TryParse(input, out int n) && n >= 1 && n <= hand.Count)
    {
        Entity card = hand[n - 1];
        Entity? target = runtime.TargetMode(card) == "enemy" ? ghoul : null;
        Console.WriteLine(runtime.Play(card, target));
    }
    else
    {
        runtime.EndTurn();
    }
}

Console.WriteLine(runtime.Won == true ? "The Ghoul falls." : "You fall.");
```

Run it with `dotnet run`. Each line shows a card's cost and its rules text with live numbers, and an `x` marks a card you cannot play right now. Above the hand, the Ghoul shows its intent: the move it will make when you end your turn, described the same way, with live numbers. `Play` answers with what happened: `Played`, `CannotAfford`, `OutOfRange` and so on.

The whole rules engine is behind those few calls. Nothing in `Program.cs` knows what Hex does or how the Ghoul chooses its move; change `content/game.cantrip`, run again, and the game changes with it.

## 5. Where next

- [godot.md](godot.md) runs this same content in Godot 4.6 through the addon: a node GDScript drives, an importer that carries `.cantrip` files into exported builds, and an editor dock for problems, tests and card text.
- [writing-content.md](writing-content.md) is for whoever writes the cards, statuses, relics and enemies: a tutorial that goes further than steps 2 and 3, the edit and test loop, and recipes for common mechanics.
- [language.md](language.md) is the full language reference: every declaration, event, verb and modifier.
- [csharp.md](csharp.md) covers the rest of the C# side: showing events in a game's frame loop, player choices a UI answers, a party of heroes the game asks for input in turn, several battles in one run, real time on a tick clock your game advances instead of turns, saving and loading, hot reload, shipping content, and tracing why something happened.
- [api/README.md](api/README.md) lists every public type and member of `Cantrip.Core`, generated from the sources. The guides are the place to start; this is the place to look a call up.
- [troubleshooting.md](troubleshooting.md) is where to go when something will not load, a card will not play, a fight never ends or a save is refused. Most of it comes from two people building whole games on Cantrip from these docs alone and writing down every place they got stuck.
- [samples/slice](../samples/slice) is a larger sample, a five-floor roguelite. `dotnet cantrip sim samples/slice` plays its tower hundreds of times with two bots and reports what the content allowed: a run that threw, a fight that never ended, a card that was never playable, an enemy move that never fired — and where the hp went, which is the engine's own arithmetic rather than a judgement about play. [Simulating](simulating.md) covers it. The samples are in the repository rather than the packages, so clone it to run them; [samples/README.md](../samples/README.md) shows how, and describes the other samples.
- [reference/](../reference) is a whole game rather than a sample: *The Drowned Chapel*, a party of three descending seven floors of a flooded chapel, on a board, with cards and abilities together and a C# host that plays the run above the battles. It was built from these docs alone, and [reference/FINDINGS.md](../reference/FINDINGS.md) records every place that hurt. [realtime/](../realtime) is the same on the tick clock: *Emberline*, three keepers holding a burning line for forty-five seconds with nothing in it taking a turn, driven from the host's own fixed timestep.
- A battle need not be one hero taking turns with cards. [The party](csharp.md#the-party) adds heroes the game asks for input from, each with its own abilities and cooldowns; a [`board`](language.md#boards) puts a fight on lanes and ranks and gives a card, an ability or an enemy's move a `range`, which is what the `OutOfRange` in step 4 means; and [Real time](csharp.md#real-time) replaces turns with a clock the game ticks from its own frame loop. The small versions are [samples/party](../samples/party), a party with an enemy that telegraphs which of them it is going to hit; [samples/board](../samples/board), a Monster Train style train three floors tall; and [samples/abilities](../samples/abilities), a turn-based fight with no cards in it at all. Real time has no small sample of its own: [realtime/](../realtime) above is it.

## Working from source

To track unreleased changes or work on Cantrip itself, clone it next to your project and reference the source instead of the package. From inside `HexDuel`:

```
cd ..
git clone https://github.com/AGomnes/Cantrip.git
cd HexDuel
dotnet remove package Cantrip.Core
dotnet add reference ../Cantrip/src/Cantrip.Core/Cantrip.Core.csproj
dotnet run --project ../Cantrip/src/Cantrip.Cli -- test content
```

The last line is the source equivalent of `dotnet cantrip test content`, and from here it is the one to use: the project now references the clone, but the tool manifest still pins the released `cantrip`, so `dotnet cantrip` would check new content with the old tool.
