extends Control

# Emberline: a real-time hold, played through the Cantrip addon's CantripRuntime node with its
# clock coming from Godot's own physics loop.
#
# What is Cantrip's: the abilities, their cooldowns in seconds, the statuses, what reaches how far
# across the board, the enemies' own two-second and three-second listeners, and the save.
#
# What is this script's, and would not be in a turn game: the wave schedule, placing everything on
# the board, keeping a battle alive between waves, the elapsed clock, and the decision that the
# hold is over. realtime/FINDINGS.md counts that up.
#
# Run it headlessly with `-- --emberline-auto` and it plays itself and quits, which is how CI
# checks it still works with nobody watching.

const CONTENT := "res://content"
const SAVE_PATH := "user://emberline.json"
const TICKS_PER_SECOND := 20
const HOLD_SECONDS := 45

# The encounter. Cantrip has no idea a wave exists, so this belongs to the game.
const SCHEDULE := [
	[0, "Hollow", 1], [2, "Hollow", 0], [4, "Hollow", 2], [6, "Wisp", 2],
	[8, "Hollow", 1], [10, "Hollow", 0], [12, "Breaker", 1], [13, "Hollow", 2],
	[16, "Hollow", 0], [18, "Wisp", 1], [20, "Wisp", 0], [21, "Hollow", 2],
	[24, "Hollow", 1], [25, "Hollow", 0], [26, "Breaker", 0], [28, "Hollow", 2],
	[30, "Hollow", 1], [32, "Wisp", 2], [34, "Wisp", 1], [35, "Hollow", 0],
	[38, "Hollow", 2], [39, "Breaker", 1], [40, "Breaker", 2], [42, "Hollow", 0],
	[43, "Hollow", 1],
]

var rules: CantripRuntime
var driver: TickDriver

var stoker := 0
var warden := 0
var lantern := 0
var dark := 0

# The game's own clock. The node has no way to say what time it is, so the front end adds up the
# ticks the driver reports and is careful to put this back when it loads a save. FINDINGS #11.
var elapsed_ticks := 0
var next_wave := 0
var killed := 0
var finished := false
var auto := false

var aiming := 0          # the ability waiting for a target, or 0

var _log: RichTextLabel
var _clock_label: Label
var _party_box: VBoxContainer
var _line_box: VBoxContainer
var _ability_box: HBoxContainer

func _ready() -> void:
	auto = "--emberline-auto" in OS.get_cmdline_user_args()
	_build_ui()
	_begin()

# --- the game ----------------------------------------------------------------------------------

func _begin() -> void:
	rules = CantripRuntime.new()
	rules.AutoLoad = false
	rules.Seed = 7
	rules.RealTime = true
	rules.TicksPerSecond = TICKS_PER_SECOND

	# The driver has to be in place before the node enters the tree.
	driver = TickDriver.new()
	driver.TicksPerSecond = TICKS_PER_SECOND
	rules.Driver = driver
	add_child(driver)
	add_child(rules)

	var report: Dictionary = rules.LoadContent(CONTENT)
	if not report["ok"]:
		_say("[color=#d66]Content failed to load: %s[/color]" % str(report))
		return

	rules.EffectEvent.connect(_on_effect_event)

	stoker = rules.CreatePlayer("Stoker", 30, 3)
	rules.GrantAbility("Ember Bolt", stoker)
	rules.GrantAbility("Backdraft", stoker)
	warden = rules.AddHero("Warden", 0)
	lantern = rules.AddHero("Lantern", 0)

	# Something has to be standing or the battle is won before it begins. FINDINGS #7.
	dark = rules.SpawnEnemy("The Dark", -1)
	rules.StartBattle(false, false)
	rules.Execute("loom self", dark, 0)
	rules.Execute("hold self 0", warden, 0)
	rules.Execute("hold self 1", stoker, 0)
	rules.Execute("hold self 2", lantern, 0)

	# The only line in this file that makes time pass is the driver's, and it is already running.
	if auto:
		driver.Running = false          # the auto run pumps the clock itself, in _process
	else:
		driver.Ticked.connect(_on_ticked)
	_release_waves()
	_refresh()
	_say("The keepers take the line. Forty-five seconds.")

func _on_ticked(count: int) -> void:
	_advance(count)

# Nobody is watching under `--emberline-auto`, so the clock is pumped as fast as the machine will
# go rather than in real time. The driver deliberately ignores the frame delta -- a fixed step is
# fixed -- so `Engine.time_scale` cannot speed a Cantrip game up and neither can anything else.
# FINDINGS #13.
func _process(_delta: float) -> void:
	if not auto or finished:
		return
	for i in range(TICKS_PER_SECOND):
		if finished:
			return
		rules.Tick(1)
		_advance(1)

func _advance(count: int) -> void:
	if finished:
		return
	elapsed_ticks += count
	_release_waves()
	if auto:
		_autopilot()
	if seconds() >= HOLD_SECONDS or rules.GetParty().is_empty() or rules.GetWon() != null:
		_finish()
	_refresh()

func seconds() -> int:
	return int(elapsed_ticks / TICKS_PER_SECOND)

func _release_waves() -> void:
	while next_wave < SCHEDULE.size() and SCHEDULE[next_wave][0] <= seconds():
		var wave: Array = SCHEDULE[next_wave]
		next_wave += 1
		var id: int = rules.SpawnEnemy(wave[1], -1)
		rules.Execute("arrive self %d" % wave[2], id, 0)
		_say("%s comes out of the dark in lane %d." % [wave[1], wave[2]])

func _finish() -> void:
	finished = true
	driver.Running = false
	var held: bool = not rules.GetParty().is_empty() and seconds() >= HOLD_SECONDS
	_say("[b]%s[/b]" % ("The line held. %d down." % killed if held else "The line broke at %ds." % seconds()))
	if auto:
		get_tree().quit(0 if held else 1)

# --- the player's hands --------------------------------------------------------------------------

func abilities_of(owner: int) -> Array:
	return rules.GetEntity(owner)["abilities"]

func _use(ability: int, target: int) -> void:
	if finished:
		return
	var answer: String = rules.UseAbility(ability, target)
	if answer == "played":
		_say("%s." % rules.GetEntity(ability)["name"])
	elif answer == "not_ready":
		_say("[color=#886]%s is still cooling.[/color]" % rules.GetEntity(ability)["name"])
	elif answer == "invalid_target":
		_say("[color=#886]%s has nothing in reach.[/color]" % rules.GetEntity(ability)["name"])
	aiming = 0
	_refresh()

func _on_ability_pressed(ability: int) -> void:
	var mode: String = rules.GetTargetMode(ability)
	if mode == "" or mode == "none":
		_use(ability, 0)
		return
	aiming = ability
	_refresh()

func _on_target_pressed(id: int) -> void:
	if aiming == 0:
		return
	_use(aiming, id)

# The autopilot, which stands in for a player so CI can play the hold. The same policy as the
# headless C# host's, written again here because the policy is the game's, not the library's.
func _autopilot() -> void:
	for member in rules.GetParty():
		for ability in abilities_of(member):
			if not rules.CanUse(ability):
				continue
			var legal: Array = rules.GetLegalTargets(ability)
			var name: String = rules.GetEntity(ability)["name"]
			match name:
				"Ember Bolt":
					var weakest := _pick(legal, "hp", true)
					if weakest != 0: _use(ability, weakest)
				"Backdraft":
					var clustered := _most_clustered(legal)
					if clustered != 0: _use(ability, clustered)
				"Haul":
					for id in legal:
						if rules.GetEntity(id)["rank"] == 0:
							_use(ability, id)
							break
				"Signal Flare":
					var biggest := _pick(legal, "hp", false)
					if biggest != 0: _use(ability, biggest)
				"Bulwark":
					for id in rules.GetEnemies():
						if id != dark and rules.GetEntity(id)["rank"] == 0:
							_use(ability, 0)
							break
				"Mend":
					for id in legal:
						if rules.GetStat(id, "hp") * 2 <= rules.GetStat(id, "max_hp"):
							_use(ability, id)
							break

func _pick(ids: Array, stat: String, lowest: bool) -> int:
	var best := 0
	for id in ids:
		if best == 0:
			best = id
		elif lowest and rules.GetStat(id, stat) < rules.GetStat(best, stat):
			best = id
		elif not lowest and rules.GetStat(id, stat) > rules.GetStat(best, stat):
			best = id
	return best

func _most_clustered(ids: Array) -> int:
	var best := 0
	var best_count := 0
	for id in ids:
		var here: Dictionary = rules.GetEntity(id)
		var count := 0
		for other in rules.GetEnemies():
			if other == dark:
				continue
			var there: Dictionary = rules.GetEntity(other)
			if abs(there["lane"] - here["lane"]) + abs(there["rank"] - here["rank"]) <= 1:
				count += 1
		if count > best_count:
			best_count = count
			best = id
	return best if best_count >= 2 else 0

# --- saving ---------------------------------------------------------------------------------

func _save() -> void:
	if not rules.CanSave():
		_say("[color=#886]Not a moment to save at.[/color]")
		return
	var saved: Dictionary = rules.Save()
	if not saved["accepted"]:
		_say("[color=#d66]%s[/color]" % str(saved.get("message", "")))
		return
	# The rules save the fight. The clock this script keeps, the wave the schedule is up to and
	# the body count are the game's, and go beside it. FINDINGS #11 and #12.
	var file := FileAccess.open(SAVE_PATH, FileAccess.WRITE)
	file.store_string(JSON.stringify({
		"rules": saved["save"], "elapsed": elapsed_ticks, "wave": next_wave, "killed": killed,
	}))
	file.close()
	_say("Saved at %ds." % seconds())

func _load() -> void:
	if not FileAccess.file_exists(SAVE_PATH):
		_say("[color=#886]Nothing saved.[/color]")
		return
	var text := FileAccess.get_file_as_string(SAVE_PATH)
	var held: Variant = JSON.parse_string(text)
	if typeof(held) != TYPE_DICTIONARY:
		_say("[color=#d66]The save file is damaged.[/color]")
		return
	var loaded: Dictionary = rules.LoadSave(held["rules"])
	if not loaded["accepted"]:
		_say("[color=#d66]%s[/color]" % str(loaded.get("message", "")))
		return
	elapsed_ticks = int(held["elapsed"])
	next_wave = int(held["wave"])
	killed = int(held["killed"])
	driver.Reset()               # the driver counts its own ticks, not the game's
	finished = false
	driver.Running = true
	_say("Loaded at %ds." % seconds())
	_refresh()

# --- presentation ------------------------------------------------------------------------------

func _on_effect_event(effect_event: Dictionary) -> void:
	# Never call back into the runtime from here; record and act later.
	if effect_event["name"] == "killed":
		killed += 1

func _build_ui() -> void:
	set_anchors_preset(Control.PRESET_FULL_RECT)
	var root := VBoxContainer.new()
	root.set_anchors_preset(Control.PRESET_FULL_RECT)
	add_child(root)

	_clock_label = Label.new()
	root.add_child(_clock_label)

	var middle := HBoxContainer.new()
	middle.size_flags_vertical = Control.SIZE_EXPAND_FILL
	root.add_child(middle)

	_line_box = VBoxContainer.new()
	_line_box.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	middle.add_child(_line_box)

	_party_box = VBoxContainer.new()
	middle.add_child(_party_box)

	_ability_box = HBoxContainer.new()
	root.add_child(_ability_box)

	var buttons := HBoxContainer.new()
	root.add_child(buttons)
	for pair in [["Save", _save], ["Load", _load]]:
		var button := Button.new()
		button.text = pair[0]
		button.pressed.connect(pair[1])
		buttons.add_child(button)

	_log = RichTextLabel.new()
	_log.bbcode_enabled = true
	_log.custom_minimum_size = Vector2(0, 140)
	root.add_child(_log)

func _say(line: String) -> void:
	if _log != null:
		_log.append_text(line + "\n")
	if auto:
		print("EMBERLINE: " + _strip(line))

func _strip(line: String) -> String:
	var out := ""
	var inside := false
	for i in line.length():
		var c := line[i]
		if c == "[": inside = true
		elif c == "]": inside = false
		elif not inside: out += c
	return out

func _refresh() -> void:
	if auto or _clock_label == null:
		return
	_clock_label.text = "%02d / %d seconds   %d down%s" % [
		seconds(), HOLD_SECONDS, killed,
		"   (aiming " + rules.GetEntity(aiming)["name"] + ")" if aiming != 0 else ""]

	for child in _party_box.get_children(): child.queue_free()
	for member in rules.GetParty():
		var who: Dictionary = rules.GetEntity(member)
		var label := Label.new()
		label.text = "%s  %d/%d hp%s" % [who["name"], rules.GetStat(member, "hp"),
			rules.GetStat(member, "max_hp"),
			"  Braced" if rules.CounterOf(member, "Braced") > 0 else ""]
		_party_box.add_child(label)

	for child in _line_box.get_children(): child.queue_free()
	for id in rules.GetEnemies():
		if id == dark:
			continue
		var it: Dictionary = rules.GetEntity(id)
		var button := Button.new()
		button.text = "%s  lane %d, rank %d  %d hp%s" % [it["name"], it["lane"], it["rank"],
			rules.GetStat(id, "hp"),
			"  Scorched %d" % rules.CounterOf(id, "Scorched") if rules.CounterOf(id, "Scorched") > 0 else ""]
		button.disabled = aiming != 0 and not rules.GetLegalTargets(aiming).has(id)
		button.pressed.connect(_on_target_pressed.bind(id))
		_line_box.add_child(button)

	for child in _ability_box.get_children(): child.queue_free()
	for member in rules.GetParty():
		for ability in abilities_of(member):
			var button := Button.new()
			button.text = "%s\n%s" % [rules.GetEntity(ability)["name"], _cooldown_text(ability)]
			button.disabled = not rules.CanUse(ability)
			button.pressed.connect(_on_ability_pressed.bind(ability))
			_ability_box.add_child(button)

# Seconds left on a cooldown, for the sweep on a button. `ready_at` is the tick it comes back at
# and is not in any document; `CanUse` is the only documented question, and it answers yes or no.
# FINDINGS #9.
func _cooldown_text(ability: int) -> String:
	if rules.CanUse(ability):
		return "ready"
	var ready_at: int = rules.GetStat(ability, "ready_at")
	var left: int = ready_at - elapsed_ticks
	if left <= 0:
		return "..."
	return "%.1fs" % (float(left) / TICKS_PER_SECOND)
