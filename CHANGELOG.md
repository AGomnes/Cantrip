# Changelog

Versions follow [semantic versioning](https://semver.org). While the major version is 0, any release may change the API, the language or the save format; [docs/stability.md](docs/stability.md) says what that means in practice. Each release's notes on GitHub are its section here.

When a release changes any of the following, its section says so under that name:

- **Breaking changes**: anything that can stop a game's code, its content or its CI working as before, including the Godot node's methods, signals and dictionary keys.
- **Save format**: whether saves made with the previous release still load.
- **Same-seed results**: whether the same content, seed and inputs still play out the same way.

## [Unreleased]

### Changed

- The documentation was reworked after a reader study:
  - The README says where to start for a C# game, a Godot game and content authors.
  - [docs/writing-content.md](docs/writing-content.md) is new: a tutorial and recipes for content authors. The recipes and their tests are in `samples/recipes`, which CI lints and tests.
  - [docs/stability.md](docs/stability.md) now has the tested platforms, a performance figure, the range of numbers, what determinism covers, and the current known limitations, which used to be in this changelog.
  - The language reference states rules it had left unstated, and the C# and Godot guides cover more of what a real game needs.

## [0.1.0-preview.2] - 2026-09-21

### Added

- `discover` can be answered by the player. Under `DeferredChooser`, which the Godot node uses, it pauses like any other choice: `PendingChoice.IsOffer` is true, `Definitions` lists the candidates, and `CardRuntime.Answer(EntityDefinition)` replays the action with the pick. In GDScript the pending choice has `"kind": "offer"`, each option carries the candidate's name, kind, tags and rules text, and the answer is its position. The pick is remembered by identity, so reloading content while an offer is open never swaps it for another card: if the reload takes it out of the offer, the player is asked again. `RandomChooser` now picks among offers at random instead of taking the first.
- `tools/godot-install-smoke.sh`, run by CI on every push and before every release, installs the addon from its zip into a blank Godot project outside the repository, following docs/godot.md, and plays a battle from GDScript.

### Changed

- `cantrip validate` also reports the linter's errors, such as an unknown verb, so a typo like `aply` no longer passes as "0 errors". Warnings stay with `lint`. Like `lint`, it takes `--suppress`, e.g. `--suppress CT301` for content that calls verbs a game registers in C#.
- Clearer messages for mistakes made in the first hour: a declaration name with spaces is one error saying to quote it (CT0029), a line indented under a line that opens no block is one error saying which lines can (CT0030), in an effect, a test or a declaration's properties alike, and a test naming an enemy nothing defines says so and how to fix it.
- `CardRuntime.Restore` abandons a pending choice, which belonged to the game being replaced. A new action does the same with a choice left half answered, so its answers can never be read as answers to the new one.

### Breaking changes, save format and same-seed results

Added after the release, which did not call these out.

- **Breaking changes.** `cantrip validate` exits 1 on the linter's errors, such as an unknown verb, so a CI step that passed with 0.1.0-preview.1 can fail; content that calls verbs a game registers in C# needs `--suppress CT301`. Under `DeferredChooser`, which the Godot node uses, `discover` now stops for the player's answer where it used to take the first offer, so a game's choice UI must handle an offer (`PendingChoice.IsOffer`, or `"kind": "offer"` in GDScript). `CardRuntime.Restore` clears `Pending`.
- **Save format.** Unchanged: saves made with 0.1.0-preview.1 load.
- **Same-seed results.** Changed for content with `discover`. `RandomChooser` now picks among the offers at random, from its own generator, and under `DeferredChooser` the player picks, where both used to take the first offer, so the same seed can play out differently from 0.1.0-preview.1. `FirstOptionChooser` and `ScriptedChooser` are unchanged.

## [0.1.0-preview.1] - 2026-09-21

The first public preview.

### What is in it

- **Cantrip.Core** (netstandard2.1): the language, content loading with diagnostics, a battle runtime with turns, card play, enemy intents and phases, a layered modifier pipeline, events with before, instead and after phases, deterministic fixed-point maths and RNG, save and load, hot reload, player choices a UI answers mid-effect, a causality trace, generated rules text with live values, a linter and a DSL test runner.
- **Cantrip.Cli**, a .NET tool: `cantrip validate`, `lint`, `test`, `describe`, `repl` and `--version`.
- **A Godot 4.6 addon**, attached to the GitHub release as a zip: a node that drives battles from GDScript, an importer so content reaches exported builds, an editor dock for problems, tests and card text, and debugger tabs for live debugging.
- **Samples**: `samples/basic` for each feature, `samples/corpus` with 93 effects from nine existing games, and `samples/slice`, a five-floor roguelite that `src/Cantrip.Sim` plays with a bot.
- **Docs**: a [quickstart](docs/quickstart.md), the [language reference](docs/language.md), [architecture](docs/architecture.md), the [Godot addon](docs/godot.md), [coverage](docs/coverage.md) and what building the slice taught ([slice friction](docs/slice-friction.md)).

### Changed since the private builds

- `event` and `encounter` declarations are now error CT0113, "reserved but not supported yet". They used to load as inert definitions that nothing ran.
- `CardRuntime.CanPlay`, `LegalTargets` and `TargetMode` answer "what can I play, and at what?" exactly as `Play` decides it. The Godot node's `CanPlay` and `GetLegalTargets` use them, which fixes legal targets that ignored taunt and stealth, and `CanPlay` checking energy for a card priced in another resource.
- The enums a save stores as numbers (`EntityKind`, `ValueKind`, `ScheduleTiming`) have explicit values, so reordering them can no longer corrupt a save.
- Reloading a file spelled with the other slash, such as `content/cards.cantrip` after `LoadFolder` recorded `content\cards.cantrip` on Windows, now replaces it. It used to report every definition as a duplicate and leave the running game on the old rules.

### Known limitations

As they stood at this release. The current list is in [docs/stability.md](docs/stability.md#known-limitations); the `discover` limitation below was fixed in 0.1.0-preview.2.

- Real time is experimental: the tick clock and abilities work, but no real-time game has been built with them, and `within` needs a host to supply space.
- `discover` in a game using `DeferredChooser`, which the Godot node does by default, takes the first offer instead of asking the player.
- A run of battles (map, rewards, carrying hp between fights) is host code; the language has no run structure yet.
- The Godot addon has only been installed by its author, and its live debugging, the round trip between a running game and the editor, has only been checked by hand.
