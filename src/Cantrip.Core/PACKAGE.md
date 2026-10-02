# Cantrip.Core

Write cards, statuses, relics, enemies, heroes and abilities as short text files instead of code, and run them, in turns or in real time, from a **Godot 4.6** game (the .NET edition, in GDScript or C#) through the [Cantrip addon](https://github.com/AGomnes/Cantrip/blob/main/docs/godot.md), or from **any other .NET game** on .NET 5 or later, `net8.0` included. This package is the rules engine underneath both: it references no game engine, has no third-party dependencies, and is marked trimmable, so a trimmed or Native AOT build keeps only the parts of it your game uses. CI publishes a whole game both ways and plays it.

**In Godot**, put the addon in the project first, then add this package at the version in the addon's `plugin.cfg` rather than the newest: `dotnet add package Cantrip.Core --version <that version>`. The addon is C# source your game compiles against this assembly, so an addon and an engine from different releases give missing-method errors at run time. [The Godot addon](https://github.com/AGomnes/Cantrip/blob/main/docs/godot.md) walks through it, and your game calls the addon's node from GDScript rather than the API below.

```
status Hex
  stacking intensity
  on turn_end:
    deal stacks to owner, ignore block
    stacks -1

card Curse
  cost 1
  target enemy
  effect:
    apply Hex 3 to target

enemy Ghoul
  hp 30
  move Claw:
    deal 7 to player
```

With that in `content/game.cantrip`, a .NET game plays it:

```csharp
using Cantrip;
using Cantrip.Content;
using Cantrip.Runtime;

var content = new ContentLibrary();
content.LoadFolder("content");
content.Diagnostics.ThrowIfErrors();

var runtime = new CardRuntime(content, new RuntimeOptions { Seed = 42 });
Entity player = runtime.CreatePlayer(hp: 40, maxEnergy: 3);
runtime.AddDeck("Curse", "Curse", "Curse");
Entity ghoul = runtime.SpawnEnemy("Ghoul");
runtime.StartBattle();
runtime.Play(runtime.State.ZoneOf(player, Zones.Hand)[0], ghoul);
runtime.EndTurn();
```

A battle is fought by a party on a board. `CreatePlayer` makes a party of one and that member is the leader; the rest are `hero` declarations, added by `AddHero` or by content's own `create`. A member may hold its own energy, hand and piles or play from the leader's, and `fallen` and `revive` cover the ones who go down. The board is a rectangle of lanes and ranks that content declares: `range` on a card, an ability or an enemy's move is its reach, `adjacent`, `within`, `lane`, `rank` and `distance` ask about it, and moving is writing to `rank` or `lane`. Content that declares neither a hero nor a board plays exactly as it did before either existed. A game that says `clock ticks` takes no turns at all: it creates the runtime with a `TickClock`, calls `Tick` from its own fixed timestep, and abilities come back after the seconds their `cooldown` names. Two whole games are in the repository, one of each kind: [The Drowned Chapel](https://github.com/AGomnes/Cantrip/tree/main/reference) and [Emberline](https://github.com/AGomnes/Cantrip/tree/main/realtime). Each has what building it found written down beside it.

Cantrip 1.0 makes this surface a promise: your code, your content and your saves keep working across every 1.x release. What may still change (what a given seed plays out as, the wording of messages, the severity of a diagnostic) is set out in [stability](https://github.com/AGomnes/Cantrip/blob/main/docs/stability.md). The linked docs describe the `main` branch, which can be ahead of this version; each release's own docs are in [its tag](https://github.com/AGomnes/Cantrip/tags).

- [The Godot addon](https://github.com/AGomnes/Cantrip/blob/main/docs/godot.md): installing it, and a first battle from GDScript
- [Quickstart](https://github.com/AGomnes/Cantrip/blob/main/docs/quickstart.md): from nothing to a playable battle in a terminal, for a game that is not in Godot
- [Using Cantrip from C#](https://github.com/AGomnes/Cantrip/blob/main/docs/csharp.md): the calls a game makes, in the order it makes them (presenting events in a frame loop, player choices, the party, real time, saving and hot reload)
- [Language reference](https://github.com/AGomnes/Cantrip/blob/main/docs/language.md)
- [API reference](https://github.com/AGomnes/Cantrip/blob/main/docs/api/README.md): every public type and member of this package, generated from its own documentation comments
- [Troubleshooting](https://github.com/AGomnes/Cantrip/blob/main/docs/troubleshooting.md): what each refusal means, how to read a `RuntimeError` and a trace, every save refusal, and what a trimmed or AOT-published game has to do differently
- The companion command-line tool, for testing and linting content: run `dotnet new tool-manifest`, then `dotnet tool install Cantrip.Cli`, and use it as `dotnet cantrip`
- [Source, issues and changelog](https://github.com/AGomnes/Cantrip)
