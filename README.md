# Cantrip

[![CI](https://github.com/AGomnes/Cantrip/actions/workflows/ci.yml/badge.svg)](https://github.com/AGomnes/Cantrip/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/Cantrip.Core?label=Cantrip.Core)](https://www.nuget.org/packages/Cantrip.Core)

**Write the rules of your card game as text, not code.**

Cantrip is a rules language and engine for card games, deckbuilders and roguelites built with .NET, with an addon for Godot. Every card, status, relic, enemy and ability is a few lines in a `.cantrip` file. The engine works out how they all interact, your game drives it from C# or GDScript, and your content is tested like code.

## Why

The hard part of a card game is rarely a single card. It is how the cards combine: a relic that reacts when a status wears off, a status that changes what fire damage does, a boss that changes its moves at half health. Written in C#, every card becomes a class, every combination a special case, and every tweak a recompile.

In Cantrip each effect is a few readable lines that say only what that effect does. Events, ordering, stacking, modifiers and targeting are the engine's job, so effects that were never written with each other in mind still combine correctly, and a designer can change a number, save, and see it in the running game.

## What it looks like

```
card Fireball
  cost 2
  target enemy
  tags attack, fire
  effect:
    deal 6 to target
    deal 2 to adjacent(target)
    if target.dead: draw 1
  text: "Hurl a ball of flame for {damage} damage. Kill it to draw {draw}."

status Frozen
  tags control, ice
  on owner.damaged(tag:fire):
    remove Frozen from owner
    deal 10 to owner

relic Kindling
  on status_removed(tag:ice):
    apply Burn 2 to event.target

test "Fireball kills a 6 hp enemy"
  enemy hp 6
  play Fireball on enemy
  expect enemy.dead
```

None of these three knows about the others. A Fireball on a frozen enemy deals its 6 damage; being fire, the hit shatters Frozen for 10 more; Frozen is an ice status, so its removal sets off Kindling, which burns the enemy. That chain comes from events, not from code that anticipated it. The `test` block at the end is content too, and `cantrip test` runs it.

## What you get

- **A language for game effects.** Cards, statuses, relics, enemies with move patterns and phases, abilities, resources and your own verbs. Listeners can act before, instead of or after any event, modifiers stack in predictable layers, and effects can be scheduled for next turn or undone at the end of this one.
- **An engine that runs it.** Turns, card play, draw and enemy intents, with the same result from the same seed on every machine, save and load, hot reload while the game runs, and choices your UI answers in the middle of an effect.
- **Tools for the people writing content.** Tests written in content, a linter that catches unknown names and effects that can never fire, rules text generated with live numbers ("deal ~~6~~ 9 damage"), and a trace of why everything happened.
- **Engine independence.** The core targets `netstandard2.1` and references no engine. A [Godot 4.6 addon](docs/godot.md) adds a node for GDScript, an importer so content ships in exported builds, and an editor dock with problems, tests, card text and live debugging.
- **Tested against real games.** A growing [corpus](docs/coverage.md) of effects from Slay the Spire, Monster Train, Hearthstone, Balatro, Magic, Inscryption, Dominion, Darkest Dungeon and Dota 2 records what the language expresses and what it cannot yet, and [`samples/slice`](samples/slice) is a small roguelite a bot plays thousands of times to check the balance.

## Status

**Preview: 0.1.0-preview.1.** Everything above works and is tested, and a small roguelite has been built and played with it, but no shipped game uses Cantrip yet. The API, the language and the save format may change between previews; [docs/stability.md](docs/stability.md) says how. The turn-based side is the focus: real time works but is experimental, since no real-time game has been built with it.

Feedback is the most useful thing right now, especially effects from your game that the language cannot express. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Get started

Add the library, and the `cantrip` tool for checking and testing content:

```
dotnet add package Cantrip.Core --prerelease
dotnet new tool-manifest
dotnet tool install Cantrip.Cli --prerelease
```

With your `.cantrip` files in a `content` folder, `dotnet cantrip test content` runs their tests and `dotnet cantrip lint content` checks them. The [quickstart](docs/quickstart.md) goes from an empty folder to a battle you can play in about fifteen minutes. For Godot, the addon is a zip on each [release](https://github.com/AGomnes/Cantrip/releases); [docs/godot.md](docs/godot.md) covers installing it.

Playing a battle from C# looks like this:

```csharp
var content = new ContentLibrary();
content.LoadFolder("content");
content.Diagnostics.ThrowIfErrors();

var runtime = new CardRuntime(content, new RuntimeOptions { Seed = 12345 });
Entity player = runtime.CreatePlayer(hp: 80, maxEnergy: 3);
runtime.AddDeck("Strike", "Strike", "Defend", "Fireball");
runtime.AddRelic("Kindling");
Entity worm = runtime.SpawnEnemy("Jaw Worm");

runtime.StartBattle();
PlayResult result = runtime.Play("Fireball", worm);   // Played, NotEnoughEnergy, InvalidTarget...
runtime.EndTurn();
```

[docs/csharp.md](docs/csharp.md) covers the rest: asking what can be played, the host, player choices, saving, hot reload, rules text and tracing.

## Documentation

- [Quickstart](docs/quickstart.md): from nothing to a playable battle
- [Language reference](docs/language.md): every declaration, event, verb and modifier
- [Using Cantrip from C#](docs/csharp.md)
- [The Godot addon](docs/godot.md)
- [Architecture](docs/architecture.md): how the library fits together, and where to extend it
- [Coverage](docs/coverage.md): which effects from existing games the language can express
- [Stability](docs/stability.md): what may change between previews, and how to report a problem
- [Changelog](CHANGELOG.md)

## Command line

```
cantrip validate <path>...                 parse and load content, report problems
cantrip lint <path>... [--suppress codes]  static checks
cantrip test <path>... [--filter text] [--trace]
cantrip describe <path>... [--name name]   print generated rules text
cantrip repl <path>...                     run statements against a live game
cantrip --version
```

Installed as a local tool, each is run as `dotnet cantrip ...`. Paths are files or folders; a folder loads every `.cantrip` file under it. The exit code is 0 for success, 1 for content errors or failing tests and 2 for bad usage, so the tool drops straight into CI.

## Working on Cantrip

You need the .NET 9 SDK or later:

```
dotnet build Cantrip.sln
dotnet test tests/Cantrip.Core.Tests
dotnet run --project src/Cantrip.Cli -- test samples/basic
```

| Path | Contents |
|---|---|
| `src/Cantrip.Core` | The library: parser, content loading, runtime, interpreter, linter, rules text, test runner |
| `src/Cantrip.Cli` | The `cantrip` tool |
| `src/Cantrip.Sim` | Plays whole runs of the slice with a bot and reports how they went |
| `samples/basic` | Small examples of each feature |
| `samples/corpus` | Effects from existing games |
| `samples/slice` | A small roguelite: a witch climbing a tower, fire against frost |
| `godot/Cantrip.Demo` | The Godot addon, a demo and headless tests |
| `tests` | Unit tests for the library and for the addon's engine-free layer |
| `tools` | Packaging the addon, and checking the quickstart against freshly built packages |

[CONTRIBUTING.md](CONTRIBUTING.md) has what a pull request needs, and how releases are made.

## License

MIT. Copyright (c) 2026 Alexander Gomnæs. See [LICENSE](LICENSE).
