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

Same-seed results are not on that list. A 1.x release may change what the same content, seed and inputs produce, to fix a rule, and its changelog says so under **Same-seed results**.

## How it is checked

- On every push to `main` and every pull request, on Linux and Windows x64: the unit tests, the content tests in `samples/`, `lint` on each sample folder with `--warnings-as-errors`, so that a new warning fails the build, and `cantrip sim` over both sample scenarios — the roguelite's gauntlet in `samples/slice` and the fight with no cards in `samples/abilities` — 100 runs of each by each of the two default bots, which fail on any run that throws, any battle that reaches the turn limit and any expectation that does not hold.
- Among the unit tests, 100 seeded random battles are each played twice and compared step by step, and a saved and restored game is played beside the original, comparing state hashes after every step.
- Every change to Cantrip.Core's public C# API must be recorded in `src/Cantrip.Core/PublicAPI.Unshipped.txt`, or the build fails.
- On the same pushes and before every release, the [quickstart](quickstart.md) is followed word for word against freshly packed packages, and the Godot addon is installed from its zip into a blank Godot project and plays a battle from GDScript.
- On the same pushes, the editor dock runs its own self-test inside a headless engine: it loads the project's content, runs its `test` blocks, describes every definition, drives the debugger's two tabs — and, since 0.1.0-preview.5, types into the Source tab, saves the file, parks the buffer, applies a suggested fix, follows a `.cantrip` file added and deleted outside the dock, checks that it indents with spaces at the buffer's own width, and prints what one check costs.
- A release publishes nothing until all of that has passed at the tagged commit.

## Platforms

| Platform | Status |
|---|---|
| Linux x64 and Windows x64, .NET 9 | Tested in CI |
| Godot 4.6.1 .NET on Linux x64 | Tested in CI: headless tests, a GDScript smoke test, the demo playing itself, the editor dock's self-test — which now edits as well as reads — and installing the addon into a blank project, which targets `net8.0` |
| Godot 4.6.2 .NET on Windows x64 | Checked by hand, including what no automated test can drive: the editor under real typing, the dock in use, and the debugger's two tabs (a causality trace with pause and step, and a view of the entities in play) |
| Godot export to Linux x64 | Tested on demand (`.github/workflows/export.yml`): the exported demo plays itself |
| macOS, and ARM on any system | Untested |
| Godot exports to Windows, macOS, the web, Android and iOS | Untested. Godot's own limits on where a .NET game can be exported apply as well |
| MonoGame, FNA and other plain .NET engines | Untried. They call the library as the [quickstart](quickstart.md)'s console app does |
| NativeAOT, trimming, Unity and IL2CPP | Untested |
| .NET Framework | Not supported: the core targets `netstandard2.1` |

## Performance

Measured again for 0.1.0-preview.5, on the same Windows laptop as before (an Intel Core Ultra 5 125U, plugged in, .NET 9). `cantrip sim` plays 500 runs of the sample roguelite's gauntlet with the cautious bot in about 8 seconds, or 16 ms per run; repeating it gives anything from about 8 to 10 seconds. That is faster than the 14 seconds this page gave for 0.1.0-preview.4, and this release is not the reason: 0.1.0-preview.4's own published tool runs the same command on the same laptop in the same 8 seconds and prints byte-identical output. Nothing in the engine changed. Every figure here is a rough one rather than a benchmark, and that is what a rough figure looks like when it is taken again. From the repository root:

```
dotnet run --project src/Cantrip.Cli -c Release -- sim samples/slice --runs 500 --bot cautious
```

The bot's table gives the time. Each run is four battles, and before every play the bot tries each legal play, ends the turn to score the result, and restores a snapshot of the game. Those trials are most of the work: over 200 runs the engine raises about 1.18 million events inside them against 91 thousand in the play that counted, thirteen times as many. Two bots play by default, which costs about twice as long — about 18 seconds of bot time for the same 500 runs — and the patient bot is slower again at about 21 ms per run; `--bot random` tries nothing and finishes the same 500 runs in about a second and a half.

In the Godot dock, one check after a pause in typing — every content file read, loaded and linted, which is what the Source tab, the Problems tab, the Tests tab and the preview all read — costs about 8 ms on the largest arrangement this repository can make, 23 files and 1,887 lines (`samples/corpus` beside the demo's own content), and about 2 ms on the demo's five files. Reading every file afresh, which is what the first check after the dock opens does, costs roughly twice that. The self-test prints both numbers for whatever content the project holds, on every CI run, so a project whose checks have become slow says so.

Nothing yet measures memory or allocations.

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

- **needs every definition it names.** If one has been renamed or removed, `CardRuntime.Restore` refuses the save with an `InvalidOperationException` naming it, and the game in progress carries on untouched.
- **keeps its saved stats.** A card whose cost the patch changed keeps its old cost in the restored game, while its effect is the new one.
- **runs work that was waiting as it was written.** A `next turn:` or `in N turns:` block waiting in the save is found by its statements, so the patch may reformat it or move it within its definition. If the patch changed its statements or removed it, `Restore` refuses the save, naming the definition. Work that `Execute` scheduled is saved with its statements, so a patch never turns it away. Saves made by 0.1.0-preview.2 or earlier find a block by its place alone, so after a patch that moves it they fail to load or run another block.
- **keeps each listener's limit with that listener.** Which turn or battle a `once per` listener last fired in, and when an `on every` listener is next due, are found by the listener's place and a hash of it, so the patch may add, remove, reorder or reformat `on` blocks, and change what a listener does while it keeps its place. A listener whose `on` line changed, or whose body changed as it moved, starts afresh, as a new listener would: a `once per battle` one changed like that can fire once more in the battle that was saved. Saves made by 0.1.0-preview.2 or earlier record the place alone, so after a patch that adds, removes or reorders `on` blocks they can give a limit to another listener.

`ContentLibrary.Fingerprint` changes when a definition, verb or resource is added, renamed or removed, but not when numbers or effects change: store it with each save and compare it before restoring. The Godot node compares it too, but a different fingerprint alone does not make it refuse a save: `LoadSave` goes on to restore it, and refuses it as `content_changed`, with `Restore`'s message, only when `Restore` does, so a patch that only adds a card keeps older saves loading. To keep saves working across patches, do not rename or remove a shipped definition, and to change what a `once per` listener does without letting it fire again where it has already fired, change only its body, in a patch that leaves the `on` blocks above it as they are. [Save and load](csharp.md#save-and-load) in the C# guide has the details.

## Known limitations

This list is kept current with each release. It was last checked for 0.1.0-preview.5.

- **Real time is experimental.** The tick clock and abilities work and are tested, but no real-time game has been built with them, and spatial queries such as `within` need your game to supply space.
- **A run of battles is your game's code.** The language has no run structure your game can play: the map, encounters and rewards live in the game, and `event` and `encounter` declarations are an error (CT0113). One runtime can play several battles in a row, as [Winning, losing and several battles](csharp.md#winning-losing-and-several-battles) in the C# guide describes. A `scenario` states a run — a deck, some fights in order and what happens between them — but only for `cantrip sim` to play: nothing above the fights it names is simulated, so there is no map, reward offer, shop or gold in it, and nothing reads a scenario at game time.
- **The Godot addon has had little use.** It has been installed only by its author and by an automated test. The dock's self-test drives the editor headlessly, but what a person does with a mouse and a keyboard is checked only by hand: real typing, the **Apply fix** button, the two buttons on the line that appears when a file changes under an unsaved buffer, and a block pasted or dropped in from another window. So are the debugger's two tabs for a running game: a causality trace with pause and step, and a view of the entities in play.
- **The editor writes content; it does not know its way around it.** The Source tab writes, saves and lints as you type, and offers the fix a diagnostic suggests, but it has no completion, no go to definition, no find references and no rename. It also never writes a tab, and indents at the width the buffer already uses, because the language decides where a block ends by how deep a line is and counts a tab as four columns — so a project that wants its `.cantrip` files indented with tabs cannot have that from the dock.
- **Unsaved buffers do not survive a C# build.** Building the project reloads the addon, and the dock's buffers go with it. Nothing is written to disk without being asked, so that work is lost; the Output panel says how many buffers went. Save before you build.
- **The editor's debugger has no breakpoints, reload or console.** The running game accepts breakpoints on an event or a line of content, a reload of changed files, and statements to run, and headless tests cover them, but the editor has no controls for any of them yet. Saving a `.cantrip` file does not reach a running game: call `ReloadContent([])` from a debug key, as [Hot reload](godot.md#hot-reload) in the Godot guide shows.
- **In Godot, starting the next battle too soon can lose a `BattleEnded`.** If an `EffectEvent` handler starts the next battle, for example on the `battle_end` event, `BattleEnded` is never emitted for the battle that just ended. If the `BattleEnded` handler starts it, and the first action after that handler returns ends the new battle, `BattleEnded` is not emitted for the new battle. Start the next battle after the handler has returned, from your reward screen or with `call_deferred`.
- **A hot reload can hold up saving.** After a reload that changes a waiting `next turn:` or `in N turns:` block, `CanCapture` is false, and in Godot so is `CanSave()`, until that block has run.
- **A mistyped block line only warns.** Inside a declaration, a line ending in `:` that is not a block Cantrip runs, such as `when card_played:` for `on card_played:`, still loads as a label and does nothing. `lint` warns about it (CT313) but exits 0 unless given `--warnings-as-errors`, and `validate` does not report it, so cover anything that must happen with a `test`.
- **A save is trusted input.** It sets every stat, and it holds the text of work that `Execute` scheduled, which the game runs when that work comes due, so an edited save can make a game do anything its content and C# verbs can. The hashes in a save recognise content; they do not show that the save is unaltered. A game that loads saves it did not write itself, such as shared, downloaded or cloud saves, should sign them or verify them another way.
- **An error in content leaves its action half done.** A runtime error while an action resolves, including going past the step limit, reaches your game as a `RuntimeError` exception. What the action changed before the error stays changed, and the work it had queued is dropped. To recover, restore a snapshot taken before the action, as [When content fails at runtime](csharp.md#when-content-fails-at-runtime) shows.
- **Numbers past ±1 million are unchecked**, as described under [Numbers](#numbers).
- **Content tests cover one battle, without your game.** A `test` cannot start a second battle, so it cannot show that something resets between battles, and it cannot check that a play was refused. A `scenario` does fight several battles in a row, but its `expect` can only compare `stalls`, `errors`, `wins`, `hp_left` or `turns` over many runs, so it cannot assert what any one of those battles did. The `cantrip` tool and the Godot dock's Tests tab run tests without the verbs, names and functions your game supplies, so a test of content that uses one fails there. A C# game can run such tests from its own test suite, as [Verbs written in C#](csharp.md#verbs-written-in-c) shows; a game that supplies them only from GDScript has nowhere to run such `test` blocks yet.
- **What `cantrip sim` proves is bounded by its bots.** A scenario's `expect no stalls` and `expect no errors` are checked, because they hold whoever plays; `expect wins`, `expect hp_left` and `expect turns` are reported as not checked, because a level is a fact about the bot. Every bot weighs a position the same way — the player's hp against the enemies' — so all of them are wrong in the same direction about a card that draws and about anything that pays off several turns later, and two of them agreeing is not evidence. There is no option for how far a bot looks ahead and no way to tell it what a status is worth, and a `choose` or `discover` part way through an effect is answered at random and reported rather than judged. "Never playable" and "never fired" mean only that these bots never reached it. [Simulating](simulating.md#what-it-will-not-tell-you) has the full list.
- **Neither `sim` nor the REPL has a tab in Godot.** The dock's Tests tab runs `test` blocks only; it neither counts nor plays a `scenario`. The addon highlights the keyword, and the content loads with no error, but `dotnet cantrip sim` from the command line is the only way to play one, and `dotnet cantrip repl` the only way to try a statement against a live game. A Godot project still installs the tool for those two.
- **No editor support outside Godot.** There is no syntax highlighting or language server for text editors yet. In Godot the dock's Source tab writes content highlighted and lints it as you type; elsewhere `dotnet cantrip lint` does the checking from the command line.
- **A `transform` cannot be put back.** Nothing remembers the old form, so content that wants a round trip stores `event.was` from the `transformed` event itself, and a form change that should expire is a status with modifiers and a duration rather than a `transform`. For the same reason a `transform` inside an `until` block is refused, at lint (CT321) and at run: `until` undoes what it did, and this cannot be undone.
- **`copy` duplicates a state, not an effect.** It brings across live stats, runtime tags and a fresh instance of every status and keyword, which is what "the card as it is now" means. Nothing can copy or switch off another entity's *effects*, so Hearthstone's Silence and Balatro's Blueprint are still out of reach; [coverage.md](coverage.md) has that as gap 5.
- **Some mechanics cannot be expressed yet**, among them a Magic-style priority window and grouping played cards into poker hands. [coverage.md](coverage.md) keeps the list.

## Reporting a problem

Use the [issue forms](https://github.com/AGomnes/Cantrip/issues/new/choose), with the smallest `.cantrip` file that shows the problem, what you expected, and what happened. `dotnet cantrip --version` names the exact build; in a Godot project without the tool, give the version in `addons/cantrip/plugin.cfg` and the Cantrip.Core version in your `.csproj`. For a problem with the rules, a failing `test` block is the best report there is: it is both the description and the proof.
