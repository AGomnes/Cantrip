extends Node

# A minimal reproduction of the crash found while building the Godot front end: any call into
# CantripRuntime made from a `BattleEnded` handler recurses until the process dies with a stack
# overflow. docs/godot.md's Between battles section happens to avoid it by putting its calls in
# a second function, and says nothing about why that matters.

var rules: CantripRuntime
var depth := 0
var mode := ""

func _ready() -> void:
	mode = "read" if "--reentry-read" in OS.get_cmdline_user_args() else "write"
	rules = CantripRuntime.new()
	rules.AutoLoad = false
	rules.Seed = 7
	add_child(rules)
	rules.LoadContent("res://content")
	rules.BattleEnded.connect(_on_battle_ended)

	rules.CreatePlayer("Acolyte", 40, 3)
	rules.Execute("speed = 6", 0, 0)
	var foe: int = rules.SpawnEnemy("Drowned Acolyte", -1)
	rules.StartBattle(true, true)
	rules.Execute("deal 99 to target", 0, foe)
	print("REENTRY: survived, handler ran %d time(s), mode %s" % [depth, mode])
	get_tree().quit(0)

func _on_battle_ended(_won: bool) -> void:
	depth += 1
	if depth > 40:
		print("REENTRY: runaway, the handler has been re-entered %d times" % depth)
		get_tree().quit(1)
		return
	if mode == "read":
		var _turn: int = rules.GetTurn()
	else:
		rules.Execute("gain 30 gold", 0, 0)
