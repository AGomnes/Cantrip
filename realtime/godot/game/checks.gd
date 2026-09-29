extends Node

# What CI checks about Emberline's Godot front end: that the addon's node runs a real-time game
# at all, that the TickDriver advances the clock from the engine's own physics loop, and that a
# save taken with cooldowns running and a delayed effect in the air comes back.
#
# Nothing else in this repository drives the addon on a tick clock -- `TickDriver` has no other
# caller anywhere, tests included -- so this file is the whole of its coverage. FINDINGS #10.

var _failures := 0
var rules: CantripRuntime
var driver: TickDriver

func _ready() -> void:
	call_deferred("_run")

func _check(what: String, ok: bool, detail: String = "") -> void:
	print(("EMBER: ok   " if ok else "EMBER: FAIL ") + what
		+ ("" if ok or detail == "" else " (" + detail + ")"))
	if not ok:
		_failures += 1

func _abilities_of(id: int) -> Array:
	return rules.GetEntity(id)["abilities"]

func _named_ability(owner: int, name: String) -> int:
	for a in _abilities_of(owner):
		if rules.GetEntity(a)["name"] == name:
			return a
	return 0

func _run() -> void:
	_static_checks()
	await _driver_checks()
	get_tree().quit(1 if _failures > 0 else 0)

# --- the node on a tick clock, driven by hand ---------------------------------------------------

func _static_checks() -> void:
	rules = CantripRuntime.new()
	rules.AutoLoad = false
	rules.Seed = 7
	rules.RealTime = true
	rules.TicksPerSecond = 20
	add_child(rules)

	var report: Dictionary = rules.LoadContent("res://content")
	_check("Emberline's content loads with nothing to report",
		report["ok"] and report["errors"] == 0 and report["warnings"] == 0, str(report))

	var stoker: int = rules.CreatePlayer("Stoker", 30, 3)
	rules.GrantAbility("Ember Bolt", stoker)
	var warden: int = rules.AddHero("Warden", 0)
	var lantern: int = rules.AddHero("Lantern", 0)
	_check("AddHero works on a tick runtime", warden != 0 and lantern != 0)

	var dark: int = rules.SpawnEnemy("The Dark", -1)
	rules.StartBattle(false, false)
	rules.Execute("loom self", dark, 0)
	rules.Execute("hold self 0", warden, 0)
	rules.Execute("hold self 1", stoker, 0)
	rules.Execute("hold self 2", lantern, 0)

	var hollow: int = rules.SpawnEnemy("Hollow", -1)
	rules.Execute("arrive self 1", hollow, 0)
	_check("a wave walks in at the back rank", rules.GetEntity(hollow)["rank"] == 3,
		str(rules.GetEntity(hollow)))
	_check("and the keepers stand abreast on the front one",
		rules.GetEntity(warden)["lane"] == 0 and rules.GetEntity(warden)["rank"] == 0
		and rules.GetEntity(lantern)["lane"] == 2, str(rules.GetEntity(lantern)))

	# The node's entity dictionary lists an actor's abilities. C# has no such member, which is
	# the difference between a row of cooldown buttons taking one call and taking a zone query
	# nothing documents. FINDINGS #9.
	_check("a keeper's abilities come back with it",
		_abilities_of(warden).size() == 2, str(_abilities_of(warden)))
	var bolt: int = _named_ability(stoker, "Ember Bolt")
	_check("and an ability is ready before it is used", rules.CanUse(bolt))

	# Range is a rule the node already answers, so a targeting highlight needs no arithmetic.
	var backdraft: int = rules.GrantAbility("Backdraft", stoker)
	_check("Ember Bolt reaches the back rank", rules.GetLegalTargets(bolt).has(hollow))
	_check("Backdraft does not", not rules.GetLegalTargets(backdraft).has(hollow),
		str(rules.GetLegalTargets(backdraft)))

	_check("using it answers played", rules.UseAbility(bolt, hollow) == "played")
	_check("and the hollow felt it", rules.GetStat(hollow, "hp") == 11,
		str(rules.GetStat(hollow, "hp")))
	_check("a second use before its second is up answers not_ready",
		rules.UseAbility(bolt, hollow) == "not_ready")
	_check("which CanUse agrees with", not rules.CanUse(bolt))

	# Seconds until it comes back, for a cooldown sweep. `ready_at` is not in any document.
	var left: int = rules.GetStat(bolt, "ready_at")
	_check("a cooldown sweep can be drawn from `ready_at`", left == 20, str(left))

	rules.Tick(20)
	_check("a second later it is back", rules.CanUse(bolt))

	# No turn was taken to get here.
	_check("no turn was ever taken", rules.GetTurn() == 1, str(rules.GetTurn()))
	_check("the battle is still running", rules.IsInBattle() and rules.GetWon() == null)

	# A save with cooldowns running, a burn part way through a second and a flare in the air.
	rules.UseAbility(bolt, hollow)
	var flare: int = _named_ability(lantern, "Signal Flare")
	_check("the Lantern can flare the back rank", rules.UseAbility(flare, hollow) == "played")
	rules.Tick(10)
	_check("a save can be taken with the clock mid-second", rules.CanSave())
	var saved: Dictionary = rules.Save()
	_check("and it is accepted", saved["accepted"], str(saved.get("message", "")))
	var hash_before: String = rules.StateHash()

	var second := CantripRuntime.new()
	second.AutoLoad = false
	second.RealTime = true
	second.TicksPerSecond = 20
	add_child(second)
	second.LoadContent("res://content")
	var loaded: Dictionary = second.LoadSave(saved["save"])
	_check("it restores into a fresh node", loaded["accepted"], str(loaded.get("message", "")))
	_check("the restored game is the same game, hash for hash",
		second.StateHash() == hash_before, "%s vs %s" % [second.StateHash(), hash_before])

	# The flare lands two seconds after it was fired, on both sides of the save.
	rules.Tick(30)
	second.Tick(30)
	_check("the delayed fire lands on both sides of a save",
		rules.StateHash() == second.StateHash(), "%s vs %s" % [rules.StateHash(), second.StateHash()])
	_check("and it really did land", rules.GetStat(hollow, "hp") < 6,
		str(rules.GetStat(hollow, "hp")))

	# Turn-shaped calls on a tick runtime. None of these refuses; every one answers as though this
	# were a turn game, and EndTurn really does run one. FINDINGS #2.
	_check("CanAct answers on a tick runtime", rules.CanAct(warden))
	_check("ActiveMemberId answers too", rules.ActiveMemberId() != 0)
	var turn_before: int = rules.GetTurn()
	rules.EndTurn()
	_check("and EndTurn runs a whole turn in a game that has none",
		rules.GetTurn() == turn_before + 1, "%d -> %d" % [turn_before, rules.GetTurn()])

	rules.queue_free()
	second.queue_free()

# --- the clock coming from the engine's own frame loop ------------------------------------------

func _driver_checks() -> void:
	var node := CantripRuntime.new()
	node.AutoLoad = false
	node.Seed = 11
	node.RealTime = true
	node.TicksPerSecond = 20

	# The driver has to be in place before the node enters the tree, as docs/godot.md says.
	driver = TickDriver.new()
	node.Driver = driver
	add_child(driver)
	add_child(node)

	node.LoadContent("res://content")
	var stoker: int = node.CreatePlayer("Stoker", 30, 3)
	var dark: int = node.SpawnEnemy("The Dark", -1)
	node.StartBattle(false, false)
	node.Execute("loom self", dark, 0)
	node.Execute("hold self 1", stoker, 0)
	var hollow: int = node.SpawnEnemy("Hollow", -1)
	node.Execute("arrive self 1", hollow, 0)

	_check("the runtime puts the driver on its own rate", driver.TicksPerSecond == 20,
		str(driver.TicksPerSecond))
	_check("and the driver starts running", driver.Running)

	# Let the engine's physics loop drive it for four seconds of game time. At 20 ticks a second
	# the hollow closes a rank every two seconds, so it should be on the line by the end.
	var waited := 0.0
	while node.GetEntity(hollow)["rank"] > 0 and waited < 12.0:
		await get_tree().physics_frame
		waited += 1.0 / Engine.physics_ticks_per_second

	_check("the physics loop advanced the game's own clock",
		node.GetEntity(hollow)["rank"] == 0, "rank %d after %.1fs of frames"
		% [node.GetEntity(hollow)["rank"], waited])
	_check("in about the six seconds the content asks for",
		waited > 5.0 and waited < 8.0, "%.2fs" % waited)

	driver.Running = false
	var rank_paused: int = node.GetEntity(hollow)["rank"]
	var hp_paused: int = node.GetStat(stoker, "hp")
	for i in range(30):
		await get_tree().physics_frame
	_check("pausing the driver stops the game's clock",
		node.GetEntity(hollow)["rank"] == rank_paused and node.GetStat(stoker, "hp") == hp_paused)

	node.queue_free()
	driver.queue_free()
