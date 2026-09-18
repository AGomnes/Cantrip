# The Godot adapter

An addon that puts the rules engine inside Godot: content loads from `res://`, a game drives
battles from one node, and the editor gets a dock for problems, tests and card text.

Targets **Godot 4.6.1 .NET**. The addon is C# source, because Godot resolves scripts by file path
inside the project's own assembly — only the engine library itself can be a DLL or a package.

## Installing

1. Copy `addons/gameplay_effects/` into your project.
2. Reference the rules engine from your project's `.csproj`. In this repository that is a project
   reference; for your own game it is a package reference or a DLL:
   ```xml
   <ProjectReference Include="path/to/GameplayEffects.Core.csproj" />
   ```
3. **Build the C# project before enabling the plugin.** Until the assembly exists, Godot cannot
   load a C# plugin and every `[GlobalClass]` node is invisible — with no error to explain why.
4. Enable *Gameplay Effects* in Project Settings → Plugins.
5. Put `.ge` files under `res://content` (or set the node's `ContentFolder`).

To hand the addon to someone else, `tools/package-addon.sh` (or `package-addon.ps1` on Windows)
writes a zip whose root is `addons/gameplay_effects`, which is what "unzip this into your project"
and the Asset Library both expect. It takes the version from `plugin.cfg`, so the two can never
disagree.

## Two rules for GDScript

Both are enforced by a smoke test in `godot/GameplayEffects.Demo/tests/`, because both fail in
confusing ways.

- **Members keep their C# names.** `rules.CreatePlayer(...)`, `rules.EffectEvent`. There is no
  snake_case alias: `load_content` does not exist.
- **A C# default argument is not a default in GDScript.** Every parameter must be passed.
  `rules.CreatePlayer()` is a parse error; `rules.CreatePlayer("Player", 80, 3)` is not.

## A battle in GDScript

```gdscript
extends Node

@onready var rules: GameplayEffectsRuntime = $GameplayEffectsRuntime

func _ready() -> void:
    var problems: Array = rules.LoadContent("res://content")
    for problem in problems:
        if problem["severity"] == "error":
            push_error("%s:%d %s %s" % [problem["file"], problem["line"], problem["code"], problem["message"]])

    rules.EffectEvent.connect(_on_effect_event)
    rules.ChoiceRequested.connect(_on_choice_requested)

    var player: int = rules.CreatePlayer("Player", 80, 3)
    var slime: int = rules.SpawnEnemy("Slime", 0)
    rules.StartBattle(true, true)

    var card: int = rules.GetHand()[0]
    match rules.Play(card, slime):
        "played": pass
        "pending": pass                       # a choice_requested signal is on its way
        "not_enough_energy": $Ui.flash_energy()
        var other: push_warning(other)

func _on_effect_event(effect_event: Dictionary) -> void:
    # The rules have already resolved. This is presentation, and it must not call back in.
    if effect_event["name"] == "damaged":
        $Fx.damage_number(effect_event["target"], effect_event["amount"])

func _on_choice_requested(request: Dictionary) -> void:
    var picked: int = await $Ui.pick_one(request["prompt"], request["options"])
    rules.AnswerChoice(request["id"], [picked])
```

A complete version of this, with a hand, enemy panels, intents and a log, is in
`godot/GameplayEffects.Demo/demo/battle.tscn`. It also plays itself:

```
godot --headless --path godot/GameplayEffects.Demo res://demo/battle.tscn -- --demo-auto
```

## The node

`GameplayEffectsRuntime` is the only surface script touches. Entities cross as `int` ids (0 means
none) and everything else as dictionaries with snake_case keys. A C# game can skip all of that and
use `Core`, the `CardRuntime` underneath.

**Exports**: `ContentFolder`, `AutoLoad`, `Seed`, `Trace`, `RealTime`, `TicksPerSecond`,
`TrackedStats`, `Presenter`, `Driver`.

**Signals**: `EffectEvent(Dictionary)`, `BattleStarted()`, `BattleEnded(bool)`,
`ChoiceRequested(Dictionary)`, `ContentReloaded(Array)`.

| Group | Methods |
|---|---|
| Content | `LoadContent(folder)`, `ReloadContent(paths)` |
| Setup | `CreatePlayer`, `AddCard`, `AddDeck`, `AddRelic`, `SpawnEnemy`, `ApplyStatus`, `GrantAbility` |
| Flow | `StartBattle`, `Play`, `PlayNamed`, `EndTurn`, `Tick`, `UseAbility`, `Execute` |
| Queries | `GetHand`, `GetZone`, `GetEnemies`, `GetAllies`, `GetActors`, `PlayerId`, `GetEntity`, `GetStat`, `CostOf`, `CanPlay`, `GetTargetMode`, `GetLegalTargets`, `IsInBattle`, `GetTurn`, `GetWon`, `StateHash` |
| Text | `Describe(id, targetId)`, `DescribeIntent(enemyId)` |
| Choices | `HasPendingChoice`, `GetPendingChoice`, `AnswerChoice`, `CancelChoice` |
| Saving | `CanSave`, `Save`, `LoadSave` |
| Host | `RegisterName`, `RegisterFunction` |

`Play` answers with a word: `played`, `pending`, `not_a_card`, `not_in_hand`, `unplayable`,
`not_enough_energy`, `invalid_target` or `cancelled`.

## Events

The rules resolve an action completely and immediately; presentation watches afterwards.

- **Events are delivered after the action finishes, never during it.** Acting again from a handler
  is fine — answer a choice, play the next card — because by then nothing is resolving. What is
  refused, with a clear error, is calling in from a **host callback** (`RegisterName`,
  `RegisterFunction`): those run in the middle of an effect and must answer and return.
- **They arrive in completion order, innermost first.** An event that wraps others completes after
  them: playing a card reports `damaged`, then `status_applied`, then `card_played`.
- **Each event carries the stats it changed, as they were then** (`after`), because by animation
  time the whole action has resolved and live stats show only the final numbers. Which stats are
  captured is the node's `TrackedStats`.

```gdscript
{ "seq": 41, "name": "damaged", "phase": "after", "time": 4,
  "source": 3, "target": 1, "card": 12,
  "amount": 9, "amount_raw": 9.0, "replaced": false,
  "tags": ["attack", "fire"], "values": {"base": 6.0, "blocked": 0.0},
  "after": { "1": {"hp": 71, "block": 0} } }
```

Add a `BattlePresenter` to pace them: it emits `Present(event)` one at a time and waits for
`Done()`, so an instantly resolved action animates as a sequence. `IsBusy()` gates input.

## Choices the player makes

Content such as `choose 1 from hand` needs an answer a UI cannot give on the spot. The runtime
rolls the action back to where it started, reports the choice, and replays it once answered — the
rollback is an exact snapshot, so the replay follows the identical path.

```gdscript
if rules.Play(card, 0) == "pending":
    var request: Dictionary = rules.GetPendingChoice()
    rules.AnswerChoice(request["id"], [request["option_ids"][0]])   # "pending" again if more are needed
```

Nothing reaches the game for the rolled-back attempt: no events, no animations.
`CancelChoice()` abandons it, leaving the game exactly as it was.

## Saving

```gdscript
if rules.CanSave():
    FileAccess.open("user://slot1.json", FileAccess.WRITE).store_string(rules.Save())

var result: Dictionary = rules.LoadSave(text)
if not result["accepted"]:
    $Ui.say(result["message"])       # "content_changed" when the save predates a content edit
```

The save carries a fingerprint of the content it was taken against, so a save from before a patch
is refused with a message instead of failing part-way through a restore. Editing a card's numbers
does not invalidate saves; adding, renaming or deleting a definition does.

## Hot reload

`ReloadContent()` reloads the `.ge` files and rebinds everything live to them. Stats the game has
changed keep their values, while a card still at its printed cost takes the new one. The report
says how many entities rebound, which definitions have gone, and whether the ruleset changed —
a running game keeps the rules it started with.

## Real time

Set `RealTime` and add a `TickDriver`, which advances the clock from `_PhysicsProcess` only. Its
ticks per second must divide into the physics rate, because content written as `cooldown 8s`
converts through that clock.

## Content and exports

`.ge` files are not resources, and Godot does not export plain files by default — content that
works in the editor would simply be missing from a shipped game. The addon therefore imports each
`.ge` into a small resource, and an export check fails the export when content would not reach the
build. Loading always goes through the engine's file access, never `System.IO`: the engine's own
`ContentLibrary.LoadFolder` cannot see inside an exported package.

Two things to know before exporting a .NET game, both of which cost an afternoon to discover:

- **Godot needs a solution file beside the project.** Without `YourProject.sln` next to the
  `.csproj`, the export prints "This project contains C# files but no solution file was found",
  ships no managed assembly, and the packaged game crashes on startup.
- **The export exits 0 even when it fails.** Check its output, or check the result: the surest
  test is to run the packaged game.

```
godot --headless --path godot/GameplayEffects.Demo --export-release "Linux" out/demo.x86_64
out/demo.x86_64 --headless -- --demo-auto
```

The demo exits non-zero when no content loaded, so that second line is the test: a build whose
content never travelled starts with an empty library and an empty hand. A workflow that does all
of this on demand is in `.github/workflows/export.yml`; it is not part of every push, because the
export templates are about a gigabyte.

## The editor dock

Enable the plugin and the dock appears at the bottom:

- **Problems** — parse errors and lint findings in one list, in source order, click to open.
- **Tests** — the `test` blocks in your content, run by the same runner as `gedsl test`, with a
  causality trace on failure.
- **Preview** — any definition's rules text with live values, its flavour, its keyword tooltips,
  and the hash to paste into `text_checked`.
- **Source** — a viewer, because Godot cannot open a non-script file at a line. Syntax highlighting
  runs the engine's own lexer, so it cannot drift from the grammar.

Running the editor headless with `--ge-selftest` exercises all of it without a mouse.

## Verifying

```
dotnet build godot/GameplayEffects.Demo/GameplayEffects.Demo.csproj
dotnet build godot/GameplayEffects.Demo/GameplayEffects.Demo.csproj -c ExportRelease
dotnet test tests/GameplayEffects.Godot.Tests
godot --headless --path godot/GameplayEffects.Demo --import
godot --headless --path godot/GameplayEffects.Demo res://tests/headless.tscn
godot --headless --path godot/GameplayEffects.Demo res://tests/gdscript_smoke.tscn
godot --headless --path godot/GameplayEffects.Demo res://demo/battle.tscn -- --demo-auto
```

The `ExportRelease` build is the cheap proof that no editor-only code escaped `#if TOOLS`; both
scenes exit non-zero on failure. Always give Godot a timeout: a script that cannot parse never
quits.

## Not done yet

**Live debugging** is built but only half proven. Run a game from the editor and the debugger gets
two *Gameplay Effects* tabs. The first is the trace: turn recording on, pull the causality tree, and
click a step to open the content line that caused it. The second is *Entities*: everything in play,
and for whichever one you pick, its zone and side, each stat's base value beside what the modifiers
make of it, its statuses, and every listener and modifier it has registered — each of those rows
opening the line of content behind it. Saving a file reloads it into the running game, and statements can be
run against it as a console. Both ends compile, the addon loads with them, and the whole
conversation is exercised headlessly by driving the game's handler directly — but a live
editor-to-game session cannot be staged without a person, so that round trip is checked by hand.

Still to do: breakpoints and stepping (which need a core change to resolve one queued trigger at a
time), and Asset Library packaging. The dock's
interactive use is likewise checked by hand; only its work is covered headlessly.
