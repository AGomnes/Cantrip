# Writing content

> These docs describe the `main` branch, which can be ahead of the latest release. The changelog's [Unreleased](../CHANGELOG.md#unreleased) section lists what that release lacks, and each release's own docs are in [its tag](https://github.com/AGomnes/Cantrip/tags).

This guide is for whoever writes a game's cards, statuses, relics and enemies. It needs no C#. It builds a card, a status, a relic and an enemy whose moves change at half health, each with a test, and explains each idea the first time it appears. Then come the commands you will use all day, a sandbox for trying single lines, and [recipes](#recipes) for common card-game mechanics.

If your game is in Godot, the checking and the testing happen inside the editor: the addon's Cantrip dock lints your files, runs your tests and previews your card text, so almost nothing here needs a command line. You write the files in the dock's Source tab, which lints them as you type and saves with Ctrl+S, or in a text editor of your own if you would rather. [7. In Godot](#7-in-godot) says what each of its tabs does, and [godot.md](godot.md) is the guide to the addon itself.

- [Before you start](#before-you-start)
- [1. A first card](#1-a-first-card)
- [2. A status](#2-a-status)
- [3. A relic](#3-a-relic)
- [4. An enemy that changes at half health](#4-an-enemy-that-changes-at-half-health)
- [5. The edit, lint and test loop](#5-the-edit-lint-and-test-loop)
- [6. Trying lines in the REPL](#6-trying-lines-in-the-repl)
- [7. In Godot](#7-in-godot)
- [Recipes](#recipes)
- [Where next](#where-next)

## Before you start

**In a Godot project** with the addon enabled, you need no tool installed for the checking or the testing. The Cantrip dock at the bottom of the editor runs `lint`, `test` and `describe` over the project's `.cantrip` files, and shows them highlighted. Skip to [7. In Godot](#7-in-godot) for its tabs, and read those three commands on this page as the dock's equivalent; the content and the tests are the same either way. Two things still need the tool: the REPL in [section 6](#6-trying-lines-in-the-repl) and `sim`. The files themselves are written in the dock's Source tab, which saves them with Ctrl+S, or in a text editor of your own. If the addon is not installed yet, [godot.md](godot.md) installs it.

**Anywhere else**, you need the `cantrip` command-line tool, installed as in [step 1 of the quickstart](quickstart.md#1-make-a-project); a programmer may already have done this for you. If you only want the tool, not the C# project, these two lines in the folder you will work in are enough, with the .NET 9 SDK installed:

```
dotnet new tool-manifest
dotnet tool install Cantrip.Cli --prerelease
```

Run the commands below from that folder, or any folder inside it. Any text editor will do for the files; there is no syntax-highlighting package for one yet. In Godot you need none: the dock's Source tab writes them, highlighted and checked as you type.

Make a folder called `tutorial` with two empty files in it, `game.cantrip` and `tests.cantrip`. If you have done the quickstart, keep this folder apart from its `content` folder: a folder loads as one game, and both define a Strike.

In Godot a separate folder is not enough, because the editor dock reads every `.cantrip` file in the project. Work through the tutorial in a Godot project of its own, or point the dock at the tutorial alone: under Project Settings, with Advanced Settings switched on, add the setting `cantrip/content/folder` with the value `res://tutorial`, then press the dock's Reload button. The setting changes only what the dock reads, not what the game loads; remove it when you are done, and press Reload again.

The finished files are in [samples/recipes/tutorial](../samples/recipes/tutorial).

## 1. A first card

Put two cards in `tutorial/game.cantrip`:

```
card Strike
  cost 1
  target enemy
  tags attack
  effect:
    deal 6 to target

card Defend
  cost 1
  effect:
    block 5
```

- `card Strike` starts a **declaration**. The lines indented under it belong to it. Indent with spaces.
- `cost 1` is a **property**. A property with a single number becomes a **stat** of the card, a number the rules can read and change.
- `target enemy` says the card is played on a living enemy. Inside the effect, that enemy is called `target`. Defend has no `target` line, so it is played without one, and `block 5` goes to the player: verbs such as `block` and `heal` default to yourself.
- `tags attack` labels the card. Other content can ask about tags, as the relic in step 3 does. A few tags have behaviour of their own, such as `exhaust`; see [Cards](language.md#cards).
- `effect:` is the block of statements that runs when the card is played. `deal 6 to target` is one statement: a verb, `deal`, and what it acts on.

Now the tests, in `tutorial/tests.cantrip`:

```
test "Strike deals 6"
  enemy hp 20
  play Strike on enemy
  expect enemy.hp == 14

test "Defend gives 5 block"
  enemy hp 20
  play Defend
  expect player.block == 5
```

Each test is a fresh battle, with a player who has 80 hp and 3 energy. `enemy hp 20` adds a plain enemy with 20 hp and no moves, which the test calls `enemy`. `play` plays the card from the hand, putting it there first if it is not already, pays its cost, and fails the test if the card cannot be played. `expect` fails the test if its condition is false. `enemy.hp` reads the enemy's `hp` stat.

Run the tests, and the linter, which checks the files for mistakes a test might not reach:

```
dotnet cantrip test tutorial
dotnet cantrip lint tutorial
```

```
  PASS Strike deals 6
  PASS Defend gives 5 block

2 passed, 0 failed
0 error(s), 0 warning(s), 0 note(s)
```

## 2. A status

A status is something attached to a player or an enemy, such as Poison. Add one to `game.cantrip`, with a card that applies it:

```
status Poison
  tags debuff
  stacking intensity
  on turn_end:
    deal stacks to owner, ignore block
    stacks -1

card "Venom Dart"
  cost 1
  target enemy
  tags attack
  effect:
    deal 3 to target
    apply Poison 4 to target
```

- `apply Poison 4 to target` attaches Poison to the enemy with 4 **stacks**. The entity a status is attached to is its **host**, and inside the status it is called `owner`.
- `stacking intensity` means that applying Poison again adds to the stacks. Inside the status, `stacks` is its count; from outside, `enemy.Poison` reads it. A status is removed when its count reaches 0. Other stacking modes count turns instead of stacks; see [Statuses](language.md#statuses).
- `on turn_end:` is a **listener**: its block runs whenever that event happens. Listeners are how statuses, relics and cards react to what goes on. `on turn_end` or `on turn_start` with nothing in front of the event name hears only the turns of the one it belongs to: for a status, its host; for a relic or a card, the player. So Poison on an enemy goes off at the end of that enemy's turn. The events you can listen to are listed under [Built-in events](language.md#built-in-events).
- `, ignore block` is a flag on the `deal` statement: the damage goes straight to hp.
- A name with a space in it, such as `"Venom Dart"`, is written in quotes, here and wherever it is used.

Add a test:

```
test "Venom Dart poisons, and Poison ticks at the end of the enemy's turn"
  enemy hp 30
  play "Venom Dart" on enemy
  expect enemy.hp == 27
  expect enemy.Poison == 4
  end turn
  expect enemy.hp == 23
  expect enemy.Poison == 3
```

`end turn` ends the player's turn, lets the enemies act and starts the player's next turn. This enemy has no moves, so it only takes its Poison.

## 3. A relic

A relic is held by the player and works for as long as it is held. Add one:

```
relic Whetstone
  modify damage where tag:attack: +2
```

This is a **modifier**. A listener does something when an event happens; a modifier changes a value while it is being worked out. `modify damage` changes the damage the relic's holder deals, `where tag:attack` narrows that to attack damage, and `+2` adds 2. `x1.5` would multiply instead, and `-1` subtract. Modifiers can change damage taken, block, costs and more; see [Modifiers](language.md#modifiers).

Damage carries the tags of whatever dealt it. Strike's damage is tagged `attack`, so the Whetstone adds to it. Poison's damage carries Poison's tags, `debuff`, so the Whetstone leaves it alone:

```
test "Whetstone sharpens attacks, not Poison"
  enemy hp 30
  relic Whetstone
  play Strike on enemy
  expect enemy.hp == 22
  play "Venom Dart" on enemy
  expect enemy.hp == 17
  end turn
  expect enemy.hp == 13
```

`relic Whetstone` gives the player the relic for this test. In the game itself, the game's code hands out relics.

## 4. An enemy that changes at half health

Enemies act through **moves**, and show the player which move comes next. Add one that learns a new move when its health drops to half:

```
enemy "Bog Troll"
  hp 40
  phase Enraged when hp <= max_hp / 2
  move Club:
    deal 8 to player
  move Guard:
    block 6
  move Thrash phase Enraged:
    repeat 2:
      deal 5 to player
  pattern cycle Thrash, Club, Guard
```

- Each `move` is a named block. Inside it, `player` is the player, and `block 6` goes to the enemy itself.
- The move the enemy will make next is its **intent**, shown to the player during their turn. It is chosen when the battle starts and again after each enemy turn.
- `pattern cycle Thrash, Club, Guard` goes through the moves in that order and then loops.
- `phase Enraged when hp <= max_hp / 2` names a **phase**, a stage of the enemy's behaviour, with the condition that turns it on. The condition is checked whenever damage takes some of the enemy's hp, and whenever its next move is chosen. Hp lost in other ways, such as `lose 5 hp`, is noticed only when the next move is chosen. `move Thrash phase Enraged` can only be chosen while the Troll is Enraged; a move with no phase can always be chosen.

Two rules decide the order the moves come in. A cycle skips any move the current phase does not allow, and entering a phase starts the cycle again from the top. So while the Troll is calm it skips Thrash and goes Club, Guard, Club, Guard. Once it is Enraged it starts again with Thrash, then Club, then Guard. That is why Thrash is listed first: with `pattern cycle Club, Guard, Thrash`, the Troll would club and guard once more before its first Thrash.

The move already shown still happens. If the Troll drops to half health during the player's turn, it becomes Enraged at once but still makes the move it showed, and Thrash comes after that. The last test below shows this. To show the new move straight away, add `retelegraph` to the phase, as the [boss recipe](#a-boss-that-switches-moves-at-half-health) does.

The tests:

```
test "The Bog Troll clubs, then guards"
  enemy "Bog Troll"
  expect enemy.intent == "Club"
  end turn
  expect player.hp == 72
  expect enemy.intent == "Guard"
  end turn
  expect enemy.block == 6

test "Below half health the Bog Troll thrashes"
  enemy "Bog Troll" hp 20
  expect enemy.phase == "Enraged"
  expect enemy.intent == "Thrash"
  end turn
  expect player.hp == 70

test "Crossing half health changes the next move, not the one shown"
  enemy "Bog Troll"
  deal 25 to enemy
  expect enemy.phase == "Enraged"
  expect enemy.intent == "Club"
  end turn
  expect player.hp == 72
  expect enemy.intent == "Thrash"
```

- `enemy "Bog Troll"` adds the enemy you defined. `enemy "Bog Troll" hp 20` also sets its hp to 20, so it starts below half; its `max_hp` stays 40.
- A move's name and a phase's name are compared as quoted text: `enemy.intent == "Club"`. Before any phase applies, `enemy.phase` is `none`, which is written without quotes: `expect enemy.phase == none`.
- Any statement from the language works in a test, such as `deal 25 to enemy`.

## 5. The edit, lint and test loop

| Command | What it does |
|---|---|
| `dotnet cantrip test tutorial` | Runs every test. `--filter <text>` runs only the tests whose names contain the text, and `--trace` prints a step-by-step account of what happened in each failing test. |
| `dotnet cantrip lint tutorial` | Checks the files: unknown names, misspelt verbs, a listener on an event nothing raises or written without `on`, a `{placeholder}` in card text that matches nothing, and more. It reports errors, warnings and notes. |
| `dotnet cantrip validate tutorial` | Reports only errors, leaving out the linter's warnings and notes. |
| `dotnet cantrip describe tutorial` | Prints the rules text of every definition, generated or written with `text:`, or of one with `--name "Venom Dart"`. |
| `dotnet cantrip sim tutorial` | Plays the `scenario` blocks hundreds of times with a bot, and reports what the content allowed: a run that threw, a fight that never ended, a card that was never playable, an enemy move that never fired. The tutorial has no scenario yet, so it says so; [Simulating](simulating.md) shows how to write one. |

In Godot the dock does three of these: its Problems tab is `lint`, its Tests tab is `test`, and its Preview tab is `describe`. `sim` and the REPL have no tab, so they need the command-line tool.

Run `test` and `lint` after every change, because they catch different things. Misspell Poison in Venom Dart as `apply Posion 4 to target`, and `test` finds it only because two tests play the card:

```
  FAIL Venom Dart poisons, and Poison ticks at the end of the enemy's turn
       tutorial/game.cantrip:26:11: runtime error: Unknown name `Posion`. Did you mean `Poison`?
  FAIL Whetstone sharpens attacks, not Poison
       tutorial/game.cantrip:26:11: runtime error: Unknown name `Posion`. Did you mean `Poison`?
```

`lint` finds it whether or not a test plays the card:

```
tutorial/game.cantrip:26:11: error CT302: Nothing called `Posion` is defined. Did you mean `Poison`?
1 error(s), 0 warning(s), 0 note(s)
```

Each message starts with the file, line and column, and [Diagnostics](language.md#diagnostics) lists every code with what it means and the usual fix. `lint` counts only errors as failure, so read its warnings too. To have a warning fail it as well, as on a build server, add `--warnings-as-errors`; notes never fail it, and `--suppress` with a warning's code, such as `--suppress CT303`, leaves that warning out.

A failing `expect` shows the value it found. Change the Strike test to expect 15, and run it alone with `--filter`, adding `--trace` to see what happened before the `expect`:

```
dotnet cantrip test tutorial --filter "Strike deals" --trace
```

```
  FAIL Strike deals 6
       tutorial/tests.cantrip:4:3: expected enemy.hp == 15, but enemy.hp was 14
       | [verb] enemy  @ tutorial/tests.cantrip:2:3
       | [event] battle_start {source=Player#1}
       | [event] turn_start {source=Player#1, target=Player#1}
       | [verb] play  @ tutorial/tests.cantrip:3:3
       |   [event] card_played {source=Player#1, target=Enemy#2, card=Strike#3, amount=1, tags=attack}
       |     [verb] deal  @ tutorial/game.cantrip:6:5
       |       [event] damaged {source=Player#1, target=Enemy#2, card=Strike#3, amount=6, tags=attack}
       | [verb] expect  @ tutorial/tests.cantrip:4:3
```

Most lines show a statement (`[verb]`), an event, or a listener that heard one (`[listener]`), with the file and line it came from. A line indented under another was caused by it: here the play raised `card_played`, Strike's effect ran `deal`, and `damaged` shows the 6 hp the enemy lost. The trace is printed only for failing tests, so to look inside a passing one, break an `expect` on purpose.

Some habits make tests easier to live with:

- **One comparison per `expect`.** `expect enemy.hp == 15 and player.energy == 2` fails with `expected (enemy.hp == 15) and (player.energy == 2)` and no values. A test also stops at its first failing `expect`.
- **Setup comes first.** The lines `enemy`, `player`, `hand`, `deck`, `discard_pile`, `relic`, `seed`, `answer`, `realtime` and `grant`, and a `setup:` block, run before the battle starts; it starts at the first other line. Starting the player's turn resets block and energy, so `player block 5` is gone by the first `expect`. Write `block 5` after the setup lines instead.
- **Cards go where you put them.** `deck Strike, Defend` puts cards in the draw pile and `hand` in the hand. A test does not shuffle or draw an opening hand.
- **A test is one battle.** It cannot show something that resets between battles, and it cannot check that a card is refused, because `play` fails the test when a card cannot be played.
- **Choices are answered with `answer`.** `answer "Defend"` picks Defend at the next choice. With no answer queued, the first option is taken.
- **`log` shows only in the trace.** With `--trace`, a failing test's trace has a `[log]` line under each `log` statement that ran, with the values it wrote. A passing test prints no trace, so there use an `expect` that shows the value. `log` also prints in the [REPL](#6-trying-lines-in-the-repl).

The full list of test verbs is under [Tests](language.md#tests).

`lint` also warns about lines that load but do nothing:

- **A line ending in a colon that is not a block the declaration runs** (CT313). `when card_played:` inside a relic is taken as a label and never runs, because a listener always starts with `on`. The warning suggests the listener the line looks like. [Declarations](language.md#declarations) lists what each kind of declaration reads.
- **A tag on a line of its own** (CT316). Under a card, `exhaust` alone is a property that nothing reads, so the card is discarded as usual; write `tags exhaust`. See [A card that draws and exhausts](#a-card-that-draws-and-exhausts).
- **A length the status never uses** (CT314, CT315). `apply Weak for 2 turns` on a `stacking duration` status lasts one turn, and a `duration 2` line in the status's declaration does nothing; see [A debuff that lasts N enemy turns](#a-debuff-that-lasts-n-enemy-turns).

Some mistakes neither command reports:

- **A property the engine does not read.** It is only a stat: `max_stack 3`, with the `s` missing, leaves the status with no cap, and `duration 2` on a status that stacks by intensity does not make it last two turns.
- **A misspelt status in a test's `enemy` or `player` line.** In `enemy hp 20 Posion 3`, a word that is not a status becomes a plain stat, so the enemy gets a stat called Posion and no status. Neither command reports the line itself; `lint` reports CT302 only where the test goes on to read `enemy.Posion`. Check what the status does, such as the hp the enemy has lost after `end turn`, and the test fails instead. A misspelt card, relic, ability or enemy on a test line is different: `hand Strik` is an error in `lint`, and the test fails naming the nearest definition.

Nor is `describe` a check: it prints rules text for what was written, whether or not it will ever run. Only a test shows that it does.

## 6. Trying lines in the REPL

The REPL runs one line at a time against a live battle, which is quicker than a test for trying an idea:

```
dotnet cantrip repl tutorial
```

It loads the folder, and stops if the files have errors. It then creates a player with 80 hp and 3 energy, and a 100 hp enemy called Dummy that has no moves, and starts a battle without drawing any cards. Each line you type is one statement, run as the player, with `target` meaning the Dummy. After each line it prints every player and enemy, with their statuses:

```
cantrip repl. Statements run as the player, targeting the Dummy.
Commands: :state  :trace  :quit
> apply Poison 4 to target
  Player       hp 80/80  block 0  energy 3
  Dummy        hp 100/100  block 0  [Poison 4]
> emit turn_end to target
  Player       hp 80/80  block 0  energy 3
  Dummy        hp 96/100  block 0  [Poison 3]
> create Whetstone
  Player       hp 80/80  block 0  energy 3
  Dummy        hp 96/100  block 0  [Poison 3]
> create Strike
  Player       hp 80/80  block 0  energy 3
  Dummy        hp 96/100  block 0  [Poison 3]
> replay hand.first on target
  Player       hp 80/80  block 0  energy 3
  Dummy        hp 88/100  block 0  [Poison 3]
> log "The Dummy has" target.hp "hp"
log: The Dummy has 88 hp
  Player       hp 80/80  block 0  energy 3
  Dummy        hp 88/100  block 0  [Poison 3]
```

| Command | What it does |
|---|---|
| `:state` | Prints the players and enemies again |
| `:trace` | Prints what has happened since the last `:trace`: events, listeners and the statements they ran |
| `:quit` | Leaves the REPL |

Useful lines:

| Line | What it does |
|---|---|
| `apply Poison 4 to target`, `deal 6 to target` | Any statement from the language |
| `create Whetstone`, `create Strike` | Gives the player a relic, or puts a card in the hand |
| `replay hand.first on target` | Runs the first card in the hand's effect, for free |
| `emit turn_end to target` | Raises `turn_end` for the Dummy, so its statuses' `on turn_end` listeners run |
| `create "Bog Troll"` | Adds one of your enemies. It never takes a turn here. |
| `log "text" target.Poison` | Prints values. Separate them with spaces, not commas: text after a comma is read as a flag. |

The REPL has limits. It has none of the test verbs, so `enemy`, `play`, `end turn` and `expect` are unknown there, and no turns pass. `replay` is not a real play: it pays no energy and raises no `card_played`, so a listener such as `on card_played` does not hear it. Each line runs on its own, so a `let` on one line is gone on the next. A status shows its stacks, so a status that counts turns shows 1; `log target.Weak` shows the turns left. For anything that spans turns, write a test.

## 7. In Godot

In a Godot game the files, the language and the tests are the same. Keep them where the game loads them from, which is `res://content` unless the programmer chose another folder.

Once the Cantrip addon is enabled, the Godot editor has a Cantrip dock at the bottom, and the loop in [section 5](#5-the-edit-lint-and-test-loop) happens there: write a card in its Source tab, read the problems as they appear, press **Run tests**, save with Ctrl+S. Nothing to install, and no command line for any of that.

| Tab | What it shows |
|---|---|
| Problems | Errors and lint findings for every `.cantrip` file. Double-click one to see the line. |
| Tests | Your `test` blocks. Its Run button runs them as `dotnet cantrip test` does. "Trace failures" is ticked to begin with, and shows the trace of each failing test. |
| Preview | Any definition's rules text, with live values |
| Source | The editor. Write content here, save with **Ctrl+S** or the **Save** button, and see each problem marked in the gutter and written out under the buffer a moment after you stop typing, with **Apply fix** for the ones that suggest a word. It indents with spaces at the width the file already uses, never a tab. **Run tests** runs your `test` blocks against the buffers, saved or not; **Open externally** opens the file in whatever your system uses for text files. |

A file you save in the dock is loaded and linted straight away, and so is one saved elsewhere, once Godot notices it — and a `.cantrip` file added or deleted outside the editor joins or leaves the content the same way. **Reload** re-reads the `cantrip/` project settings and reads every file again from scratch; it keeps your unsaved buffers. A running game picks up changed files when it reloads its content, which the game's own code starts, for example from a debug key. See [The editor dock](godot.md#the-editor-dock) and [Hot reload](godot.md#hot-reload).

`sim` and the REPL are the two commands with no tab. To play a scenario hundreds of times with a bot, install the command-line tool beside the game as [Before you start](#before-you-start) shows, and run `dotnet cantrip sim content` in the project folder; [Simulating](simulating.md) covers it.

## Recipes

Short answers to common questions, each with a test that passes. Every recipe is a file in [samples/recipes](../samples/recipes), and CI runs `test` and `lint` over the folder. Some recipes use Strike, Defend, Poison or the Bog Troll from the tutorial, and say so; everything else a recipe needs is in it.

| I want | Recipe |
|---|---|
| a debuff that lasts N of the enemy's turns | [A debuff that lasts N enemy turns](#a-debuff-that-lasts-n-enemy-turns) |
| a debuff that makes its host take more damage, such as Vulnerable | [A debuff that makes its host take more damage](#a-debuff-that-makes-its-host-take-more-damage) |
| a buff that protects the player from the next N attacks | [A buff that lasts N enemy attacks](#a-buff-that-lasts-n-enemy-attacks) |
| damage worked out from a status, such as twice the target's Poison | [Damage that scales with a status](#damage-that-scales-with-a-status) |
| a card that draws and then leaves play | [A card that draws and exhausts](#a-card-that-draws-and-exhausts) |
| a relic that does something every turn | [A relic that works at the start of each turn](#a-relic-that-works-at-the-start-of-each-turn) |
| a relic that works once per battle | [A relic that works once per battle](#a-relic-that-works-once-per-battle) |
| a boss that changes its moves at half health | [A boss that switches moves at half health](#a-boss-that-switches-moves-at-half-health) |
| an effect that heals for the damage it dealt | [Healing for the damage dealt](#healing-for-the-damage-dealt) |
| a card that lets the player choose | [Discard, then draw](#discard-then-draw) |
| a card that offers a pick of three | [Discover a card](#discover-a-card) |
| upgraded cards, kept out of rewards | [Card upgrades](#card-upgrades) |
| a status that goes off when it builds up | [A status that goes off at a threshold](#a-status-that-goes-off-at-a-threshold) |

### A debuff that lasts N enemy turns

A status with `stacking duration` counts turns, not stacks: applying 2 gives it 2, and it loses 1 at the end of each of its host's turns. On an enemy, that covers the enemy's next N turns.

```
status Weak
  tags debuff
  stacking duration
  modify damage: x0.75

card Sap
  cost 1
  target enemy
  effect:
    apply Weak 2 to target

enemy Brute
  hp 40
  move Smash:
    deal 10 to player

test "Sap weakens the Brute's next two attacks"
  enemy Brute
  play Sap on enemy
  end turn
  expect player.hp == 73
  end turn
  expect player.hp == 66
  expect not enemy.has(Weak)
  end turn
  expect player.hp == 56
```

The number goes after the status's name: `apply Weak 2`. `apply Weak for 2 turns` is a different thing, a deadline added to a duration of 1, and covers only one enemy turn; `lint` warns about it (CT314). A `duration 2` line in the status's declaration does nothing either (CT315). The same number means something else on the player, because the player's turn ends before the enemies act: see [How long a duration status lasts](language.md#how-long-a-duration-status-lasts), which also shows how to protect the player for the next N enemy turns. File: [debuff-for-enemy-turns.cantrip](../samples/recipes/debuff-for-enemy-turns.cantrip).

### A debuff that makes its host take more damage

`modify damage_taken` changes the damage a status's host receives. This uses Strike from the tutorial.

```
status Vulnerable
  tags debuff
  stacking duration
  modify damage_taken: x1.5

card Expose
  cost 1
  target enemy
  effect:
    apply Vulnerable 2 to target

enemy Hexer
  hp 30
  move Hex:
    apply Vulnerable 2 to player
  move Jab:
    deal 10 to player
  pattern cycle Hex, Jab, Jab

test "Vulnerable 2 on an enemy lasts the rest of this turn and the next"
  enemy hp 40
  play Expose on enemy
  play Strike on enemy
  expect enemy.hp == 31
  end turn
  play Strike on enemy
  expect enemy.hp == 22
  end turn
  expect not enemy.has(Vulnerable)
  play Strike on enemy
  expect enemy.hp == 16

test "Vulnerable 2 from an enemy's move covers only the enemy's next turn"
  enemy Hexer
  end turn
  expect player.Vulnerable == 2
  end turn
  expect player.hp == 65
  end turn
  expect player.hp == 55
  expect not player.has(Vulnerable)
```

- `damage` is what the status's host deals and `damage_taken` what it receives. Weak uses `modify damage: x0.75`. Written with `damage` instead, Vulnerable would make the enemy's own hits bigger by half.
- The status loses 1 at the end of each of its host's turns. On an enemy, it is the player's attacks that it changes, so `apply Vulnerable 2` covers the rest of the player's turn and the whole of the next one.
- On the player it is the enemies' hits that count, and the player's own turn end ticks it first, so 2 from an enemy's move covers only the next enemy turn. [How long a duration status lasts](language.md#how-long-a-duration-status-lasts) sets out both cases.

File: [debuff-more-damage-taken.cantrip](../samples/recipes/debuff-more-damage-taken.cantrip).

### A buff that lasts N enemy attacks

To count attacks rather than turns, give the status stacks and spend one on each hit. This uses the Bog Troll from the tutorial.

```
status Parry
  tags buff
  stacking intensity
  on owner.before_damaged(source:enemies):
    event.amount = event.amount / 2
    stacks -1
  text_override: "Each hit from an enemy deals half damage and uses up 1 Parry."

card Brace
  cost 1
  effect:
    apply Parry 2 to player

enemy Raider
  hp 30
  move Flurry:
    repeat 2:
      deal 6 to player

test "Parry halves the next two attacks, however many turns they take"
  enemy "Bog Troll"
  play Brace
  end turn
  expect player.hp == 76
  expect player.Parry == 1
  end turn
  expect player.Parry == 1
  end turn
  expect player.hp == 72
  expect not player.has(Parry)

test "Each hit of a multi-hit move uses one Parry"
  enemy Raider
  play Brace
  end turn
  expect player.hp == 74
  expect not player.has(Parry)
```

- `before_damaged` runs before the damage lands, and can change `event.amount`. `owner.` in front of the event limits it to hits on the status's host, and `(source:enemies)` to hits from enemies. A scope in front of an event is always matched against the event's target, so on a relic `on owner.card_played` would hear cards played at the player, not by them; for the cards the player plays, write `on card_played`.
- The Troll's Guard turn does not use a stack, because nothing hits. Each hit of a multi-hit move uses one.
- Damage rounds down, so a hit of 7 becomes 3.
- Write `cancel` instead of the `event.amount` line to stop the hits entirely.
- `text_override` replaces the generated rules text word for word; see [Descriptions](language.md#descriptions).

File: [buff-for-enemy-attacks.cantrip](../samples/recipes/buff-for-enemy-attacks.cantrip).

### Damage that scales with a status

An amount can be any expression. This uses Poison from the tutorial.

```
card Rupture
  cost 1
  target enemy
  tags attack
  effect:
    deal 2 * target.Poison to target
  text_override: "Deal damage equal to twice the target's Poison."

test "Rupture deals twice the target's Poison"
  enemy hp 30 Poison 4
  play Rupture on enemy
  expect enemy.hp == 22

test "Rupture does nothing to an enemy without Poison"
  enemy hp 30
  play Rupture on enemy
  expect enemy.hp == 30
```

`target.Poison` reads the target's stacks of Poison, and 0 when it has none. Without `text_override`, the generated text would print the formula. `enemy hp 30 Poison 4` in a test applies 4 Poison as the enemy is added. File: [damage-scaling-with-status.cantrip](../samples/recipes/damage-scaling-with-status.cantrip).

### A card that draws and exhausts

The `exhaust` tag sends a card to the exhaust pile after it is played, instead of the discard pile. This uses Strike from the tutorial.

```
card Insight
  cost 0
  tags exhaust
  effect:
    draw 2

test "Insight draws two and is exhausted"
  enemy hp 20
  deck Strike, Strike, Strike
  play Insight
  expect count(hand) == 2
  expect count(draw) == 1
  expect count(exhaust) == 1
  expect count(discard) == 0
```

`hand`, `draw`, `discard` and `exhaust` name the player's piles, and `count` counts what is in one. When the draw pile runs out, `draw` shuffles the discard pile into it.

Write `tags exhaust`. On a line of its own, `exhaust` is a property that nothing reads, so the card goes to the discard pile as usual; `lint` warns about it (CT316). The same goes for the other tags with behaviour of their own: `retain`, `ethereal`, `unplayable`, `power` and `attack`. File: [draw-and-exhaust.cantrip](../samples/recipes/draw-and-exhaust.cantrip).

### A relic that works at the start of each turn

This uses the Bog Troll from the tutorial.

```
relic Kettle
  on turn_start:
    block 3

test "Kettle gives 3 block at the start of every turn"
  enemy "Bog Troll"
  relic Kettle
  expect player.block == 3
  end turn
  expect player.hp == 75
  expect player.block == 3
```

On a relic, `on turn_start` with nothing in front of it hears only the player's turns, not the enemies'. It also runs after the start of the turn has reset block and energy, so the block is kept. The test's first `expect` starts the battle, and so the first turn. File: [relic-each-turn.cantrip](../samples/recipes/relic-each-turn.cantrip).

### A relic that works once per battle

This uses Strike and Defend from the tutorial.

```
relic "War Horn"
  on card_played(tag:attack) once per battle:
    draw 2

test "War Horn draws two after the first attack of the battle only"
  enemy hp 50
  relic "War Horn"
  deck Defend, Defend, Defend, Defend
  play Strike on enemy
  expect count(hand) == 2
  play Strike on enemy
  expect count(hand) == 2
```

- `once per battle` goes after the event and its filter, just before the colon. Written first, as in `once per battle on card_played:`, the line is taken as a label and never runs, and `lint` warns about it (CT313).
- The limit is spent when the listener fires, whatever its body then does. A condition in an `if` inside the body still uses it up, so put the condition in the filter instead: `on owner.damaged(owner.hp <= owner.max_hp / 2) once per battle:` waits for the first hit that leaves the holder at half health or below.
- The limit resets when the next battle starts. A test is a single battle, so it cannot show that.
- `once per turn`, `once per run` and `once per chain` work the same way; see [Listeners](language.md#listeners).

File: [relic-once-per-battle.cantrip](../samples/recipes/relic-once-per-battle.cantrip).

### A boss that switches moves at half health

The Bog Troll in step 4 adds a move at half health. To swap one set of moves for another, give the enemy two phases, one for each half, and put every move in one of them.

```
enemy "Storm Warden"
  hp 60
  phase Calm when hp > max_hp / 2
  phase Tempest when hp <= max_hp / 2, retelegraph
  move Gust phase Calm:
    deal 6 to player
  move Shelter phase Calm:
    block 8
  move Lightning phase Tempest:
    deal 14 to player
  move Squall phase Tempest:
    repeat 3:
      deal 3 to player
  pattern cycle Gust, Shelter, Lightning, Squall

test "While calm, the Warden gusts and shelters"
  enemy "Storm Warden"
  expect enemy.phase == "Calm"
  expect enemy.intent == "Gust"
  end turn
  expect player.hp == 74
  expect enemy.intent == "Shelter"
  end turn
  expect enemy.intent == "Gust"

test "At half health the Warden switches at once"
  enemy "Storm Warden"
  deal 30 to enemy
  expect enemy.phase == "Tempest"
  expect enemy.intent == "Lightning"
  end turn
  expect player.hp == 66
  expect enemy.intent == "Squall"
  end turn
  expect player.hp == 57
  expect enemy.intent == "Lightning"
```

- One pattern serves both phases, because the cycle skips the moves the current phase does not allow.
- `retelegraph` changes the shown move the moment the threshold is crossed. Without it, the Warden would still make the Gust it had shown. The cost is that the move the player planned their turn around can change part way through it; see [Phases](language.md#phases).

File: [boss-switches-at-half-health.cantrip](../samples/recipes/boss-switches-at-half-health.cantrip).

### Healing for the damage dealt

`into` keeps what a damage statement actually did, for the lines after it.

```
card Leech
  cost 1
  target enemy
  tags attack
  effect:
    deal 8 to target into dealt
    heal dealt
  text: "Deal {damage} damage. Heal as much hp as it took."

test "Leech heals for what got through block"
  player hp 50
  enemy hp 30 block 3
  play Leech on enemy
  expect enemy.hp == 25
  expect player.hp == 55

test "Leech heals no more than the target had left"
  player hp 50
  enemy hp 4
  play Leech on enemy
  expect enemy.dead
  expect player.hp == 54
```

`dealt` is the hp the target lost: after modifiers, after block, and no more than it had left. `into` works on `deal`, `damage` and `attack`. In the `text` line, `{damage}` is filled in with the damage, including modifiers such as the Whetstone's when a game shows the card. File: [heal-for-damage-dealt.cantrip](../samples/recipes/heal-for-damage-dealt.cantrip).

### Discard, then draw

`discard 1` asks the player to choose a card from their hand. This uses Strike and Defend from the tutorial.

```
card Sift
  cost 0
  effect:
    discard 1
    draw 2

test "Sift discards the chosen card, then draws two"
  enemy hp 20
  hand Strike, Defend
  deck Strike, Strike
  answer "Defend"
  play Sift
  expect count(hand) == 3
  expect count(discard where name:Defend) == 1
```

The discard is chosen before the draw, so the new cards cannot be discarded. In a game, the game's interface asks the player; see [Player choices](csharp.md#player-choices) or, in Godot, [Choices the player makes](godot.md#choices-the-player-makes). In a test, `answer` makes the choice. File: [discard-then-draw.cantrip](../samples/recipes/discard-then-draw.cantrip).

### Discover a card

`discover` offers definitions from your content rather than cards in play, and the player picks one.

```
card Study
  cost 1
  effect:
    discover 3 cards where tag:tome as found
    create found into hand
  text_override: "Choose 1 of 3 tomes and add it to your hand."

card Flare
  cost 1
  target enemy
  tags tome, attack
  effect:
    deal 9 to target

card Rime
  cost 1
  tags tome
  effect:
    block 9

card Surge
  cost 0
  tags tome, exhaust
  effect:
    gain 2 energy

test "Study adds the tome the player picks"
  enemy hp 20
  answer "Rime"
  play Study
  expect count(hand) == 1
  expect count(hand where name:Rime) == 1
```

- `where tag:tome` picks the pool, `as found` names the pick, and `create found into hand` makes that card and puts it in the hand.
- `discover 1` picks at random without asking. `, weighted` after the filter uses each definition's `weight` property.
- The picks come from the game's seeded random numbers, so a replay offers the same cards.

More in [Built-in verbs](language.md#built-in-verbs). File: [discover.cantrip](../samples/recipes/discover.cantrip).

### Card upgrades

Cantrip has no upgrade mechanism of its own. An upgraded card is a card of its own, and a tag marks it so that pools can leave it out:

```
card Cleave
  cost 1
  tags attack, reward
  effect:
    deal 8 to all enemies

card "Cleave+"
  cost 1
  tags attack, reward, upgraded
  effect:
    deal 11 to all enemies

card Spoils
  cost 0
  tags exhaust
  effect:
    discover 3 cards where tag:reward and not tag:upgraded as prize
    create prize into hand
  text_override: "Choose 1 of 3 reward cards and add it to your hand. Exhaust."

test "Cleave+ hits harder than Cleave"
  enemy hp 30
  enemy hp 30
  play Cleave
  play "Cleave+"
  expect enemy.hp == 11
  expect enemy2.hp == 11

test "Spoils never offers an upgraded card"
  enemy hp 20
  answer "Cleave+"
  play Spoils
  expect count(hand) == 1
  expect count(hand where tag:upgraded) == 0
```

- The second test answers "Cleave+", so if Cleave+ were ever offered, it would be picked and the last line would fail. As it is, the pool holds only Cleave, and with a single candidate `discover` does not ask. An answer that names nothing on offer is passed over, and `discover` takes the first option.
- The second enemy in a test is `enemy2`, the third `enemy3`, and so on.
- Upgrading a card in the deck between battles is the game's job: it puts the new definition in the deck in place of the old one. A card can also make the swap during a battle; see [Cards](language.md#cards).

A game that picks rewards in its own C# code can leave upgraded cards out by the same tag:

```csharp
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;

// content is the game's ContentLibrary, loaded at startup.
List<EntityDefinition> rewards = content.Pool("card")
    .Where(card => card.HasTag("reward") && !card.HasTag("upgraded"))
    .ToList();
```

File: [upgrades.cantrip](../samples/recipes/upgrades.cantrip).

### A status that goes off at a threshold

A status can listen for its own application and act once its stacks reach a number.

```
status Static
  tags debuff
  stacking intensity
  on owner.status_applied(Static):
    if stacks >= 5:
      deal 15 to owner
      remove Static from owner
  text_override: "At 5 or more, take 15 damage and lose all Static."

card Jolt
  cost 1
  target enemy
  tags attack
  effect:
    deal 3 to target
    apply Static 2 to target

test "Static goes off when it reaches 5"
  enemy hp 50
  play Jolt on enemy
  play Jolt on enemy
  expect enemy.hp == 44
  expect enemy.Static == 4
  play Jolt on enemy
  expect enemy.hp == 26
  expect not enemy.has(Static)

test "Five Static applied at once go off straight away"
  enemy hp 50
  apply Static 5 to enemy
  expect enemy.hp == 35
  expect not enemy.has(Static)
```

- `on owner.status_applied(Static)` hears every application of Static to its host, the first included, and `stacks` already counts the new ones.
- On a host that already has Static, `gain 2 Static to enemy` and `enemy.Static += 2` change the stacks without applying the status, so this listener does not hear them. If your content uses those, listen with `on self.stacks_changed:` as well, which hears every change to this status's stacks except the first application. Without `self.`, it would also hear the stacks of every other status change, such as Poison ticking down.

File: [status-threshold.cantrip](../samples/recipes/status-threshold.cantrip).

## Where next

The [language reference](language.md) has the full rules for each idea used here:

| Idea | Reference |
|---|---|
| declarations, properties, stats, and what each kind reads | [Declarations](language.md#declarations), [Terms](language.md#terms) |
| cards, targets, tags such as `exhaust`, upgrades | [Cards](language.md#cards) |
| statuses, stacking modes, how long they last | [Statuses](language.md#statuses), [How long a duration status lasts](language.md#how-long-a-duration-status-lasts) |
| relics | [Relics, items and keywords](language.md#relics-items-and-keywords) |
| enemies, moves, patterns, intents | [Enemies](language.md#enemies) |
| phases, `retelegraph` | [Phases](language.md#phases) |
| listeners, `before_`, `once per` | [Listeners](language.md#listeners), [Built-in events](language.md#built-in-events) |
| modifiers | [Modifiers](language.md#modifiers) |
| verbs such as `deal`, `discover` and `into` | [Built-in verbs](language.md#built-in-verbs) |
| rules text and placeholders | [Descriptions](language.md#descriptions) |
| test verbs, and what a test cannot do | [Tests](language.md#tests) |
| scenarios, and playing them many times with a bot | [Scenarios](language.md#scenarios), [Simulating](simulating.md) |
| the order of a turn | [How a battle runs](language.md#how-a-battle-runs) |

For more worked content with tests, [samples/basic](../samples/basic) has small examples of many features, and [samples/slice](../samples/slice) is the content of a small five-floor roguelite, including the Archmage, a boss with two phases.

Looking for an effect you know from another game? [coverage.md](coverage.md) maps effects from nine games to working content in [samples/corpus](../samples/corpus), including power cards, and its [Sharp edges](coverage.md#sharp-edges) list the rules that most often catch authors out.

For the programmer on the team, [godot.md](godot.md) covers running battles, choices, saves and hot reload in a Godot game, and [csharp.md](csharp.md) does the same for a game that calls the library itself.
