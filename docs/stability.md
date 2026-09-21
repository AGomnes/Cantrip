# Stability

Cantrip is in preview (0.x). It works, it is tested, and it has been used to build and play a small roguelite, but nobody has shipped a game with it yet. Expect it to change as that happens. This page says what may change, how it is checked and on which platforms, how fast it is, and what is known not to work yet.

## What may change between previews

- **The C# API.** Names, signatures and types may change. The [changelog](../CHANGELOG.md) lists every change that could break a caller, under **Breaking changes**.
- **The Godot addon's surface.** The node's methods, signals and exports, the keys of the dictionaries it returns, and words such as `not_enough_energy` may change in the same way, and are listed in the same place. The build keeps no record of them as it does of the C# API, and GDScript notices a renamed key only when that line runs, so read the changelog before updating the addon.
- **The language.** Keywords, verbs and their defaults may change, though the aim is to keep content that works today working, and to make any change that breaks it an error at load time rather than a silent change in behaviour. That is why `event` and `encounter`, which have no meaning yet, are an error (CT0113) instead of being quietly ignored.
- **The save format.** A save made with one preview may not load in the next. `GameSnapshot.FormatVersion` changes when it cannot, and loading an older one is refused with a clear error rather than misread. The changelog says so under **Save format**.
- **Same-seed results.** A release may change what the same content, seed and inputs produce, for example by fixing a rule. The changelog says so under **Same-seed results**.
- **Diagnostic codes.** Codes may be added; an existing code keeps its meaning.

## What will not change without a major version, once 1.0 is out

The public API, which includes the Godot node's methods, signals and dictionary keys; the language; and the ability to load a save made with any earlier 1.x release.

## How it is checked

- On every push to `main` and every pull request, on Linux and Windows x64: the unit tests, the content tests in `samples/`, and 100 runs of the sample roguelite played by a bot, which fail on any run that throws.
- Among the unit tests, 100 seeded random battles are each played twice and compared step by step, and a saved and restored game is played beside the original, comparing state hashes after every step.
- Every change to Cantrip.Core's public C# API must be recorded in `src/Cantrip.Core/PublicAPI.Unshipped.txt`, or the build warns.
- On the same pushes and before every release, the [quickstart](quickstart.md) is followed word for word against freshly packed packages, and the Godot addon is installed from its zip into a blank Godot project and plays a battle from GDScript.
- A release publishes nothing until all of that has passed at the tagged commit.

## Platforms

| Platform | Status |
|---|---|
| Linux x64 and Windows x64, .NET 9 | Tested in CI |
| Godot 4.6.1 .NET on Linux x64 | Tested in CI: headless tests, a GDScript smoke test, the demo playing itself, the editor dock's self-test, and installing the addon into a blank project, which targets `net8.0` |
| Godot 4.6.2 .NET on Windows x64 | Checked by hand, including what no automated test can drive: the dock in use and live debugging |
| Godot export to Linux x64 | Tested on demand (`.github/workflows/export.yml`): the exported demo plays itself |
| macOS, and ARM on any system | Untested |
| Godot exports to Windows, macOS, the web, Android and iOS | Untested. Godot's own limits on where a .NET game can be exported apply as well |
| MonoGame, FNA and other plain .NET engines | Untried. They call the library as the [quickstart](quickstart.md)'s console app does |
| NativeAOT, trimming, Unity and IL2CPP | Untested |
| .NET Framework | Not supported: the core targets `netstandard2.1` |

## Performance

On a Windows laptop, the simulator plays its default 500 runs of the sample roguelite in about 12 seconds, or 23 ms per run. From the repository root:

```
dotnet run --project src/Cantrip.Sim -c Release
```

The first line of its report gives the time. Each run is up to five floors of battles, and before each card it plays, the bot tries every legal play, ends the turn to score the result, and restores a snapshot of the game. A build instrumented to count them shows that the 500 runs make about 205,000 plays, 214,000 turn ends and 206,000 restores, most of them trial moves that are rolled back. So a play or a turn end, with its share of the snapshots, averages about 30 microseconds. Nothing yet measures memory, allocations or how long content takes to load.

## Determinism

Within one version of Cantrip, the same content, seed and inputs produce the same game on every machine: fixed-point maths, a seeded generator and explicit orderings, with no floating point or hash-order dependence in the rules. CI runs the same tests and simulated games on Linux and Windows x64, and the content tests assert exact numbers on both. Nothing yet compares the two platforms' state hashes directly, and macOS and ARM are untested. A difference between machines is a bug, and the most important kind to report.

The promise does not cross versions. A new release may play the same seed differently, and says so in the changelog, so store the Cantrip version with a replay, a daily seed or a bug report.

It also holds only if your own code inside the rules is deterministic. Verbs your game registers in C# (`RegisterVerb`), and the names and functions it supplies (`IEffectHost.TryResolveName` and `TryCall`, or the Godot node's `RegisterName` and `RegisterFunction`), run in the middle of effects. They must not:

- use floating-point arithmetic, which may round differently on another machine;
- use `System.Random`, or GDScript's `randi()` and `randf()`;
- read the clock, files or anything else outside the game;
- depend on the order of a `Dictionary` or `HashSet`;
- keep state in fields of their own. Keep it in stats: restoring a save, or rolling an action back while the player makes a choice, undoes only what is in the game state, and a rolled-back action runs again once the choice is answered.

A verb that needs a random number draws it from the game's own generator, `call.State.Rng`, which is saved and restored with the game. A name or function must only answer, and never change anything, not even by drawing from that generator: rules text with live numbers asks it too, as often as the game redraws that text.

## Numbers

Every number in the rules is fixed-point, with six decimal places, stored in 64 bits. Arithmetic is correct while values stay within ±1 million, far beyond ordinary hp, damage and costs. Past that nothing checks it: a sum or product beyond about ±9.2 trillion wraps round to a wrong value (`4000000 * 4000000` comes out as a large negative number), and dividing by a value above about 9.2 million can give a wrong answer. Neither raises an error or a warning, so a game whose numbers can grow that large, such as a score that multiplies, must keep them in range itself. From C#, `Entity.GetInt` rounds to an `int` and so stops at about ±2.1 billion; `Entity.Get` returns the full value.

## Saves after a content update

A save holds each entity's stats and the names of the definitions behind them, not the rules themselves. So a save made before a content patch:

- **needs every definition it names.** If one has been renamed or removed, `CardRuntime.Restore` throws part-way through replacing the current game.
- **keeps its saved stats.** A card whose cost the patch changed keeps its old cost in the restored game, while its effect is the new one.

`ContentLibrary.Fingerprint` changes when a definition, verb or resource is added, renamed or removed, but not when numbers or effects change: store it with each save and compare it before restoring. The Godot node does this itself and refuses any save whose fingerprint differs, so there a patch that only adds a card also turns older saves away. To keep saves working across patches, do not rename or remove a shipped definition. [Save and load](csharp.md#save-and-load) in the C# guide has the details and a safe way to restore.

## Known limitations

This list is kept current with each release.

- **Real time is experimental.** The tick clock and abilities work and are tested, but no real-time game has been built with them, and spatial queries such as `within` need your game to supply space.
- **A run of battles is your game's code.** The language has no run structure yet: the map, encounters and rewards live in the game, and `event` and `encounter` declarations are an error (CT0113). One runtime can play several battles in a row, as [Winning, losing and several battles](csharp.md#winning-losing-and-several-battles) in the C# guide describes.
- **The Godot addon has had little use.** It has been installed only by its author and by an automated test. Its live debugging, the round trip between a running game and the editor, and the dock's interactive use are checked by hand.
- **A mistyped block line is silent.** Inside a declaration, a line ending in `:` that is not a block Cantrip knows, such as `when card_played:` for `on card_played:`, is read as a label for readers and does nothing. Neither `validate` nor `lint` reports it, so cover anything that must happen with a `test`.
- **An error in content leaves its action half done.** A runtime error while an action resolves, including going past the step limit, reaches your game as a `RuntimeError` exception. What the action changed before the error stays changed, and the work it had queued is dropped. To recover, restore a snapshot taken before the action, as [When content fails at runtime](csharp.md#when-content-fails-at-runtime) shows.
- **Numbers past ±1 million are unchecked**, as described under [Numbers](#numbers).
- **Content tests cover one battle.** A `test` cannot start a second battle, so it cannot show that something resets between battles, and it cannot check that a play was refused.
- **Some mechanics cannot be expressed yet**, among them a Magic-style priority window and grouping played cards into poker hands. [coverage.md](coverage.md) keeps the list.

## Reporting a problem

Use the [issue forms](https://github.com/AGomnes/Cantrip/issues/new/choose), with the smallest `.cantrip` file that shows the problem, what you expected, and what happened. `dotnet cantrip --version` names the exact build; in a Godot project without the tool, give the version in `addons/cantrip/plugin.cfg` and the Cantrip.Core version in your `.csproj`. For a problem with the rules, a failing `test` block is the best report there is: it is both the description and the proof.
