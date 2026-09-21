# Cantrip

[![CI](https://github.com/AGomnes/Cantrip/actions/workflows/ci.yml/badge.svg)](https://github.com/AGomnes/Cantrip/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/Cantrip.Core?label=Cantrip.Core)](https://www.nuget.org/packages/Cantrip.Core)

**Write your cards, statuses, relics and enemies as short scripts instead of C# classes.**

Cantrip is a rules language and rules engine for the battles in single-player, turn-based card games: deckbuilders and roguelites in the style of Slay the Spire, where one player fights AI enemies that show their next move. You write cards, statuses, relics, enemies and abilities as short `.cantrip` files, and the rules engine works out how they interact. Your game drives it from C#, or from GDScript through a Godot addon, and keeps the rendering, input, map and rewards between battles in its own code. Content carries its own tests, which the `cantrip` tool runs.

## Why

The hard part of a card game is rarely a single card. It is how the cards combine: a relic that reacts when a status wears off, a status that changes what fire damage does, a boss that changes its moves at half health. Hand-written in C#, cards tend to become classes, combinations tend to become special cases, and a balance tweak usually means a rebuild.

In Cantrip each effect is a few readable lines that say only what that effect does. Events, ordering, stacking, modifiers and targeting are the rules engine's job, so effects that were never written with each other in mind still combine, by rules the [language reference](docs/language.md) sets out. Those rules have sharp edges, and [docs/coverage.md](docs/coverage.md) records each one found so far. A designer can change a number, save, and see it in the running game once it reloads the content.

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

status Burn
  stacking intensity
  on turn_end:
    deal stacks to owner, ignore block
    stacks -1

relic Kindling
  on status_removed(tag:ice):
    apply Burn 2 to event.target

test "Fireball kills a 6 hp enemy"
  enemy hp 6
  play Fireball on enemy
  expect enemy.dead
```

None of these knows about the others. A Fireball on a frozen enemy deals its 6 damage, and the damage carries the card's `fire` tag, so the hit shatters Frozen for 10 more. Frozen is an `ice` status, so its removal sets off Kindling, which burns the enemy for 2 at the end of each of its turns, 1 less each time. That chain comes from events, not from code that anticipated it. A status's `on owner.` listener hears only what happens to its host, while a relic hears the whole battle, so Kindling would fire for ice coming off the player too.

`{damage}` in the card's text is the first amount it deals, shown with whatever modifiers apply at the moment; leave `text:` out and Cantrip writes the rules text itself. The `test` block is content too, and `cantrip test` runs it.

## What you get

- **A language for game effects.** Cards, statuses, relics, enemies with move patterns and phases, abilities, resources, and your own verbs beside built-in ones such as `deal` and `apply`. Listeners (`on ...` blocks) can act before, instead of or after any event. Modifiers combine in layers, by default adding first, then multiplying, then clamping and overriding. Effects can be scheduled for next turn or undone at the end of this one.
- **A rules engine that runs it.** Turns, card play, draw and enemy intents. It is deterministic by design, with fixed-point maths and a seeded random generator, so the same seed and inputs replay the same game; CI runs the tests on Linux and Windows x64. Battle state can be saved between actions and loaded again against the same content. Content can be reloaded into a running game. And an effect can stop to ask the player something, such as which card to discard, and carry on once your UI answers.
- **Tools for the people writing content.** Tests written in content, a linter for unknown names, events nothing raises and similar mistakes, rules text that shows live numbers ("deal ~~6~~ 9 damage"), and a trace of why everything happened.
- **No game engine required.** The core targets `netstandard2.1` and references no game engine. A [Godot addon](docs/godot.md) for the .NET edition of Godot 4.6 adds a node GDScript can drive, an importer so content ships in exported builds, and an editor dock for problems, tests and card text.
- **Checked against effects from real games.** [docs/coverage.md](docs/coverage.md) re-creates effects from Slay the Spire, Monster Train, Hearthstone, Balatro, Magic, Inscryption, Dominion, Darkest Dungeon and Dota 2 under its own names, and records which the language writes directly, which need a workaround and which it cannot express yet, such as Balatro's poker hands. Cantrip is not affiliated with these games or their publishers. [`samples/slice`](samples/slice) is a small five-floor roguelite that a bot plays, 500 runs by default, to report win rates by card, relic and encounter.

## What it does not do

- **Two players, or an opponent with its own hand and deck.** There is one player, and enemies act from move patterns. There is no networking.
- **The run.** The map, rewards, shops and events between battles are your game's code.
- **Space.** There is one row of actors per side. Positions and area queries such as `within` must come from your game.
- **Rendering, input and animation.** Your game presents what happened from the events the rules engine reports.
- Some mechanics are not expressible yet, among them a Magic-style priority window, copying or silencing another entity's effects, and grouping played cards into poker hands. [docs/coverage.md](docs/coverage.md) keeps the list.

## Status

**Preview: 0.1.0-preview.1.** The core has unit tests and content tests, and a small roguelite has been built with it and played headlessly by a bot, but no shipped game uses Cantrip yet. The API, the language and the save format may change between previews; [docs/stability.md](docs/stability.md) says how. Known gaps are in the [changelog](CHANGELOG.md): real time (a tick clock and abilities with cooldowns) is experimental, and the Godot addon has so far been installed only by its author and by an automated test.

Cantrip is written and maintained by one person. Feedback is the most useful thing right now, especially effects from your game that the language cannot express. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Get started

You need the .NET 9 SDK or later. The library targets `netstandard2.1`, so it runs on .NET Core 3.0, .NET 5 and later, but not .NET Framework. It has been used from .NET 9 console apps and Godot 4.6 .NET; Unity and other game engines are untried.

In your game's project folder, add the library, and the `cantrip` tool for checking and testing content:

```
dotnet add package Cantrip.Core --prerelease
dotnet new tool-manifest
dotnet tool install Cantrip.Cli --prerelease
```

The tool manifest lets the folder run the tool as `dotnet cantrip`. With your `.cantrip` files in a `content` folder, `dotnet cantrip test content` runs their tests and `dotnet cantrip lint content` checks them. The [quickstart](docs/quickstart.md) goes from an empty folder to a battle you can play in the terminal in about fifteen minutes. For Godot, the addon is a zip on each [release](https://github.com/AGomnes/Cantrip/releases); [docs/godot.md](docs/godot.md) covers installing it.

Playing a battle from C#, with Strike, Defend and a Jaw Worm defined in your content alongside the example above:

```csharp
using Cantrip;
using Cantrip.Content;
using Cantrip.Runtime;

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

Your game learns what happened, to animate it, from events: damage, cards moving, statuses applied. [docs/csharp.md](docs/csharp.md) covers that and the rest: asking what can be played, player choices, saving, hot reload, rules text and tracing.

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

Installed as a local tool, the commands run as `dotnet cantrip ...`:

```
dotnet cantrip validate <path>... [--suppress codes]  load content and report its errors, including unknown verbs
dotnet cantrip lint <path>... [--suppress codes]  report likely mistakes too (unknown names, events nothing raises)
dotnet cantrip test <path>... [--filter text] [--trace]
dotnet cantrip describe <path>... [--name name]   print generated rules text
dotnet cantrip repl <path>...                     start a game from the content and try statements in it
dotnet cantrip --version
```

Paths are files or folders; a folder loads every `.cantrip` file under it. The exit code is 0 for success, 1 for content errors or failing tests and 2 for bad usage. `lint` fails only on errors: its warnings are printed but still exit 0, so in CI cover an effect with a `test` if it must not silently stop working.

## Working on Cantrip

You need the .NET 9 SDK or later:

```
dotnet build Cantrip.sln
dotnet test tests/Cantrip.Core.Tests
dotnet run --project src/Cantrip.Cli -- test samples/basic
```

| Path | Contents |
|---|---|
| `src/Cantrip.Core` | The library: parser, content loading, rules engine, interpreter, linter, rules text, test runner |
| `src/Cantrip.Cli` | The `cantrip` tool |
| `src/Cantrip.Sim` | Plays whole runs of the sample roguelite with a bot and reports how they went |
| `samples/basic` | Small examples of each feature, including Fireball, Frozen and Kindling above, and the Strike, Defend and Jaw Worm the C# example uses |
| `samples/corpus` | Effects re-created from existing games |
| `samples/slice` | The sample roguelite: a witch climbing a tower, fire against frost |
| `godot/Cantrip.Demo` | The Godot addon, a demo and headless tests |
| `tests` | Unit tests for the library and for the addon's engine-free layer |
| `tools` | Packaging the addon, and checking the quickstart against freshly built packages |

[CONTRIBUTING.md](CONTRIBUTING.md) has what a pull request needs, and how releases are made.

## License

MIT. Copyright (c) 2026 Alexander Gomnæs. See [LICENSE](LICENSE).
