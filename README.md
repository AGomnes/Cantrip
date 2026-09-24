# Cantrip

[![CI](https://github.com/AGomnes/Cantrip/actions/workflows/ci.yml/badge.svg)](https://github.com/AGomnes/Cantrip/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/Cantrip.Core?label=Cantrip.Core)](https://www.nuget.org/packages/Cantrip.Core)

**Write your cards, abilities, statuses, relics and enemies as short scripts instead of code.**

[Godot addon](docs/godot.md) · [Quickstart](docs/quickstart.md) · [Writing content](docs/writing-content.md) · [Language reference](docs/language.md) · [C# guide](docs/csharp.md) · [Stability](docs/stability.md) · [Changelog](CHANGELOG.md)

Cantrip is a rules language and rules engine for the combat in single-player, turn-based games, where one side fights AI enemies that show their next move. It was written for deckbuilders and roguelites in the style of Slay the Spire, and cards are optional: a turn-based roguelike whose actors use abilities on cooldowns is the same engine without them, as [samples/abilities](samples/abilities) shows. You write cards, abilities, statuses, relics and enemies as short `.cantrip` files, with their own tests, and the rules engine works out how they interact. Your game drives the battles and keeps the rendering, input, map and rewards between battles in its own code.

You use it from **Godot 4.6, on Godot's .NET edition**, writing GDScript or C#, through the [Cantrip addon](docs/godot.md) — or from **any other .NET project** on .NET 5 or later, because the rules engine underneath the addon references no game engine at all.

**Where to start**

- **A Godot game, in GDScript or C#:** [docs/godot.md](docs/godot.md) installs the addon and plays a [first battle](docs/godot.md#your-first-battle). You need Godot's .NET edition, the .NET SDK, and a C# solution in the project even if the game is all GDScript — but no C# of your own to write.
- **Any other .NET game:** the [quickstart](docs/quickstart.md) goes from an empty folder to a battle you can play in a terminal, in about fifteen minutes.
- **Writing cards rather than code:** [docs/writing-content.md](docs/writing-content.md) walks through a first card, status, relic and enemy with their tests, then gives recipes for common effects. It needs no C#, and in Godot no command line either: the addon's dock checks and tests the files in the editor.

## Why

The hard part is rarely a single card or ability. It is how they combine: a relic that reacts when a status wears off, a status that changes what fire damage does, a boss that changes its moves at half health. Hand-written in C#, cards tend to become classes, combinations tend to become special cases, and a balance tweak usually means a rebuild.

What Cantrip models is the combat action. A card is an action whose availability is a deck, an ability is one on a cooldown, and an enemy's move is one its pattern picks; the effect body is the same in all three. That is why a game with no cards is the same engine with the piles left out.

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

`{damage}` in the card's text is the first amount it deals, shown with whatever modifiers apply at the moment; leave `text:` out and Cantrip writes the rules text itself. The `test` block is content too, and `dotnet cantrip test` runs it.

## What you get

- **A language for game effects.** Cards, statuses, relics, enemies with move patterns and phases, abilities, resources, and your own verbs beside built-in ones such as `deal` and `apply`. Listeners (`on ...` blocks) can act before, instead of or after any event. Modifiers combine in layers, by default adding first, then multiplying, then clamping and overriding. Effects can be scheduled for next turn or undone at the end of this one.
- **A rules engine that runs it.** Turns, card play, draw and enemy intents. It is deterministic by design, with fixed-point maths and a seeded random generator, so the same seed and inputs replay the same game within one version of Cantrip; CI checks exact results on Linux and Windows x64. Battle state can be saved between actions and loaded again against the same content. Content can be reloaded into a running game. And an effect can stop to ask the player something, such as which card to discard, and carry on once your UI answers.
- **Tools for the people writing content.** Tests written in content, a linter for unknown names, events nothing raises and similar mistakes, rules text that shows live numbers ("deal ~~6~~ 9 damage"), and a trace of why everything happened.
- **A Godot addon, over an engine-free core.** The [addon](docs/godot.md), for the .NET edition of Godot 4.6, adds a node GDScript can drive, an importer so `.cantrip` files reach exported builds, and an editor dock for problems, tests and card text. Underneath it the core targets `netstandard2.1` and references no game engine, so the same content runs in a console app, a plain .NET engine or a test with nothing of Godot's in the way.
- **Checked against effects from real games.** [docs/coverage.md](docs/coverage.md) re-creates effects from Slay the Spire, Monster Train, Hearthstone, Balatro, Magic, Inscryption, Dominion, Darkest Dungeon and Dota 2 under its own names, and records which the language writes directly, which need a workaround and which it cannot express yet, such as Balatro's poker hands. Cantrip is not affiliated with these games or their publishers. [`samples/slice`](samples/slice) is a small five-floor roguelite that a bot plays, to find content that throws, stalls or can never be played. [samples/README.md](samples/README.md) lists it with the other samples and says how to test each one.

## What it does not do

- **A second player.** There is one player; enemies act from move patterns, and nothing can respond to a card while it is being played, so a counterspell or an interrupt has nowhere to happen. There is no networking. An enemy *can* hold cards of its own and play them, since a card belongs to whoever controls it, but your game decides what it plays.
- **The run.** The map, rewards, shops and events between battles are your game's code.
- **Space.** There is one row of actors per side. Positions and area queries such as `within` must come from your game.
- **Rendering, input and animation.** Your game presents what happened from the events the rules engine reports.
- Some mechanics are not expressible yet, among them a Magic-style priority window, copying or silencing another entity's effects, and grouping played cards into poker hands. [docs/coverage.md](docs/coverage.md) keeps the list.

## Status

**Preview (0.x).** The current version is on the NuGet badge above and in the [changelog](CHANGELOG.md). The core has unit tests and content tests, and a small roguelite has been built with it and played headlessly by a bot, but no shipped game uses Cantrip yet. The API, the language and the save format may change between previews; [docs/stability.md](docs/stability.md) says how, and lists the tested platforms and the known limitations. Among them, real time (a tick clock, and cooldowns counted in seconds rather than turns) is experimental, and the Godot addon has so far been installed only by its author and by an automated test.

These docs describe the `main` branch, which can be ahead of the latest release. The changelog's [Unreleased](CHANGELOG.md#unreleased) section lists what that release lacks, and each release's own docs are in [its tag](https://github.com/AGomnes/Cantrip/tags).

Cantrip is written and maintained by one person. Feedback is the most useful thing right now, especially effects from your game that the language cannot express. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Get started

### In a Godot game

You need the **.NET edition** of Godot 4.6 and the .NET SDK 8 or later. A project written entirely in GDScript also needs a C# solution in it, which Project → Tools → C# → Create C# solution writes: the addon is C# source, because Godot finds scripts by path inside the project's own assembly, so only the library underneath can be a package. You write none of that C# yourself.

Put `addons/cantrip` in the project, from any of three places: the editor's AssetLib tab, once Cantrip is listed there; the zip on any [release](https://github.com/AGomnes/Cantrip/releases), whose root is `addons/cantrip`; or [AGomnes/cantrip-godot](https://github.com/AGomnes/cantrip-godot), the mirror each release copies the addon to in the layout the Asset Library installs from. Then add the rules engine, at the version in the addon's `plugin.cfg` rather than with `--prerelease`, which may fetch a newer preview than the addon you have:

```
dotnet add package Cantrip.Core --version <the version in plugin.cfg>
```

Build, enable the plugin in Project Settings → Plugins, and put your `.cantrip` files under `res://content`; [Installing](docs/godot.md#installing) is the same thing step by step, with what each mistake looks like. A `CantripRuntime` node then runs battles, and hands everything to your scripts as ids and dictionaries, with [samples/basic/content.cantrip](samples/basic/content.cantrip) in `res://content` for its Strike, Defend, Fireball, Kindling and Jaw Worm:

```gdscript
extends Node

@onready var rules: CantripRuntime = $CantripRuntime  # loads res://content as it enters the tree
var worm := 0

func _ready() -> void:
	rules.EffectEvent.connect(_on_effect_event)   # damage, cards moving, statuses applied
	rules.CreatePlayer("Player", 80, 3)           # name, hp, energy each turn
	rules.AddDeck(["Strike", "Strike", "Defend", "Fireball"])
	rules.AddRelic("Kindling")
	worm = rules.SpawnEnemy("Jaw Worm", 0)        # 0: the hp its content gives it
	rules.StartBattle(true, true)                 # shuffle the draw pile, draw the opening hand

# A card button in your scene calls this; an End turn button calls rules.EndTurn().
func _on_card_pressed(card: int) -> void:
	var target: int = worm if rules.GetTargetMode(card) == "enemy" else 0
	print(rules.Play(card, target))               # played, not_enough_energy, invalid_target...

func _on_effect_event(effect_event: Dictionary) -> void:
	print(effect_event["name"])                   # damaged, gained_block, status_applied...
```

Every argument is passed, because a C# default argument is not a default in GDScript, and members keep their C# names. [docs/godot.md](docs/godot.md) is the full guide: installing the addon step by step, a [first battle](docs/godot.md#your-first-battle) that plays itself, every method and dictionary the node hands you, pacing events, choices the player makes, saving, hot reload, and the editor dock. Exporting a .NET game has limits of Godot's own — not to the web, and to Android and iOS only experimentally — and [Platforms](docs/stability.md#platforms) says which exports have been tried.

### In any other .NET game

The library targets `netstandard2.1`, so your game can target .NET 5 or later, such as `net8.0`, but not .NET Framework. MonoGame, FNA and other plain .NET engines call it just as the quickstart's console app does; none of them has been tried with it yet, and neither has Unity. Only the `cantrip` tool, which checks and tests content, needs the .NET 9 SDK or later.

In your game's project folder, add the library and the tool:

```
dotnet add package Cantrip.Core --prerelease
dotnet new tool-manifest
dotnet tool install Cantrip.Cli --prerelease
```

The tool manifest lets the folder run the tool as `dotnet cantrip`. With your `.cantrip` files in a `content` folder, `dotnet cantrip test content` runs their tests and `dotnet cantrip lint content` checks them. The [quickstart](docs/quickstart.md) takes the same steps to a battle you can play in the terminal.

Playing a battle from C#, with [samples/basic/content.cantrip](samples/basic/content.cantrip) in the `content` folder for its Strike, Defend, Fireball, Kindling and Jaw Worm:

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

Your game learns what happened, to animate it, from events: damage, cards moving, statuses applied. [docs/csharp.md](docs/csharp.md) covers that and the rest: pacing events in a frame loop, player choices, saving, shipping content, hot reload, rules text and tracing.

## Documentation

- [The Godot addon](docs/godot.md): installing it, a first battle from GDScript, and the whole node
- [Quickstart](docs/quickstart.md): from nothing to a playable battle in a terminal, in C#
- [Writing content](docs/writing-content.md): a tutorial and recipes for content authors
- [Language reference](docs/language.md): every declaration, event, verb and modifier
- [Using Cantrip from C#](docs/csharp.md): for a game that calls the library itself
- [Architecture](docs/architecture.md): how the library fits together, and where to extend it
- [Coverage](docs/coverage.md): which effects from existing games the language can express
- [Simulating](docs/simulating.md): playing a scenario many times with a bot, and what that measures
- [Slice friction](docs/slice-friction.md): what building a small roguelite on Cantrip needed
- [Stability](docs/stability.md): what may change, platforms, performance and known limitations
- [Changelog](CHANGELOG.md)

## Command line

Installed as a local tool, the commands run as `dotnet cantrip ...`. In Godot the tool is optional: the addon's dock does `lint`, `test` and `describe` in the editor, as its Problems, Tests and Preview tabs. What the tool adds there is `sim`, the REPL, and one line a build server can run.

| Command | What it does |
|---|---|
| `dotnet cantrip validate <path>... [--suppress codes]` | Reports errors, including unknown verbs |
| `dotnet cantrip lint <path>... [--suppress codes] [--warnings-as-errors]` | Also reports likely mistakes, such as events nothing raises |
| `dotnet cantrip test <path>... [--filter text] [--trace]` | Runs the `test` blocks; `--trace` adds the causality trace, with any `log` output, to each failure |
| `dotnet cantrip sim <path>... [--runs N] [--seed S] [--bot name] [--turn-limit N] [--watch SEED]` | Plays the `scenario` blocks many times with two bots, and reports what the content allowed and where the hp went |
| `dotnet cantrip describe <path>... [--name name]` | Prints generated rules text |
| `dotnet cantrip repl <path>...` | Runs each statement you type as the player, against a 100 hp Dummy; `:state`, `:trace`, `:quit` |
| `dotnet cantrip --version` | Prints the version and the commit it was built from |

Paths are files or folders; a folder loads every `.cantrip` file under it. `validate` and `lint` report each problem with its file, line and column and a code such as CT302; [Diagnostics](docs/language.md#diagnostics) lists every code with its typical fix. The exit code is 0 for success, 1 for content errors or failing tests and 2 for bad usage. `lint` fails only on errors, unless it is given `--warnings-as-errors`, which makes a warning fail it too, as this repository's CI does for its samples. The linter does not run the content, so cover an effect with a `test` if it must not silently stop working. [Trying lines in the REPL](docs/writing-content.md#6-trying-lines-in-the-repl) shows a REPL session and what it cannot do.

## Working on Cantrip

You need the .NET 9 SDK or later:

```
dotnet build Cantrip.sln
dotnet test tests/Cantrip.Core.Tests
dotnet run --project src/Cantrip.Cli -- test samples/basic
dotnet run --project src/Cantrip.Cli -- sim samples/slice
```

The last line has a bot play the sample roguelite; [Simulating](docs/simulating.md) says what it measures and what it refuses to, and [samples/README.md](samples/README.md) says what each sample shows.

The addon lives in `godot/Cantrip.Demo/addons/cantrip`, with a demo project around it that plays a battle from GDScript. Working on it needs the .NET edition of Godot 4.6 as well; [Working on the addon](docs/godot.md#working-on-the-addon) has the commands to build, run and package it, and the headless scenes CI runs. [CONTRIBUTING.md](CONTRIBUTING.md) has a map of the repository, what a pull request needs, and how releases are made.

## License

MIT, for the library, the tool and the Godot addon. Cantrip.Core has no third-party runtime dependencies. Copyright (c) 2026 Alexander Gomnæs. See [LICENSE](LICENSE).
