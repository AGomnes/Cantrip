# Quickstart

In about fifteen minutes this gets you from nothing to a small card battle you can play in a terminal: three cards, a status and an enemy written in Cantrip, checked by tests, and driven from C#. It assumes you know C# and the .NET command line, and nothing about Cantrip.

## 1. Make a project

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download) or later. Create a console project, add the library, and install the `cantrip` command-line tool into the project:

<!-- smoke: run -->
```
dotnet new console -o HexDuel
cd HexDuel
dotnet add package Cantrip.Core --prerelease
dotnet new tool-manifest
dotnet tool install Cantrip.Cli --prerelease
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

A few things to notice. Indentation makes the blocks. A number on a property (`cost 1`, `hp 30`) becomes a stat. `target` is the enemy the card was played on, and `target.Hex` reads that enemy's stacks of Hex. The `text` line is optional; without it Cantrip writes serviceable rules text itself.

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
    Console.WriteLine($"Ghoul: {ghoul.GetInt("hp")} hp, {ghoul.CounterOf("Hex")} Hex, next move {ghoul.Intent}");

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

Run it with `dotnet run`. Each line shows a card's cost and its rules text with live numbers, and an `x` marks a card you cannot play right now. `Play` answers with what happened: `Played`, `NotEnoughEnergy`, `InvalidTarget` and so on.

The whole rules engine is behind those few calls. Nothing in `Program.cs` knows what Hex does or how the Ghoul chooses its move; change `content/game.cantrip`, run again, and the game changes with it.

## 5. Where next

- [language.md](language.md) is the full language reference: every declaration, event, verb and modifier.
- [godot.md](godot.md) runs the same content in Godot 4.6 through an addon, with an editor dock for problems, tests and card text.
- [csharp.md](csharp.md) covers the rest of the C# side: saving and loading, hot reload, player choices a UI answers, and tracing why something happened.
- `samples/slice` is a bigger example, a five-floor roguelite, and `src/Cantrip.Sim` plays it with a bot, 500 runs by default, and reports win rates by card, relic and encounter.

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

The last line is the source equivalent of `dotnet cantrip test content`.
