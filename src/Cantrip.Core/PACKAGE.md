# Cantrip.Core

Write cards, statuses, relics, enemies and abilities as short text files instead of code, and run them turn-based from any .NET game. The rules engine has no engine dependencies; a [Godot 4.6 addon](https://github.com/AGomnes/Cantrip/blob/main/docs/godot.md) is available separately.

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

```csharp
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

This is a **preview**. The API and the save format may change between previews; see [stability](https://github.com/AGomnes/Cantrip/blob/main/docs/stability.md).

- [Quickstart](https://github.com/AGomnes/Cantrip/blob/main/docs/quickstart.md): from nothing to a playable battle in about fifteen minutes
- [Language reference](https://github.com/AGomnes/Cantrip/blob/main/docs/language.md)
- The companion command-line tool, for testing and linting content: run `dotnet new tool-manifest`, then `dotnet tool install Cantrip.Cli --prerelease`, and use it as `dotnet cantrip`
- [Source, issues and changelog](https://github.com/AGomnes/Cantrip)
