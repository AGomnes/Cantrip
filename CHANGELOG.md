# Changelog

Versions follow [semantic versioning](https://semver.org). While the major version is 0, any release may change the API, the language or the save format; [docs/stability.md](docs/stability.md) says what that means in practice. Each release's notes on GitHub are its section here.

When a release changes any of the following, its section says so under that name:

- **Breaking changes**: anything that can stop a game's code, its content or its CI working as before, including the Godot node's methods, signals and dictionary keys.
- **Save format**: whether saves made with the previous release still load.
- **Same-seed results**: whether the same content, seed and inputs still play out the same way.

## [Unreleased]

### Added

- `lint` warns about three things that load without error but do nothing:
  - a line in a declaration that ends in `:` but is not `effect:`, a `move` or a listener, such as `when card_played:`, which loads as a label and never runs (CT313; the warning suggests the listener it looks like);
  - `for N turns` giving a `duration` or `refresh` status more turns than the amount applied, which it cannot (CT314);
  - a `duration` line on a `duration`, `refresh` or `both` status (CT315).

  A game that runs blocks of its own from C# lists them in the new `LintOptions.HostBlocks`, or passes `--suppress CT313` to the tool.
- `cantrip lint --warnings-as-errors` exits 1 when there are warnings. CI uses it on every sample folder. `--suppress` now also leaves out warnings and notes found while loading, such as CT0105, so the two combine; errors from loading are always reported.
- A qualifier can name something that is not one word by quoting it: `card:"Fire Bolt"`, `hand where name:"Strike+"`.
- `cantrip test --trace` shows what `log` statements wrote, as `[log]` lines in a failing test's trace.
- An enemy's generated rules text names the phase a move is limited to: "Split (Broken phase only): Deal 5 damage to the player." Translations can supply the new `move.enemy.phase` phrase.
- `lint` warns (CT316) about a tag with behaviour of its own written on a line of its own, such as `exhaust` or `attack` under a card, which does nothing, and suggests the whole `tags` line.
- CT304 suggests the nearest event for one written in other words, such as `turn_start` for `start_of_turn`, and CT302 suggests `owner` for `host`.
- `DslTestRunner.ConfigureRuntime` and `DslTestRunner.CreateHost`, so a game's own test suite can run content tests that use the verbs, names and functions it registers in C#.
- Godot: `RemoveCard`, `GetDefinitions`, `DescribeDefinition` and `NewRun`, for removing and upgrading cards between battles, reward and shop screens, and a new run on the same node.
- Godot: with `AutoLoad` on, the node reports each content error and warning in the Output panel, one line each. Before, only the editor dock showed them.
- `tools/godot-install-smoke.sh` runs the first battle in docs/godot.md exactly as written and checks its output against the page.

### Changed

- The documentation was reworked after a reader study:
  - The README says where to start for a C# game, a Godot game and content authors.
  - [docs/writing-content.md](docs/writing-content.md) is new: a tutorial and recipes for content authors. The recipes and their tests are in `samples/recipes`, which CI lints and tests.
  - [docs/stability.md](docs/stability.md) now has the tested platforms, a performance figure, the range of numbers, what determinism covers, and the current known limitations, which used to be in this changelog.
  - The language reference states rules it had left unstated, and the C# and Godot guides cover more of what a real game needs: presenting events in a frame loop, several battles in one run, shipping content, saves across a content patch and what happens when content fails at runtime.
  - The Godot guide has a first battle that runs as written, a full reference for the node, and its contributor material in a section of its own.
- `CardRuntime.Restore` checks everything a snapshot needs (every definition, waiting block and entity it refers to, and that no list or record in it is missing) before it changes anything, so a save it refuses, a damaged one included, leaves the game in progress untouched. It used to throw with that game half taken apart.
- `CanCapture` is false, and `Capture` throws, while an action is running, including in a host callback part way through a card's effect, where nothing is queued yet. Such a save used to succeed and hold half an action.
- `Restore` refuses work from `Execute` saved without its block hash, or naming a block that is not a `next turn:` or `in N turns:` body. `Capture` never writes either.
- `GameState.ComputeHash` counts a waiting block by what it does rather than where it was written, so a save restored after a patch that moved the block hashes like the original, and two games waiting on different statements no longer hash alike.
- Godot: `AnswerChoice`'s `reason` words are snake_case, like every other word the node gives: `nothing_pending`, `stale_request`, `unknown_option`, `duplicate_option`, `too_few` and `too_many`. They come from a fixed table, so renaming an enum can no longer change them.
- A test line that names a card, relic, item, ability or enemy that is not defined, such as `hand Strik`, is lint error CT302, no longer a warning. The test fails saying so, with the nearest name, instead of with a .NET message ending `(Parameter 'name')`.
- Godot: `LoadSave` no longer refuses a save only because the content's fingerprint differs. It restores it when the content still has everything it needs, so a patch that only adds a card keeps older saves loading, and refuses it as `content_changed`, with the rules' message, only when `Restore` does.
- Godot: `GetWon()` returns null, not false, while a battle runs and before the first battle ends, as its documentation always said.
- Godot: `CreatePlayer`, `AddCard`, `AddDeck`, `SpawnEnemy`, `GrantAbility`, `LoadSave` and `CancelChoice` fail with an error when called from a host callback, as `Play` and the other actions already did, including from a callback that a query such as `CanPlay` or `Describe` ran.
- Godot: the editor dock's Reload button re-reads the `cantrip/*` project settings, so a changed setting no longer needs the plugin switched off and on.
- Godot: `CantripRuntime.Execute` is documented for its supported uses, such as a rest between battles, and its limits.
- Releases: the addon README in the release zip and in AGomnes/cantrip-godot links the Godot guide as it is at that release's tag, not on `main`. `tools/package-addon.sh` and `package-addon.ps1` take the tag as an optional argument.
- Contributors: building Cantrip.Core fails, rather than warns, when its public API changes without a line in `PublicAPI.Unshipped.txt` (RS0016, RS0017).

### Fixed

- Work that `CardRuntime.Execute` schedules, such as `Execute("next turn: draw 1")`, can be saved: the save holds the statements themselves. Before, `Capture` threw although `CanCapture` said it would succeed, and under `DeferredChooser`, which the Godot node uses, every later `Play`, `EndTurn` and `Execute` threw, so the battle could not go on. `CanCapture` and `Capture` now always agree.
- A `next turn:` or `in N turns:` block waiting in a save is recorded by a hash of its statements as well as its place in its definition. After a content patch, `Restore` runs the block that was saved even if the patch moved it within its definition, and refuses the save, naming the definition, if the patch changed or removed it. Before, it could silently run whichever block now stood in that place.
- A save restored after a content patch that adds, removes or reorders a definition's `on` blocks gives each used `once per` limit and each `on every` timer back to its own listener, found by a hash of the listener, instead of to whichever listener is now at the same place, which could let a `once per battle` listener fire twice. A listener whose `on` line changed, or whose body changed as it moved, starts afresh; one whose body alone changed keeps its limit.
- A hot reload keeps what listeners remember: a `once per` listener that has fired stays used, and an `on every` timer keeps its next due time, matched as a restored save matches them. Before, editing any definition in a file let a `once per battle` listener from that file fire again.
- Under `DeferredChooser`, an action that asks for a choice no longer throws ("The snapshot needs ..., which is not loaded") after a hot reload has removed a definition still in play.
- A save taken after a hot reload names a waiting block in the definition that scheduled it, where that definition still has the same statements, so a later patch to an unrelated definition no longer refuses it.
- Generated rules text puts a space before a unit that is a word: "for 2 turns", not "for 2turns".
- CT313's suggestion keeps the listener's timing and filter: for `once per battle on before_damaged(target:owner):` it suggests `on before_damaged(target:owner) once per battle:`, not `on damaged(...)`, which cannot `cancel`.
- Godot: an action a script takes in an `EffectEvent` handler has its events emitted before the outer call returns, after the events already on their way, rather than held until the game's next call. `BattleEnded` for a battle such an action ends comes after all of them.
- `tools/package-addon.sh` works with a relative output folder.
- Godot: loading or reloading content, from the node or the dock's Reload button, no longer crashes at random. The addon now reads each imported content file past Godot's resource cache, where a copy the garbage collector had already let go of could be found.
- Godot: `LoadSave` no longer throws for a save whose waiting `next turn:` or `in N turns:` block a patch has changed. It refuses it as `"content_changed"` with the rules' message, leaving the game and any open choice untouched. A save whose game cannot be read, or is in a snapshot format this Cantrip.Core does not read, is refused as `"wrong_format"`.
- Godot: `Save()` fails with "Cannot save while effects are still resolving." when a host callback calls it part way through an effect, as `CanSave()` and the guide already said; it used to save half an action. When a hot reload has changed a waiting block, it passes on the rules' own error, which says so, instead of blaming resolving effects.
- Godot: `RegisterName` and `RegisterFunction` accept any Callable. A GDScript lambda, or any Callable with `.bind()`, used to arrive empty ("Attempt to call callable null::null"). A value that cannot be called is refused when it is registered, one that takes the wrong arguments fails with an error naming it, and registered callables are released when the node is freed.

### Breaking changes, save format and same-seed results

- **Breaking changes.**
  - Godot: the `AnswerChoice` `reason` words were `nothingpending`, `stalerequest`, `unknownoption`, `duplicateoption`, `toofew` and `toomany`. A script that compares against them needs the snake_case words above.
  - Godot: a script that stores `GetWon()` in a `bool` variable, or compares it with `false`, must allow for null.
  - Saving from a host callback part way through an effect now fails, in C# (`Capture`) and in Godot (`Save()`), instead of saving half an action.
  - Lint: a test line naming a definition that does not exist is error CT302, so `lint` and `validate` fail on content they passed before.
  - Godot: a host callback that changed the game with a setup call now fails. The guide already said not to.
  - Godot, C# only: `CantripRuntime` and `GodotEffectHost` `RegisterName` and `RegisterFunction` take a `Variant` and dispose it when done. Pass a `Callable`, not a Variant you keep using or register twice.
  - `CanCapture` can be false in one new case: while a waiting block is one that a hot reload has changed since it was scheduled, until that block has run.
- **Save format.** Saves made with 0.1.0-preview.2 still load; the format number stays 1, and their waiting blocks and listener records are matched by place, as before. Saves gain optional fields: `ScheduledSnapshot.Statements` and `BlockHash`, and `ListenerLimitSnapshot.ListenerHash` and `ListenerDueSnapshot.ListenerHash`. A save holding work scheduled by `Execute` does not load in 0.1.0-preview.2. A save that used to load after a patch that changed its waiting block, and then ran the wrong block, is now refused, as is a damaged one. In Godot, a save refused before only because a definition had since been added now loads.
- **Same-seed results.** Unchanged: the simulator plays the same seeds identically. `ComputeHash` values of games with work waiting differ from 0.1.0-preview.2, but play does not.

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
