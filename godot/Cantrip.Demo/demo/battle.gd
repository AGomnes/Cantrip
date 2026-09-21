extends Control

# A small battle, built in code so the scene file stays one node and the interesting part is
# readable. It is the worked example for docs/godot.md: load content, set a game up, play cards,
# animate what happened, and answer a choice the rules could not make alone.
#
# Run it headlessly with `-- --demo-auto` and it plays itself and quits, which is how CI checks
# that the demo still works without anyone watching it.

const CONTENT := "res://content"
const DECK := ["Ember", "Ember", "Guard", "Sort", "Ember", "Guard"]

var rules: CantripRuntime
var presenter: BattlePresenter

var _player := 0
var _selected_card := 0
var _auto := false
var _load_errors := 0
var _log: RichTextLabel
var _enemies_box: HBoxContainer
var _hand_box: HBoxContainer
var _player_label: Label
var _end_turn: Button

func _ready() -> void:
	_auto = "--demo-auto" in OS.get_cmdline_user_args()
	_build_ui()
	_build_game()
	if _auto:
		call_deferred("_play_itself")

# --- setup --------------------------------------------------------------------------------------

func _build_game() -> void:
	rules = CantripRuntime.new()
	rules.AutoLoad = false
	rules.ContentFolder = CONTENT
	rules.Seed = 7
	add_child(rules)

	var problems: Array = rules.LoadContent(CONTENT)
	for problem in problems:
		if problem["severity"] == "error":
			_load_errors += 1
			push_error("%s:%d %s %s" % [problem["file"], problem["line"], problem["code"], problem["message"]])

	# Without a presenter the node emits every event at once; with one, events arrive one at a
	# time and wait for Done(), which is what lets an instant action animate as a sequence.
	if not _auto:
		presenter = BattlePresenter.new()
		add_child(presenter)
		rules.Presenter = presenter
		presenter.Present.connect(_on_present)
		presenter.Settled.connect(_refresh)

	rules.EffectEvent.connect(_on_effect_event)
	rules.ChoiceRequested.connect(_on_choice_requested)
	rules.BattleEnded.connect(_on_battle_ended)

	_player = rules.CreatePlayer("Player", 80, 3)
	for card in DECK:
		rules.AddCard(card, "draw")
	rules.SpawnEnemy("Slime", 0)
	rules.SpawnEnemy("Slime", 20)
	rules.StartBattle(true, true)

	_say("A battle begins.")
	_refresh()

# --- input --------------------------------------------------------------------------------------

func _on_card_pressed(card_id: int) -> void:
	if _busy():
		return

	# Aiming is decided before the card is played; a choice inside an effect is a different thing,
	# and arrives later as choice_requested.
	var mode: String = rules.GetTargetMode(card_id)
	if mode == "enemy" and _selected_card != card_id:
		_selected_card = card_id
		_say("Pick a target for %s." % _name_of(card_id))
		_refresh()
		return

	_play(card_id, _first_enemy() if mode == "enemy" else 0)

func _on_enemy_pressed(enemy_id: int) -> void:
	if _busy() or _selected_card == 0:
		return
	_play(_selected_card, enemy_id)

func _play(card_id: int, target_id: int) -> void:
	_selected_card = 0
	var result: String = rules.Play(card_id, target_id)
	match result:
		"played", "pending":
			pass
		"not_enough_energy":
			_say("Not enough energy.")
		"invalid_target":
			_say("That card needs a target.")
		_:
			_say("Could not play that: %s" % result)
	_refresh()

func _on_end_turn_pressed() -> void:
	if _busy():
		return
	rules.EndTurn()
	_say("Turn %d." % rules.GetTurn())
	_refresh()

# --- what happened ------------------------------------------------------------------------------

func _on_present(effect_event: Dictionary) -> void:
	# The rules resolved already; this is only the telling of it.
	_on_effect_event(effect_event)
	await get_tree().create_timer(0.12).timeout
	presenter.Done()

func _on_effect_event(effect_event: Dictionary) -> void:
	match effect_event["name"]:
		"damaged":
			_say("%s takes %d." % [_name_of(effect_event["target"]), effect_event["amount"]])
		"gained_block":
			_say("%s gains %d block." % [_name_of(effect_event["target"]), effect_event["amount"]])
		"status_applied":
			_say("%s is afflicted." % _name_of(effect_event["target"]))
		"died":
			_say("%s dies." % _name_of(effect_event["target"]))

func _on_choice_requested(request: Dictionary) -> void:
	# A real game would open a picker here. The demo takes the first option and says so.
	var options: Array = request["option_ids"]
	if options.is_empty():
		rules.CancelChoice()
		return

	_say("%s: choosing %s." % [request["prompt"], _name_of(options[0])])
	rules.AnswerChoice(request["id"], [options[0]])
	_refresh()

func _on_battle_ended(won: bool) -> void:
	_say("Victory." if won else "Defeat.")
	_refresh()

# --- views --------------------------------------------------------------------------------------

func _refresh() -> void:
	var player: Dictionary = rules.GetEntity(_player)
	if not player.is_empty():
		_player_label.text = "You  hp %d   block %d   energy %d" % [
			player["stats"].get("hp", 0), player["stats"].get("block", 0), player["stats"].get("energy", 0)]

	for child in _enemies_box.get_children():
		child.queue_free()
	for enemy_id in rules.GetEnemies():
		var view: Dictionary = rules.GetEntity(enemy_id)
		var intent: Dictionary = rules.DescribeIntent(enemy_id)
		var button := Button.new()
		button.text = "%s  hp %d\n%s" % [view["name"], view["stats"].get("hp", 0), intent.get("plain", "")]
		button.tooltip_text = _statuses_of(view)
		button.pressed.connect(_on_enemy_pressed.bind(enemy_id))
		_enemies_box.add_child(button)

	for child in _hand_box.get_children():
		child.queue_free()
	for card_id in rules.GetHand():
		var described: Dictionary = rules.Describe(card_id, 0)
		var button := Button.new()
		button.text = "%s (%d)" % [described["name"], rules.CostOf(card_id)]
		button.tooltip_text = described["plain"]
		button.disabled = not rules.CanPlay(card_id)
		if card_id == _selected_card:
			button.modulate = Color(1.0, 0.9, 0.5)
		button.pressed.connect(_on_card_pressed.bind(card_id))
		_hand_box.add_child(button)

	_end_turn.disabled = not rules.IsInBattle()

func _statuses_of(view: Dictionary) -> String:
	var parts: Array[String] = []
	for status in view.get("statuses", []):
		if not status["hidden"]:
			parts.append("%s %d" % [status["name"], status["counter"]])
	return ", ".join(parts)

func _name_of(entity_id: int) -> String:
	var view: Dictionary = rules.GetEntity(entity_id)
	return view.get("name", "something")

func _first_enemy() -> int:
	var enemies: Array = rules.GetEnemies()
	return enemies[0] if not enemies.is_empty() else 0

func _busy() -> bool:
	return presenter != null and presenter.IsBusy()

func _say(line: String) -> void:
	_log.append_text(line + "\n")
	if _auto:
		print("DEMO: ", line)

# --- scene --------------------------------------------------------------------------------------

func _build_ui() -> void:
	set_anchors_preset(Control.PRESET_FULL_RECT)

	var root := VBoxContainer.new()
	root.set_anchors_preset(Control.PRESET_FULL_RECT)
	add_child(root)

	var title := Label.new()
	title.text = "Cantrip demo"
	root.add_child(title)

	_enemies_box = HBoxContainer.new()
	root.add_child(_enemies_box)

	_player_label = Label.new()
	root.add_child(_player_label)

	_log = RichTextLabel.new()
	_log.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_log.scroll_following = true
	root.add_child(_log)

	_hand_box = HBoxContainer.new()
	root.add_child(_hand_box)

	_end_turn = Button.new()
	_end_turn.text = "End turn"
	_end_turn.pressed.connect(_on_end_turn_pressed)
	root.add_child(_end_turn)

# --- playing itself, for CI ----------------------------------------------------------------------

func _play_itself() -> void:
	# The check that makes this worth running against an exported build: a packaged game whose
	# content did not travel starts with an empty library and an empty hand, and would otherwise
	# "play" a battle of nothing and report success.
	if _load_errors > 0 or rules.GetHand().is_empty():
		print("DEMO: content did not load (%d error(s), %d card(s) in hand)" % [_load_errors, rules.GetHand().size()])
		get_tree().quit(1)
		return

	for turn in 3:
		for attempt in 8:
			var playable := _playable()
			if playable == 0:
				break
			var mode: String = rules.GetTargetMode(playable)
			_play(playable, _first_enemy() if mode == "enemy" else 0)
		if not rules.IsInBattle():
			break
		rules.EndTurn()

	var alive := rules.GetEnemies().size()
	print("DEMO: finished on turn %d with %d enem%s standing" % [rules.GetTurn(), alive, "y" if alive == 1 else "ies"])
	get_tree().quit(0)

func _playable() -> int:
	for card_id in rules.GetHand():
		if rules.CanPlay(card_id):
			return card_id
	return 0
