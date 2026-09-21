extends Node

# Proves the GDScript path, which C# tests cannot reach: that the node is instantiable by its
# global class name, that its members answer under their C# (PascalCase) names, that dictionaries
# and arrays marshal both ways, and that signals arrive.
#
# Two rules this file exists to pin down, both of which fail loudly rather than subtly:
#   - members keep their C# names; there is no snake_case alias
#   - a C# default argument is not a default here. GDScript requires every parameter, so
#     CreatePlayer() is a parse error and CreatePlayer("Player", 80, 3) is not.
#
# Exits 0 when every check passes, 1 when one fails.

var _checks := 0
var _failures: Array[String] = []
var _events: Array[String] = []

func _ready() -> void:
	call_deferred("_run")

func _run() -> void:
	var rules := CantripRuntime.new()
	rules.AutoLoad = false
	rules.ContentFolder = "res://content"
	rules.Seed = 11
	add_child(rules)

	# Named exactly as C# declares them: there is no snake_case alias.
	_check("the node answers its C# method names", rules.has_method("LoadContent") and not rules.has_method("load_content"))

	var problems: Array = rules.LoadContent("res://content")
	_check("content loaded without errors", _errors(problems) == 0, str(problems))

	rules.EffectEvent.connect(_on_effect_event)

	var player: int = rules.CreatePlayer("Player", 80, 3)
	var slime: int = rules.SpawnEnemy("Slime", 0)
	rules.StartBattle(false, false)
	_check("the battle is running", rules.IsInBattle())

	var ember: int = rules.AddCard("Ember", "hand")
	_check("the hand came back as an array of ids", rules.GetHand().has(ember))

	var result: String = rules.Play(ember, slime)
	_check("playing a card answers with a word", result == "played", result)
	_check("the enemy took the damage", rules.GetStat(slime, "hp") == 25, str(rules.GetStat(slime, "hp")))
	_check("the game heard the events", _events.has("damaged"), ", ".join(_events))

	var view: Dictionary = rules.GetEntity(slime)
	_check("an entity arrives as a dictionary", view.has("stats") and view["stats"]["hp"] == 25, str(view.get("stats")))
	_check("with its statuses", _counter(view, "Burn") == 2, str(view.get("statuses")))

	var described: Dictionary = rules.Describe(ember, 0)
	_check("a description arrives as a dictionary", described.has("plain") and described["plain"].length() > 0, str(described.get("plain")))

	var intent: Dictionary = rules.DescribeIntent(slime)
	_check("so does an intent", intent.get("name", "") == "Swipe", str(intent.get("name")))

	_finish()

func _on_effect_event(effect_event: Dictionary) -> void:
	_events.append(effect_event["name"])

func _errors(problems: Array) -> int:
	var count := 0
	for problem in problems:
		if problem["severity"] == "error":
			count += 1
	return count

func _counter(view: Dictionary, name: String) -> int:
	for status in view.get("statuses", []):
		if status["name"] == name:
			return status["counter"]
	return 0

func _check(what: String, passed: bool, detail: String = "") -> void:
	_checks += 1
	if not passed:
		_failures.append(what if detail.is_empty() else "%s (%s)" % [what, detail])

func _finish() -> void:
	print("GDSCRIPT: %d/%d checks passed" % [_checks - _failures.size(), _checks])
	for failure in _failures:
		print("GDSCRIPT FAIL: ", failure)
	print("GDSCRIPT: all checks passed" if _failures.is_empty() else "GDSCRIPT: failures")
	get_tree().quit(0 if _failures.is_empty() else 1)
