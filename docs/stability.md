# Stability

Cantrip 1.0 makes its surface a promise, and this page is that promise. The [changelog](../CHANGELOG.md) is where every release says which of these it touched.

> These docs describe the `main` branch, which can be ahead of the latest release. The changelog's [Unreleased](../CHANGELOG.md#unreleased) section lists what that release lacks, and each release's own docs are in [its tag](https://github.com/AGomnes/Cantrip/tags).

The short form: **your code, your content and your saves keep working across every 1.x release.** What the same seed plays out as does not, and neither does anything this page does not name.

## What does not change without a 2.0

**The C# API of `Cantrip.Core`.** Every public type, member and signature in [the API reference](api/README.md). It is recorded in `src/Cantrip.Core/PublicAPI.Shipped.txt`, so this is checked rather than intended.

**The Godot addon's script surface.** The `CantripRuntime` node's methods, signals, signal arguments and exported properties; the keys of every dictionary it returns; and the *words inside* those dictionaries: `"cannot_afford"`, `"content_changed"`, `"enemy"`, `"before"`, `"info"`. A renamed key is invisible to GDScript until that line runs, so these are frozen as firmly as a method signature, and `Words`, `SaveCheck.NameOf` and `ChoiceAnswer.NameOf` exist so that renaming a C# member cannot quietly change one. The whole addon, `shared/` included, is in [the API reference](api/Cantrip.GodotAdapter.md).

**The language.** Declaration keywords, clause words, selector words, flag words, unit words, built-in verbs, event names, group and reserved names, zone names, ruleset settings and their values, target modes, and the defaults each of them carries. Content that loads and runs in 1.0 loads and runs in every 1.x.

**Saves.** A save made with any 1.x release loads in every later 1.x. `GameSnapshot.FormatVersion` says which format a save is in and only ever increases; `MinimumReader` says the oldest build that can be trusted with it; `GameSnapshot.ReaderNeededBy` answers "can this build read this?" before a restore, so a game can tell a player it needs a newer version rather than catch an exception. A save from a newer release is refused, naming the version that wrote it, rather than misread.

**What a diagnostic code means.** A code's meaning is fixed; a code is never re-used for something else, and a retired code stays retired (CT334 is one). Codes are compared as whole strings, case-insensitively: **the digits are part of the code, and leading zeros are never stripped.** Loading and parsing use four digits (`CT0001` to `CT0202`), the linter three (`CT301` upwards), the description builder three (`CT4xx`), and the Godot host `CT09xx`. `CT0301` and `CT301` are different codes and only one of them exists; a tool that matches codes with a pattern has to allow both widths. `--suppress` matches the whole string, so a code written at the wrong width suppresses nothing and says nothing about it.

## What may change in a 1.x release

**Same-seed results.** A release may change what the same content, seed and inputs produce, to fix a rule. Its changelog says so under **Same-seed results**. Store the Cantrip version with a replay, a daily seed or a bug report; a save records it for you as `GameSnapshot.WrittenBy`.

**The text of messages.** Diagnostic sentences, exception messages, `cantrip sim` reports and generated rules text may be reworded. Match on `Diagnostic.Code`, never on `Diagnostic.Message`.

**The severity of a diagnostic**, in the direction that makes a silent wrong answer loud: a note may become a warning, or a warning an error, when a release decides the mistake it names is worse than it looked. The changelog says so under **Breaking changes**, because a build running `--warnings-as-errors` stops.

**Performance and memory.** The figures below are measurements, not limits.

**Anything not public, and anything reached by reflection.** Internals move between releases. A game that reflects over Cantrip's types is outside the promise.

**Platforms not in the table below.** An untested platform is untested, not promised.

## What a 1.x release may add

- **A member on one of the four seams** (`IEffectHost`, `IGameClock`, `IChoiceProvider`, `IDescriptionLocalizer`), as long as it has a default implementation. An implementation written today keeps compiling and keeps working, and inherits the new member's default until it chooses to say something else. Removing a member, or adding one without a default, stays a 2.0 change. Giving an existing member a default later is itself additive, so nothing is frozen by a member deliberately having none: `IGameClock.Now`, `Advanced` and `Restore` have none because the only defaults available are a clock stuck at zero, an event that never fires and a restore that quietly keeps the wrong time.

  That rests on default interface members dispatching correctly at run time, which is checked rather than assumed. The unit tests implement each seam with the smallest type the compiler accepts and drive a battle through it, and the Godot headless suite does the same inside the engine a game ships on. **It is checked on .NET 9 and on Godot's .NET runtime, and nowhere else.** On an untested runtime, treat every seam member as one you must implement.
- **A member on an enum.** `ActionResult`, `SaveRejection`, `EntityKind`, `Team` and the rest may gain one. Switch on them with a `default` arm, and treat an unfamiliar word out of a dictionary as "something new" rather than as an error. The numeric values of the enums a save stores (`EntityKind`, `Team`, `EventPhase`, `ModifierLayer`, `StackingMode`, `ScheduleTiming`) are fixed, never renumbered and never re-used.
- **A diagnostic code.** New codes appear; existing ones keep their meaning. A build that runs `lint --warnings-as-errors` can therefore fail on a release that adds a warning. Pin the Cantrip version in CI if you would rather choose when that happens.
- **A field on `GameSnapshot`.** A new optional field bumps `FormatVersion` without moving `MinimumReader`, so older builds still read the save and simply miss what they never knew about.
- **A keyword, a verb, an event or a ruleset setting.** These are additions to the language, and the aim is always that content which works today keeps working. Where that cannot hold (a new keyword colliding with a name content already uses), the release makes it an **error at load time**, never a silent change in meaning. That is why `event` and `encounter`, which have no meaning yet, are CT0113 rather than quietly ignored.

## What your game has to do to stay inside the promise

This list is collected here because each item is a way to be broken by a release that kept every promise on this page.

- **Switch with a `default` arm.** On `ActionResult`, on a `reason` string, on `entity["kind"]`. A new member is additive for the library and a crash for an exhaustive `match`.
- **Compare codes, not messages**, and compare a code as a whole string.
- **Use the words the addon gives you**: `Words.ActionName`, `SaveCheck.NameOf`, `ChoiceAnswer.NameOf`. Hand-copying `"content_changed"` into ten GDScript files is how a frozen string drifts.
- **Do not rename or remove a shipped definition.** A save holds the names of the definitions behind its entities, and `CardRuntime.Restore` refuses a save that names one which has gone. See [Saves after a content update](#saves-after-a-content-update).
- **Keep the tick rate.** A real-time save records `GameSnapshot.ClockUnitsPerSecond`, and a restore into a clock at another rate is refused rather than re-timing every cooldown in it. Changing **Ticks Per Second** in a shipped game invalidates its saves, so treat that number as part of the game's data and not as a tuning knob.
- **Keep your own code inside the rules deterministic**, as [Determinism](#determinism) sets out, or the same-seed promise is not one Cantrip can keep for you.
- **Store the Cantrip version with anything you intend to reproduce**, because same-seed results are not promised across releases.
- **Do not reflect over Cantrip's types**, and do not depend on the order of anything the API does not say is ordered.
- **Treat a save as untrusted input** if it did not come from this machine. See [Known limitations](#known-limitations).

## How it is checked

- On every push to `main` and every pull request, on Linux and Windows x64: the unit tests, the content tests in `samples/`, `lint` on each sample folder with `--warnings-as-errors`, so that a new warning fails the build, and `cantrip sim` over all four sample scenarios: the roguelite's gauntlet in `samples/slice`, the fight with no cards in `samples/abilities`, the board in `samples/board` and the party in `samples/party`. That is 100 runs of each by each of the two default bots, which fail on any run that throws, any battle that reaches the turn limit and any expectation that does not hold.
- Among the unit tests, 100 seeded random battles are each played twice and compared step by step, and a saved and restored game is played beside the original, comparing state hashes after every step.
- Every change to Cantrip.Core's public C# API must be recorded in `src/Cantrip.Core/PublicAPI.Unshipped.txt`, or the build fails.
- [The API reference](api/README.md) is regenerated from the XML documentation comments on every push and the build fails if `docs/api/` disagrees (`tools/api-docs.sh --check`), so the reference for a frozen surface cannot drift from it. It covers `Cantrip.Core` and both halves of the Godot addon, `shared/` included, because the words in `shared/` are as frozen as the node's methods.
- Two whole games ([reference/](../reference) and [realtime/](../realtime)) are linted, tested and played headlessly on every push, in a console host and inside Godot, and the reference game is simulated too. They exist because a sample is built to demonstrate one feature and so tends to confirm what its author already believed.
- On every push, the reference game is published trimmed and (on Linux) with Native AOT, with the trim and AOT analysers on and warnings as errors, and the published binary then plays its eight seeds with a save and a restore in each (`tools/publish-check.sh`).
- On the same pushes and before every release, the [quickstart](quickstart.md) is followed word for word against freshly packed packages, and the Godot addon is installed from its zip into a blank Godot project and plays a battle from GDScript.
- On the same pushes, the editor dock runs its own self-test inside a headless engine: it loads the project's content, runs its `test` blocks, describes every definition, drives the debugger's two tabs, types into the Source tab, saves the file, parks the buffer, applies a suggested fix, follows a `.cantrip` file added and deleted outside the dock, checks that it indents with spaces at the buffer's own width, and prints what one check costs.
- A release publishes nothing until all of that has passed at the tagged commit.

## Platforms

| Platform | Status |
|---|---|
| Linux x64 and Windows x64, .NET 9 | Tested in CI |
| Godot 4.6.1 .NET on Linux x64 | Tested in CI: headless tests, a GDScript smoke test, the demo playing itself, the editor dock's self-test (which edits as well as reads) and installing the addon into a blank project, which targets `net8.0` |
| Godot 4.6.2 .NET on Windows x64 | Checked by hand, including what no automated test can drive: the editor under real typing, the dock in use, and the debugger's two tabs (a causality trace with pause and step, and a view of the entities in play) |
| Godot export to Linux x64 | Tested on demand (`.github/workflows/export.yml`): the exported demo plays itself |
| macOS, and ARM on any system | Untested |
| Godot exports to Windows, macOS, the web, Android and iOS | Untested. Godot's own limits on where a .NET game can be exported apply as well |
| MonoGame, FNA and other plain .NET engines | Untried. They call the library as the [quickstart](quickstart.md)'s console app does |
| Trimming and Native AOT, .NET 9 | Tested in CI: trimmed with `TrimMode=full`, and Native AOT on Linux x64. `Cantrip.Core` is marked `IsTrimmable` and produces no trim or AOT warnings. See [Publishing a game](#publishing-a-game) |
| Unity and IL2CPP | Untested. The core targets `netstandard2.1` so that the door is open, but nothing has walked through it, and the additive-seam promise above rests on a runtime feature Unity's runtimes may not have |
| .NET Framework | Not supported: the core targets `netstandard2.1` |

## Publishing a game

A shipped game is usually published trimmed, and sometimes with Native AOT, and a rules engine is the kind of library that dies there: reflection, `System.Text.Json` and dynamic dispatch are the usual casualties and this one has all three within reach. `tools/publish-check.sh` publishes [the reference game](../reference) both ways, with the trim and AOT analysers on and every trim and AOT warning an error, and then plays eight whole runs in what it built, saving each one part way and restoring it into a second game that has to agree hash for hash. On a Windows laptop a trimmed self-contained build is 21 MB and a Native AOT binary is 5.3 MB with no runtime beside it.

**The engine needs nothing from you.** `Cantrip.Core` carries `[AssemblyMetadata("IsTrimmable", "True")]`, so a published game's trimmer removes what its game does not use rather than keeping the whole assembly. Nothing in it reflects over its own types: the snapshot is plain data by design and the interpreter dispatches on syntax nodes rather than on names. Its only reflection is one read of its own version attribute, which is what stamps `GameSnapshot.WrittenBy`. The reference game checks that that read still answers in the published binary, because a trimmer taking it would leave every save from a shipped game with no record of what wrote it and nothing else would notice.

**Your serializer does.** Reflection-based `System.Text.Json` works in a normal build and throws `Reflection-based serialization has been disabled for this application` the first time a trimmed or AOT build saves. `GameSnapshot` is plain data so that a source-generated `JsonSerializerContext` handles it with no converters of its own; [reference/host/RunState.cs](../reference/host/RunState.cs) is the four lines, and [troubleshooting.md](troubleshooting.md#publishing-a-game) has them inline.

**Native AOT needs a platform linker**: the Desktop Development for C++ workload on Windows, `clang` and `zlib1g-dev` on Linux. Without one, `dotnet publish` reports `Platform linker not found` after compiling everything, and `tools/publish-check.sh` skips that half loudly rather than failing.

## Performance

Measured on a Windows laptop (an Intel Core Ultra 5 125U, plugged in, .NET 9), last taken for 1.0. `cantrip sim` plays 500 runs of the sample roguelite's gauntlet with the cautious bot in about 9 seconds, or 18 ms per run; repeating it gives anything from about 9 to 10 seconds. Every figure here is a rough one rather than a benchmark. From the repository root:

```
dotnet run --project src/Cantrip.Cli -c Release -- sim samples/slice --runs 500 --bot cautious
```

The bot's table gives the time. Each run is four battles, and before every play the bot tries each legal play, ends the turn to score the result, and restores a snapshot of the game. Those trials are most of the work: over 200 runs the engine raises about 1.18 million events inside them against 91 thousand in the play that counted. Two bots play by default, which costs about twice as long (about 18 seconds of bot time for the same 500 runs), and the patient bot is slower again at about 21 ms per run; `--bot random` tries nothing and finishes the same 500 runs in about a second and a half.

In the Godot dock, one check after a pause in typing reads, loads and lints every content file, which is what the Source tab, the Problems tab, the Tests tab and the preview all read. That check costs about 8 ms on the largest arrangement this repository can make, 23 files and 1,997 lines (`samples/corpus` beside the demo's own content), and about 2 ms on the demo's five files. Reading every file afresh, which is what the first check after the dock opens does, costs roughly twice that. The self-test prints both numbers for whatever content the project holds, on every CI run, so a project whose checks have become slow says so.

Nothing yet measures memory or allocations.

## Determinism

Within one version of Cantrip, the same content, seed and inputs produce the same game on every machine: fixed-point maths, a seeded generator and explicit orderings, with no floating point or hash-order dependence in the rules. CI runs the same tests and simulated games on Linux and Windows x64, and the content tests assert exact numbers on both. Nothing yet compares the two platforms' state hashes directly, and macOS and ARM are untested. A difference between machines is a bug, and the most important kind to report.

The promise does not cross versions. A save records the version for you, as `GameSnapshot.WrittenBy`, alongside `RngGenerator`, the name of the generator its random state came from: a release that changes the generator changes that name, and a build without it refuses the save rather than continuing someone else's random numbers.

To save a generator, save `Rng.GetState()`. `Rng.Seed` is a label rather than a position: it names where a run started, not where it has got to, and it is `null` in a generator that was restored from a state. So a game that stores it and later calls `new Rng(seed)` starts the stream again from the beginning rather than continuing it.

It also holds only if your own code inside the rules is deterministic. Verbs your game registers in C# (`RegisterVerb`), and the names and functions it supplies (`IEffectHost.TryResolveName` and `TryCall`, or the Godot node's `RegisterName` and `RegisterFunction`), run in the middle of effects. They must not:

- use floating-point arithmetic, which may round differently on another machine;
- use `System.Random`, or GDScript's `randi()` and `randf()`;
- read the clock, files or anything else outside the game;
- depend on the order of a `Dictionary` or `HashSet`;
- keep state in fields of their own. Keep it in stats: restoring a save, or rolling an action back while the player makes a choice, undoes only what is in the game state, and a rolled-back action runs again once the choice is answered.

A verb that needs a random number draws it from the game's own generator, `call.State.Rng`, which is saved and restored with the game. A name or function must only answer, and never change anything, not even by drawing from that generator: rules text with live numbers asks it too, as often as the game redraws that text.

## Numbers

Every number in the rules is fixed-point, with six decimal places, stored in 64 bits. Arithmetic is correct while values stay within plus or minus 1 million, far beyond ordinary hp, damage and costs. Past that nothing checks it: a sum or product beyond about plus or minus 9.2 trillion wraps round to a wrong value (`4000000 * 4000000` comes out as a large negative number), and dividing by a value above about 9.2 million can give a wrong answer. Neither raises an error or a warning, so a game whose numbers can grow that large, such as a score that multiplies, must keep them in range itself. From C#, `Entity.GetInt` rounds to an `int` and so stops at about plus or minus 2.1 billion; `Entity.Get` returns the full value.

`Num` prints as itself by default (invariant, with trailing zeros trimmed, which is the form `Num.Parse` reads back) and honours a format string when it is given one, so `$"{damage:F2}"` is `5.00` and `$"{damage}"` is `5`. A format is applied to the exact value as a `decimal` and never through `ToDouble`, so the text is the number rather than a rounding of it, and it is the same text on every machine.

## Saves after a content update

A save holds each entity's stats and the names of the definitions behind them, not the rules themselves. So a save made before a content patch:

- **needs every definition it names.** If one has been renamed or removed, `CardRuntime.Restore` refuses the save with an `InvalidOperationException` naming it, and the game in progress carries on untouched.
- **keeps its saved stats.** A card whose cost the patch changed keeps its old cost in the restored game, while its effect is the new one.
- **runs work that was waiting as it was written.** A `next turn:` or `in N turns:` block waiting in the save is found by its statements, so the patch may reformat it or move it within its definition. If the patch changed its statements or removed it, `Restore` refuses the save, naming the definition. Work that `Execute` scheduled is saved with its statements, so a patch never turns it away.
- **keeps each listener's limit with that listener.** Which turn or battle a `once per` listener last fired in, and when an `on every` listener is next due, are found by the listener's place and a hash of it, so the patch may add, remove, reorder or reformat `on` blocks, and change what a listener does while it keeps its place. A listener whose `on` line changed, or whose body changed as it moved, starts afresh, as a new listener would: a `once per battle` one changed like that can fire once more in the battle that was saved.
- **is played at the rate it was written at.** A save from a real-time game records `ClockUnitsPerSecond`, and a restore into a clock at another rate is refused, because every cooldown, every `for 3s` and every `on every 2s` in it would otherwise mean a different length of time. In Godot that refusal arrives as the reason `"clock_changed"`, which is not `"content_changed"`: nothing about the content is wrong.

`ContentLibrary.Fingerprint` changes when a definition, verb or resource is added, renamed or removed, but not when numbers or effects change: store it with each save and compare it before restoring. The Godot node compares it too, but a different fingerprint alone does not make it refuse a save: `LoadSave` goes on to restore it, and refuses it as `content_changed`, with `Restore`'s message, only when `Restore` does, so a patch that only adds a card keeps older saves loading. To keep saves working across patches, do not rename or remove a shipped definition, and to change what a `once per` listener does without letting it fire again where it has already fired, change only its body, in a patch that leaves the `on` blocks above it as they are. [Save and load](csharp.md#save-and-load) in the C# guide has the details.

## Known limitations

This list is kept current with each release.

- **Real time has no simulator and no spatial questions of its own.** [realtime/](../realtime) is a whole game on the tick clock, and [csharp.md](csharp.md#real-time) and [godot.md](godot.md#real-time) describe it, but two things a real-time game might look for are not there. `cantrip sim` cannot play one and refuses to try, because when to act in continuous time is the game's own frame loop and not a bot's, so a real-time game is covered by `test` blocks and by its own host. And space on the tick clock is the same board it is on the turn clock: `within(x, 2)` counts slots and the engine answers it, while `within(x, 5m)` is a question about a world the engine knows nothing about and still needs your game to answer it through `IEffectHost.TryCall`.
- **A real-time game cannot gate an action on a resource.** A cooldown is the only limiter an ability has: `cost` on an `ability` is refused at lint (CT339) because nothing spends it, and the resources that reset on `turn_start` do not reset in a game with no turns. Content can still keep a resource of its own and refill it from an `on every <n>s:` listener, and read it in an effect, but nothing in the engine will stop an ability being used because the pool is empty. Mana, rage, heat and a charge that builds are all written that way or not at all.
- **A speed control is the game's own loop.** `Engine.time_scale` does not speed a Cantrip game up, because the tick driver ignores the frame delta on purpose. A fixed step is fixed. A game that wants fast-forward, slow motion or a paused-but-drawing state calls `Tick(count)` itself, which is documented under [Running faster or slower](godot.md#running-faster-or-slower). That is the only speed control there is, and the tick rate is not a second one: it is part of a save.
- **A refused action does not say which `targetable` rule stopped it.** The three refusals a game acts on differently are told apart: `OutOfRange` for reach, `NoTarget` for a side with nobody on it, and `InvalidTarget` for somebody the action will not take. What `InvalidTarget` does not say is *which* rule refused: the `where` on the action's own `target` line, or a taunt or stealth on the `targetable` channel. `LegalTargets` gives the list to compare against, and [troubleshooting.md](troubleshooting.md) has how to tell those two apart, but the engine does not. A `WhyNotTargetable` would be additive, so it can arrive in a 1.x release.
- **A run of battles is your game's code.** The language has no run structure your game can play: the map, encounters and rewards live in the game, and `event` and `encounter` declarations are an error (CT0113). One runtime can play several battles in a row, as [Winning, losing and several battles](csharp.md#winning-losing-and-several-battles) in the C# guide describes. A `scenario` states a run (a deck, some fights in order and what happens between them) but only for `cantrip sim` to play: nothing above the fights it names is simulated, so there is no map, reward offer, shop or gold in it, and nothing reads a scenario at game time.
- **A listener cannot be shared between declarations.** Two enemies that should behave alike carry the same lines twice, and nothing notices when one copy drifts from the other.
- **The Godot addon has had little use.** It has been installed only by its author and by an automated test. The dock's self-test drives the editor headlessly, but what a person does with a mouse and a keyboard is checked only by hand: real typing, the **Apply fix** button, the two buttons on the line that appears when a file changes under an unsaved buffer, and a block pasted or dropped in from another window. So are the debugger's two tabs for a running game: a causality trace with pause and step, and a view of the entities in play.
- **The editor writes content; it does not know its way around it.** The Source tab writes, saves and lints as you type, and offers the fix a diagnostic suggests, but it has no completion, no go to definition, no find references and no rename. It also never writes a tab, and indents at the width the buffer already uses, because the language decides where a block ends by how deep a line is and counts a tab as four columns. So a project that wants its `.cantrip` files indented with tabs cannot have that from the dock.
- **Unsaved buffers do not survive a C# build.** Building the project reloads the addon, and the dock's buffers go with it. Nothing is written to disk without being asked, so that work is lost; the Output panel says how many buffers went. Save before you build.
- **The editor's debugger has no breakpoints, reload or console.** The running game accepts breakpoints on an event or a line of content, a reload of changed files, and statements to run, and headless tests cover them, but the editor has no controls for any of them yet. Saving a `.cantrip` file does not reach a running game: call `ReloadContent([])` from a debug key, as [Hot reload](godot.md#hot-reload) in the Godot guide shows.
- **A hot reload can hold up saving.** After a reload that changes a waiting `next turn:` or `in N turns:` block, `CanCapture` is false, and in Godot so is `CanSave()`, until that block has run.
- **A mistyped block line only warns.** Inside a declaration, a line ending in `:` that is not a block Cantrip runs, such as `when card_played:` for `on card_played:`, still loads as a label and does nothing. `lint` warns about it (CT313) but exits 0 unless given `--warnings-as-errors`, and `validate` does not report it, so cover anything that must happen with a `test`.
- **A save is trusted input.** It sets every stat, and it holds the text of work that `Execute` scheduled, which the game runs when that work comes due, so an edited save can make a game do anything its content and C# verbs can. The hashes in a save recognise content; they do not show that the save is unaltered. A game that loads saves it did not write itself, such as shared, downloaded or cloud saves, should sign them or verify them another way.
- **An error in content leaves its action half done.** A runtime error while an action resolves, including going past the step limit, reaches your game as a `RuntimeError` exception. What the action changed before the error stays changed, and the work it had queued is dropped. To recover, restore a snapshot taken before the action, as [When content fails at runtime](csharp.md#when-content-fails-at-runtime) shows.
- **Numbers past plus or minus 1 million are unchecked**, as described under [Numbers](#numbers).
- **Content tests cover one battle, without your game.** A `test` cannot start a second battle, so it cannot show that something resets between battles, and it cannot check that a play was refused. A `scenario` does fight several battles in a row, but its `expect` can only compare `stalls`, `errors`, `wins`, `hp_left` or `turns` over many runs, so it cannot assert what any one of those battles did. The `cantrip` tool and the Godot dock's Tests tab run tests without the verbs, names and functions your game supplies, so a test of content that uses one fails there. A C# game can run such tests from its own test suite, as [Verbs written in C#](csharp.md#verbs-written-in-c) shows; a game that supplies them only from GDScript has nowhere to run such `test` blocks yet.
- **What `cantrip sim` proves is bounded by its bots.** A scenario's `expect no stalls` and `expect no errors` are checked, because they hold whoever plays; `expect wins`, `expect hp_left` and `expect turns` are reported as not checked, because a level is a fact about the bot. Every bot weighs a position the same way: the player's hp against the enemies'. So all of them are wrong in the same direction about a card that draws and about anything that pays off several turns later, and two of them agreeing is not evidence. There is no option for how far a bot looks ahead and no way to tell it what a status is worth, and a `choose` or `discover` part way through an effect is answered at random and reported rather than judged. "Never playable" and "never fired" mean only that these bots never reached it. [Simulating](simulating.md#what-it-will-not-tell-you) has the full list.
- **Neither `sim` nor the REPL has a tab in Godot.** The dock's Tests tab runs `test` blocks only; it neither counts nor plays a `scenario`. The addon highlights the keyword, and the content loads with no error, but `dotnet cantrip sim` from the command line is the only way to play one, and `dotnet cantrip repl` the only way to try a statement against a live game. A Godot project still installs the tool for those two.
- **No editor support outside Godot.** There is no syntax highlighting or language server for text editors yet. In Godot the dock's Source tab writes content highlighted and lints it as you type; elsewhere `dotnet cantrip lint` does the checking from the command line.
- **A `transform` cannot be put back.** Nothing remembers the old form, so content that wants a round trip stores `event.was` from the `transformed` event itself, and a form change that should expire is a status with modifiers and a duration rather than a `transform`. For the same reason a `transform` inside an `until` block is refused, at lint (CT321) and at run: `until` undoes what it did, and this cannot be undone. A *place* is the other case: two integers whose inverse is exact, so `until turn_end: self.rank = 0` is reverted rather than refused.
- **`copy` duplicates a state, not an effect.** It brings across live stats, runtime tags and a fresh instance of every status and keyword, which is what "the card as it is now" means. Nothing can copy or switch off another entity's *effects*, so Hearthstone's Silence and Balatro's Blueprint are still out of reach; [coverage.md](coverage.md) has that as gap 5.
- **Some mechanics cannot be expressed yet**, among them a Magic-style priority window and grouping played cards into poker hands. [coverage.md](coverage.md) keeps the list.

## Reporting a problem

Use the [issue forms](https://github.com/AGomnes/Cantrip/issues/new/choose), with the smallest `.cantrip` file that shows the problem, what you expected, and what happened. `dotnet cantrip --version` names the exact build; in a Godot project without the tool, give the version in `addons/cantrip/plugin.cfg` and the Cantrip.Core version in your `.csproj`. For a problem with the rules, a failing `test` block is the best report there is: it is both the description and the proof.
