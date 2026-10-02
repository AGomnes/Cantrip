# Troubleshooting

What goes wrong, what it means, and what to do about it. Most of this page comes from two people
building whole games on Cantrip from the published docs alone and writing down every place they
got stuck: [reference/FINDINGS.md](../reference/FINDINGS.md) and
[realtime/FINDINGS.md](../realtime/FINDINGS.md). Nearly everything they hit has been fixed. What is
left is the part that cannot be fixed, only explained. This page explains it.

If you are looking for what a call does rather than why it went wrong, the
[API reference](api/README.md) lists every public type and member.

**Contents**

- [How to read an error](#how-to-read-an-error)
- [It will not load](#it-will-not-load)
- [It loaded, it ran, and it did the wrong thing](#it-loaded-it-ran-and-it-did-the-wrong-thing)
- [A card or ability will not play](#a-card-or-ability-will-not-play)
- [The fight never ends](#the-fight-never-ends)
- [Saves](#saves)
- [Godot](#godot)
- [Publishing a game](#publishing-a-game)
- [Still stuck](#still-stuck)

---

## How to read an error

There are three kinds, and they come from three different places.

### A diagnostic

Everything `lint`, `validate`, `test` and the Godot dock report about content that has not run yet:

```
content/cards.cantrip:10:5: error CT301: Unknown verb `dealt`. Did you mean `deal`?
```

`file:line:column`, then the severity, then the code, then what to do. Every code is in
[language.md's diagnostics table](language.md#diagnostics), with what it means and how to fix it.
Some carry a suggestion the Godot dock's **Apply fix** button can write for you.

The severities are not decoration:

| | Means |
|---|---|
| **error** | The content is wrong. `lint`, `validate` and `test` all exit 1. |
| **warning** | Almost certainly not what you meant, but it runs. Exit 0 unless `--warnings-as-errors`. |
| **note** | Worth knowing. Never fails anything. |

**Run `lint` with `--warnings-as-errors` in CI.** A warning nobody fails on is a warning everybody
scrolls past; every sample folder in this repository is clean under it, which is what lets a new
warning mean something.

### A runtime error

Content that loaded and then could not be run raises one. In a `test` it fails the test:

```
content/cards.cantrip:5:25: runtime error: `0` has no property `name`.
```

In C# it is a `RuntimeError` exception, with the same text as its `Message`, plus a `Span` of the
line it was on and a `Detail` of the message without the line. In Godot it reaches
`EffectError(message)` and stops the action.

**What the action already did stays done.** A runtime error part way through an effect leaves what
it changed changed, and drops the work it had queued. To recover properly, restore a snapshot
taken before the action. [When content fails at runtime](csharp.md#when-content-fails-at-runtime)
has the pattern.

### A trace

When a test fails and you cannot see why, `--trace` prints the causality tree: every verb that
ran, every event it raised, and every `log` line, nested by what caused what.

```
$ dotnet cantrip test content --trace
  FAIL a fight
       content/c.cantrip:17:3: expected Ghost.hp == 0, but Ghost.hp was 5
       | [verb] player  @ content/c.cantrip:14:3
       | [verb] enemy  @ content/c.cantrip:15:3
       |   [event] created {source=Ghost#2, target=Ghost#2}
       | [event] battle_start {source=Player#1}
       | [event] turn_start {source=Player#1, target=Player#1}
       | [verb] play  @ content/c.cantrip:16:3
       |   [event] card_played {source=Player#1, target=Ghost#2, card=Zap#3, amount=1}
       |     [verb] deal  @ content/c.cantrip:5:5
       | [verb] expect  @ content/c.cantrip:17:3
```

Read it for what is **missing**. Above, `deal` ran and no `damaged` event came out of it, which is
the whole answer: the amount was zero. A listener that did not fire is not in the tree; a listener
that fired twice is in it twice, indented under whatever caused each one.

`#2` and `#3` are entity ids, and they are stable across a save and restore, so an id in a trace is
the same actor as that id in a save file or a bug report.

From C#, `runtime.State.Trace.FormatTree()` is the same text, and `ComputeHash()` beside it is how
two games are compared. [What a battle screen reads](csharp.md#what-a-battle-screen-reads) has the call.

---

## It will not load

### The first five diagnostics, and what they really mean

These are the ones a new project meets, in roughly the order it meets them.

| Code | What it says | What to do |
|---|---|---|
| **CT0029** | `A name with spaces must be in quotes: card "Ice Shard".` | Quote it. A bare name is one word. |
| **CT301** | `Unknown verb `dealt`. Did you mean `deal`?` | Spell it right, or, if your game registers the verb in C#, pass `--suppress CT301` so the tool stops reporting the verbs it cannot see. |
| **CT302** | `Nothing called `Ghost` is defined.` | The name is misspelled, or the file that declares it is not in the folder the tool was pointed at. `cantrip lint` takes several paths. |
| **CT303** | A tag nothing has. | A `tag:frost` that matches nothing is almost always a typo in one of the two places. |
| **CT0010** | `Expected ')' but found ,` | Usually a verb called like a function. Verbs are commands: `hold leader 1`, not `hold(leader, 1)`. Functions are called with parentheses; verbs are not. |

### Indentation

The language decides where a block ends by how deep a line is, and counts a tab as four columns. A
line indented under a plain statement, or a dedent that lines up with no block, is one error that
says so, and the rest of the declaration still loads. Fix the first one and re-run rather than
reading a cascade.

The Godot dock never writes a tab, and indents at the width the buffer already uses.

### The tool and the game disagree about what loads

`cantrip lint` loads what you point it at. Your game loads what it points itself at. When those
differ, a folder lints clean and the game fails, or the reverse.

- A **C# game** loads what `ContentLibrary.LoadFolder` was given. Compare that path with the one
  you lint.
- A **Godot game** loads `res://content` unless the node's `ContentFolder` says otherwise, and only
  files Godot has *imported*. See [The content is not there](#the-content-is-not-there).
- **Verbs, names and functions your game supplies in C#** do not exist for the tool. It reports
  them as CT301, CT302 or CT304. `--suppress CT301,CT302` is the answer, and
  [Verbs written in C#](csharp.md#verbs-written-in-c) is the fuller one.

---

## It loaded, it ran, and it did the wrong thing

This is the dangerous class, and the one most of Cantrip's diagnostics exist to prevent. If the
rules did something you did not write, look here before looking for an engine bug.

### `player` in a party game

`player` is **one entity**: the party's leader, the one `CreatePlayer` made, the one that holds the
run's relics and gold. It is never "whoever is acting" and never "all of you". In a party game,
`deal 5 to player` inside an enemy move hits the leader however carefully the enemy telegraphed
somebody else.

Content that declares a `hero` is refused at lint for exactly that: **CT326**, an error, wherever a
member could be meant, which is **every body on every declaration** (an `effect`, a `move`, a
listener, a `modify` line and a `target … where` filter). A hero's own listener is the one that
catches people: `hero "Cleric" / on damaged: block 2 to player` blocks the *leader*, not the
Cleric. The message names the word that does what was meant: `owner` for something carried, `self`
in a `hero` or an `actor`, `target` for a card, an ability or an enemy. `leader` says the run's own
actor where that really is meant, and `party` says all of them. See
[The party](language.md#the-party).

`player` stays legal, and stays right, in a carried declaration's listener that is **about its own
owner** (`on owner.turn_start:`, or a run-level event such as `on battle_start:`), and in a
`verb`, which has no owner for the diagnostic to name a word from, and in tests, scenarios, and
your game's own C#.

### `source` is not `event.source`

Inside a listener, `source` is the thing whose listener this is. `event.source` is what caused the
event. They are different words for different things and both are often in scope, which is why a
retaliation card written with `source` hits itself.

### `modify <channel> of <group>` names whose value it is

An `of` group names whoever the value **belongs to**, not who is being acted on. On `damage` the
value belongs to the dealer; on `cooldown` it belongs to whoever is waiting for the ability. If a
modifier is reaching the wrong half of the fight, this is why.

### A line that looks like a block and is not

Inside a declaration, a line ending in `:` that Cantrip does not run (`when card_played:` for
`on card_played:`) loads as a label and does nothing at all. `lint` reports it as **CT313**, a
warning, so it passes unless you use `--warnings-as-errors`. Anything that must happen deserves a
`test`.

### A clause the verb does not read

`block 8 for 2 turns` used to give ordinary block and drop the `for`. Now it is **CT323**, an
error, naming the clause and the verb. If you meet it, the verb genuinely does not do that. The
diagnostic table says which clauses each built-in verb reads.

### An unknown member reads as zero

`target.frobnicate` is "a stat nothing has", which is `0`. `deal target.frobnicate to target` deals
nothing, loads clean and lints clean. Two lines of defence: a `test` with an `expect` on the number
you meant, and `--trace`, where a `deal` that raised no `damaged` event is the tell.

On a *group*, `count`, `size`, `length`, `first`, `last`, `empty`, `any`, `lane`, `rank`, `name`,
`zone`, `controller` and stats all answer; anything else falls through to 0. `created.first.zone`
is what `created.zone` was reaching for.

### The clock decides which declarations are live

Under `clock ticks` there are no turns, so a `move`, a `pattern`, a `phase`, a
`stacking duration` counted in turns, a `decay ... on turn_end`, an `until turn_end:` or a
`next turn:` is dead content. All twelve shapes are **CT337**, an error, naming what to write
instead (usually `on every <n>s:` or `in <n>s:`). **CT335** is the other half: a turn order stated
in a game with no turns. A real-time enemy's whole behaviour is listeners.

`cantrip sim` refuses a `clock ticks` folder outright, and a `scenario` written in one is
**CT338**: a bot plays by taking turns, and there are none.

### A relic's or status's listener heard the wrong turn

An unscoped listener on a status or relic hears only its own controller's `turn_start` and
`turn_end`. To hear the holder's own, scope it: `on owner.turn_start:`. To hear anyone's, filter it.

### A hero's abilities are registered against the clock that exists when `AddHero` runs

Give the runtime its clock before anybody is created. In content, put `realtime <rate>` first in a
test; in C#, pass `RuntimeOptions.Clock` at construction.

---

## A card or ability will not play

`Play` and `UseAbility` answer with an
[`ActionResult`](api/Cantrip.md#actionresult), and it is worth reading the one you got rather than
assuming:

| Answer | What it means |
|---|---|
| `NotInHand` | Not in hand, or not in the pile a `from` named. |
| `Unplayable` | Tagged `unplayable`: a curse or a wound. It is never offered, on purpose. |
| `CannotAfford` | The payer is short of whatever the card is priced in, which is not always energy. That is why the word does not say so. `CostResourceOf(card)` names it and `CostOf(card)` gives the price. An ability has no cost and never answers this. |
| `NotReady` | An ability still on cooldown. `IsReady(ability)` asks in advance. |
| `ChoicePending` | The action stopped for a decision and **rolled the game back**. Read `Pending`, then call `Answer(...)`. |
| `Cancelled` | A choice was answered with nothing, or `CancelPending()` was called. Nothing stands. |
| `OutOfRange` | Too far. The action's `range` after the `range` channel cannot reach. See below. |
| `NoTarget` | Nobody on the side it asks for. Nothing was filtered out and nothing was too far away: the table is empty. |
| `InvalidTarget` | Somebody the action will not take. See below. |

### Which refusal was it?

Three words share the work that `InvalidTarget` used to do alone, and they are worth
reading rather than lumping together, because a game says something different about each:

| Answer | What happened | What to tell the player |
|---|---|---|
| `OutOfRange` | The one you named is too far, or every candidate is. Reach is the action's `range` after the `range` channel has had it. | "Out of reach." It is the only one of the three a player can act on. |
| `NoTarget` | There is nobody on the side it asks for. | Usually nothing: it is a bug in your own loop. The battle should have ended, or the wave should not have let you act. |
| `InvalidTarget` | Somebody the action will not take: the wrong side, not alive, excluded by its own `target … where`, or drawn away by a taunt or hidden by a stealth on the `targetable` channel. | "Not that one." |

`OutOfRange` is only said when reach is the **whole** of the problem. With one enemy too far and
another behind a taunt, the answer is `InvalidTarget`, because "move closer" would send the player
at somebody they still could not hit.

To tell the last two of `InvalidTarget`'s cases apart (the action's own filter against a taunt),
call `LegalTargets(action)` and read the list:

1. **The list is empty.** The action's own `target … where` excluded everybody, or a taunt drew
   targeting elsewhere. Read the `where` clause against the actual board.
2. **The list does not hold the target you passed.** You aimed at somebody the action will not
   accept. In a party game, check you are not passing the leader out of habit.
3. **The list holds fewer actors than you expected, and nothing is out of range.** A `targetable`
   rule somewhere is speaking for them.

The engine does not name which of those two it was; `WhyNotTargetable` is not a thing yet, and
adding it later is additive, so it can arrive in a 1.x release.

**Range is still the one that surprises people.** On a `facing` board the distance across the sides
is `a.rank + b.rank + 1`, so on a 2x2 board your back rank to their back rank is **three** steps and
`range 1..2` cannot make it. A card printed `range 1..2` on a board two ranks deep can be unable to
reach half the enemies, for ever, and nothing warns: lint knows the board's depth but does not yet
check that a `range` can span it (`reference/FINDINGS.md` #8). Do the arithmetic once, write it in
a comment, and pin both ends with a test. `OutOfRange` now says when it has happened, which is the
difference between an hour and a minute.

A `test` block cannot assert a refusal: `play` fails the test when a card cannot be played. So
the way to test an exclusion today is to play the card with no target and check which one it
picked.

---

## The fight never ends

### A stall

`cantrip sim` calls a battle that reaches the turn limit a **stall** and fails the command. It is
the single most useful thing the simulator finds, and it is nearly always one of four things:

- **Nothing can reach.** A `range` that cannot span the board, or a summon standing where no card
  or ability of yours can touch it. See above.
- **The party out-heals the enemies.** A fixed pool of enemy damage against block and healing that
  refreshes. The standard fix is enemies that get stronger: `on every 3 turns: gain 1 Fervour`.
- **A card that does nothing is the best play.** A bot plays the highest-scoring legal play; if
  every play scores zero it still ends the turn, so this shows up as turns passing with nothing
  moving. The `hp went` tables in the report say so at a glance.
- **A loop between listeners.** See below.

`--watch <seed>` replays one run turn by turn and usually answers it in one command.

### A loop between listeners

Two listeners that trigger each other are **CT306**, a note, naming the pair. It is a note rather
than a warning because plenty of legitimate content pairs a listener with itself.

At run time the ruleset protects you: `LoopProtection.OncePerChain` is the default, and `MaxDepth`
stops a chain that will not settle. A skipped listener is in the trace with the reason
(`skipped ...: already in this chain`), which is the fastest way to see it.

Going past the step limit is a `RuntimeError` like any other, so the action is half done and the
right recovery is a snapshot.

### A wave game that ends the moment the board is empty

By default a battle ends when one side has nobody left, which is wrong for survival, horde, endless
arena and tower defence. `ends: called` in the ruleset turns that off and hands the decision to the
game, which then calls `EndBattle(won)`. Without it, a wave game needs an untargetable decoy to
exist at all, which is a workaround and not a design.

---

## Saves

A restore refuses before it touches the game, so **a refused save leaves the game in progress
exactly as it was.** Every refusal is an `InvalidOperationException` whose message names the fix.

| Message begins | What happened | What to do |
|---|---|---|
| `This save is in format N and needs a Cantrip that reads format M` | A save from a newer build. | Nothing, in this build. Ask before restoring with `GameSnapshot.ReaderNeededBy(snapshot)` and tell the player their game needs a newer version. The message names the version that wrote it. |
| `This save's random numbers come from X, which this Cantrip cannot continue` | The generator changed between releases, which [stability.md](stability.md) allows. | The run cannot be continued. Treat it the same as a save from a newer build. |
| `The snapshot's random number generator state is not four numbers` | Truncated or hand-built. | The file is damaged. |
| `The snapshot is damaged: its random number generator state is all zeros` | An empty or truncated file that deserialized into a shape rather than into nothing. | The file is damaged. This is the message an empty save gives. |
| `The snapshot needs <kind> "<name>", which is not loaded` | The save names content this build does not have. | Load the same content, or refuse the save yourself with a nicer message. Card renamed? That is a content change, and [Saves after a content update](stability.md#saves-after-a-content-update) covers what survives one. |
| `This save was played on board "X" (...), and the loaded content has reshaped it to ...` | A board got smaller under a saved actor. | Growing a board is fine; shrinking one past a standing actor is not. Keep the old board declared under its old name. |
| `The snapshot has work waiting (...) that does not match the statements run by Execute saved with it, so the save has been altered.` | The text of scheduled `Execute` work does not hash to what was saved. | Either the save was edited, or the content changed under it. |

**`CanCapture` is false while an action is resolving**, and also after a hot reload that changed a
waiting `next turn:` or `in N turns:` block, until that block has run. In Godot the question is
`CanSave()`. Asking before offering the player a save button is the whole of the handling.

**A save is trusted input.** It sets every stat, and it carries the text of work `Execute`
scheduled, which the game will run. A game that loads saves it did not write (shared, downloaded,
cloud) should sign them.

**Saving is not the same as serializing.** `GameSnapshot` is plain data on purpose so that any
serializer can store it. If you publish your game trimmed or AOT, the serializer is the part that
needs telling; see [Publishing a game](#publishing-a-game).

---

## Godot

### Two rules that account for most of it

**A C# default argument is not a default in GDScript.** `rules.CreatePlayer()` is a parse error;
`rules.CreatePlayer("Player", 80, 3)` is not. Every parameter, every time. The node's table in
[godot.md](godot.md#the-node) lists the values to pass.

**Members keep their C# names.** `rules.CreatePlayer(...)`, not `create_player(...)`. There is no
snake_case alias.

### `Nonexistent function 'X' in base 'CantripRuntime'`

GDScript compiles nothing until the line runs, so a method that does not exist is a runtime error
the first time that code path is taken, not a build error.

If the name used to exist: **`GetHand()` was removed in 1.0.** It was exactly
`GetZone(PlayerId(), "hand")`, and the moment a party exists it has no single right answer. Write
`GetZone(PlayerId(), "hand")`, or `GetZone(<member id>, "hand")` for a hero.

### The node publishes every method it has

Godot's source generator publishes every ordinary method of a `[GlobalClass]` to script, whatever
its C# accessibility says. That is why `CantripRuntime` has no private helpers: a private helper
there would be a method your game can call and this addon has promised to keep. If you are
extending the addon, helpers belong on `RunLoop`, `VariantMap` or `GodotContentLoader`.

The practical consequence for a game: if you find a method on the node that is not in
[godot.md](godot.md#the-node) or the [API reference](api/Cantrip.GodotAdapter.md), it is not part
of the promise, and it may not be there next release.

### The game crashes with a stack overflow

```
Stack overflow.
Repeated 789 times:
   at Cantrip.GodotAdapter.CantripRuntime+RunLoop.AfterAction()
```

Something called back into the runtime from inside a host callback. Every entry point that changes
the game refuses such a call now, and the queries still answer. But if you are on an older build,
or you have written your own host, the rule is: **do not act from inside an event handler.** Queue
it and act on the next frame. `BattleEnded` is the signal this used to bite hardest, because its
name says the battle is over.

### Nothing appears in the Create New Node dialog

Build the C# project first. Until the assembly exists Godot cannot load a C# plugin, and every node
the addon adds is simply missing, with no error to say why. Then enable the plugin in **Project
Settings → Plugins**; the Output panel says `Cantrip: dock ready, ...` when it has.

### The content is not there

`No content loaded` or an empty game usually means one of:

- **The `.cantrip` files are not under `res://content`**, and the node's `ContentFolder` was not
  changed to where they are.
- **Godot has not imported them.** Godot finds files through its import database. A file added
  outside the editor, or a project copied without its `.godot` folder, needs
  `godot --headless --path <project> --import` before anything can load it.
- **A stale `.godot` folder.** If content that is plainly there will not load, or the editor shows
  a file that was deleted, delete the project's `.godot` folder and re-import. It is a cache; it is
  rebuilt. This is the first thing to try for anything that looks impossible.
- **The addon was never assembled.** In this repository, `reference/godot` and `realtime/godot` are
  Godot projects with no addon and no content of their own: `bash reference/godot/assemble.sh`
  copies both into place. Both folders are gitignored, so a fresh clone has neither and the project
  will not open properly until the script has run.

### The addon and the library disagree

The addon is a set of scripts; `Cantrip.Core` is a separate assembly your `.csproj` references.
Updating one without the other gives missing-method errors at run time. Keep the version in
`addons/cantrip/plugin.cfg` and the `Cantrip.Core` version in your `.csproj` the same.

Put the DLL somewhere an update will not overwrite and `.gitignore` will not drop. Use `lib/`, not
`addons/cantrip/` and not `bin/`.

### Unsaved dock buffers vanished

Building the C# project reloads the addon and takes its buffers with it. Nothing is written to disk
without being asked, so that work is gone; the Output panel says how many buffers went. Save before
you build.

### The game will not speed up

`Engine.time_scale` has no effect on a Cantrip game, and neither does
`Engine.physics_ticks_per_second`. That is deliberate: the tick driver ignores the frame delta so
that a fixed step stays fixed, and a game's results never depend on how fast it was watched.
Raising the runtime's `TicksPerSecond` changes the game rather than its speed, because `cooldown 6s`
would come to mean a different length of time.

Fast-forward, slow motion and a 2x option are built by calling `Tick(count)` from your own loop.
[Running faster or slower](godot.md#running-faster-or-slower) has the four lines.

### A wave of enemies acts in lockstep

`on every 2s:` counts from when the listener registered, so two enemies created in the same call
fire on the same tick for ever, and five drones read as one drone with five times the damage.
There is no jitter or phase offset in the language. Spawn a squad on consecutive ticks, or give
each member a randomised `in <n>s:` warm-up.

### Everybody stands in one lane

The board fills in single file: three party members on a 3x5 board go to (0,0), (0,1) and (0,2).
If the lanes mean something in your game, place people yourself with `Place`, which refuses an
off-board slot with a message naming the board's shape. **Writing a slot that is taken swaps the
two actors** rather than refusing: a swap is its own inverse, which is what makes `until` able to
revert a move. So a queue of enemies advancing into each other shuffles instead of queueing.
`before_moved` can refuse a move, which is how a game that wants a queue gets one.

---

## Publishing a game

Cantrip is usable from a trimmed or a Native AOT published game, and CI keeps it that way:
`tools/publish-check.sh` publishes the reference game both ways with the trim and AOT analysers on
and warnings as errors, then plays eight whole runs with saves and restores in what it built.

**The engine needs nothing from you.** `Cantrip.Core` is marked `IsTrimmable`, produces no trim or
AOT warnings, and carries no reflection except one read of its own version attribute.

**Your serializer does.** Reflection-based `System.Text.Json` works perfectly in a normal build and
throws the first time a trimmed or AOT build saves:

```
System.InvalidOperationException: Reflection-based serialization has been disabled for this
application. Either use the source generator APIs or explicitly configure the
'JsonSerializerOptions.TypeInfoResolver' property.
```

The first save comes after the player has already finished a floor. The fix is four lines, and
`reference/host/RunState.cs` is the worked example:

```csharp
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MySave))]
internal sealed partial class MyJson : JsonSerializerContext { }

public string ToJson() => JsonSerializer.Serialize(this, MyJson.Default.MySave);
```

Naming the root type makes the generator walk `GameSnapshot` at build time, so the trimmer keeps
every property it reaches.

**Native AOT needs a platform linker**: the Desktop Development for C++ workload on Windows,
`clang` and `zlib1g-dev` on Linux. Without one, `dotnet publish` reports `Platform linker not
found` *after* compiling everything, which reads like a failure of your code and is not.

[Platforms](stability.md#platforms) has what is tested and what is not.

---

## Still stuck

- **A failing `test` block is the best bug report there is**: it is both the description and the
  proof. [Tests](language.md#tests) is three lines long.
- `dotnet cantrip --version` names the exact build, commit included. In a Godot project without the
  tool, give the version in `addons/cantrip/plugin.cfg` and the `Cantrip.Core` version in your
  `.csproj`.
- `dotnet cantrip repl <folder>` runs statements against a live game, which settles "does this line
  do what I think" faster than anything else here.
- [The issue forms](https://github.com/AGomnes/Cantrip/issues/new/choose) ask for the smallest
  `.cantrip` file that shows the problem, what you expected, and what happened.

## Where next

- [The API reference](api/README.md): every public type and member.
- [Diagnostics](language.md#diagnostics): every code, what it means and how to fix it.
- [Stability](stability.md): what is tested, what is not, and the known limitations in full.
- [reference/FINDINGS.md](../reference/FINDINGS.md) and
  [realtime/FINDINGS.md](../realtime/FINDINGS.md): two records of building a whole game on this
  library, kept as they were written.
