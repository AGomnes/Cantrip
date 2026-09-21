# The Godot addon

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
- **Godot's limits on exporting a .NET game.** Only a Linux x64 export has been tried with
  Cantrip; [Platforms](stability.md#platforms) lists what has and has not.

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
   - the Godot Asset Library: search for Cantrip in the editor's AssetLib tab once the listing,
     which is awaiting review, is approved;
   - `cantrip-godot-<version>.zip` on a [GitHub release](https://github.com/AGomnes/Cantrip/releases),
     whose root is `addons/cantrip`: unzip it into the project folder;
   - the [AGomnes/cantrip-godot](https://github.com/AGomnes/cantrip-godot) repository, which each
     release copies the addon to: copy its `addons/cantrip` folder.

   Until step 2, a build fails with many errors such as `CS0234: The type or namespace name
   'Content' does not exist in the namespace 'Cantrip'`. That is expected.
2. **Add the rules engine** from NuGet, in the folder with your `.csproj`, at the same version as
   the addon (the addon's is in its `plugin.cfg`):
   ```
   dotnet add package Cantrip.Core --version 0.1.0-preview.2
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

Godot has no editor for `.cantrip` files; write them in any text editor. The quickstart explains
the content and how to test it, and [writing-content.md](writing-content.md) goes further.

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

	var problems: Array = rules.LoadContent("res://content")
	for problem in problems:
		print("%s:%d %s %s" % [problem["file"], problem["line"], problem["code"], problem["message"]])
	if problems.any(func(problem): return problem["severity"] == "error"):
		return

	rules.EffectEvent.connect(_on_effect_event)
	rules.ChoiceRequested.connect(_on_choice_requested)
	rules.BattleEnded.connect(_on_battle_ended)

	rules.CreatePlayer("Player", 40, 3)  # name, hp, energy each turn
	rules.AddDeck(["Strike", "Strike", "Strike", "Defend", "Defend", "Curse", "Curse", "Sift"])
	ghoul = rules.SpawnEnemy("Ghoul", 0)  # 0: the hp its content gives it, 30
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

	for card in rules.GetHand():
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
  it enters the tree, and the problems it finds are not returned to your script; the dock lists
  them in the editor. This script turns it off and calls `LoadContent`, which returns them. Load
  before anything else: once a player, a card or a query has brought the rules into being,
  `LoadContent` fails and [`ReloadContent`](#hot-reload) is the way to change content.
- **Every argument is passed.** `SpawnEnemy("Ghoul", 0)` passes 0 for the hp, which means the hp
  its content gives it; any other number overrides that. `StartBattle(true, true)` shuffles the
  draw pile and then draws the opening hand.
- **`Play` answers with a word**: `played`, or why not (`not_enough_energy`, `invalid_target` and
  the others under [The node](#the-node)). `pending` means the card stopped for a choice. The
  `ChoiceRequested` signal has fired before `Play` returns, and because this stand-in answers at
  once, Sift has been played by then; a real game opens a picker and answers later.
- **`AnswerChoice` returns a dictionary**, not a word: `accepted`, a `reason` and a `message`
  saying why when it is false, and the `result` word when it is true. See
  [Choices the player makes](#choices-the-player-makes).
- **Events come after the action.** The card is announced before `Play` is called because its
  events arrive during the call, once the play has resolved. `amount` on `damaged` is the hp
  actually lost, so a hit that the block absorbs is `damaged` with 0.

A fuller version, with a hand of buttons, enemy panels with intents, a log and paced animation, is
the demo's [battle.gd](https://github.com/AGomnes/Cantrip/blob/main/godot/Cantrip.Demo/demo/battle.gd).
The addon download contains only the addon; to run the demo, clone the repository and open
`godot/Cantrip.Demo` in Godot .NET.

A `CantripRuntime` node can equally be added to a scene in the editor, with its exports set in the
Inspector, and found with `@onready var rules: CantripRuntime = $CantripRuntime`. The snippets on
the rest of this page assume `rules` is such a node, with its content loaded.

## The node

`CantripRuntime` is the only surface a script touches. Entities cross as `int` ids, and 0 means
none. Everything else crosses as strings, numbers, arrays and dictionaries with snake_case keys.

### Exports

| Export | Default | What it does |
|---|---|---|
| `ContentFolder` | `"res://content"` | Where `.cantrip` files are found, including every folder below it |
| `AutoLoad` | `true` | Loads `ContentFolder` when the node enters the tree |
| `Seed` | `1` | Every random roll comes from it: the same seed and the same calls play the same game |
| `Trace` | `false` | Records why things happened, for the debugger. It costs time. |
| `RealTime` | `false` | Runs on a tick clock instead of turns; see [Real time](#real-time) |
| `TicksPerSecond` | `60` | The tick clock's rate, which `cooldown 8s` in content converts through |
| `TrackedStats` | `["hp", "block", "energy"]` | The stats each event records as they were at that moment |
| `Presenter` | none | A `BattlePresenter` that paces events; see [Pacing events](#pacing-events-with-a-battlepresenter) |
| `Driver` | none | A `TickDriver` for a real-time game |

`Seed`, `Trace`, `RealTime`, `TicksPerSecond` and `TrackedStats` are read once, when the first
call that sets up, plays or reads the game brings the rules into being, so set them before that.
`Driver` is read when the node enters the tree.

### Signals

| Signal | When |
|---|---|
| `EffectEvent(effect_event: Dictionary)` | Once for each event, after the action that raised it has finished. Not emitted while a `Presenter` is set. |
| `ChoiceRequested(request: Dictionary)` | The rules are waiting for the player to choose. Emitted after the action's events. |
| `BattleStarted()` | `StartBattle` has started a battle |
| `BattleEnded(won: bool)` | The action that won or lost the battle has finished |
| `ContentReloaded(diagnostics: Array)` | `ReloadContent` has run |

### Methods

The signatures are written GDScript-style. Where the C# method has a default, the table gives it:
that is the value to pass if you have no other in mind.

**Content**

| Method | |
|---|---|
| `LoadContent(folder: String) -> Array` | Loads every `.cantrip` file under `folder` (`""` means `ContentFolder`) and returns the problems found. Only before any other call. |
| `ReloadContent(paths: Array) -> Dictionary` | Reloads the given `res://` files into a running game, or for `[]` every file under `ContentFolder`, even if `LoadContent` was given another folder. See [Hot reload](#hot-reload). |

**Setting a game up**

| Method | |
|---|---|
| `CreatePlayer(name: String, hp: int, maxEnergy: int) -> int` | Creates the player, once per node. `maxEnergy` is the energy each turn starts with. Defaults: `"Player", 80, 3`. |
| `AddCard(name: String, zone: String) -> int` | Adds a card to one of the player's zones. Default zone: `"draw"`. |
| `AddDeck(names: Array) -> Array` | Adds cards to the draw pile, returning their ids |
| `AddRelic(name: String) -> int` | Gives the player a relic |
| `SpawnEnemy(name: String, hp: int) -> int` | Adds an enemy. An `hp` of 0 uses the content's. Default: `0`. |
| `ApplyStatus(status: String, targetId: int, stacks: int) -> int` | Applies a status, as the player. Returns the status's id, or 0. Default stacks: `1`. |
| `GrantAbility(name: String, ownerId: int) -> int` | Attaches an ability to an actor. Returns its id, or 0 when `ownerId` is unknown. |

**Playing**

| Method | |
|---|---|
| `StartBattle(shuffle: bool, drawOpeningHand: bool) -> void` | Starts a battle. Defaults: `true, true`. |
| `Play(cardId: int, targetId: int) -> String` | Plays a card from the hand, aimed at `targetId`, or 0 for none. Default target: `0`. |
| `PlayNamed(cardName: String, targetId: int) -> String` | Plays the first card of that name in the hand |
| `EndTurn() -> void` | Ends the player's turn; the enemies act, and the next turn starts |
| `Tick(count: int) -> void` | Advances a real-time clock by `count` ticks. Default: `1`. |
| `UseAbility(abilityId: int, targetId: int) -> bool` | Uses an ability; false when it was not used, as when it is not ready. Default target: `0`. |
| `Execute(statements: String, selfId: int, targetId: int) -> void` | Runs statements as content would, for a console, a cheat key or a heal between battles. `selfId` 0 runs them as the player. Defaults: `0, 0`. |

`Play` and `PlayNamed` answer `played`, `pending` (see [Choices](#choices-the-player-makes)),
`not_a_card`, `not_in_hand`, `unplayable`, `not_enough_energy`, `invalid_target` or `cancelled`.

**Reading the game**

| Method | |
|---|---|
| `PlayerId() -> int` | The player's id, or 0 before `CreatePlayer` |
| `GetHand() -> Array` | Ids of the cards in the player's hand |
| `GetZone(ownerId: int, zone: String) -> Array` | Ids in one of an owner's zones; `ownerId` 0 means the player |
| `GetEnemies() -> Array`, `GetAllies() -> Array`, `GetActors() -> Array` | Ids of the living enemies, the living actors on the player's side, or both |
| `GetEntity(entityId: int) -> Dictionary` | Everything a UI shows about one entity; empty for an unknown id |
| `GetStat(entityId: int, stat: String) -> int` | One stat after modifiers, which is the number the rules would use now; 0 when unknown |
| `CostOf(cardId: int) -> int` | What the card costs now |
| `CanPlay(cardId: int) -> bool` | Whether `Play` would accept it: in hand, affordable, with a legal target if it needs one |
| `GetTargetMode(cardId: int) -> String` | What the card is aimed at: `"enemy"`, `"ally"`, `"self"`, `"any"` or `"none"` |
| `GetLegalTargets(cardId: int) -> Array` | The ids it may be aimed at, for highlighting |
| `IsInBattle() -> bool` | Whether a battle is running |
| `GetTurn() -> int` | The turn number, counted from 1 in each battle |
| `GetWon() -> bool` | True once a battle has been won. It is also false while a battle runs, so ask `IsInBattle()` first. |
| `StateHash() -> String` | The whole rules state as 16 hex digits, for checking that two runs agree |

**Text**

| Method | |
|---|---|
| `Describe(entityId: int, targetId: int) -> Dictionary` | Rules text with live values, for a card frame or a tooltip. `targetId` counts that target's statuses, or 0 for none. |
| `DescribeIntent(enemyId: int) -> Dictionary` | What an enemy will do next, with live values. Until the battle has started, its `empty` is true and its text is `""`. |

**Choices**

| Method | |
|---|---|
| `HasPendingChoice() -> bool` | Whether the rules are waiting for an answer |
| `GetPendingChoice() -> Dictionary` | The same dictionary `ChoiceRequested` carried; empty when nothing is waiting |
| `AnswerChoice(requestId: int, chosen: Array) -> Dictionary` | Answers it with option ids, then finishes the action |
| `CancelChoice() -> void` | Abandons the action that asked, leaving the game as it was before it |

**Saving**

| Method | |
|---|---|
| `CanSave() -> bool` | Whether `Save` would succeed now |
| `Save() -> String` | The whole game as a string |
| `LoadSave(json: String) -> Dictionary` | Restores a saved game, or says why not |

**Callbacks**

| Method | |
|---|---|
| `RegisterName(name: String, callable: Callable) -> void` | Answers a name content uses that only your game knows |
| `RegisterFunction(name: String, callable: Callable) -> void` | Answers a function content calls that only your game knows |

See [Callbacks from content](#callbacks-from-content). A C# game has two more members: `Core`,
the `CardRuntime` underneath, and `Content`, the loaded `ContentLibrary`.

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

### Dictionaries

**An entity**, from `GetEntity`:

| Key | |
|---|---|
| `id`, `name` | Its id and its name in content |
| `kind` | `"actor"`, `"card"`, `"status"`, `"relic"`, `"ability"`, `"keyword"`, `"item"` or `"global"` |
| `team` | `"player"`, `"enemy"` or `"neutral"` |
| `zone`, `position` | Where it is (see [Zones](#zones)) and its board slot |
| `alive`, `dead`, `removed` | A body still on the board is `dead`: neither alive nor removed |
| `intent` | An enemy's next move, such as `"Claw"`; `""` before the battle starts |
| `owner`, `source` | Whose it is (a card's player, a status's host) and who made or applied it; 0 for none |
| `tags` | Its tags, sorted |
| `stats` | Every stat after modifiers, such as `{"hp": 24, "max_hp": 30, "block": 0}` |
| `statuses` | Its statuses and keywords, in the order they arrived; each as below |
| `abilities` | Ids of its abilities |

Each status in `statuses` has `id`, `name`, `stacks`, `duration` (turns left, or 0), `counter`,
`hidden` and `tags`. `counter` is the number to show beside the icon: the turns left for a
duration status, the stacks for any other. `hidden` is true for `flags hidden`, which a status bar
leaves out.

**Rules text**, from `Describe` and `DescribeIntent`:

| Key | |
|---|---|
| `plain` | `"Deal 9 damage."` |
| `bbcode` | The same, with a changed value struck through and coloured: `"Deal [s]6[/s] [color=#6fcf6f]9[/color] damage."` |
| `segments` | The text in runs, for drawing it yourself; each as below |
| `tooltips` | The statuses and keywords it mentions, explained, each with `name`, `plain` and `bbcode` |
| `cost` | The card's cost as a segment. Missing when the entity has no cost. |
| `name` | The definition's name, or for an intent the move's name |
| `flavour` | The flavour line, never mixed into the rules text; `""` when there is none |
| `level` | Where the words came from: `"auto"`, `"custom"` (a `text:` line) or `"override"` (a `text_override:` line, shown as written, without live values) |
| `empty` | True when there is nothing to show, as for an intent before the battle starts |

Each segment has `kind` (`"text"` or `"value"`), `text` (what to show), `placeholder` (the value
it stands for, such as `"damage"`, or `""`), `has_number`, `base` and `current` (the printed and
the live number), `base_text` (the printed number as text), `changed`, `trend` (`"unchanged"`,
`"buffed"` or `"debuffed"`, as the player sees it) and `lower_is_better` (true for a cost).

**A choice**, from `ChoiceRequested` and `GetPendingChoice`:

| Key | |
|---|---|
| `id` | The request's number, to answer with. Never reused. |
| `kind` | `"entities"` to choose among things in play; `"offer"` for `discover` |
| `prompt` | What content asked, such as `"discard 1"` |
| `min`, `max` | How many options to pick |
| `option_ids` | What to answer with: entity ids, or for an offer the positions `0, 1, 2...` |
| `options` | For `"entities"`, one entity dictionary for each option, with only the `TrackedStats`. For `"offer"`, the candidates' `name`, `kind`, `tags` and rules `text`. |
| `chooser` | The id of the one choosing, usually the player |
| `file`, `line` | The line of content that asked |

**The answer**, from `AnswerChoice`: `accepted`, `reason`, `message`, and when it was accepted,
`result`, which is the same word `Play` gives. `reason` is `"none"`, or why the answer was turned
away: `"nothingpending"`, `"stalerequest"` (answering a request that has been dealt with),
`"unknownoption"`, `"duplicateoption"`, `"toofew"` or `"toomany"`. `message` says the same in a
sentence.

**A problem**, in the arrays from `LoadContent`, `ReloadContent` and `ContentReloaded`:
`severity` (`"error"`, `"warning"` or `"info"`), `code` (such as `"CT0101"`), `message`,
`suggestion`, `file`, `line` and `column`.

**A reload**, from `ReloadContent`: `rebound` (how many entities were rebound to their new
definitions), `missing` (names of definitions that have gone), `ruleset_changed` and
`diagnostics` (the problems found).

**A load**, from `LoadSave`: `accepted`, `reason` and `message`. `reason` is `"none"`,
`"wrong_format"` (not a save, or one from another version of the addon), `"no_payload"` or
`"content_changed"`.

**An event** is under [Events](#events).

## Events

The rules resolve an action completely and at once; presentation watches afterwards.

- **Events are delivered after the action finishes, never during it.** Acting again from a handler
  is fine, such as answering a choice or playing the next card, because by then nothing is
  resolving. What is refused, with an error, is acting from a
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
| `seq` | Its position in the run of events, rising by one each time |
| `time` | The game clock when it happened: the tick in a real-time game. In a turn game it counts turns from 0 and does not start again with each battle, so it is not the turn number; `GetTurn()` gives that. |
| `replaced` | True when an `instead` listener ran in place of the usual action |
| `phase` | Always `"after"` |

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
a pick that was not offered, and leaves the choice open. `CancelChoice()` abandons the action
instead, leaving the game as it was.

Listen for the signal rather than checking what `Play` returned, because more than `Play` can
stop. A relic that asks for a choice at the start of a turn stops `EndTurn`; one that asks at the
start of a battle stops `StartBattle`. `Execute`, `UseAbility` and `AnswerChoice` itself can stop
too. `AddRelic`, `ApplyStatus` and `Tick` never do: a choice they raise takes the first option.

Most choices are between things already in play, and `option_ids` are their entity ids. `discover`
offers content that does not exist yet, so its request has `"kind": "offer"`: each of `options` is a
candidate's `name`, `kind`, `tags` and rules `text`, and `option_ids` are simply their positions,
`[0, 1, 2]`. Answer with the position of the one the player picked:

```gdscript
func _on_offer(request: Dictionary, picked_position: int) -> void:
	if request["kind"] == "offer":
		rules.AnswerChoice(request["id"], [picked_position])
```

## Between battles

One node plays a whole run. The player keeps its hp, its deck and its relics from one battle to
the next; the enemies that died are cleared away when the next battle starts, and the player's
statuses end with the battle unless content flags them `persistent`.
[Winning, losing and several battles](csharp.md#winning-losing-and-several-battles) in the C#
guide lists what carries over and what starts afresh; it is the same here.

The map, the rewards and the encounters are your game's code. Between battles, hand out rewards,
heal, spawn the next encounter and start again:

```gdscript
func _on_battle_ended(won: bool) -> void:
	print("Won" if won else "Lost")  # show the reward or game-over screen here

func start_next_battle() -> void:  # when the reward screen closes
	rules.AddCard("Curse", "draw")  # a reward
	rules.AddRelic("Lantern")  # any relic your content defines
	rules.Execute("heal 12", 0, 0)  # a rest: statements run as the player
	rules.SpawnEnemy("Ghoul", 0)  # the next encounter
	rules.StartBattle(true, true)
```

Outside the `BattleEnded` handler, `GetWon()` tells a won battle from a lost one, once
`IsInBattle()` is false.

## Saving

```gdscript
func save_game() -> void:
	if rules.CanSave():
		FileAccess.open("user://slot1.json", FileAccess.WRITE).store_string(rules.Save())

func load_game() -> void:
	var result: Dictionary = rules.LoadSave(FileAccess.get_file_as_string("user://slot1.json"))
	if not result["accepted"]:
		print(result["message"])  # reason "content_changed": the save predates a content edit
```

`CanSave()` is false while effects are resolving, which only a callback sees. A save holds the
whole game, the player, the piles and the enemies included, so it loads into a node that has only
loaded its content: there is no need to create a player first.

The save carries a fingerprint of the content it was taken against, so a save from before a patch
is refused with a message instead of failing part-way through a restore. Editing a card's numbers
or effects does not change the fingerprint; adding, renaming or deleting a definition does. See
[Saves after a content update](stability.md#saves-after-a-content-update).

## Hot reload

`ReloadContent` reloads `.cantrip` files into a running game and rebinds everything live to them:

```gdscript
func reload_content() -> void:  # call it from a debug key, for example
	var report: Dictionary = rules.ReloadContent([])  # [] reloads every file under ContentFolder
	for problem in report["diagnostics"]:
		print(problem["message"])
```

Stats the game has changed keep their values, while a card still at its printed cost takes the new
one. The report says how many entities rebound, which definitions have gone, and whether the
ruleset changed: a running game keeps the rules it started with. Wire it to a debug key, and a
designer can change a number, save, press the key and play on.

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
number is a number), or null for no answer. Pass a method of your script, as above: a lambda, or a
method with `.bind()`, reaches the addon as an empty callable, and content that uses it fails with
an error.

These run in the middle of an effect, so they may read the game (`GetEntity`, `GetStat` and the
other queries) but must not change it. `Play`, `EndTurn`, `Save`, `ReloadContent` and the other
calls that run rules fail with an error there; the setup calls, such as `AddCard` and
`SpawnEnemy`, are not stopped, so do not make them either. They must also give the same answer
every time the same game asks, or a seed stops replaying and a choice replays differently: answer
from the node's queries and from values fixed for the run, in whole numbers.
[Determinism](stability.md#determinism) lists what to avoid, `randi()` among it.

The linter reports a name it does not know (CT302). To keep it quiet about the names your game
answers, list them in the dock's settings (see [The editor dock](#the-editor-dock)).

## When a call fails

A call the rules cannot carry out, such as `SpawnEnemy` with a name no content defines, a second
`CreatePlayer`, or content that fails while it runs, prints the error to the Output panel with the
C# exception's message and returns null, even where the method is declared to return an `int`.
Your script carries on, so watch the Output panel.

An action whose content failed is not rolled back: what it did before the failure stays done, and
the events it had collected are dropped. Content errors name the file and line; the linter and the
dock's Tests tab catch most of them before the game runs.

## Real time

Set `RealTime`, and add a `TickDriver` as the node's `Driver` before the node enters the tree, for
example in the Inspector. The driver advances the clock from `_PhysicsProcess` only, in whole ticks,
because the length of a rendered frame is not an input a deterministic game can use. Give it the
same `TicksPerSecond` as the `CantripRuntime`, whose rate `cooldown 8s` in content converts
through; any rate works against any physics rate. Its `Running` pauses the clock, and its
`MaxCatchUp` caps how many ticks one frame may run after a stall. A game can also call
`Tick(count)` itself.

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
  open the line.
- **Tests**: the `test` blocks in your content, run by the same runner as `dotnet cantrip test`,
  with a trace of what happened when one fails.
- **Preview**: any definition's rules text with live values, its flavour, its keyword tooltips,
  and the hash to paste into `text_checked`.
- **Source**: a viewer, because Godot cannot open a non-script file at a line. Its highlighting
  uses the engine's own lexer, so it cannot drift from the language.

The dock does not notice a saved file by itself: press its **Reload** button. It reads every
`.cantrip` file in the project. These project settings change what it does; add them under
Project Settings with Advanced Settings switched on:

| Setting | |
|---|---|
| `cantrip/content/folder` | Read only this folder, such as `res://content` |
| `cantrip/lint/host_names` | Names your callbacks answer, so that the linter does not report them as unknown (CT302) |
| `cantrip/lint/host_events` | Events your game's C# raises |
| `cantrip/lint/host_verbs` | Verbs your game's C# registers |

Each list is written as words separated by commas or spaces. The dock reads these settings when
the plugin starts, so after changing one, switch the plugin off and on again in Project Settings →
Plugins, or reopen the project.

## Live debugging

Run a game from the editor and the debugger gets two *Cantrip* tabs. The first is the trace: turn
recording on, pull the causality tree, and click a step to open the line of content that caused
it. It also holds the game: pause, and queued triggers wait; step, and exactly one of them
resolves; or set a breakpoint on an event or a line of content, and the game stops itself the next
time that trigger comes up. The second is *Entities*: everything in play, and for whichever one you
pick, its zone and side, each stat's base value beside what the modifiers make of it, its
statuses, and every listener and modifier it has registered, each row opening the line of content
behind it. Saving a file reloads it into the running game, and statements can be run against it as
a console.

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
- [stability.md](stability.md) says what may change between previews, where it has been tested,
  its known limitations and how to report a problem.

## Working on the addon

This section is for contributors to Cantrip, working in a clone of its repository.

### Packaging

`tools/package-addon.sh`, or `package-addon.ps1` on Windows, writes a zip whose root is
`addons/cantrip`, which is what "unzip this into your project" and the Asset Library both expect.
It takes the version from `plugin.cfg`, so the two cannot disagree. Each release attaches the zip
and copies the addon to AGomnes/cantrip-godot, whose README is `tools/addon-repo/README.md`.

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
scenes exit non-zero on failure, and `--cantrip-selftest` exercises the dock without a mouse.
Always give Godot a timeout: a script that cannot parse never quits. `gdscript_smoke` enforces the
two rules for GDScript, because both fail in confusing ways.

The demo inherits this repository's build settings, so it cannot show what a user's own project
sees. `tools/godot-install-smoke.sh` can: it unzips the addon into a blank Godot project outside
the repository, adds Cantrip.Core with the command in [Installing](#installing), and fails on any
build warning or a plugin that does not load. It then runs [Your first battle](#your-first-battle)
from the blocks marked `<!-- smoke: ... -->` in this page, exactly as written, and compares what it
prints with the first turn shown there. It also checks that the content is the quickstart's, word
for word, and plays a `discover` offer through the node. CI runs it on every push to `main`, on
pull requests and before every release:

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
