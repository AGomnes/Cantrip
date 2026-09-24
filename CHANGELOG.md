# Changelog

Versions follow [semantic versioning](https://semver.org). While the major version is 0, any release may change the API, the language or the save format; [docs/stability.md](docs/stability.md) says what that means in practice. Each release's notes on GitHub are its section here.

When a release changes any of the following, its section says so under that name:

- **Breaking changes**: anything that can stop a game's code, its content or its CI working as before, including the Godot node's methods, signals and dictionary keys.
- **Save format**: whether saves made with the previous release still load.
- **Same-seed results**: whether the same content, seed and inputs still play out the same way.
- **Bot behaviour**: whether `cantrip sim`'s bots play a scenario the same way, so that a report whose numbers have moved says why.

## [Unreleased]

### Added

- **`copy`: duplicate something that is in the game, as it stands.** `create Strike` makes a Strike as it is *printed*; `copy picked` makes one as it *is*, with the buff it was given this battle and the cost it was discounted to. `copy [who] [N] [into zone]` takes the zone words `create` takes, defaults to itself, and binds **`copied`**, always a list. Slay the Spire's Dual Wield and Burst, and most of Hearthstone's board copies, are one line now instead of a fresh card that quietly loses everything the original had gained.

  Two rules decide what a copy is, and both of them are the answer to a trap. **Its side and owner come from the original**, never from whoever is copying: a player's card that copies an enemy's minion gives the *enemy* a second minion, where taking the side from the declaration would hand it to the player. And it is **placed by kind, where a new one would go** — never in the zone the original sits in, which would otherwise drop a copy of an active power straight into `powers` as a second power nobody played, and a copy of an exhausted card where nothing can reach it.

  Live stats, runtime tags and a fresh instance of every status and keyword come across, with their `stacks`, `duration` and `expires_at` as they stand. Those arrive silently: no `status_applied` is raised and `immune` is never asked, because a copy is a snapshot of a state, not a new application. Nothing is restored — a wounded minion is copied wounded, and `copied.hp = copied.max_hp` is the one visible line that says otherwise. A copy raises `created` with `event.copy_of` set to the original, so the original hears `on created(self)` about its own copying; it is in the sharp edges. `copy Strike` is refused and names `create Strike`, at lint (**CT320**) and at run. See [Built-in verbs](docs/language.md#built-in-verbs) and the recipe [Copy a card as it is now](docs/writing-content.md#copy-a-card-as-it-is-now).

- **`transform`: replace what something is while it keeps who it is.** `transform [who] into Definition` keeps the entity's id, owner, source, side, sequence, zone **and index in that zone**, its board slot and its history counters — so a `let`, an `event.target`, a test's `enemy2` and a C# reference a game has cached all still hold the same object, now wearing the new name. Polymorph, and every "becomes a different card". The workaround was `destroy` plus `create`, which loses the slot, the id and, for a generic `actor`, the side.

  Everything the old definition brought goes, and goes silently: stats, tags, every status and keyword, the spent `once per ...` windows, the intent, the phase and its own `next turn:` plans. The new definition's stats and tags take their place, so the new thing arrives whole; three visible lines carry a wound across. Statuses raise **no** `status_removed`, because one verb raising a variable number of cancellable events — each able to destroy the host half way through — is not something content could reason about, and `destroy` already sheds its attachments the same way. The one event is **`transformed`**, raised once through all three phases with the tags the entity had before and both definitions as `event.was` and `event.into`. It is not a `created`, a `died`, a `killed` or a `destroyed`, so a Polymorph never fires a death rattle.

  An actor rolls a fresh intent at once, and an `enemy` may become an `actor` declaration and back with its side kept, so one `actor "Sheep"` serves both sides. `into` takes a definition and never an entity, which is refused by name rather than quietly giving the other thing's *printed* stats. A `transform` inside an `until` block is refused at lint (**CT321**) and at run: `until` puts back what it did, and none of this can be put back. See [Built-in verbs](docs/language.md#built-in-verbs) and the recipe [Turn something into something else](docs/writing-content.md#turn-something-into-something-else).

- **`play`: play a card out of a pile, without the player choosing it.** `play card [on target] [, free]` — "play the top card of your draw pile", Mayhem, Monster Train's automatic plays. The card leaves its pile, pays, raises `card_played` with its tags, counts towards `cards_played` and `attacks`, runs its effect, and is filed afterwards by the tags it has *then*. It never passes through hand, so nothing is `drawn` and the hand limit is not involved. `replay` remains the other verb and is not a play: it resolves an effect again for nothing, leaving the card where it is.

  It binds **`played`** to the card, or to **`none`** when it was not played, and binds it before anything else so the name always exists. A refusal the rules allow — an empty pile, an unplayable Curse, a cost the payer cannot afford, no legal target, a card already in `play` or `powers` — is not an error, because "play the top card of your draw pile" must not crash the first time the top card is a Curse.

  `, free` changes what is **paid**, never what the card **sees**: an X-cost card played free binds `x` to what the payer has and spends nothing, and the `card_played` event's `amount` is what was actually paid, so "gain 1 hp per energy spent" stays honest. **Targeting never prompts**: a card that needs a target and was not given one has one rolled from the game's own RNG, through the `targetable` channel. A target written with `on` is used exactly as written; the running effect's own target is inherited only where the card could legally be pointed at it, because a `play` inside a listener inherits whatever that event was about and a hint the card cannot take would otherwise refuse the play silently. The roll goes through the same channel, so a Taunt binds an automatic play as it binds a hand-played one and the roll replays and saves. A nested play extends its caller's chain, so `once per chain` counts one chain across a cascade; direct recursion stops at `max_call_depth` with a message that says what happened. See [Built-in verbs](docs/language.md#built-in-verbs) and the recipe [Play a card the player did not choose](docs/writing-content.md#play-a-card-the-player-did-not-choose).

- **The Godot dock's Source tab is an editor.** Write `.cantrip` content in it, save with Ctrl+S or the Save button, and read what the parser and the linter make of it a third of a second after you stop typing: the line marked in the gutter, the message written out under the buffer as the caret reaches its line, and an **Apply fix** button that puts the suggested word in for you. **Run tests** runs the content's own tests against the buffers, saved or not. Saving writes the `res://` file and tells Godot, so the importer picks it up, and a file written with Windows line endings is saved back with `\n`. Problems, Tests and Preview read the same answer, so nothing in the dock disagrees with what is on screen. A check costs about ten milliseconds on the largest arrangement in this repository, 23 files and 1,800 lines.

  **It indents with spaces, at the width the buffer already uses, and never writes a tab**, including a pasted block, a middle-click paste, and a block dropped in from another window. The language decides where a block ends by how deep a line is and counts a tab as four columns, so one tab in a two-space file moves a line into another block without changing how it looks.

  A file changed on disk under an unsaved buffer is left alone, with one line offering to keep yours or take theirs; the warning follows the file rather than the tab, so switching away and back brings it with you. A clean buffer is simply reread. A file deleted underneath a buffer leaves the buffer alone, and Save writes the file again; a `.cantrip` file added or deleted outside the editor joins or leaves the content by itself. Switching files parks a buffer with its caret rather than asking anything, and the tab counts what is unsaved.

  It does not complete names, go to a definition, or push a save into a running game: those are the next pass. Unsaved buffers do not survive a C# build, which reloads the addon; the Output panel says so when it happens.

### Changed

- **`play` is one word that means two things, told apart by where it is written.** It was a test verb; it is now a rule verb as well. A `play` in a test's own body is the test's — it still puts a card into hand by name and plays it from there — and a `play` written anywhere else, in a card's effect or in a content verb a test calls, is the rules'. The rule is lexical rather than dynamic because a content verb takes its context from whoever called it, so no check made while the verb runs could read `verb cascade(): play draw.first` correctly when a test line calls it.

  Two diagnostics move with it. `play Strike` in a card effect was CT301, an unknown verb that "only exists inside `test` blocks"; it is now **CT320**, which says the more exact thing — a card is played out of a pile, not out of content — and names the fix. Inside a `scenario` it is still **CT301** with the message it has always had: a scenario states the fight, and a bot plays it.

- **A card played while another card's effect is resolving no longer drains the outer effect's queued triggers.** Only the outermost play drains, and only a play at the top level checks whether the battle is over. A nested play that drained would resolve the outer effect's already-queued after-listeners in the middle of that effect, which nothing else in the language does. This is visible to a game whose `IEffectHost.OnEvent` calls `CardRuntime.Play` during an action: the triggers and the battle-over check now happen when the outer action finishes rather than inside the inner play. Order is unchanged and nothing is lost.

- **`Entity.Name` can change**, and one internal rule changed with it. `transform` renames an entity in place, so a game that caches or keys on a card's or an actor's name has to expect that name to move. `GameState.Restore` used to reuse an existing `Entity` instance only when its id, kind **and name** all matched, which silently assumed a name is fixed for the life of an entity; it now matches on id and kind and assigns the saved name. Without that, an effect that transforms something and then asks the player a question would hand the game a stale, removed object while the runtime held a new one — because a deferred choice rolls the action back by restoring a snapshot. The save format is unchanged: `EntitySnapshot` has always stored the name and the definition's name separately.

- **The docs treat Godot as the front door.** Cantrip is a Godot library first, and the docs now read that way. The [README](README.md) says in its opening that you use it from Godot 4.6 on Godot's .NET edition, in GDScript or C#, or from any other .NET project; the Godot route is first in **Where to start**, first under **Get started** — with a GDScript battle beside the C# one — and first in the documentation list, and its command-line table says which of those commands the dock already covers. The [quickstart](docs/quickstart.md) sends a Godot reader to [the addon guide](docs/godot.md) before its first step and says plainly that it is the route for a game that is not in Godot. [Using Cantrip from C#](docs/csharp.md) opens by saying a Godot game needs none of it. [Writing content](docs/writing-content.md), [the language reference](docs/language.md), [Simulating](docs/simulating.md) and [samples/README.md](samples/README.md) say where the editor dock does the work of a command and where it does not, both NuGet pages lead with Godot, and [CONTRIBUTING.md](CONTRIBUTING.md) maps the addon before the library, as the README now does for a contributor.

  What Godot costs is said in the same breath and not softened: the .NET edition of the engine for everyone who opens the project, the .NET SDK, a C# solution even in a game written entirely in GDScript, and Godot's own limits on where a .NET game can be exported. The dock's limits are stated as plainly: neither `sim` nor the REPL has a tab, so a Godot project still installs the tool for those. Nothing about the engine-free core changed. It still targets `netstandard2.1` and references no game engine, the console quickstart is still followed word for word against freshly packed packages before every release, and [docs/stability.md](docs/stability.md) remains the only place that says how much of any of this has been tested.

- The README said Cantrip does not do "two players, or an opponent with its own hand and deck". The second half was wrong: a card belongs to whoever controls it, so an enemy can hold cards and play them at the player, and hands and draw piles are per-owner. Tests now pin that. What is actually missing for a second player is the priority window, nothing being able to respond to a card while it is played, which is what the bullet now says.
- The [quickstart](docs/quickstart.md) says what a `#` line in a `.cantrip` file is, where the reader first meets one. The first content block on the page opens with a comment, and until now nothing on the page said so; [Files](docs/language.md#files) in the language reference had it, several pages later.

## [0.1.0-preview.4] - 2026-09-23

### Added

- A `scenario` declaration, beside `test`: a whole run stated once — a deck, some fights in order, and whatever happens between them — for a bot to play many times. Its body is statements, as a test's is, with three lines of its own: `battle "Cinder Imp", "Frost Wisp"`, `runs 500`, and an `expect` that measures every run rather than one game (`expect no stalls`, `expect wins >= 55%`). See [Scenarios](docs/language.md#scenarios).

  `validate` counts the scenarios in a folder and `lint` checks them as it checks a test, so a misspelt card, relic, ability or enemy is the same error, CT302. Three new codes: CT317, a warning, a scenario with nothing to fight; CT318, a note below 100 runs, where the same content answers differently each time, and an error when the count is not a whole number of one or more; CT319, an error, an `expect` a scenario cannot check. CT301 now names the block a verb belongs to in both directions, so `play` in a scenario and `battle` in a test each say where the line should live. A file with a `scenario` in it does not load in 0.1.0-preview.3, which reads the word as error CT0012.
- `cantrip sim <path>...` plays those scenarios, hundreds of times each, and reports what happened over them: a run that threw, with the seed and the statement to replay it; a battle that reached the turn limit; a card that was held and never playable, or never drawn; an enemy move that never fired; and, where nothing the scenario rolls changed an outcome, that every run came out the same way. What happened at least once happened in the content, whoever plays; what never happened is bounded by what the bots reached, and the block itself says so. A card drawn or created part way through a turn counts as having reached a hand and, if it was played, as having been playable, because the engine raised an event saying so. Its options are `--name`, `--runs`, `--seed`, `--bot`, `--turn-limit`, `--watch SEED` and `--suppress`, and it exits 1 on a content error, no scenario to play, a run that threw, a battle that stalled or an expectation that failed. See [Simulating](docs/simulating.md).

  **Three bots, and two of them play by default.** `cautious` tries every legal play and ability through the engine — capture the game, make the play, end the turn so the enemies answer, score what is left, roll back — and then makes the best play it found; `patient` does the same scoring two turns out, and is there to disagree; `random` is the floor and the fastest way to fuzz. `--bot cautious|patient|random|both` chooses, and `both` is the default. Each bot plays every run, from the same seeds, and the report keeps a table per bot apart from the block of facts, which is one block for all of them: a move either bot reached is a move the content reached.

  None of them knows a card, status or enemy by name, and all of them weigh a position the same way — the player's hp against the enemies' — so all three are wrong in the same direction about a card that draws and about anything that pays off several turns later, and two agreeing is not evidence. Their levels are printed under a heading that says not to quote one: on `samples/slice`, over the same 500 seeds, the cautious bot finishes 70.8% of the runs, the patient bot 74.0% and the random bot 0.6%. `expect wins`, `expect hp_left` and `expect turns` are still reported as not checked for the same reason; `expect no stalls` and `expect no errors` are checked, because they hold whoever plays. There is no option for how far a bot looks ahead and no way to tell it what a status is worth: both were measured, both move the answer by tens of points, and neither has a defensible setting.

  Nothing a bot merely tried is counted. A play tried and rolled back raises the same events as a real one, so while a bot is trying one the run records no enemy move, spends none of the scenario's `answer` lines, and reseeds the dice, so the lookahead cannot read the roll that is about to decide the play. Two bots cost about twice as long as one: the slice's 500 runs take about 30 seconds with both, about 14 with `--bot cautious` and under 2 with `--bot random`. From here on this changelog carries a **Bot behaviour** line whenever a release changes what a bot does.
- **The meter**, under each bot's table: what the engine raised while the game was really being played. Where the damage the enemies took came from, by the card or status or enemy that dealt it; the same hits by the tags they carried; what took the player's hp; and the healing, block, statuses, cards and enemy moves beside them. Every amount is one the engine itself set — a `damaged` event carries the hp actually lost, after block and capped by the hp that was left — so given the plays that were made, that is where the hp went, for anyone who made them. Which plays were made is still the bot's, which is why there is one meter per bot.

  The tag column is what makes it answer a question about content rather than about play. On `samples/slice` it reports that no tick of Burn ever carried `attack`, so `modify damage where tag:attack` would have reached none of the damage Burn dealt — and Burn's share of that damage is large: 37.6% of what the cautious bot's plays dealt and 48.2% of what the patient bot's dealt. The share is a fact about the bot as much as about the deck, so the report prints it inside that bot's block and never on its own.

  A card the tool cannot judge is named rather than guessed at: a `choose` or `discover` part way through an effect is answered at random, counted with the line that asked, and reported as such, because a bot looks one play ahead and not into the middle of one.

  The meter counts none of it either, and that is the whole of its correctness. Over 200 runs of the slice the plays the cautious bot tries and rolls back raise about 1.18 million events against 91 thousand in the play that counted, so a meter that counted them would be out by an order of magnitude. The guard is checked from the engine's own side: for every actor, the damage counted less the healing counted has to equal the hp it actually lost.
- `Cantrip.Testing.SetupSession`: the setup half of a `test` or a `scenario` — the verbs `enemy`, `player`, `hand`, `deck`, `discard_pile`, `relic`, `seed`, `answer`, `realtime` and `grant` registered on a runtime, and the statement loop that runs a line. `DslTestRunner` and `cantrip sim` share it, so a setup line cannot mean one thing in a test and another in a scenario.
- The rest of Cantrip.Core's new public API, which is what a scenario is made of: `ContentLibrary.Scenarios` and `Cantrip.Content.ScenarioDefinition`; `Cantrip.Syntax.ScenarioDeclNode`; `Cantrip.Content.Scenario`, holding the verbs a scenario body may use and the measurements its `expect` may name, which the linter and whatever plays a scenario both read, so a word cannot be legal in one and unknown in the other; and the codes `Linter.NothingToFight`, `Linter.RunCount` and `Linter.UnknownMeasurement`. Nothing was removed or changed: code written against 0.1.0-preview.3 compiles unchanged.
- Godot: the editor's syntax highlighting knows `scenario`, so a scenario block reads like a `test` block rather than like prose. Nothing else in the addon changed.

### Changed

- The README says what Cantrip is for more exactly: the combat of single-player turn-based games, cards optional. `samples/abilities` is new and shows a fight with no cards in it, run by CI like the other sample folders.
- `samples/slice/sim.cantrip` and `samples/abilities/sim.cantrip` state each folder's fights as a scenario, and CI plays both.

### Removed

- The `cantrip-sim` program, which played whole runs of the slice over a tower written in C# rather than in content. `cantrip sim` covers what it covered and states the gauntlet in content instead; what it had beyond that was a reward offer after each battle, a branch at floor 3 between the elite and a rest, and per-card and per-relic tables comparing the runs that took one with the runs that did not — which the bot decided both sides of, and which were the most quotable things in the report and the least defensible. `src/Cantrip.Sim` is now a library, shipped inside the `cantrip` tool, holding the scenario runner, the bots and the meter. It was never packed or installed on its own, so nothing a game depends on changes.

### Breaking changes, save format and same-seed results

- **Breaking changes.**
  - A whole number before a name in a setup line now repeats it: `deck 4 Zap, 4 Ward` puts eight cards in the draw pile. Before, the number was dropped with no word from anywhere, so the line put in one of each and the test measured a different deck from the one it was written as. It reads the same way on `deck`, `hand`, `discard_pile`, `relic`, `grant` and `answer`, in a `test` as well as a `scenario`. A number that is not a whole count from 1 to 1000, and a count with no name after it, now fail the line rather than being ignored. No sample or test in this repository wrote a number on such a line; content elsewhere that did will hold more cards than it did before, and a test that measured the smaller deck will fail.
  - `cantrip validate`'s summary line counts scenarios too: `2 file(s), 21 definition(s), 1 verb(s), 13 test(s), 0 scenario(s): 0 error(s), 0 warning(s)`. The count is printed for content that has no scenario as well, so a script that reads that line needs the extra field.
- **Save format.** Unchanged: saves made with 0.1.0-preview.3 load, and the format number stays 1. Nothing this release changed is written to or read from a snapshot, and a `scenario` block is not a definition, so adding one leaves the content fingerprint alone and every save that loaded before still loads.
- **Same-seed results.** Unchanged for the same content, seed and inputs: nothing in the engine changed, and a setup line with no count builds exactly the game it did. Content that wrote a count on a `deck`, `hand`, `discard_pile`, `relic`, `grant` or `answer` line plays differently, because the line now puts in the cards it says.

## [0.1.0-preview.3] - 2026-09-22

### Added

- `lint` warns about three things that load without error but do nothing:
  - a line in a declaration that ends in `:` but is not `effect:`, a `move` or a listener, such as `when card_played:`, which loads as a label and never runs (CT313; the warning suggests the listener it looks like, keeping its timing, filter and limit: for `once per battle on before_damaged(target:owner):` it suggests `on before_damaged(target:owner) once per battle:`);
  - `for N turns` giving a `duration` or `refresh` status more turns than the amount applied, which it cannot (CT314);
  - a `duration` line on a `duration`, `refresh` or `both` status (CT315).

  A game that runs blocks of its own from C# lists them in the new `LintOptions.HostBlocks`, or passes `--suppress CT313` to the tool.
- `cantrip lint --warnings-as-errors` exits 1 when there are warnings; any other command refuses the option with exit code 2. CI uses it on every sample folder. In `lint` and `validate`, `--suppress` now also leaves out warnings and notes found while loading, such as CT0105, so the two combine; errors from loading are always reported.
- A qualifier can name something that is not one word by quoting it: `card:"Fire Bolt"`, `hand where name:"Strike+"`.
- `cantrip test --trace` shows what `log` statements wrote, as `[log]` lines in a failing test's trace.
- An enemy's generated rules text names the phase a move is limited to: "Split (Broken phase only): Deal 5 damage to the player." Translations can supply the new `move.enemy.phase` phrase; until they do, such moves use the English one.
- `lint` warns (CT316) about a tag with behaviour of its own written on a line of its own, such as `exhaust` or `attack` under a card, which does nothing, and suggests the whole `tags` line.
- CT304 suggests the nearest event for one written in other words, such as `turn_start` for `start_of_turn`, and CT302 suggests `owner` for `host`.
- `DslTestRunner.ConfigureRuntime` and `DslTestRunner.CreateHost`, so a game's own test suite can run content tests that use the verbs, names and functions it registers in C#.
- Godot: `RemoveCard`, `GetDefinitions`, `DescribeDefinition` and `NewRun`, for removing and upgrading cards between battles, reward and shop screens, and a new run on the same node. The debugger's trace tab starts afresh when a game starts a new run.
- Godot: with `AutoLoad` on, the node reports each content error and warning in the Output panel, one line each. Before, a running game printed nothing, and they showed only in the editor.
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
- A test line that names a card, relic, item, ability or enemy that is not defined, such as `hand Strik`, is lint error CT302. It used to be a warning, or, for an enemy (`enemy Slme hp 12`), not reported at all. The test fails saying so, with the nearest name, and without the `(Parameter 'name')` that .NET added to the message.
- Godot: `LoadSave` no longer refuses a save only because the content's fingerprint differs. It restores it when the content still has everything it needs, so a patch that only adds a card keeps older saves loading, and refuses it as `content_changed`, with the rules' message, only when `Restore` does.
- Godot: `GetWon()` returns null, not false, while a battle runs and before the first battle ends, as its documentation always said.
- Godot: `CreatePlayer`, `AddCard`, `AddDeck`, `SpawnEnemy`, `GrantAbility`, `LoadSave` and `CancelChoice` fail with an error when called from a host callback, as `Play` and the other actions already did. Every call that changes the game, the actions included, now also fails from a callback that a query such as `CanPlay` or `Describe` ran, where nothing refused it before.
- Godot: the editor dock's Reload button re-reads the `cantrip/*` project settings, so a changed setting no longer needs the plugin switched off and on.
- Godot: `CantripRuntime.Execute` is documented for its supported uses, such as a rest between battles, and its limits.
- Releases: the addon README in the release zip and in AGomnes/cantrip-godot links the Godot guide as it is at that release's tag, not on `main`. `tools/package-addon.sh` and `package-addon.ps1` take the tag as an optional argument.
- Contributors: building Cantrip.Core fails, rather than warns, when its public API changes without a line in `PublicAPI.Unshipped.txt` (RS0016, RS0017). The editor run with `--cantrip-selftest` exits with 1 when one of the dock's own checks fails.

### Fixed

- Work that `CardRuntime.Execute` schedules, such as `Execute("next turn: draw 1")`, can be saved: the save holds the statements themselves. Before, `Capture` threw although `CanCapture` said it would succeed, and under `DeferredChooser`, which the Godot node uses, every later `Play`, `EndTurn` and `Execute` threw, so the battle could not go on. `CanCapture` and `Capture` now always agree.
- A `next turn:` or `in N turns:` block waiting in a save is recorded by a hash of its statements as well as its place in its definition. After a content patch, `Restore` runs the block that was saved even if the patch moved it within its definition, and refuses the save, naming the definition, if the patch changed or removed it. Before, it could silently run whichever block now stood in that place.
- A save made with this release and restored after a content patch that adds, removes or reorders a definition's `on` blocks gives each used `once per` limit and each `on every` timer back to its own listener, found by a hash of the listener, instead of to whichever listener is now at the same place, which could let a `once per battle` listener fire twice. A listener whose `on` line changed, or whose body changed as it moved, starts afresh; one whose body alone changed keeps its limit.
- A hot reload keeps what listeners remember: a `once per` listener that has fired stays used, and an `on every` timer keeps its next due time, matched as a restored save matches them. Before, editing any definition in a file let a `once per battle` listener from that file fire again.
- Under `DeferredChooser`, an action that asks for a choice no longer throws ("The snapshot needs ..., which is not loaded") after a hot reload has removed a definition still in play.
- After a hot reload of a file with a `next turn:` or `in N turns:` block waiting, even a reload that changed nothing in it, `Capture` threw although `CanCapture` said it would succeed, and under `DeferredChooser`, which the Godot node uses, every later `Play`, `EndTurn` and `Execute` threw, so the battle could not go on. Play now goes on, and a save names the same statements in the loaded content, in the definition that scheduled the block where it can. While a waiting block is one the reload changed, `CanCapture`, and in Godot `CanSave()`, are false and `Capture` and `Save()` fail with an error saying so, until the block has run.
- Generated rules text, and `AstPrinter.Print`, put a space before a unit that is a word: "for 2 turns", not "for 2turns".
- Godot: an action a script takes in an `EffectEvent` handler has its events emitted before the outer call returns, after the events already on their way, rather than held until the game's next call. `BattleEnded` for a battle such an action ends comes after all of them.
- `tools/package-addon.sh` works with a relative output folder.
- Godot: loading or reloading content, from the node or the dock's Reload button, no longer crashes at random. The addon now reads each imported content file past Godot's resource cache, where a copy the garbage collector had already let go of could be found.
- Godot: `LoadSave` no longer throws when the rules refuse a save, such as one whose waiting `next turn:` or `in N turns:` block a patch has removed. It refuses it as `"content_changed"` with the rules' message, leaving the game and any open choice untouched. A save whose game cannot be read, or is in a snapshot format this Cantrip.Core does not read, is refused as `"wrong_format"` rather than throwing.
- Godot: `Save()` fails with "Cannot save while effects are still resolving." when a host callback calls it part way through an effect, as `CanSave()` and the guide already said; it used to save half an action.
- Godot: `RegisterName` and `RegisterFunction` accept any Callable. A GDScript lambda, or any Callable with `.bind()`, used to arrive empty ("Attempt to call callable null::null"). A value that cannot be called is refused when it is registered, one that takes the wrong arguments fails with an error naming it, and registered callables are released when the node is freed.

### Breaking changes, save format and same-seed results

- **Breaking changes.**
  - Godot: the `AnswerChoice` `reason` words were `nothingpending`, `stalerequest`, `unknownoption`, `duplicateoption`, `toofew` and `toomany`. A script that compares against them needs the snake_case words above.
  - Godot: a script that stores `GetWon()` in a `bool` variable, or compares it with `false`, must allow for null.
  - Saving from a host callback part way through an effect now fails, in C# (`Capture`) and in Godot (`Save()`), instead of saving half an action.
  - Lint: a test line naming a definition that does not exist is error CT302, so `lint` and `validate` fail on content they passed before.
  - Godot: a host callback that changed the game with a setup call now fails, as does any call that changes the game from a callback a query such as `CanPlay` or `Describe` ran. The guide already said not to.
  - Godot: `LoadSave` restores a save whose content fingerprint differs whenever the rules can, where it used to refuse it as `content_changed`. A game that relied on that refusal to turn away every save from before a patch must check a version of its own.
  - Godot: `RegisterName` and `RegisterFunction` throw an `ArgumentException` for a value that cannot be called, such as a Callable whose object has been freed, which they used to accept.
  - Godot, C# only: `CantripRuntime` and `GodotEffectHost` `RegisterName` and `RegisterFunction` take a `Variant` and dispose it when done. Pass a `Callable`, not a Variant you keep using or register twice.
  - Rules text: a move limited to a phase is written with the new `move.enemy.phase` phrase instead of `move.enemy`. A translation (an `IDescriptionLocalizer`) that does not supply it shows those moves in English, as "Split (Broken phase only): ...", and a test that compares an enemy's generated text sees the new wording.
- **Save format.** Saves made with 0.1.0-preview.2 still load; the format number stays 1, and their waiting blocks and listener records are matched by place, as before. Saves gain optional fields: `ScheduledSnapshot.Statements` and `BlockHash`, and `ListenerLimitSnapshot.ListenerHash` and `ListenerDueSnapshot.ListenerHash`. A save holding work scheduled by `Execute` carries those statements as text, which run when they come due, so treat saves as trusted input ([docs/stability.md](docs/stability.md#known-limitations)); such a save does not load in 0.1.0-preview.2. A save made with this release is refused if a later patch changed or removed its waiting block, where 0.1.0-preview.2 went by place alone and could run another block; a damaged save is refused before it changes anything. In Godot, a save made before a patch that added, renamed or removed a definition, verb or resource, which 0.1.0-preview.2 refused as `content_changed`, now loads unless it needs something the patch took away.
- **Same-seed results.** Unchanged for the same content, seed and inputs: the simulator plays the same seeds identically. Play differs only after a hot reload, where a used `once per` listener no longer fires again and an `on every` timer keeps its time. `ComputeHash`, and so the Godot node's `StateHash()`, gives different values from 0.1.0-preview.2 while a `next turn:`, `in N turns:` or `until` block is waiting, so a hash recorded with 0.1.0-preview.2 will not match there.

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
