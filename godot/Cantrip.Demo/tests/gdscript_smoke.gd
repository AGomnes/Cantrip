extends Node

# Proves the GDScript path, which C# tests cannot reach: that the node is instantiable by its
# global class name, that its members answer under their C# (PascalCase) names, that dictionaries
# and arrays marshal both ways, that signals arrive, that a lambda or a bound method registered
# as a callback reaches the rules whole, and that an answer turned away says why in the word
# docs/godot.md gives.
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
	_check("and is neither won nor lost: GetWon() is null", rules.GetWon() == null, str(rules.GetWon()))

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

	_check_callbacks(rules, slime)
	_check_answers(rules)

	_finish()

# An answer that is turned away says why in a snake_case word, like every other word the node gives.
func _check_answers(rules: CantripRuntime) -> void:
	var early: Dictionary = rules.AnswerChoice(1, [])
	_check("an answer with nothing asked is nothing_pending", not early["accepted"] and early["reason"] == "nothing_pending", str(early))

	var spare: int = rules.AddCard("Guard", "hand")
	var sort: int = rules.AddCard("Sort", "hand")
	_check("a card that asks stops for the answer", rules.Play(sort, 0) == "pending")
	var first: int = rules.GetPendingChoice()["id"]
	rules.CancelChoice()
	rules.Play(sort, 0)
	var request: Dictionary = rules.GetPendingChoice()

	var late: Dictionary = rules.AnswerChoice(first, [spare])
	_check("an answer to a request already dealt with is stale_request", late["reason"] == "stale_request", str(late))
	var twice: Dictionary = rules.AnswerChoice(request["id"], [spare, spare])
	_check("the same pick twice is duplicate_option", twice["reason"] == "duplicate_option", str(twice))
	var none: Dictionary = rules.AnswerChoice(request["id"], [])
	_check("no pick at all is too_few", none["reason"] == "too_few", str(none))

	var answer: Dictionary = rules.AnswerChoice(request["id"], [spare])
	_check("and the right answer is accepted", answer["accepted"] and answer["reason"] == "none" and answer["result"] == "played", str(answer))

# Any Callable answers content: a lambda, a lambda kept in a variable, a method with .bind(), and a
# plain method. C#'s Callable can hold only the last, so the first three used to arrive empty.
func _check_callbacks(rules: CantripRuntime, enemy: int) -> void:
	var bonus := 1
	rules.RegisterName("two_at_target", func(context: Dictionary) -> Variant: return 2 if context["target"] == enemy else 0)
	var plus_bonus := func(args: Array, _context: Dictionary) -> Variant: return int(args[0]) + bonus
	rules.RegisterFunction("plus_bonus", plus_bonus)
	rules.RegisterFunction("plus_ten", _plus.bind(10))
	rules.RegisterName("three", _three)

	var hp: int = rules.GetStat(enemy, "hp")
	rules.Execute("deal two_at_target to target", 0, enemy)
	_check("a lambda answers a name, with the context", rules.GetStat(enemy, "hp") == hp - 2, str(rules.GetStat(enemy, "hp")))

	hp = rules.GetStat(enemy, "hp")
	rules.Execute("deal plus_bonus(3) to target", 0, enemy)
	_check("a stored lambda answers a function", rules.GetStat(enemy, "hp") == hp - 4, str(rules.GetStat(enemy, "hp")))

	hp = rules.GetStat(enemy, "hp")
	rules.Execute("deal plus_ten(1) to target", 0, enemy)
	_check("so does a method with .bind()", rules.GetStat(enemy, "hp") == hp - 11, str(rules.GetStat(enemy, "hp")))

	hp = rules.GetStat(enemy, "hp")
	rules.Execute("deal three to target", 0, enemy)
	_check("and a plain method still answers", rules.GetStat(enemy, "hp") == hp - 3, str(rules.GetStat(enemy, "hp")))

func _plus(args: Array, _context: Dictionary, amount: int) -> Variant:
	return int(args[0]) + amount

func _three(_context: Dictionary) -> Variant:
	return 3

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
