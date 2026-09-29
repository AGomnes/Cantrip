# The Godot addon

> Read on the `main` branch, this guide can be ahead of the latest release. The changelog's
> [Unreleased](../CHANGELOG.md#unreleased) section lists what that release lacks, and each
> release's own guide is in its [tag](https://github.com/AGomnes/Cantrip/tags).

The Cantrip addon puts the rules engine inside a Godot game. Content loads from `res://`, one node
runs battles and hands everything to your scripts as ids and dictionaries, `.cantrip` files reach
exported builds, and the editor gets a dock for problems, tests and card text.

It needs **Godot 4.6.x, .NET edition** (tested on 4.6.1 and 4.6.2), but not C# of your own: a game
written entirely in GDScript drives it. The addon itself is C# source, because Godot finds scripts
by file path inside the project's own assembly, so only the engine library underneath it can be a
package.

What a GDScript project takes on by using it:

- **The .NET edition of Godot**, for everyone who opens the project, since the standard edition
  cannot run the addon's C#. Your GDScript works in it unchanged.
- **The .NET SDK**, which builds the C# part. You never edit that part.
- **Godot's limits on exporting a .NET game.** A .NET project cannot export to every platform
  Godot supports: not to the web at all, and to Android and iOS only experimentally, as
  [C# platform support](https://docs.godotengine.org/en/4.6/tutorials/scripting/c_sharp/index.html#c-platform-support)
  in Godot's documentation explains. With Cantrip, only a Linux x64 export has been tried; a
  Windows export has not been tried yet. [Platforms](stability.md#platforms) lists what has and
  has not.

## Installing

0. **Get the project ready for C#.**
   - Godot 4.6.x, .NET edition. The download page offers it beside the standard edition.
   - The .NET SDK 8 or later, from [dotnet.microsoft.com](https://dotnet.microsoft.com/download).
     In a terminal, `dotnet --version` should print 8 or higher.
   - A C# solution in the project. A GDScript-only project has none: open it and choose
     Project → Tools → C# → Create C# solution. That writes `YourGame.csproj` and `YourGame.sln`
     into the project folder.
   - Building, which later steps ask for, is the editor's **Build** button, the hammer at the top
     right, or `dotnet build` in the project folder.
1. **Put the addon in the project**, so that `res://addons/cantrip/plugin.cfg` exists. It comes
   from any of these:
   - the Godot Asset Library: search for Cantrip in the editor's AssetLib tab, and if it is not
     listed yet, use one of the other two;
   - `cantrip-godot-<version>.zip` on a [GitHub release](https://github.com/AGomnes/Cantrip/releases),
     whose root is `addons/cantrip`: unzip it into the project folder;
   - the [AGomnes/cantrip-godot](https://github.com/AGomnes/cantrip-godot) repository, which each
     release copies the addon to: copy its `addons/cantrip` folder.

   Until step 2, a build fails with many errors such as `CS0234: The type or namespace name
   'Content' does not exist in the namespace 'Cantrip'`. That is expected.
2. **Add the rules engine** from NuGet, in the folder with your `.csproj`, at the same version as
   the addon (the addon's is in its `plugin.cfg`):
   ```
   dotnet add package Cantrip.Core --version 0.1.0-preview.5
   ```
   `--prerelease` alone would install the newest preview, which may not match the addon you have.

   Offline, reference the DLL instead. The Cantrip.Core `.nupkg` attached to each GitHub release is a
   zip file: copy `lib/netstandard2.1/Cantrip.Core.dll` (and `Cantrip.Core.xml`, for editor help)
   out of it into a folder such as `lib/` beside your `.csproj`. It has no dependencies. Keep it out
   of `addons/cantrip/`, which an update replaces, and out of any `bin/` folder, which most
   `.gitignore` files leave out of your commits:
   ```xml
   <ItemGroup>
     <Reference Include="Cantrip.Core">
       <HintPath>lib/Cantrip.Core.dll</HintPath>
     </Reference>
   </ItemGroup>
   ```
3. **Build, then enable the plugin** in Project Settings → Plugins. Build first: until the
   assembly exists, Godot cannot load a C# plugin, and every node the addon adds is missing from
   the Create New Node dialog, with no error to say why. Once the plugin is enabled, a *Cantrip*
   dock appears at the bottom of the editor and the Output panel says
   `Cantrip: dock ready, ...`.
4. **Put `.cantrip` files under `res://content`**, where the node looks unless you set its
   `ContentFolder`.

## Two rules for GDScript

- **Members keep their C# names.** `rules.CreatePlayer(...)`, `rules.EffectEvent`. There is no
  snake_case alias: `load_content` does not exist.
- **A C# default argument is not a default in GDScript.** Every parameter must be passed.
  `rules.CreatePlayer()` is a parse error; `rules.CreatePlayer("Player", 80, 3)` is not.
  [The node](#the-node) lists every method with the values to pass.

## Your first battle

This plays the [quickstart](quickstart.md)'s battle in Godot, with one card added so that the
player has a choice to make. In the project folder, save the quickstart's content as
`content/game.cantrip`:

<!-- smoke: file content/game.cantrip -->
```
# A status: stacks add up, and at the end of its host's turn it deals that much damage and fades.
status Hex
  tags debuff
  stacking intensity
  on turn_end:
    deal stacks to owner, ignore block
    stacks -1

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

card Curse
  cost 1
  target enemy
  effect:
    apply Hex 3 to target
    if target.Hex >= 6:
      draw 1
  text: "Apply {Hex} Hex. If it now has 6 or more, draw {draw}."

enemy Ghoul
  hp 30
  move Claw:
    deal 7 to player
  move Howl:
    block 6
  pattern cycle Claw, Claw, Howl
```

and this card as `content/sift.cantrip`:

<!-- smoke: file content/sift.cantrip -->
```
# A card that asks the player something: which card to discard.
card Sift
  cost 0
  effect:
    discard 1
    draw 2
```

Write it in the dock's Source tab, or in any text editor; see
[The editor dock](#the-editor-dock). The quickstart explains the content and how to test it, and
[writing-content.md](writing-content.md) goes further.

Then create a scene whose root is a plain Node, attach a new script called `first_battle.gd` to
it, and replace the script's text with this:

<!-- smoke: file first_battle.gd -->
```gdscript
extends Node

var rules: CantripRuntime
var ghoul := 0

func _ready() -> void:
	# The node that runs the rules, made in code so that this script is the whole scene.
	rules = CantripRuntime.new()
	rules.AutoLoad = false  # load by hand below, to see what the parser found
	add_child(rules)

	var report: Dictionary = rules.LoadContent("res://content")
	for problem in report["diagnostics"]:
		print("%s:%d %s %s" % [problem["file"], problem["line"], problem["code"], problem["message"]])
	if not report["ok"]:
		return

	rules.EffectEvent.connect(_on_effect_event)
	rules.ChoiceRequested.connect(_on_choice_requested)
	rules.BattleEnded.connect(_on_battle_ended)

	rules.CreatePlayer("Player", 40, 3)  # name, hp, energy each turn
	rules.AddDeck(["Strike", "Strike", "Strike", "Defend", "Defend", "Curse", "Curse", "Sift"])
	ghoul = rules.SpawnEnemy("Ghoul", -1)  # -1: the hp its content gives it, 30
	rules.StartBattle(true, true)  # shuffle the draw pile, draw the opening hand

	# There is no UI yet, so the script plays for you, for at most 20 turns.
	for _turn in 20:
		if not rules.IsInBattle():
			break
		_take_turn()

# Stands in for the player: plays every card it can, then ends the turn. In a game, a card
# button calls Play and an End turn button calls EndTurn.
func _take_turn() -> void:
	print("Turn %d. You have %d hp. The Ghoul has %d hp and intends: %s" % [rules.GetTurn(),
		rules.GetStat(rules.PlayerId(), "hp"), rules.GetStat(ghoul, "hp"), rules.DescribeIntent(ghoul)["plain"]])

	for card in rules.GetZone(rules.PlayerId(), "hand"):
		if not rules.CanPlay(card):
			continue
		var target := ghoul if rules.GetTargetMode(card) == "enemy" else 0
		print("You play %s: %s" % [_name_of(card), rules.Describe(card, target)["plain"]])
		var result: String = rules.Play(card, target)
		if result != "played" and result != "pending":
			print("  Refused: %s" % result)
		if not rules.IsInBattle():
			return

	print("You end the turn.")
	rules.EndTurn()

# By the time this runs, the whole action has resolved. The events tell the story in order, each
# with the stats as they were at that moment.
func _on_effect_event(effect_event: Dictionary) -> void:
	var target: int = effect_event["target"]
	match effect_event["name"]:
		"damaged":
			var hp: int = effect_event["after"].get(target, {}).get("hp", 0)
			print("  %s loses %d hp, down to %d" % [_name_of(target), effect_event["amount"], hp])
		"gained_block":
			print("  %s gains %d block" % [_name_of(target), effect_event["amount"]])
		"status_applied":
			print("  %s gets %d %s" % [_name_of(target), effect_event["amount"], effect_event["values"]["status_name"]])

# Content such as `discard 1` asks the player. A game shows request["options"] and answers once
# the player has picked; this stand-in takes the first options, as few as it may.
func _on_choice_requested(request: Dictionary) -> void:
	var picked: Array = request["option_ids"].slice(0, request["min"])
	print("  Asked to %s; picking %s" % [request["prompt"], ", ".join(picked.map(_name_of))])
	var answer: Dictionary = rules.AnswerChoice(request["id"], picked)
	if not answer["accepted"]:
		print("  The answer was refused: %s" % answer["message"])

func _on_battle_ended(won: bool) -> void:
	print("The Ghoul falls." if won else "You fall.")

func _name_of(entity_id: int) -> String:
	return rules.GetEntity(entity_id).get("name", "")
```

Run the scene with F6 (Run Current Scene). The Output panel shows the whole battle, which the
Ghoul loses on turn 3. The first turn reads:

<!-- smoke: prints first_battle.gd -->
```
Turn 1. You have 40 hp. The Ghoul has 30 hp and intends: Deal 7 damage to the player.
You play Sift: Discard 1 card. Draw 2 cards.
  Asked to discard 1; picking Strike
You play Strike: Deal 6 damage.
  Ghoul loses 6 hp, down to 24
You play Defend: Gain 5 Block.
  Player gains 5 block
You play Defend: Gain 5 Block.
  Player gains 5 block
You end the turn.
  Player loses 0 hp, down to 40
```

The same seed plays the same battle, so yours reads the same. What the script relies on:

- **Loading.** With `AutoLoad` on, which is the default, the node loads `ContentFolder` itself as
  it enters the tree. Nothing hands your script what the parser found, so the node reports each
  error and warning in the Output panel, one line each, in the form `dotnet cantrip lint` uses:
  `res://content/game.cantrip:12:3: warning CT0101: ...`. Loading does not lint; the dock shows
  the linter's findings. This script turns `AutoLoad` off and calls `LoadContent`, which returns a
  report instead: `ok`, how many `errors` and `warnings` there were, and the `diagnostics`
  themselves. Load before anything else: once a player, a card or a query has brought the rules
  into being, `LoadContent` fails and [`ReloadContent`](#hot-reload) is the way to change content.
- **Every argument is passed.** `SpawnEnemy("Ghoul", -1)` passes -1 for the hp, which means the hp
  its content gives it; any number from 1 up overrides that, and 0 is refused, because an enemy
  with no health is not something to guess at. `StartBattle(true, true)` shuffles the draw pile and
  then draws the opening hand.
- **The player's hand is `GetZone(PlayerId(), "hand")`.** There is no `GetHand`: a zone belongs to
  whoever owns it, and a game with more than one hero has more than one hand.
- **`Play` answers with a word**: `played`, or why not (`not_enough_energy`, `invalid_target` and
  the others under [The node](#the-node)). `pending` means the card stopped for a choice. The
  `ChoiceRequested` signal has fired before `Play` returns, and because this stand-in answers at
  once, Sift has been played by then; a real game opens a picker and answers later.
- **`AnswerChoice` returns a dictionary**, not a word: `accepted`, a `reason` and a `message`
  saying why when it is false, and the `result` word when it is true. See
  [Choices the player makes](#choices-the-player-makes).
- **Events come after the action.** `EffectEvent` is emitted for each of the card's events before
  `Play` returns, but only after the whole play has resolved, which is why the script announces
  the card before calling `Play`. `amount` on `damaged` is the hp actually lost, so a hit that the
  block absorbs is `damaged` with 0.

A fuller version, with a hand of buttons, enemy panels with intents, a log and paced animation, is
the demo's [battle.gd](https://github.com/AGomnes/Cantrip/blob/main/godot/Cantrip.Demo/demo/battle.gd).
The addon download contains only the addon; to run the demo, clone the repository and open
`godot/Cantrip.Demo` in Godot .NET.

A `CantripRuntime` node can equally be added to a scene in the editor, with its exports set in the
Inspector, and found with `@onready var rules: CantripRuntime = $CantripRuntime`. The snippets on
the rest of this page assume `rules` is such a node, with its content loaded.

## The node

`CantripRuntime` is the only surface a script touches. Entities cross as `int` ids, and 0 means
none — except for the two arguments that name whose side a call acts on, `GetZone`'s `ownerId` and
`Execute`'s `selfId`, where 0 means the player. Everywhere a call takes a target, 0 means nobody.
Pass `PlayerId()` wherever the id is worked out rather than written down, so an id that comes out 0
cannot read someone else's pile. Everything else crosses as strings, numbers, arrays and
dictionaries with snake_case keys.

### Exports

| Export | Default | What it does |
|---|---|---|
| `ContentFolder` | `""` | Where `.cantrip` files are found, including every folder below it. Empty reads the `cantrip/content/folder` project setting, and `res://content` when that is unset. |
| `AutoLoad` | `true` | Loads `ContentFolder` when the node enters the tree, reporting its errors and warnings in the Output panel |
| `Seed` | `1` | Every random roll comes from it: the same seed and the same calls play the same game. Any 64-bit number is a seed of its own, 0 and negative ones included. |
| `Trace` | `false` | Records why things happened, for the debugger. It costs time. |
| `RealTime` | `false` | Runs on a tick clock instead of turns; see [Real time](#real-time) |
| `TicksPerSecond` | `60` | The tick clock's rate, which `cooldown 8s` in content converts through. A `Driver` is put on this rate as the node enters the tree, with a warning if it disagreed. |
| `TrackedStats` | `["hp", "block", "energy"]` | The stats each event records as they were at that moment, and the stats a choice's options carry. Read live: set it again and the next event and the next question both follow. |
| `Presenter` | none | A `BattlePresenter` that paces events; see [Pacing events](#pacing-events-with-a-battlepresenter) |
| `Driver` | none | A `TickDriver` for a real-time game |

`Seed`, `Trace`, `RealTime` and `TicksPerSecond` are read when the first call that sets up, plays
or reads the game brings the rules into being, so set them before that, and again by `NewRun`.
`Driver` is read when the node enters the tree. `TrackedStats` is read each time it is used.

### Signals

| Signal | When |
|---|---|
| `EffectEvent(effect_event: Dictionary)` | Once for each event, after the action that raised it has resolved and before the call that started it returns; for an action taken in a handler, see [Events](#events). Not emitted while a `Presenter` is set. |
| `ChoiceRequested(request: Dictionary)` | The rules are waiting for the player to choose. Emitted after the action's events. |
| `BattleStarted()` | `StartBattle` has started a battle |
| `BattleEnded(won: bool)` | The action that won or lost the battle has finished. **Act freely here**: hand out the reward, take the gold, start the next battle. See [Acting from a signal](#acting-from-a-signal). |
| `ContentReloaded(report: Dictionary)` | `ReloadContent` has run, and carries the same report it returned |

### Methods

The signatures are written GDScript-style. Where the C# method has a default, the table gives it:
that is the value to pass if you have no other in mind.

**Content**

| Method | |
|---|---|
| `LoadContent(folder: String) -> Dictionary` | Loads every `.cantrip` file under `folder` (`""` means `ContentFolder`) and returns a report: `ok`, `errors`, `warnings`, `diagnostics`. Only before any other call. |
| `ReloadContent(paths: Array) -> Dictionary` | Reloads the given `res://` files into a running game, or for `[]` every file under `ContentFolder`, even if `LoadContent` was given another folder. The same report, plus `rebound`, `missing` and `ruleset_changed`. See [Hot reload](#hot-reload). |
| `GetDefinitions(kind: String, tag: String) -> Array` | The names of every loaded definition of one kind, such as `"card"`, `"relic"` or `"enemy"`, that has `tag` among its tags, or of every one for `""`. Sorted by name, for a reward screen or a shop. Default tag: `""`. |

**Setting a game up**

| Method | |
|---|---|
| `CreatePlayer(name: String, hp: int, max_energy: int) -> int` | Creates the player, once per run. `max_energy` is the energy each turn starts with. Defaults: `"Player", 80, 3`. |
| `AddCard(name: String, zone: String) -> int` | Adds a card to one of the player's zones. Default zone: `"draw"`. A zone outside [the list below](#zones) still works, but says so in the Output panel. |
| `AddDeck(names: Array) -> Array` | Adds cards to the draw pile, returning their ids |
| `AddRelic(name: String) -> int` | Gives the player a relic |
| `SpawnEnemy(name: String, hp: int) -> int` | Adds an enemy. A negative `hp` uses the content's; 0 is refused. Default: `-1`. |
| `ApplyStatus(status: String, target_id: int, stacks: int) -> int` | Applies a status, as the player. Returns the status's id, or 0 when the target is unknown or nothing of that name is loaded. Default stacks: `1`. |
| `GrantAbility(name: String, owner_id: int) -> int` | Attaches an ability to an actor. Returns its id, or 0 when the owner is unknown or nothing of that name is loaded. |
| `RemoveCard(card_id: int) -> bool` | Takes a card out of the game for good, as `destroy` does in content, which hears it as `destroyed`. False, having changed nothing, when the id is not a card still in the game. |
| `AddHero(name: String, hp: int) -> int` | Adds a party member from a `hero` declaration, with the abilities its `abilities` line grants. A positive `hp` overrides the content's; 0 takes the content's own. Returns 0 when nothing of that name is declared as a `hero`. Default: `0`. |
| `NewRun() -> void` | Starts a new run: the rules begin again from the loaded content, with no player, cards or enemies, and read `Seed` and the other exports again. See [Between battles](#between-battles). |

**Playing**

| Method | |
|---|---|
| `StartBattle(shuffle: bool, draw_opening_hand: bool) -> void` | Starts a battle on whichever board is in play, which before the first battle is the content's default. Defaults: `true, true`. |
| `StartBattleOn(board: String, shuffle: bool, draw_opening_hand: bool) -> void` | The same, on a named `board`. `""` keeps the board in play. See [Boards](#boards). |
| `Play(card_id: int, target_id: int) -> String` | Plays a card from the hand, aimed at `target_id`, or 0 for none. Default target: `0`. |
| `PlayNamed(card_name: String, target_id: int) -> String` | Plays the first card of that name in the hand |
| `PlayBy(card_id: int, target_id: int, by_id: int) -> String` | The same play, made by a named party member. The cost still comes out of the card owner's pool; everything else — `source`, the damage, the statuses that apply — is the performer's. `by_id` 0 is `Play`. |
| `PlayNamedBy(card_name: String, target_id: int, by_id: int) -> String` | The same, by name: the card is looked for in that member's own hand first, then in the party's |
| `EndTurn() -> void` | Ends the party's turn: every member that has not acted gives its step up, the enemies act, and the next turn starts |
| `Pass(actor_id: int) -> void` | One member is done for this turn. When the last one that could act has passed, the enemies take theirs — so for a party of one this is `EndTurn`. Does nothing for an id that names nobody, or somebody who is not a party member. |
| `Revive(actor_id: int, hp: int) -> bool` | Brings a fallen actor back, and says whether it rose. False for an unknown id, for somebody already alive, and when content cancelled the `revived` event. `heal` refuses a corpse and always will, which is why this is its own call. Default: `1`. |
| `Tick(count: int) -> void` | Advances a real-time clock by `count` ticks. Default: `1`. In a turn game it fails with an error saying the runtime uses turns. |
| `UseAbility(ability_id: int, target_id: int) -> String` | Uses an ability, answering with the same words `Play` does. An ability with a `target` line settles its own target from 0, the way a card does, and answers `invalid_target` when there is nobody legal to aim it at. Default target: `0`. |
| `Execute(statements: String, self_id: int, target_id: int) -> void` | Runs statements as content would, for a console, a cheat key or a heal between battles. `self_id` 0 runs them as the player; `target_id` 0 means nobody. Defaults: `0, 0`. |

`Play`, `PlayNamed` and `UseAbility` answer `played`, `pending`
(see [Choices](#choices-the-player-makes)), `not_a_card`, `not_in_hand`, `unplayable`,
`not_enough_energy`, `invalid_target`, `cancelled` or `not_ready`. For an ability, `not_a_card`
means the id is not an ability that can be used — gone, or on a dead owner — and `not_ready` means
it is still on cooldown. `not_ready` never comes back from `Play`, and `not_in_hand` never from
`UseAbility`; the one table is shared so that one ending always has one word.

**Reading the game**

| Method | |
|---|---|
| `PlayerId() -> int` | The leader's id, or 0 before `CreatePlayer` |
| `GetParty() -> Array` | The living party, in the order the engine offers its members: the actors this game is asked for input for. A game that declares no `hero` gets one id, `PlayerId()`. Not the same as `GetAllies`, which counts summons. |
| `GetFallen() -> Array` | The party's dead, in the order they fell. `GetParty`, `GetAllies` and `GetActors` all leave them out, so this is the list a shrine that offers to raise somebody reads. Pair it with `Revive`. Content calls the same group `fallen`. |
| `ActiveMemberId() -> int` | The member whose step it is, or 0 when none of the party's is. Binding under `turns: initiative`; under `turns: sides` it is the one the engine would offer next, which a UI highlights and `CanAct` overrules. |
| `CanAct(actor_id: int) -> bool` | Whether that member still has a step this turn. False for an unknown id, for anyone who is not a party member, and for one that has passed. |
| `CanUse(ability_id: int) -> bool` | `CanPlay` for an ability: off cooldown, affordable, and with something legal to aim at if it needs one. False for an id that is not an ability. |
| `GetZone(owner_id: int, zone: String) -> Array` | Ids in one of an owner's zones; `owner_id` 0 means the player. The player's hand is `GetZone(PlayerId(), "hand")`. |
| `GetEnemies() -> Array`, `GetAllies() -> Array`, `GetActors() -> Array` | Ids of the living enemies, the living actors on the player's side, or both |
| `GetEntity(entity_id: int) -> Dictionary` | Everything a UI shows about one entity; empty for an unknown id |
| `GetStat(entity_id: int, stat: String) -> int` | One stat after modifiers, which is the number the rules would use now; 0 both when the id is unknown and when the entity has no such stat. A status is **not** a stat: ask `CounterOf`. |
| `CounterOf(entity_id: int, status: String) -> int` | How many of a status somebody is holding: stacks for a stacking status, 1 or 0 for one that does not stack. This is content's `Warden.Fervour` and C#'s `entity.CounterOf("Fervour")`. 0 for an unknown id and for somebody without it. A status bar wants this rather than `GetEntity(id)["statuses"]`, which builds every stat, tag and status to answer one number. |
| `SetStat(entity_id: int, stat: String, value: int) -> int` | Writes a stat, exactly as content's `speed = 6` does: the resource's own bounds, the `<stat>_changed` event, and death when hp reaches zero. Returns the change actually applied, which a bound or a listener may have cut short. 0 for an unknown id. |
| `ChangeStat(entity_id: int, stat: String, by: int) -> int` | Adds to a stat, or takes away with a negative amount: content's `gain 2 gold` and `lose 2 gold`. The way a shop spends the run's purse. |
| `CostOf(card_id: int) -> int` | What the card costs now; 0 for an unknown id |
| `CanPlay(card_id: int) -> bool` | Whether `Play` would accept it: in hand, affordable, with a legal target if it needs one |
| `GetTargetMode(card_id: int) -> String` | What the card is aimed at: whatever word content wrote after `target`, usually `"enemy"`, `"ally"`, `"self"`, `"any"` or `"none"`. An ability id works here too, and answers about its `target` line. `""` for an id that names nothing, which is how a stale id is told from a card needing no target. |
| `GetLegalTargets(card_id: int) -> Array` | The ids it may be aimed at, for highlighting, after the card's own `target … where` and content's `targetable` rules. An ability id works here too. Empty for a `target` word the rules do not recognise. |
| `IsInBattle() -> bool` | Whether a battle is running |
| `GetTurn() -> int` | The turn number, counted from 1 in each battle. 0 for the whole of a real-time battle, which takes none |
| `IsRealTime() -> bool` | Whether this runtime measures time in ticks. True when `RealTime` is set, and when the content's ruleset says `clock ticks`. A shared UI asks this before it draws an **End turn** button, because every turn-shaped call below refuses on a tick runtime |
| `GetTicks() -> int` | What time it is, in clock units: the tick-clock answer to `GetTurn`. This is the **game's** clock, the one a save brings back, and not `TickDriver.TotalTicks()` |
| `GetSeconds() -> float` | The same number in seconds, for an "18 / 45 seconds" read-out. 0 in a turn game, which measures no seconds |
| `CooldownLeft(ability_id: int) -> float` | Seconds until an ability comes back, for a sweep on a button. 0 when it is ready and for an id that is not an ability |
| `Place(actor_id: int, lane: int, rank: int) -> bool` | Stands an actor at that place, raising `moved`, which `before_moved` can refuse. What a game that spawns its own waves uses; content writes the same move as `target.rank = 0`. False for an id that names nobody on the board and for a place this board does not have |
| `EndBattle(won: bool) -> bool` | Ends the running battle as though the last enemy had fallen. What a game whose ruleset says `ends: called` uses to say the fight is over. False when no battle is running |
| `GetWon() -> Variant` | How the last battle ended: `true` if the player won it, `false` if not, and `null` while a battle runs and before the first has ended |
| `StateHash() -> String` | The whole rules state as 16 hex digits, for checking that two runs agree |

**Text**

| Method | |
|---|---|
| `Describe(entity_id: int, target_id: int) -> Dictionary` | Rules text with live values, for a card frame or a tooltip. `target_id` counts that target's statuses, or 0 for none. |
| `DescribeIntent(enemy_id: int) -> Dictionary` | What an enemy will do next and who to, with live values. `target` is the member it is telegraphing against and `target_name` is that member's name, so `line` reads `"Cutthroat → Vestal: Deal 8 damage and apply 2 Bleeding."` The target is asked afresh on every call, so a taunt applied since the intent was rolled has already moved it. Until intents have been rolled, its `empty` is true, its text is `""`, its `target` is 0, and its `name` is the enemy's own name rather than a move's. |
| `DescribeDefinition(name: String, kind: String) -> Dictionary` | A definition's rules text with its printed values, for something not in play, such as a reward. `kind` `""` takes the first definition of that name. Empty when none is loaded. Default kind: `""`. |

**Two vocabularies called kind.** `GetDefinitions` and `DescribeDefinition` take the keyword that
*declares* a definition in content — `"card"`, `"relic"`, `"enemy"`, `"status"`, `"ability"`. An
entity's `kind`, in the dictionary `GetEntity` gives, says what it *is* in the rules — `"actor"`,
`"card"`, `"status"`, and so on. They overlap but are not the same list: a Slime declared with
`enemy Slime` is an `"actor"` once it is in play, so `DescribeDefinition(name, "actor")` and
`GetDefinitions("actor")` find nothing at all. Pass an entity's `kind` to either of them and the
answer is empty, with no error.

**Choices**

| Method | |
|---|---|
| `HasPendingChoice() -> bool` | Whether the rules are waiting for an answer |
| `GetPendingChoice() -> Dictionary` | The same dictionary `ChoiceRequested` carried; empty when nothing is waiting |
| `AnswerChoice(request_id: int, chosen: Array) -> Dictionary` | Answers it with option ids, then finishes the action |
| `CancelChoice() -> void` | Abandons the action that asked, leaving the game as it was before it |

**Saving**

| Method | |
|---|---|
| `CanSave() -> bool` | Whether `Save` would succeed now, for greying out a button |
| `Save() -> Dictionary` | The whole game under `save`, or `accepted` false with a `reason` and a `message` saying why not |
| `LoadSave(json: String) -> Dictionary` | Restores a saved game, or says why not, in the same shape |

**Callbacks**

| Method | |
|---|---|
| `RegisterName(name: String, callable: Callable) -> void` | Answers a name content uses that only your game knows |
| `RegisterFunction(name: String, callable: Callable) -> void` | Answers a function content calls that only your game knows |

See [Callbacks from content](#callbacks-from-content). A C# game has two more members: `Core`,
the `CardRuntime` underneath, which is a new object after `NewRun`, and `Content`, the loaded
`ContentLibrary`.

### Zones

A zone is a string. The ones a card can be in, for `AddCard` and `GetZone`:

| Zone | Holds |
|---|---|
| `"draw"` | The draw pile |
| `"hand"` | The hand |
| `"discard"` | The discard pile |
| `"exhaust"` | Cards exhausted for the rest of the battle |
| `"powers"` | Played cards that stay in effect for the rest of the battle |
| `"play"` | A card while it is being played |

The player's relics are in `"relics"`. The `zone` in an entity's dictionary can also be `"board"`
for an actor in the battle, `"attached"` for a status, keyword or ability, `"dead"` for an enemy
that died, until the next battle starts, or `""`.

Those eleven are the whole list. The rules take any other string as a zone too, so a game can
invent one, but `AddCard` and `GetZone` say so in the Output panel when they are given a name that
is not one of them — because `AddCard("Guard", "hnd")` otherwise makes a real card in a pile
nothing will ever draw from, and answers no differently from the zone you meant.

### Boards

A battle is fought on a **board**: lanes across and ranks along the facing axis, both counting from
0. Content declares the shapes â [Boards](language.md#boards) in the language reference says how â
and a game with more than one says which fight is fought where:

```gdscript
rules.StartBattleOn("Nave", true, true)   # this fight is in the nave
rules.StartBattle(true, true)             # this one is wherever the last one was
```

`StartBattleOn` is a method of its own rather than a third argument to `StartBattle` for the reason
under [Two rules for GDScript](#two-rules-for-gdscript): a C# default is not a default here, so a
third parameter would have been a parse error in every game that already calls `StartBattle`. An
empty name keeps the board in play, which before the first battle is the content's default. A name
no `board` declaration matches is refused rather than invented, because the linter has to know how
deep a board is to check what reaches across it.

With no `board` declared at all, content gets one lane, unbounded ranks, facing sides and a
manhattan metric â today's board, spelled out â so a game that never mentions one never notices any
of this.

Where each actor stands is in its entity dictionary, as `lane` and `rank`:

```gdscript
var who: Dictionary = rules.GetEntity(id)
print("aisle %d, rank %d" % [who["lane"], who["rank"]])
```

Those two are the whole of a board a front end needs: lay the party out by `(lane, rank)`, and
`GetLegalTargets` already answers what is in reach, so a highlight needs no distance arithmetic of
its own. Content moves an actor by writing `target.rank = 0`; nothing on the node does, because
where somebody stands is a rule and not a view.

### Dictionaries

**An entity**, from `GetEntity`:

| Key | |
|---|---|
| `id`, `name` | Its id and its name in content |
| `kind` | `"actor"`, `"card"`, `"status"`, `"relic"`, `"ability"`, `"keyword"`, `"item"` or `"global"` |
| `team` | `"player"`, `"enemy"` or `"neutral"` |
| `zone` | Where it is (see [Zones](#zones)) |
| `lane`, `rank` | Where it stands on the board, both counting from 0. `position` is the older name for `rank` and carries the same number |
| `alive`, `dead`, `removed` | A body still on the board is `dead`: neither alive nor removed |
| `intent` | An enemy's next move, such as `"Claw"`; `""` before the battle starts |
| `owner`, `source` | Whose it is (a card's player, a status's host) and who made or applied it; 0 for none |
| `party_member` | True for an actor the game is asked for input for: the leader and every `hero`. A summoned minion is an ally and not a member, which is what decides whether it takes a step and whether the battle is lost when it falls. |
| `acted` | True when it has already taken its step this round |
| `tags` | Its tags, sorted |
| `stats` | Every stat after modifiers, such as `{"hp": 24, "max_hp": 30, "block": 0}` |
| `statuses` | Its statuses and keywords, in the order they arrived; each as below |
| `abilities` | Ids of its abilities |

Each status in `statuses` has `id`, `name`, `stacks`, `duration` (turns left, or 0), `counter`,
`hidden` and `tags`. `counter` is the number to show beside the icon: the turns left for a
duration status, the stacks for any other. `hidden` is true for `flags hidden`, which a status bar
leaves out.

**Rules text**, from `Describe`, `DescribeIntent` and `DescribeDefinition`:

| Key | |
|---|---|
| `plain` | `"Deal 9 damage."` |
| `bbcode` | The same, with a changed value struck through and coloured: `"Deal [s]6[/s] [color=#6fcf6f]9[/color] damage."` |
| `segments` | The text in runs, for drawing it yourself; each as below |
| `tooltips` | The statuses and keywords it mentions, explained, each with `name`, `plain` and `bbcode` |
| `cost` | The card's cost as a segment, or `null` when the entity has no cost. Always present. |
| `name` | The definition's name, or for an intent the move's name once intents have been rolled |
| `flavour` | The flavour line, never mixed into the rules text; `""` when there is none |
| `level` | Where the words came from: `"auto"`, `"custom"` (a `text:` line) or `"override"` (a `text_override:` line, shown as written, without live values) |
| `target`, `target_name` | For an intent, the member it is telegraphed against, by id and by name. 0 and `""` for everything else, and for an intent before it has been rolled. |
| `line` | The whole thing on one line, as an intent panel shows it: `"Cutthroat → Vestal: Deal 8 damage and apply 2 Bleeding."` For a card it is its name and its text. |
| `empty` | True when there is nothing to show, as for an intent before the battle starts |

Each segment has `kind` (`"text"` or `"value"`), `text` (what to show), `placeholder` (the value
it stands for, such as `"damage"`, or `""`), `has_number`, `base` and `current` (the printed and
the live number), `base_text` (the printed number as text), `changed`, `trend` (`"unchanged"`,
`"buffed"` or `"debuffed"`, as the player sees it) and `lower_is_better` (true for a cost).

**A choice**, from `ChoiceRequested` and `GetPendingChoice`:

| Key | |
|---|---|
| `id` | The request's number, to answer with. Never reused. |
| `mode` | `"entities"` to choose among things in play; `"offer"` for `discover` |
| `prompt` | What content asked, such as `"discard 1"` |
| `min`, `max` | How many options to pick |
| `option_ids` | What to answer with: entity ids, or for an offer the candidates' places, `1, 2, 3...` |
| `options` | For `"entities"`, one entity dictionary for each option, carrying only the `TrackedStats` rather than every stat `GetEntity` gives. For `"offer"`, the candidates' `name`, `kind`, `tags` and rules `text`. |
| `chooser` | The id of the one choosing, usually the player |
| `file`, `line` | The line of content that asked |

The request says `mode`, not `kind`, because `kind` already means three other things in these
dictionaries: what an entity is, what keyword declared an offered definition, and whether a segment
is text or a value. So `options[i]["kind"]` speaks a different vocabulary depending on `mode`.

**The answer**, from `AnswerChoice`: `accepted`, `reason`, `message` and `result`. `result` is the
same word `Play` gives when the answer was accepted, and `""` when it was refused; it is always
there. `message` says in a sentence what `reason` says in a word:

| `reason` | |
|---|---|
| `"none"` | The answer was accepted |
| `"nothing_pending"` | No choice is waiting |
| `"stale_request"` | The request has already been answered or cancelled, and another is waiting |
| `"unknown_option"` | A pick that is not in `option_ids` |
| `"duplicate_option"` | The same pick twice |
| `"too_few"` | Fewer picks than `min` |
| `"too_many"` | More picks than `max` |

**A content report**, from `LoadContent`, `ReloadContent` and `ContentReloaded`: `ok` (true when
nothing was an error), `errors` and `warnings` (how many of each), and `diagnostics`. `ok` is there
so that no caller has to scan the array to find out whether its game has content to play.

**A problem**, in that `diagnostics` array: `severity` (`"error"`, `"warning"` or `"info"`), `code`
(such as `"CT0101"`), `message`, `suggestion`, `file`, `line` and `column`.

**A reload**, from `ReloadContent` and `ContentReloaded`: the content report above, plus `rebound`
(how many entities were rebound to their new definitions), `missing` (names of definitions that have
gone) and `ruleset_changed`.

**A save**, from `Save`: `accepted`, `reason`, `message` and `save`, the whole game as a string.
`reason` is `"none"`, `"resolving"` (effects are still running, which only a
[callback](#callbacks-from-content) sees) or `"reload_pending"` (a waiting block a reload has
changed; see [Saving](#saving)). `save` is `""` when it was refused.

**A load**, from `LoadSave`: `accepted`, `reason` and `message`. `reason` is `"none"`,
`"wrong_format"` (not a save, a damaged one, or one from a version of the addon or of Cantrip.Core
that writes saves differently), `"no_payload"` or `"content_changed"` (the content has changed in a
way the save cannot survive; see [Saving](#saving)).

**An event** is under [Events](#events).

## Events

The rules resolve an action completely and at once; presentation watches afterwards.

- **Events are emitted before the call that caused them returns, but only after the whole action
  has resolved**, never part way through it; with a [`Presenter`](#pacing-events-with-a-battlepresenter),
  they go to it then, and it hands them on one at a time. So acting again from a handler is fine,
  such as answering a choice or playing the next card, because by then nothing is resolving. An
  action taken in an `EffectEvent` handler resolves at once as well, and its events are emitted
  after the rest of those already on their way, still before the outer call returns; `BattleEnded`
  comes after all of them. What is refused, with an error, is acting from a
  [callback](#callbacks-from-content): those run in the middle of an effect.
- **They arrive in completion order, innermost first.** An event that wraps others completes after
  them: playing a card reports `damaged`, then `status_applied`, then `card_played`.
- **Each event carries the stats it changed, as they were then** (`after`), because by animation
  time the whole action has resolved and live stats show only the final numbers. Which stats are
  captured is the node's `TrackedStats`.

A Strike on the Ghoul, the first card played in a battle set up as in
[Your first battle](#your-first-battle):

```gdscript
{ "seq": 8, "name": "damaged", "phase": "after", "time": 0,
  "source": 1, "target": 10, "card": 2,
  "amount": 6, "amount_raw": 6.0, "replaced": false, "tags": ["attack"],
  "values": { "base": 6.0, "total": 6.0, "blocked": 0.0, "overkill": 0.0 },
  "after": { 10: { "hp": 24, "block": 0 }, 1: { "hp": 40, "block": 0, "energy": 2 } } }
```

| Key | |
|---|---|
| `name` | The event, such as `"damaged"`. [Built-in events](language.md#built-in-events) lists them with their data. |
| `source`, `target`, `card` | Ids: who caused it, whom it happened to, the card involved; 0 for none |
| `amount` | The number a UI prints, rounded |
| `amount_raw` | The exact amount, as a float |
| `values` | The event's data, such as `blocked` on `damaged` or `status_name` on `status_applied`. Numbers arrive as floats and entities as ids. |
| `after` | For each entity that took part, keyed by its id as an `int`, the tracked stats as they were |
| `tags` | The event's tags: the damage type, the card's tags |
| `seq` | Its position in the run of events, rising by one each time. It starts again from 1 after `LoadSave` and `NewRun`. |
| `time` | The game clock when it happened: the tick in a real-time game. In a turn game it counts turns from 0 and does not start again with each battle, so it is not the turn number; `GetTurn()` gives that. |
| `replaced` | True when an `instead` listener ran in place of the usual action |
| `phase` | Always `"after"` |

### Acting from a signal

**A signal handler may do anything a game may do.** By the time one runs, the action that caused
it has resolved, so `Execute`, `Play`, `Pass`, `AddCard`, `StartBattle` and the rest are ordinary
calls there, and whatever they raise is told before the outer call returns. Hand out the reward
where you would write it:

```gdscript
func _on_battle_ended(won: bool) -> void:
	if not won:
		show_game_over()
		return
	rules.ChangeStat(rules.PlayerId(), "gold", 30)   # the reward, right here
	show_reward_screen()                              # which calls StartBattle when it closes
```

**Starting the next battle from inside `BattleEnded` is ordinary too.** The battle it starts is
the one in play when the handler returns, and its own end is told when it comes. There is one thing
to know about the screen rather than the rules: a `BattleEnded` handler runs *while the play that
won is still returning*, so whatever that play does afterwards â a `_refresh()` that rebuilds the
hand â runs after your reward screen is already up. Leave the between-battles screen alone when
`IsInBattle()` is false.

Until 1.0 the first of those was a hard crash: a call from a `BattleEnded` handler re-entered it,
and the process died with a stack overflow and no diagnostic. Reads were fine and writes were
fatal. Nothing about it needs a workaround now, and `call_deferred` around the reward is no longer
doing anything for you.

## Pacing events with a BattlePresenter

Without help, an action's events arrive together, in one frame. A `BattlePresenter` hands them
over one at a time and waits until your animation says it has finished, so an action that resolved
instantly plays out as a sequence:

```gdscript
var presenter := BattlePresenter.new()

func _ready() -> void:
	add_child(presenter)
	rules.Presenter = presenter  # from now on, events come through Present, not EffectEvent
	presenter.Present.connect(_on_present)
	presenter.Settled.connect(_on_settled)

func _on_present(effect_event: Dictionary) -> void:
	print(effect_event["name"])  # start the animation for it here
	await get_tree().create_timer(0.3).timeout  # stands in for the animation
	presenter.Done()  # then the next event comes

func _on_settled() -> void:
	print("All shown.")  # read the live state again and redraw
```

Call `Done()` once for each `Present`. `IsBusy()` is true while an event is being shown, so
gate input on it; `SkipAll()` drops the rest of the queue for a player who does not want to watch.
`BattleEnded` and `ChoiceRequested` do not wait for the presenter: show a victory screen or a
picker after `Settled`. The presenter can also be a node in the scene, set as the
`CantripRuntime`'s `Presenter` in the Inspector.

## Card text and intents

`Describe` gives a card's rules text with the numbers as they are now. `bbcode` is ready for a
`RichTextLabel` with BBCode enabled: a value that a modifier changed shows the printed number
struck through beside the live one, green when it is better for the player and red when it is
worse. Pass the target the player is aiming at, and its statuses count too.

```gdscript
@export var card_text: RichTextLabel  # with BBCode Enabled ticked in the Inspector
@export var intent_text: Label

func show_card(card: int, aimed_at: int) -> void:
	var text: Dictionary = rules.Describe(card, aimed_at)
	card_text.text = text["bbcode"]  # "Deal [s]6[/s] [color=#6fcf6f]9[/color] damage."
	for tooltip in text["tooltips"]:
		print(tooltip["name"], ": ", tooltip["plain"])  # each status or keyword it mentions

func show_intent(enemy: int) -> void:
	var intent: Dictionary = rules.DescribeIntent(enemy)
	intent_text.text = intent["plain"]  # "Deal 7 damage to the player."
	# intent["name"] is the move, "Claw", for choosing an icon
```

An intent is described with the numbers it would deal now, after the enemy's buffs and the
player's debuffs, and its trends are turned round: an enemy that hits harder is `"debuffed"`, red,
because that is worse for the player. For your own colours or icons, draw `segments` instead of
`bbcode`, and use `cost` for the card's cost. The keys are under [Dictionaries](#dictionaries).

## Choices the player makes

Content such as `discard 1` or `choose 1 from hand` needs an answer a UI cannot give on the spot.
The runtime rolls the action back to where it started, emits `ChoiceRequested`, and replays the
action once the choice is answered. The rollback is an exact snapshot, so the replay follows the
identical path, and nothing reaches the game for the rolled-back attempt: no events, no animations.

```gdscript
func _on_choice_requested(request: Dictionary) -> void:
	# Show request["options"] and wait for the player. Here, the first request["min"] of them:
	var answer: Dictionary = rules.AnswerChoice(request["id"], request["option_ids"].slice(0, request["min"]))
	if not answer["accepted"]:
		push_warning(answer["message"])  # the question stays open: ask again
	elif answer["result"] == "pending":
		pass  # the replay asked something else, and ChoiceRequested has fired again
```

`min` and `max` say how many options to pick: `discard 2` needs both at once, in one answer.
Unlike the C# runtime, the node refuses an answer with too few or too many picks, a pick twice, or
a pick that was not offered, and leaves the choice open; the answer's `reason` says which, in the
words listed under [Dictionaries](#dictionaries). `CancelChoice()` abandons the action instead,
leaving the game as it was.

Listen for the signal rather than checking what `Play` returned, because more than `Play` can
stop. A relic that asks for a choice at the start of a turn stops `EndTurn`; one that asks at the
start of a battle stops `StartBattle`. `Execute`, `UseAbility` and `AnswerChoice` itself can stop
too; `UseAbility` says so with the same `pending` word `Play` uses. `AddRelic`, `ApplyStatus` and
`Tick` never do: a choice they raise takes the first option.

Most choices are between things already in play, and `option_ids` are their entity ids. `discover`
offers content that does not exist yet, so its request has `"mode": "offer"`: each of `options` is a
candidate's `name`, `kind`, `tags` and rules `text`, and `option_ids` are their places in the offer,
`[1, 2, 3]`, counted from 1 so that 0 goes on meaning "no entity" as it does everywhere else.
Answer with the place of the one the player picked, which is its index in `options` plus one:

```gdscript
func _on_offer(request: Dictionary, picked_index: int) -> void:
	if request["mode"] == "offer":
		rules.AnswerChoice(request["id"], [request["option_ids"][picked_index]])
```

## Between battles

One node plays a whole run. When a battle ends, every card the player owns goes back to the draw
pile, so between battles the draw pile, `rules.GetZone(0, "draw")`, is the deck.

| Carries over | Starts afresh |
|---|---|
| The player, with its hp, max hp and any other stats | Enemies: the dead are removed when the next battle starts |
| Every card still in the game, back in the draw pile, exhausted cards and cards made during the battle included | The player's statuses, unless flagged `persistent` |
| Relics, and `once per run` limits | `until` effects, which are undone, and scheduled work, which is dropped |
| | `once per battle` limits, the battle's history counters and the turn number |

A card made during a battle, such as a Wound, stays in the deck like any other; take it out with
`RemoveCard` to make it last only for the battle.

The map, the rewards and the encounters are your game's code. Between battles, hand out rewards,
heal, spawn the next encounter and start again:

```gdscript
func _on_battle_ended(won: bool) -> void:
	if not won:
		show_game_over()
		return
	rules.ChangeStat(rules.PlayerId(), "gold", 30)  # the reward, in the handler that says it is due
	show_reward_screen()  # which calls start_next_battle when it closes

func start_next_battle() -> void:  # when the reward screen closes
	rules.AddCard("Curse", "draw")  # a reward
	rules.AddRelic("Lantern")  # any relic your content defines
	rules.Execute("heal 12", 0, 0)  # a rest: statements run as the player
	rules.SpawnEnemy("Ghoul", -1)  # the next encounter, at the hp its content gives it
	rules.StartBattle(true, true)
```

Either function may act: see [Acting from a signal](#acting-from-a-signal).

Outside the `BattleEnded` handler, `GetWon()` says how the last battle ended: `true` or `false`,
or `null` while a battle is running and before the first one has ended.

`Execute` suits a step like the rest above: a line or two, run now and then. It is checked only
when it runs, so anything longer, or anything a card, relic or status should own, belongs in
content, where the linter and your tests see it.

### Removing, upgrading, rewards and a new run

The example below removes a card from the deck, upgrades one, offers three reward cards with their
text, and starts a new run. Beside the first battle's content it uses these cards, saved as
`content/rewards.cantrip`. An upgraded card is a definition of its own, named here after the card
with a `+`, and tags mark the reward pool, as [Card upgrades](writing-content.md#card-upgrades)
explains:

<!-- smoke: file content/rewards.cantrip -->
```
card "Strike+"
  cost 1
  target enemy
  tags attack, upgraded
  effect:
    deal 9 to target

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

card Brace
  cost 1
  tags skill, reward
  effect:
    block 8

card Flurry
  cost 1
  target enemy
  tags attack, reward
  effect:
    deal 4 to target
    deal 4 to target
```

These functions go in the script that holds `rules`:

<!-- smoke: file between_battles.gd -->
```gdscript
# Between battles the deck is the draw pile.
func deck() -> Array:
	return rules.GetZone(0, "draw")

# At a shop, or to lift a curse: the card leaves the game for good.
func remove_card(card: int) -> void:
	rules.RemoveCard(card)

# An upgrade is the upgraded definition, put in the deck in place of the card.
func upgrade_card(card: int) -> bool:
	var upgraded: String = rules.GetEntity(card)["name"] + "+"
	if rules.DescribeDefinition(upgraded, "card").is_empty():
		return false  # this card has no upgrade
	rules.RemoveCard(card)
	rules.AddCard(upgraded, "draw")
	return true

# Three reward cards, with their text, picked by a RandomNumberGenerator your game seeds with the run.
func offer_rewards(rng: RandomNumberGenerator) -> Array:
	var upgraded: Array = rules.GetDefinitions("card", "upgraded")
	var pool: Array = rules.GetDefinitions("card", "reward").filter(func(card_name): return not upgraded.has(card_name))
	var offer: Array = []
	while offer.size() < 3 and not pool.is_empty():
		offer.append(pool.pop_at(rng.randi_range(0, pool.size() - 1)))
	for card_name in offer:
		print("%s: %s" % [card_name, rules.DescribeDefinition(card_name, "card")["plain"]])
	return offer  # the one the player picks goes in with rules.AddCard(card_name, "draw")

# After a loss, or from the title screen: a new player and deck on the same node.
func new_run(run_seed: int) -> void:
	rules.Seed = run_seed
	rules.NewRun()
	rules.CreatePlayer("Player", 40, 3)
	rules.AddDeck(["Strike", "Strike", "Strike", "Defend", "Defend", "Curse", "Curse", "Sift"])
```

With that content, `offer_rewards` offers Brace, Cleave and Flurry in an order the generator
decides, and prints lines such as `Cleave: Deal 8 damage to ALL enemies.` and
`Flurry: Deal 4 damage. Deal 4 damage.`

- **`RemoveCard`** takes the card out of the game for good, as `destroy` does in content, so a relic
  that listens for `destroyed` hears it. It returns false, and changes nothing, for an id that is
  not a card still in the game.
- **`GetDefinitions`** lists the names of the definitions of one kind, and with one tag unless that
  is `""`. To leave a tag out, list that tag as well and filter, as `offer_rewards` does with
  `upgraded`. The list is sorted by name, so picks made from it with a generator seeded for the run
  replay. **`DescribeDefinition`** gives the same dictionary as `Describe`, with the printed
  numbers. Both read the content alone, so a card library can use them before any run has started.
- **`NewRun`** starts again on the same node. The rules begin anew from the loaded content, with no
  player, cards or enemies, and read `Seed` and the other exports again, so set the new seed first.
  What belonged to the old run goes with it: a choice waiting for an answer (answering it now gives
  `nothing_pending`), events not yet emitted, and whatever the `Presenter` still had to show. No
  `BattleEnded` is emitted for a battle it cuts short. The content stays as it is, reloads
  included, and a ruleset a reload has changed takes effect. The callables registered with
  `RegisterName` and `RegisterFunction` stay registered, and signal connections stay connected.

## Saving

```gdscript
func save_game() -> void:
	var saved: Dictionary = rules.Save()
	if saved["accepted"]:
		FileAccess.open("user://slot1.json", FileAccess.WRITE).store_string(saved["save"])
	else:
		print(saved["message"])  # reason "resolving" or "reload_pending"

func load_game() -> void:
	var result: Dictionary = rules.LoadSave(FileAccess.get_file_as_string("user://slot1.json"))
	if not result["accepted"]:
		print(result["message"])  # reason "content_changed": the save needs content a patch removed
```

`Save()` answers the same `accepted`, `reason`, `message` dictionary `LoadSave` does, with the game
itself under `save`, so a save button can tell the player why it did nothing. `CanSave()` answers
the same question as a bool, for greying that button out; it cannot say which of the two refusals
applies, and its answer can be stale by the time you act on it, so check what `Save()` returns
rather than calling `CanSave()` first.

A save is refused while effects are resolving, which only a [callback](#callbacks-from-content)
sees, with the reason `"resolving"`. It is also refused after `ReloadContent` has changed a waiting
`next turn:` or `in N turns:` block, until that block has run, with the reason `"reload_pending"`:
the block runs the statements it was scheduled with, and a save can only name statements the loaded
content still has. The message names the definition the block belongs to.

A save holds the whole game, the player, the piles and the enemies included, so it loads into a
node that has only loaded its content: there is no need to create a player first.

A save from before a content patch loads as long as the content still has everything the save
needs. A patch that only adds a card, or only changes numbers or effects, keeps that true, except
for a waiting block whose statements it changes, below. The save carries a fingerprint of the
content it was taken against, which adding, renaming or removing a definition changes, but a
different fingerprint does not turn a save away by itself: the rules look up what the save needs
as they restore it, before they change anything.

A save is refused, with the `reason` `"content_changed"`, when:

- a definition it names has since been renamed or removed. The message names it, as in
  `The snapshot needs card "Sift", which is not loaded.`
- a `next turn:` or `in N turns:` block was waiting when the game was saved, and a patch has since
  changed that block's statements. The message names the definition the block belongs to. A save
  made by 0.1.0-preview.2 or earlier finds such a block by its place alone instead: it runs whatever
  block is in that place now, and is refused only if there is none.

A refused save leaves the game as it was, a choice it is waiting on included. What a patch does to
the stats and listeners of a save that loads is under
[Saves after a content update](stability.md#saves-after-a-content-update).

A save is trusted input: the node restores whatever the file holds, including statements the game
runs later, so a game that loads saves it did not write itself, such as shared or downloaded ones,
should check them first, for example with a signature.

## Hot reload

`ReloadContent` reloads `.cantrip` files into a running game and rebinds everything live to them:

```gdscript
func reload_content() -> void:  # call it from a debug key, for example
	var report: Dictionary = rules.ReloadContent([])  # [] reloads every file under ContentFolder
	if not report["ok"]:
		print("%d error(s); the game is running on what loaded" % report["errors"])
	for problem in report["diagnostics"]:
		print(problem["message"])
```

Stats the game has changed keep their values, while a card still at its printed cost takes the new
one. A `once per` listener that has fired stays used, and an `on every` listener stays on its
interval, unless the reload changed its `on` line. The report is `LoadContent`'s, so `ok` says
whether anything was an error, and it also says how many entities rebound, which definitions have
gone, and whether the ruleset changed: a running game keeps the rules it started with until
`NewRun`. A reload with errors in it is still applied — what parsed is in, and `ok` is false — so
check it before telling a designer the change went through. The `ContentReloaded` signal carries
the same dictionary, so a game that watches it sees everything the caller does. Wire it to a debug
key, and a designer can change a number, save, press the key and play on.

## Callbacks from content

Content can use a name or call a function the rules do not know, such as `front_row` or
`ascended(6)`, and your game answers it. Register a method of your script:

```gdscript
var ascension := 2  # chosen when the run starts, and saved by your game with the run

func _ready() -> void:
	rules.RegisterName("front_row", _front_row)  # deal 2 to front_row
	rules.RegisterFunction("ascended", _ascended)  # deal ascended(6) to player

func _front_row(context: Dictionary) -> Variant:
	return rules.GetEnemies().slice(0, 2)  # entity ids

func _ascended(args: Array, context: Dictionary) -> Variant:
	return int(args[0]) + ascension  # arguments arrive as floats
```

A name's method gets `context`; a function's gets `args` and then `context`. `context` holds the
ids of `self`, `source`, `target` and `card`, and the name of the `event` being handled, if any.
Return a number, a bool, a string, an array of entity ids (one entity is `[id]`, since a bare
number is a number), or null for no answer.

Any Callable will do, not only a method: a lambda suits a one-line answer, and `.bind()` passes
more arguments after `context`. Registering a name again replaces its callable. One that cannot be
called, such as a method the object does not have, is refused with an error as you register it;
one that takes the wrong arguments fails with an error naming it when content first asks.

```gdscript
func _register_more() -> void:
	rules.RegisterName("ascension", func(_context: Dictionary) -> Variant: return ascension)
	rules.RegisterFunction("elite", _scaled.bind(3))  # elite(6) calls _scaled([6.0], context, 3)

func _scaled(args: Array, _context: Dictionary, factor: int) -> Variant:
	return int(args[0]) * factor
```

Callbacks run in the middle of an effect, or of a query such as `CanPlay` or `Describe` that
works a number out from content. They may read the game (`GetEntity`, `GetStat` and the other
queries) but not change it: every call that would, from `CreatePlayer`, `AddCard` and
`SpawnEnemy` to `Play`, `EndTurn`, `LoadSave` and `ReloadContent`, fails there with an error that
says so, and `Save` fails in the middle of an effect. They must also give the same answer every
time the same game asks, or a seed stops replaying and a choice replays differently: answer from
the node's queries and from values fixed for the run, in whole numbers.
[Determinism](stability.md#determinism) lists what to avoid, `randi()` among it.

The linter reports a name it does not know (CT302). To keep it quiet about the names your game
answers, list them in the dock's settings (see [The editor dock](#the-editor-dock)). That quiets
the linter only: the dock's Tests tab runs without your callbacks, so a test of content that uses
one fails there.

## When a call fails

A call the rules cannot carry out, such as `SpawnEnemy` with a name no content defines, a second
`CreatePlayer` in one run, or content that fails while it runs, prints the error to the Output
panel with the C# exception's message and returns null, even where the method is declared to
return an `int`.
Your script carries on, so watch the Output panel.

That is for a call the rules cannot carry out at all. The answers a game reads are not like this:
`Play` and `UseAbility` answer a word, `Save`, `LoadSave`, `LoadContent`, `ReloadContent` and
`AnswerChoice` a dictionary that says whether it worked, and `ApplyStatus` and `GrantAbility` 0 for
a name nothing defines as well as for an id nothing names. None of those is a failure to watch the
console for.

A typed variable does not catch that null. Stored in one, as in
`var result: String = rules.Play(card, target)`, it raises no second error, and `result == null`
is false there, so that check misses the failure. To check a call that can fail, keep its result
in an untyped variable and compare that with `null`:

<!-- smoke: file spawn.gd -->
```gdscript
func spawn(enemy_name: String) -> int:
	var spawned = rules.SpawnEnemy(enemy_name, -1)  # untyped, so a failure can be seen
	if spawned == null:
		return 0  # no enemy: the Output panel says why
	return spawned
```

An action whose content failed is not rolled back: what it did before the failure stays done, and
the events it had collected are dropped. Content errors name the file and line; the linter and the
dock's Tests tab catch most of them before the game runs.

## Real time

Set `RealTime`, and add a `TickDriver` as the node's `Driver` before the node enters the tree, for
example in the Inspector. The driver advances the clock from `_PhysicsProcess` only, in whole ticks,
because the length of a rendered frame is not an input a deterministic game can use. Both nodes
have a `TicksPerSecond`, and the runtime's is the one `cooldown 8s` in content converts through: as
the runtime enters the tree it puts its driver on that rate, warning in the Output panel if the two
disagreed, so a driver left at another rate can no longer make a cooldown mean two different lengths
of time. Any rate works against any physics rate.

```gdscript
func _ready() -> void:
	rules = CantripRuntime.new()
	rules.RealTime = true
	rules.TicksPerSecond = 20

	driver = TickDriver.new()          # before the runtime enters the tree
	rules.Driver = driver
	add_child(driver)
	add_child(rules)

	rules.LoadContent("res://content")
	driver.Ticked.connect(_on_ticked)  # the one place to act between ticks

func _on_ticked(_count: int) -> void:
	_refresh()                         # hp bars, cooldown sweeps, "%d seconds" % rules.GetSeconds()
```

**The driver**

| Member | |
|---|---|
| `TicksPerSecond` | How many ticks make a second. The runtime forces this onto its own rate as it enters the tree |
| `Running` | Pauses and resumes the clock. Everything stops: cooldowns, `on every`, timed statuses, delayed effects |
| `MaxCatchUp` | The most ticks one frame may run after a stall |
| `Ticked(count: int)` | Emitted after each frame's ticks have run. The only place a game may act between ticks without polling in `_process` |
| `TotalTicks() -> int` | How many ticks **this driver** has run since it was last reset |
| `DroppedTicks() -> int` | How many it abandoned to `MaxCatchUp`: time the game skipped |
| `Reset()` | Clears the carried remainder and both counters, as after loading a save |

`TotalTicks()` is the driver's count and not the game's clock. They agree until a save is restored:
the clock comes back where it was and the counter does not, so a game that schedules anything off
wall time reads `GetTicks()` or `GetSeconds()` on the runtime, and calls `driver.Reset()` after a
load so the two start together again.

**A stall skips time rather than replaying it.** After a frame longer than `MaxCatchUp` allows, the
driver abandons the ticks it could not run and `DroppedTicks()` counts them. That is deliberate — a
frame that tried to run two seconds of simulation would stall the next one too — but it means the
game's clock falls behind wall time on a slow machine, and a fight timed in seconds is shorter
there. A game that cares should show `DroppedTicks()` in its diagnostics, or raise `MaxCatchUp`
and accept the hitch.

### Running faster or slower

**`Engine.time_scale` has no effect on a Cantrip game, and neither has
`Engine.physics_ticks_per_second`.** The driver counts physics frames and converts them at its own
configured rate; the frame's delta is deliberately ignored, because a fixed step is fixed by
definition and using the measured delta would make the simulation depend on how long the last frame
happened to take. Raising the runtime's `TicksPerSecond` is not the answer either — it is the rate
every `cooldown 6s` in the content converts through, so it changes the game rather than its speed.

The way to run at any other speed is to drive the clock yourself:

```gdscript
driver.Running = false                 # take the clock off the physics loop

func _process(_delta: float) -> void:
	rules.Tick(2)                      # double speed; Tick(0) to pause, Tick(20) to fast-forward
```

`Tick(n)` and `n` calls to `Tick(1)` are the same game, to the state hash, so a replay, a
fast-forward or a "2× speed" option changes nothing about what happens — only when it is watched.

### What has no turns

Every turn-shaped call on the node refuses on a tick runtime, the way `Tick` refuses on a
turn-based one: `EndTurn()`, `Pass(id)`, `CanAct(id)` and `ActiveMemberId()` all raise an error
naming `Tick`. `GetTurn()` is 0 for the whole fight, and `turn_start` and `turn_end` are never
raised. Ask `IsRealTime()` before drawing anything that ends a turn.

`Place(actor_id, lane, rank)` is how a game that spawns its own waves says where they walk in, and
`EndBattle(won)` is how a game whose ruleset says `ends: called` says the fight is over — without
it, a wave game is won by the first empty board between two waves. Both are in the table above.

[realtime/godot](../realtime/godot) is a whole real-time front end built on this: a forty-five
second hold against waves, with ability buttons and cooldown sweeps, targeting, saving mid-fight and
an autopilot that plays it headlessly for CI.

## Content and exports

`.cantrip` files are not resources, and Godot does not export plain files by default, so content
that works in the editor would be missing from a shipped game. The addon therefore imports each
`.cantrip` file into a small resource, and an export check fails the export when content would
not reach the build. The node always loads through the engine's file access, which works inside
an exported package, where the C# library's own `ContentLibrary.LoadFolder` sees nothing.

Two things to know before exporting a .NET game, both of which cost an afternoon to discover:

- **Godot needs a solution file beside the project.** Without `YourGame.sln` next to the
  `.csproj`, the export prints "This project contains C# files but no solution file was found",
  ships no managed assembly, and the packaged game crashes on startup. Create C# solution, in step 0
  of [Installing](#installing), writes it.
- **The export exits 0 even when it fails.** Check its output, and run the packaged game: that is
  the surest test.

## The editor dock

With the plugin enabled, the *Cantrip* dock sits at the bottom of the editor:

- **Problems**: parse errors and lint findings in one list, in source order. Double-click one to
  open the line. [Diagnostics](language.md#diagnostics) in the language reference explains each
  code, such as CT302, and how to fix it.
- **Tests**: the `test` blocks in your content, run by the same runner as `dotnet cantrip test`,
  with a trace of what happened when one fails. It runs them without your game, so without the
  names and functions your scripts answer with `RegisterName` and `RegisterFunction`: a test of
  content that uses one fails there, with an error such as ``Unknown name `front_row`.``
- **Preview**: any definition's rules text with live values, its flavour, its keyword tooltips,
  and the hash to paste into `text_checked`.
- **Source**: the editor. Write a card here, save it with Ctrl+S, and read what the parser and the
  linter make of it while you type. Godot cannot open a non-script file at a line, so this is also
  where every double-click in the dock lands. Its highlighting uses the engine's own lexer, so it
  cannot drift from the language.

### Writing content in the Source tab

Double-click a problem, a test or a definition anywhere in the dock and the file opens at that
line, ready to type in. The buttons across the top are:

| Button | |
|---|---|
| **Save** | Writes the buffer to the file, and tells Godot, so the importer picks it up. Ctrl+S does the same while the caret is in the buffer |
| **Revert** | Throws away the unsaved changes in this buffer and reads the file again |
| **Run tests** | Runs the `test` blocks against what is in the buffers, saved or not |
| **Open externally** | Opens the file in whatever your system uses for text files |

A buffer with unsaved changes says so in three places: a `*` before the path, the Save and Revert
buttons becoming available, and the tab itself reading **Source (1)** for as long as one file is
waiting. Nothing else in the editor marks it, so the dock has to.

One buffer the dock does not keep: building the C# project reloads the addon's assembly, which takes
the dock down and puts a new one up, and unsaved buffers go with the old one. The Output panel says
so when it happens. Save your content before you build.

**It indents with spaces, at the width the file already uses.** Every example in this guide, in the
quickstart and in the samples is written with two spaces, so in those files two spaces is what Tab
inserts, what a new line after `effect:` starts with, and what a pasted block is widened to. This is
not a preference. The language decides where a block ends by how deep a line is, and it counts a tab
as four columns, so one tab in a two-space file opens a block that nothing on screen looks like
opening. A file written with four spaces gets four; one written with tabs gets four spaces, which is
what its tabs already meant.

A file written with spaces never acquires an indenting tab, whatever route the text arrives by.
Typing is covered by Godot's own settings, both clipboards are widened as they are pasted — the
ordinary one and the primary selection a middle click pastes on Linux — and a block dropped on the
editor from another window, which Godot handles itself and no addon can stand in front of, is
widened the moment it lands, into exactly the columns its tabs already stood for. A file whose
author wrote it with tabs is left alone: its tabs mean what they mean, and rewriting somebody's
indentation because they typed a character into the file would be the worse surprise.

Line endings: Godot's editor buffer holds a line without its carriage return, so a file written on
Windows is saved back with `\n`. Nothing in the language or the tools reads it differently, and
`.gitattributes` in a repository that normalises text will not show a diff for it.

### Problems while you type

A third of a second after you stop typing, the content is loaded and linted again, and the file
you are in shows what came of it: a mark in the gutter beside each line with a problem on it — `!`
for an error, `*` for a warning, `.` for a note — the line tinted to match, and the problem itself
written out under the buffer as you put the caret on its line: its line and column, its code, its
message, and the word it suggests when it has one. **Apply fix** puts that word in, so a misspelt
`targt` becomes `target` without retyping it.

The Problems tab is looking at the same answer, so the two never disagree, and neither does the
Preview tab or the count in the dock's own bar.

**What is checked is the folder, not the file.** A card that names a status defined next door is
correct, so a single file cannot be judged on its own. Each check loads the folder as the dock last
read it, with every unsaved buffer put in place of its own file, and lints the lot. Two consequences
worth knowing: a problem can appear in a file you are not editing, because of what you typed in the
one you are; and the tests, the preview and the card text all answer about your unsaved text too.

A file the dock does not load — one outside `cantrip/content/folder` — can still be opened and
edited here, and the line under the buffer says that nothing checks it. "No problems" would be a
claim about a file nobody looked at.

A check costs one to three milliseconds in this repository's demo project, which has five files, and
about ten with `samples/corpus` copied into it — 23 files, 1,800 lines, 131 definitions and 90 test
blocks, the largest arrangement there is here. Reading the files is most of what a check would
otherwise cost, so the dock keeps what it read and reads again only when Godot says the project's
files have changed; the first check after that takes about 20 ms on those same 23 files.
`--cantrip-selftest` prints both numbers for your own project, as
`check: 23 file(s), 19.6 ms reading them all, 9.6 ms while typing`. Those are from a Godot with a
window on one machine; the same thing headless, as CI runs it, measured two to three times slower.

It is one folder's worth of parsing and linting, on the editor's own thread, so it grows with the
project: a single 12,000-line file measured about 80 ms a check on that machine, which is a pause
you would feel. Splitting content across files does not help by itself — the folder is what is
loaded — but it is the shape the numbers above were measured in.

### Running the tests from the editor

**Run tests** brings the buffers up to date and then presses the Tests tab's own Run, so there is
one runner and one list of results. A clean run says `12 passed, 0 failed` under the buffer and
leaves you where you were typing; a run with a failure in it brings the Tests tab forward, where
each test says PASS or FAIL and a double-click opens the line it failed on. The tests run without
your game, as they do in the Tests tab and on the command line: see that tab's note above.

### When the file changes underneath you

Save from another editor, or pull a teammate's change, and Godot tells the dock. If you have typed
nothing, the buffer is reread and a line under it says so. If you have unsaved changes, nothing is
touched: one line appears across the top saying the file changed on disk, with **Keep mine**, which
dismisses it and leaves your buffer to be saved over the top, and **Take theirs**, which throws your
buffer away and reads the file. The same change cannot raise that line twice, the warning follows
the file rather than the tab — switch away with one pending and it is there again when you come
back — and there is no dialog anywhere in the dock.

Delete the file from underneath an open buffer and the buffer stays: it is the only copy of that
text left, and **Save** writes the file again. The line under the buffer says what happened. The
content itself loses the file, because a file that is not there is not part of the game.

A `.cantrip` file added or deleted anywhere in the project joins or leaves the loaded content by
itself, a third of a second after Godot notices it. **Reload** is for the `cantrip/` settings, and
for reading everything again when you want to be sure.

Switching files with changes pending asks nothing either. The buffer is parked with its caret where
you left it, it keeps being checked, the tab keeps saying how many files are waiting, and opening the
file again brings it all back.

How much of this is proven: `--cantrip-selftest` types into a file, checks it while it is unsaved,
parks it, applies a suggested fix, saves it and reads the file back; it measures what Godot inserts
when it indents, and what a block of tabs dropped into a two-space file turns into; it types into a
file written with Windows line endings and takes the character out again, which has to leave the
file unmarked; it presses **Run tests** with a test block that exists only in an unsaved buffer and
counts the results; and it adds and deletes a file outside the dock and checks that the content
follows. Run in a Godot with a window rather than headless, it also pastes a tabbed block from the
real clipboard and checks what lands.

What is left by hand, because a headless editor has no mouse and no keyboard: the Ctrl+S keystroke
and the button presses reaching the handlers underneath them, the line that appears when a file
changes underneath you, and the primary selection a middle click pastes on Linux, which Windows has
no equivalent of to test.

### What it does not do yet

No completion, no go-to-definition, and no editing a file a running game has loaded — the game
picks up a saved file only when its own code calls `ReloadContent`, as [Hot reload](#hot-reload)
shows. Unsaved buffers do not survive a C# build. The check runs on the editor's thread, so a very
large project pauses for it.

The next pass should send a save to the attached game over the debugger channel, which already
accepts one; offer the names a file can use as you type them, which the linter already knows, since
CT302 is the same question asked after the fact; keep unsaved buffers across an assembly reload; and
do the check off the editor's thread so that a big project does not wait for it.

### Settings

The dock reads every `.cantrip` file in the project. These project settings change what it does;
the plugin puts them in Project Settings itself, under Advanced Settings:

| Setting | |
|---|---|
| `cantrip/content/folder` | Read only this folder, such as `res://content`. A `CantripRuntime` whose `ContentFolder` is empty loads this folder too, so the dock and the game cannot end up reading different ones. |
| `cantrip/lint/host_names` | Names your callbacks answer, so that the linter does not report them as unknown (CT302) |
| `cantrip/lint/host_events` | Events your game's C# raises |
| `cantrip/lint/host_verbs` | Verbs your game's C# registers |

Each list is written as words separated by commas or spaces. The dock reads these settings when
the plugin starts and again each time you press **Reload**, so after changing one, press it.
**Reload** also reads and lints every file again from scratch, and it keeps your unsaved buffers.
Adding or deleting a `.cantrip` file outside the editor does not need it: Godot tells the dock, and
the next check discovers afresh.

## Live debugging

Run a game from the editor and the debugger gets two tabs:

- **Cantrip**, the trace. Tick *Record* to turn recording on in the running game, then press
  *Fetch*, or leave *Follow* ticked, to pull the causality tree; double-click a step to open the
  line of content that caused it. *Pause* holds the game's queued triggers, *Step* then resolves
  exactly one of them, and *Resume* lets the rest resolve. A new run started with `NewRun` clears
  the tab, because its steps are numbered from 1 again.
- **Entities**: everything in play. For whichever one you pick, it shows its kind and side, its
  zone, each stat's base value beside what the modifiers make of it, its statuses, and every
  listener and modifier it has registered; double-click one to open the line of content behind it.
  *Refresh* asks the game again.

The editor offers nothing more. The running game also accepts breakpoints on an event or a line of
content, a reload of changed files, and statements to run as a console would, but the editor has
no controls for them yet: saving a file does not reach a running game. To pick up a saved file
while playing, call `ReloadContent([])` from a debug key, as [Hot reload](#hot-reload) shows.

This is built but only half proven. Both ends compile, the addon loads with them, and the
conversation between them is exercised headlessly by driving the game's side directly; a live
session between the editor and a running game cannot be staged without a person, so that round
trip, like the dock in use, is checked by hand.

## Where next

- [writing-content.md](writing-content.md) teaches writing cards, statuses, relics and enemies,
  with recipes for common mechanics and a section on working in Godot.
- [language.md](language.md) is the full language reference.
- Steps 2 and 3 of the [quickstart](quickstart.md) write content and test it from the command line
  with `dotnet cantrip test`. The tool needs the .NET 9 SDK and is installed as its step 1 shows;
  the dock's Tests tab runs the same tests without it.
- [csharp.md](csharp.md) is for a game that uses the rules from C# through `Core`.
- [troubleshooting.md](troubleshooting.md#godot) collects what goes wrong in a Godot project: a
  `Nonexistent function`, a stale `.godot` folder, content that will not load, the addon and the
  library out of step, and why the game will not speed up.
- [The API reference](api/Cantrip.GodotAdapter.md) is the node's whole script surface, generated
  from the sources, beside [the rest of the library](api/README.md).
- [stability.md](stability.md) says what may change between previews, where it has been tested,
  its known limitations and how to report a problem.

## Working on the addon

This section is for contributors to Cantrip, working in a clone of its repository.

### Packaging

`tools/package-addon.sh`, or `package-addon.ps1` on Windows, writes a zip whose root is
`addons/cantrip`, which is what "unzip this into your project" and the Asset Library both expect.
It takes the version from `plugin.cfg`, so the two cannot disagree. Each release attaches the zip
and copies the addon to AGomnes/cantrip-godot, whose README is `tools/addon-repo/README.md`.

Both READMEs link this guide on `main`. Given a tag or branch as well, such as
`bash tools/package-addon.sh artifacts v0.1.0-preview.5` or
`pwsh tools/package-addon.ps1 -Ref v0.1.0-preview.5`, the script points the addon README's links
into this repository at that tag instead, and the release workflow does the same to the
cantrip-godot README, so a user reads the guide for the version they installed. Without one, the
links are left as they are.

The repository's own demo uses a project reference to the source instead of the package.

### Verifying

```
dotnet build godot/Cantrip.Demo/Cantrip.Demo.csproj
dotnet build godot/Cantrip.Demo/Cantrip.Demo.csproj -c ExportRelease
dotnet test tests/Cantrip.Godot.Tests
godot --headless --path godot/Cantrip.Demo --import
godot --headless --path godot/Cantrip.Demo res://tests/headless.tscn
godot --headless --path godot/Cantrip.Demo res://tests/gdscript_smoke.tscn
godot --headless --path godot/Cantrip.Demo res://demo/battle.tscn -- --demo-auto
godot --headless --path godot/Cantrip.Demo --import -- --cantrip-selftest
```

The `ExportRelease` build is the cheap proof that no editor-only code escaped `#if TOOLS`; the
scenes exit non-zero on failure, and `--cantrip-selftest` exercises the dock without a mouse,
exiting with 1 when one of its own checks prints `FAILED`. That includes the editing: it asks
Godot to indent a line and measures what went in, then types into a scratch file, checks it while
it is unsaved, parks it, applies a suggested fix, saves it and takes it away again. Always give Godot a timeout: a script
that cannot parse never quits. `gdscript_smoke` enforces the two rules for GDScript, because both
fail in confusing ways.

The demo inherits this repository's build settings, so it cannot show what a user's own project
sees. `tools/godot-install-smoke.sh` can: it unzips the addon into a blank Godot project outside
the repository, adds Cantrip.Core with the command in [Installing](#installing), and fails on any
build warning or a plugin that does not load. It then runs [Your first battle](#your-first-battle)
from the blocks marked `<!-- smoke: ... -->` in this page, exactly as written, and compares what it
prints with the first turn shown there. It runs the functions under
[Between battles](#removing-upgrading-rewards-and-a-new-run) and
[When a call fails](#when-a-call-fails) the same way, through a run of its own. It also checks
that the content is the quickstart's, word for word, answers content from a GDScript lambda, and
plays a `discover` offer through the node.
CI runs it on every push to `main`, on pull requests and before every release:

```
dotnet pack src/Cantrip.Core -c Release -o /tmp/feed
bash tools/package-addon.sh /tmp/addon
bash tools/godot-install-smoke.sh godot /tmp/addon/cantrip-godot-*.zip /tmp/feed
```

To check an export, which is not part of every push because the export templates are about a
gigabyte, `.github/workflows/export.yml` exports the demo for Linux and runs it:

```
godot --headless --path godot/Cantrip.Demo --export-release "Linux" out/demo.x86_64
out/demo.x86_64 --headless -- --demo-auto
```

The demo exits non-zero when no content loaded, so that second line is the test: a build whose
content never travelled starts with an empty library and an empty hand.
