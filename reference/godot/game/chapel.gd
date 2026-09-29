extends Control

# The Drowned Chapel: a party of three descending a flooded chapel, played through the Cantrip
# addon's CantripRuntime node.
#
# The battles are Cantrip's. Everything above them -- the floors, the rewards, the shrine, the
# shop and the save file -- is this script, because Cantrip does not model a run. That division
# is the point of this front end: the battle code below is short and reads like rules, and the
# run code is long and reads like bookkeeping.
#
# Run it headlessly with `-- --chapel-auto` and it plays itself and quits, which is how CI checks
# it still works with nobody watching.

const CONTENT := "res://content"
const SAVE_PATH := "user://chapel.json"

const STARTER_DECK := [
	"Censer", "Censer", "Censer", "Litany", "Litany", "Litany", "Wade", "Wade", "Pike", "Grace",
]

# Seven floors. An empty encounter is the shrine (floor 3) or the vestry (floor 5).
const FLOORS := [
	["Drowned Acolyte"],
	["Drowned Acolyte", "Choir of Teeth"],
	[],
	["Bell Warden", "Tidewalker"],
	[],
	["Bell Warden", "Drowned Acolyte", "Choir of Teeth"],
	["The Antiphonary"],
]

const SHOP := [
	["Drowned Coin", 20], ["Tideglass", 35], ["Pilgrim's Token", 40],
	["Choirmaster's Baton", 45], ["Bell Rope", 55], ["Lantern of Ebb", 60],
]

var rules: CantripRuntime

# The run above the battle, which the rules know nothing about and a save has to carry itself.
var floor_index := 0
var roster: Array = []          # [{id, name}], because a fallen hero leaves GetParty() entirely
var run_seed := 7
var run_rng := RandomNumberGenerator.new()
var auto := false
var selected_card := 0
var _won_last := false

var _log: RichTextLabel
var _party_box: VBoxContainer
var _enemy_box: VBoxContainer
var _hand_box: HBoxContainer
var _ability_box: HBoxContainer
var _choice_box: HBoxContainer
var _header: Label

func _ready() -> void:
	auto = "--chapel-auto" in OS.get_cmdline_user_args()
	_build_ui()
	rules = CantripRuntime.new()
	rules.AutoLoad = false
	rules.ContentFolder = CONTENT
	rules.Seed = run_seed
	add_child(rules)

	var report: Dictionary = rules.LoadContent(CONTENT)
	if not report["ok"]:
		for problem in report["diagnostics"]:
			push_error("%s:%d %s" % [problem["file"], problem["line"], problem["message"]])
		return

	rules.BattleEnded.connect(_on_battle_ended)
	_begin_run(run_seed)
	if auto:
		call_deferred("_play_itself")

# --- the run ------------------------------------------------------------------------------------

func _begin_run(seed_value: int) -> void:
	run_seed = seed_value
	run_rng.seed = seed_value
	floor_index = 0
	roster = []

	var leader: int = rules.CreatePlayer("Acolyte", 40, 3)
	# The leader is the one member no declaration describes, so the speed that decides the whole
	# initiative order is written afterwards, as a statement, against a `resource "speed"`
	# declared only so that there is something to write.
	rules.Execute("speed = 6", 0, 0)
	rules.Execute("grant Invocation", 0, 0)
	roster.append({"id": leader, "name": "Acolyte"})
	roster.append({"id": rules.AddHero("Warden", 0), "name": "Warden"})
	roster.append({"id": rules.AddHero("Cantor", 0), "name": "Cantor"})
	rules.AddDeck(STARTER_DECK)
	_say("[b]The Drowned Chapel[/b] -- seed %d. Three go down into the water." % seed_value)
	_enter_floor()

func _enter_floor() -> void:
	if floor_index >= FLOORS.size():
		_say("[color=#8fd48f]The chapel is silent. The party climbs out.[/color]")
		_offer([])
		return

	var encounter: Array = FLOORS[floor_index]
	if encounter.is_empty():
		if floor_index == 2: _shrine()
		else: _vestry()
		return

	for name in encounter:
		rules.SpawnEnemy(name, -1)
	# StartBattle takes no board: the Godot node cannot pick one, so a game with more than one
	# `board` declared is stuck with the first. The Chapel is the only one here.
	rules.StartBattle(true, true)
	_say("[b]Floor %d[/b]: %s." % [floor_index + 1, ", ".join(encounter)])
	_refresh()

# Deferred, and it must be. Any CantripRuntime call that goes through an action -- Execute,
# Play, Pass, AddCard -- made from inside a `BattleEnded` handler re-enters the handler and
# recurses until the process dies with a stack overflow. game/reentry.tscn reproduces it in
# fifteen lines. Read-only calls are safe. See reference/FINDINGS.md.
func _on_battle_ended(won: bool) -> void:
	_won_last = won
	call_deferred("_after_battle")

func _after_battle() -> void:
	var won: bool = _won_last
	if not won:
		_say("[color=#d48f8f]The water closes over them.[/color]")
		_offer([])
		return

	# `gold` is a stat on the leader, so the run pays into the rules rather than keeping a purse
	# of its own. It is the one field the part Cantrip models and the part it does not both use.
	rules.Execute("gain 30 gold", 0, 0)
	for entry in roster:
		if rules.GetEntity(entry["id"]).get("dead", false):
			_say("  %s did not get up." % entry["name"])
	floor_index += 1
	_reward()

func _reward() -> void:
	var upgraded: Array = rules.GetDefinitions("card", "upgraded")
	var pool: Array = rules.GetDefinitions("card", "reward").filter(
		func(n): return not upgraded.has(n))
	var offer: Array = []
	while offer.size() < 3 and not pool.is_empty():
		offer.append(pool.pop_at(run_rng.randi_range(0, pool.size() - 1)))
	if offer.is_empty():
		_enter_floor()
		return

	_say("A reward. Take one:")
	_offer(offer.map(func(n): return {
		"label": "%s -- %s" % [n, rules.DescribeDefinition(n, "card")["plain"]],
		"act": func(): _take_reward(n),
	}))

func _take_reward(card_name: String) -> void:
	rules.AddCard(card_name, "draw")
	_say("  Took %s." % card_name)
	_enter_floor()

func _shrine() -> void:
	_say("[b]Floor 3[/b]: a shrine above the water line.")
	var options: Array = []

	# Raising a fallen hero lives here rather than on a card because nothing in content can name
	# one: `target ally` and `target any` both want somebody living, `allies` and `party` leave
	# the dead out, and `everyone where zone:dead` binds nothing. The node's Revive takes an id,
	# and the run kept the ids.
	for entry in roster:
		var who: Dictionary = rules.GetEntity(entry["id"])
		if who.get("dead", false):
			options.append({"label": "Raise %s" % entry["name"], "act": func(): _raise(entry)})

	if not _has("Ferryman"):
		options.append({"label": "Recruit the Ferryman", "act": func(): _recruit()})
	options.append({"label": "Rest: the party heals 12", "act": func(): _rest()})
	_offer(options)

func _has(name: String) -> bool:
	for entry in roster:
		if entry["name"] == name: return true
	return false

func _raise(entry: Dictionary) -> void:
	var half: int = max(1, int(rules.GetStat(entry["id"], "max_hp") / 2.0))
	rules.Revive(entry["id"], half)
	_say("  %s is raised at %d hp." % [entry["name"], half])
	floor_index += 1
	_enter_floor()

func _recruit() -> void:
	roster.append({"id": rules.AddHero("Ferryman", 0), "name": "Ferryman"})
	_say("  The Ferryman joins the party.")
	floor_index += 1
	_enter_floor()

func _rest() -> void:
	rules.Execute("heal 12 to party", 0, 0)
	_say("  The party rests.")
	floor_index += 1
	_enter_floor()

func _vestry() -> void:
	_say("[b]Floor 5[/b]: the vestry. %d gold." % _gold())
	var options: Array = []
	for card in rules.GetZone(rules.PlayerId(), "draw"):
		if rules.GetEntity(card)["name"] == "Brine" and _gold() >= 15:
			options.append({"label": "Lift a Brine out of the book (15)", "act": func(): _lift(card)})
			break
	for pair in [["Censer", "Censer+"], ["Pike", "Pike+"], ["Grace", "Grace+"]]:
		var card: int = _first_in_deck(pair[0])
		if card != 0 and _gold() >= 30:
			options.append({"label": "Rewrite %s as %s (30)" % [pair[0], pair[1]],
				"act": func(): _upgrade(card, pair[1])})
	for entry in SHOP:
		if _gold() >= entry[1] and not _holds(entry[0]):
			options.append({"label": "%s (%d)" % [entry[0], entry[1]],
				"act": func(): _buy(entry[0], entry[1])})
	options.append({"label": "Leave the vestry", "act": func(): _leave_shop()})
	_offer(options)

func _gold() -> int:
	return rules.GetStat(rules.PlayerId(), "gold")

func _spend(amount: int) -> void:
	# There is no call that changes a stat, so the shop writes DSL text from GDScript.
	rules.Execute("lose %d gold" % amount, 0, 0)

func _holds(relic: String) -> bool:
	for id in rules.GetZone(rules.PlayerId(), "relics"):
		if rules.GetEntity(id)["name"] == relic: return true
	return false

func _first_in_deck(name: String) -> int:
	for id in rules.GetZone(rules.PlayerId(), "draw"):
		if rules.GetEntity(id)["name"] == name: return id
	return 0

func _lift(card: int) -> void:
	rules.RemoveCard(card)
	_spend(15)
	_say("  A Brine is lifted out of the book.")
	_vestry()

func _upgrade(card: int, into: String) -> void:
	rules.RemoveCard(card)
	rules.AddCard(into, "draw")
	_spend(30)
	_say("  Rewritten as %s." % into)
	_vestry()

func _buy(relic: String, price: int) -> void:
	rules.AddRelic(relic)
	_spend(price)
	_say("  Bought %s." % relic)
	_vestry()

func _leave_shop() -> void:
	floor_index += 1
	_enter_floor()

# --- the battle ---------------------------------------------------------------------------------

func _active() -> int:
	# Under `turns: initiative` ActiveMemberId is the rule, not a suggestion: Pass on anybody
	# else is refused, so the UI follows it rather than letting the player pick a member.
	return rules.ActiveMemberId()

func _on_card_pressed(card: int) -> void:
	var member: int = _active()
	if member == 0: return
	var mode: String = rules.GetTargetMode(card)
	var aim: int = 0
	if mode in ["enemy", "ally", "any"]:
		var legal: Array = rules.GetLegalTargets(card)
		if legal.is_empty():
			_say("  [i]%s cannot reach anybody from there.[/i]" % rules.GetEntity(card)["name"])
			return
		aim = _pick(legal, mode)
	var result: String = rules.PlayBy(card, aim, member)
	if result != "played":
		_say("  [i]%s: %s[/i]" % [rules.GetEntity(card)["name"], result])
	_refresh()

func _on_ability_pressed(ability: int) -> void:
	var member: int = _active()
	if member == 0: return
	var mode: String = rules.GetTargetMode(ability)
	var aim: int = 0
	if mode in ["enemy", "ally", "any"]:
		var legal: Array = rules.GetLegalTargets(ability)
		if legal.is_empty():
			_say("  [i]Nothing in reach.[/i]")
			return
		aim = _pick(legal, mode)
	rules.UseAbility(ability, aim)
	_refresh()

# A real game opens a picker here. This takes the weakest enemy, or the most hurt ally, which is
# what a one-click front end can do and still be honest about which four things `invalid_target`
# can mean.
func _pick(legal: Array, mode: String) -> int:
	var best: int = legal[0]
	for id in legal:
		var better: bool = rules.GetStat(id, "hp") < rules.GetStat(best, "hp")
		if better: best = id
	return best

func _on_pass_pressed() -> void:
	var member: int = _active()
	if member == 0: return
	rules.Pass(member)
	_refresh()

# --- saving ---------------------------------------------------------------------------------

func _on_save_pressed() -> void:
	var saved: Dictionary = rules.Save()
	if not saved["accepted"]:
		_say("  [i]Cannot save: %s[/i]" % saved["message"])
		return
	# Two halves in one file. The node's Save() holds the battle, the party, the deck, the relics
	# and the gold; the floor, the roster and the run's own generator are this script's and would
	# be lost without the other half.
	var file := {
		"run": {"floor": floor_index, "seed": run_seed, "roster": roster,
			"rng": run_rng.state},
		"game": saved["save"],
	}
	FileAccess.open(SAVE_PATH, FileAccess.WRITE).store_string(JSON.stringify(file))
	_say("  Saved on floor %d." % (floor_index + 1))

func _on_load_pressed() -> void:
	if not FileAccess.file_exists(SAVE_PATH):
		_say("  [i]No save.[/i]")
		return
	var file: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(SAVE_PATH))
	var result: Dictionary = rules.LoadSave(file["game"])
	if not result["accepted"]:
		_say("  [i]Cannot load: %s[/i]" % result["message"])
		return
	floor_index = int(file["run"]["floor"])
	run_seed = int(file["run"]["seed"])
	run_rng.state = int(file["run"]["rng"])
	# Ids survive a restore, which is what makes the roster usable again: nothing else would find
	# a hero that is lying dead in the `dead` zone.
	roster = file["run"]["roster"]
	_say("  Loaded on floor %d." % (floor_index + 1))
	if not rules.IsInBattle():
		_enter_floor()
	_refresh()

# --- the screen ---------------------------------------------------------------------------------

func _refresh() -> void:
	var member: int = _active()
	_header.text = "Floor %d of %d    gold %d    turn %d    %s" % [
		min(floor_index + 1, FLOORS.size()), FLOORS.size(), _gold(), rules.GetTurn(),
		"" if member == 0 else "%s to act" % rules.GetEntity(member)["name"]]

	_fill(_party_box, rules.GetAllies(), func(id): return _party_line(id, member))
	_fill(_enemy_box, rules.GetEnemies(), func(id): return _enemy_line(id))
	_clear(_choice_box)

	_clear(_hand_box)
	_clear(_ability_box)
	if member == 0 or not rules.IsInBattle(): return
	for card in rules.GetZone(rules.PlayerId(), "hand"):
		var button := Button.new()
		var text: Dictionary = rules.Describe(card, 0)
		button.text = "%s (%d)\n%s" % [rules.GetEntity(card)["name"],
			rules.GetStat(card, "cost"), text["plain"]]
		button.disabled = not rules.CanPlay(card)
		button.pressed.connect(_on_card_pressed.bind(card))
		_hand_box.add_child(button)
	for ability in rules.GetEntity(member)["abilities"]:
		var button := Button.new()
		button.text = rules.GetEntity(ability)["name"]
		button.disabled = not rules.CanUse(ability)
		button.pressed.connect(_on_ability_pressed.bind(ability))
		_ability_box.add_child(button)
	var pass_button := Button.new()
	pass_button.text = "Pass"
	pass_button.pressed.connect(_on_pass_pressed)
	_ability_box.add_child(pass_button)

func _party_line(id: int, active: int) -> String:
	var who: Dictionary = rules.GetEntity(id)
	var marks: Array = []
	for status in who["statuses"]:
		if not status["hidden"]: marks.append("%s %d" % [status["name"], status["counter"]])
	return "%s%s  %d/%d hp  %d block  aisle %d rank %d  %s" % [
		"> " if id == active else "  ", who["name"], rules.GetStat(id, "hp"),
		rules.GetStat(id, "max_hp"), rules.GetStat(id, "block"),
		who["lane"], who["rank"], ", ".join(marks)]

func _enemy_line(id: int) -> String:
	var who: Dictionary = rules.GetEntity(id)
	var intent: Dictionary = rules.DescribeIntent(id)
	return "  %s  %d hp  %d block  aisle %d rank %d\n      %s" % [
		who["name"], rules.GetStat(id, "hp"), rules.GetStat(id, "block"),
		who["lane"], who["rank"], "" if intent["empty"] else intent["line"]]

func _offer(options: Array) -> void:
	_clear(_hand_box)
	_clear(_ability_box)
	_clear(_choice_box)
	for option in options:
		var button := Button.new()
		button.text = option["label"]
		button.pressed.connect(option["act"])
		_choice_box.add_child(button)
	_refresh_header_only()

func _refresh_header_only() -> void:
	_header.text = "Floor %d of %d    gold %d" % [
		min(floor_index + 1, FLOORS.size()), FLOORS.size(), _gold()]
	_fill(_party_box, rules.GetAllies(), func(id): return _party_line(id, 0))
	_fill(_enemy_box, rules.GetEnemies(), func(id): return _enemy_line(id))

func _fill(box: VBoxContainer, ids: Array, line: Callable) -> void:
	_clear(box)
	for id in ids:
		var label := Label.new()
		label.text = line.call(id)
		box.add_child(label)

func _clear(box: Node) -> void:
	for child in box.get_children():
		child.queue_free()
		box.remove_child(child)

func _say(line: String) -> void:
	_log.append_text(line + "\n")
	if auto: print(_strip(line))

func _strip(line: String) -> String:
	var out := ""
	var inside := false
	for character in line:
		if character == "[": inside = true
		elif character == "]": inside = false
		elif not inside: out += character
	return out

func _build_ui() -> void:
	set_anchors_preset(Control.PRESET_FULL_RECT)
	var root := VBoxContainer.new()
	root.set_anchors_preset(Control.PRESET_FULL_RECT)
	add_child(root)

	_header = Label.new()
	root.add_child(_header)

	var bar := HBoxContainer.new()
	root.add_child(bar)
	for pair in [["Save", _on_save_pressed], ["Load", _on_load_pressed]]:
		var button := Button.new()
		button.text = pair[0]
		button.pressed.connect(pair[1])
		bar.add_child(button)

	var columns := HBoxContainer.new()
	columns.size_flags_vertical = Control.SIZE_EXPAND_FILL
	root.add_child(columns)

	_party_box = VBoxContainer.new()
	_party_box.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	columns.add_child(_party_box)
	_enemy_box = VBoxContainer.new()
	_enemy_box.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	columns.add_child(_enemy_box)

	_log = RichTextLabel.new()
	_log.bbcode_enabled = true
	_log.scroll_following = true
	_log.custom_minimum_size = Vector2(0, 220)
	root.add_child(_log)

	_choice_box = HBoxContainer.new()
	root.add_child(_choice_box)
	_ability_box = HBoxContainer.new()
	root.add_child(_ability_box)
	_hand_box = HBoxContainer.new()
	root.add_child(_hand_box)

# --- playing itself, for CI ----------------------------------------------------------------------

func _play_itself() -> void:
	for step in range(4000):
		# A frame between steps, because the work after a battle has to be deferred out of the
		# BattleEnded handler.
		await get_tree().process_frame
		if rules.IsInBattle():
			var member: int = _active()
			if member == 0:
				await get_tree().process_frame
				continue
			if not _auto_act(member):
				rules.Pass(member)
			continue

		# Between battles the screen is a row of buttons; the auto-player presses the first.
		if _choice_box.get_child_count() > 0:
			var button: Button = _choice_box.get_child(0)
			if button.text.begins_with("Leave") and _choice_box.get_child_count() > 1:
				button = _choice_box.get_child(1)
			button.pressed.emit()
			continue
		break

	print("CHAPEL: finished on floor %d, won=%s" % [floor_index + 1, str(rules.GetWon())])
	get_tree().quit(0)

func _auto_act(member: int) -> bool:
	for ability in rules.GetEntity(member)["abilities"]:
		if rules.CanUse(ability) and not rules.GetLegalTargets(ability).is_empty():
			_on_ability_pressed(ability)
			return true
		if rules.CanUse(ability) and rules.GetTargetMode(ability) == "none":
			_on_ability_pressed(ability)
			return true
	for card in rules.GetZone(rules.PlayerId(), "hand"):
		if not rules.CanPlay(card): continue
		var about: Dictionary = rules.GetEntity(card)
		if about["tags"].has("curse"): continue
		# Wade costs nothing and moves a rank, so a player that is already at the front can play
		# it for ever: the move is refused at the edge of the board but the card is still played.
		if about["name"] == "Wade" and rules.GetEntity(member)["rank"] == 0: continue
		_on_card_pressed(card)
		return true
	return false
