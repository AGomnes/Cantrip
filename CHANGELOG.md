# Changelog

Versions follow [semantic versioning](https://semver.org). While the major version is 0, any release may change the API, the language or the save format; [docs/stability.md](docs/stability.md) says what that means in practice. Each release's notes on GitHub are its section here.

## [Unreleased]

Nothing yet.

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
- Reloading a file spelled with the other slash, such as `content/cards.cantrip` after `LoadFolder` recorded `contentrds.cantrip` on Windows, now replaces it. It used to report every definition as a duplicate and leave the running game on the old rules.

### Known limitations

- Real time is experimental: the tick clock and abilities work, but no real-time game has been built with them, and `within` needs a host to supply space.
- `discover` in a game using `DeferredChooser`, which the Godot node does by default, takes the first offer instead of asking the player.
- A run of battles (map, rewards, carrying hp between fights) is host code; the language has no run structure yet.
- The Godot addon has only been installed by its author, and its live debugging, the round trip between a running game and the editor, has only been checked by hand.
