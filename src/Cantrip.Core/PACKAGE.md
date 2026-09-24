# Cantrip.Core

Write cards, statuses, relics, enemies and abilities as short text files instead of code, and run them turn-based from a **Godot 4.6** game — the .NET edition, in GDScript or C# — through the [Cantrip addon](https://github.com/AGomnes/Cantrip/blob/main/docs/godot.md), or from **any other .NET game** on .NET 5 or later, `net8.0` included. This package is the rules engine underneath both: it references no game engine and has no third-party dependencies.

**In Godot**, put the addon in the project first, then add this package at the version in the addon's `plugin.cfg` — `dotnet add package Cantrip.Core --version <that version>` — rather than with `--prerelease`, which may fetch a newer preview than the addon you have. [The Godot addon](https://github.com/AGomnes/Cantrip/blob/main/docs/godot.md) walks through it, and your game calls the addon's node from GDScript rather than the API below.

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

This is a **preview**. The API and the save format may change between previews; see [stability](https://github.com/AGomnes/Cantrip/blob/main/docs/stability.md). The linked docs describe the `main` branch, which can be ahead of this version; each release's own docs are in [its tag](https://github.com/AGomnes/Cantrip/tags).

- [The Godot addon](https://github.com/AGomnes/Cantrip/blob/main/docs/godot.md): installing it, and a first battle from GDScript
- [Quickstart](https://github.com/AGomnes/Cantrip/blob/main/docs/quickstart.md): from nothing to a playable battle in a terminal, for a game that is not in Godot
- [Language reference](https://github.com/AGomnes/Cantrip/blob/main/docs/language.md)
- The companion command-line tool, for testing and linting content: run `dotnet new tool-manifest`, then `dotnet tool install Cantrip.Cli --prerelease`, and use it as `dotnet cantrip`
- [Source, issues and changelog](https://github.com/AGomnes/Cantrip)
