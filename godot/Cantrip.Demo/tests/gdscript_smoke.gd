extends Node

# Proves the GDScript path, which C# tests cannot reach: that the node is instantiable by its
# global class name, that its members answer under their C# (PascalCase) names, that dictionaries
# and arrays marshal both ways, that signals arrive, that a lambda or a bound method registered
# as a callback reaches the rules whole, that an answer turned away says why in the word
# docs/godot.md gives, and that the calls a run makes between battles answer as it says.
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
	_check_surface(rules)

	var report: Dictionary = rules.LoadContent("res://content")
	_check("content loaded without errors", report["ok"] and report["errors"] == 0, str(report))
	_check("and the report carries the problems themselves", _errors(report["diagnostics"]) == 0, str(report["diagnostics"]))

	rules.EffectEvent.connect(_on_effect_event)

	var player: int = rules.CreatePlayer("Player", 80, 3)
	var slime: int = rules.SpawnEnemy("Slime", -1)
	rules.StartBattle(false, false)
	_check("the battle is running", rules.IsInBattle())
	_check("and is neither won nor lost: GetWon() is null", rules.GetWon() == null, str(rules.GetWon()))

	var ember: int = rules.AddCard("Ember", "hand")
	_check("the hand came back as an array of ids", rules.GetZone(player, "hand").has(ember))

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
	_check_refusals(rules, player, slime)
	_check_saving(rules)
	_check_answers(rules)
	_check_between_battles(rules)
	_check_party(rules)
	_check_empty_board(rules)

	_finish()

# Nobody left to aim at is `no_target` and not `invalid_target`: nothing was filtered out and
# nothing was too far away, the table is simply empty, and a game usually wants to say something
# different about that. Until 1.0 the two were one word. Last, because it clears the board.
func _check_empty_board(rules: CantripRuntime) -> void:
	for enemy_id in rules.GetEnemies():
		rules.Execute("kill target", 0, enemy_id)
	_check("the board is clear", rules.GetEnemies().is_empty(), str(rules.GetEnemies()))

	var nobody: String = rules.Play(rules.AddCard("Ember", "hand"), 0)
	_check("a card with nobody to aim at answers no_target", nobody == "no_target", nobody)

# What the node does and does not publish. GDScript compiles nothing until the line runs, so a
# method that went away at 1.0 must be gone from has_method too, and a method that was never meant
# for script must never have been there: Godot publishes every C# method of a [GlobalClass],
# private ones included, so these are the ones that had to be moved off the node.
func _check_surface(rules: CantripRuntime) -> void:
	_check("GetHand is gone, so a party game cannot read one hero's hand by accident",
		not rules.has_method("GetHand"))

	var internals := ["Guard", "AfterAction", "SyncChoice", "Word", "Refused"]
	var published: Array[String] = []
	for name in internals:
		if rules.has_method(name):
			published.append(name)
	_check("the node's own loop is not callable from script", published.is_empty(), ", ".join(published))

	var presenter := BattlePresenter.new()
	_check("nor the presenter's", not presenter.has_method("Pump"))
	presenter.free()

# Every answer a game reads rather than a value it asked for: a word, a number, or a key that is
# always there. Each of these used to be a silence or a stack trace.
func _check_refusals(rules: CantripRuntime, player: int, slime: int) -> void:
	_check("an id that names nothing has no target mode, which is not the same as needing none",
		rules.GetTargetMode(999999) == "" and rules.GetTargetMode(rules.AddCard("Guard", "hand")) == "none",
		rules.GetTargetMode(999999))

	print("GDSCRIPT: two calls with names no content defines follow; what they report is expected.")
	_check("a status no content defines answers 0, as an unknown target does",
		rules.ApplyStatus("Nothing", slime, 1) == 0 and rules.ApplyStatus("Burn", 999999, 1) == 0)
	_check("and so does an ability", rules.GrantAbility("Nothing", player) == 0)

	print("GDSCRIPT: an enemy asked for with 0 health follows; the error it reports is expected.")
	var nothing = rules.SpawnEnemy("Slime", 0)  # untyped: a refused call answers null
	_check("an enemy with 0 health is refused rather than quietly given the content's", nothing == null, str(nothing))

	var described: Dictionary = rules.Describe(slime, 0)
	_check("a description always has a cost key, null when there is none",
		described.has("cost") and described["cost"] == null, str(described.get("cost")))

	var ability: Dictionary = rules.Describe(rules.GetZone(player, "hand")[0], 0)
	_check("and a segment for a card that has one", ability.has("cost") and ability["cost"] != null, str(ability.get("cost")))

	_check("UseAbility answers a word, not a bool", rules.UseAbility(999999, 0) == "not_a_card", str(rules.UseAbility(999999, 0)))

	# The refusal words, which are the frozen strings a game's `match` arms are written against.
	# `cannot_afford` was `not_enough_energy` until 1.0 and came back for a card priced in anything
	# at all, so a game with bones, rage or charges showed the player the wrong noun -- and a
	# GDScript arm goes on compiling however wrong the word is.
	var ember: int = rules.AddCard("Ember", "hand")
	var energy: int = rules.GetStat(player, "energy")
	rules.SetStat(player, "energy", 0)
	var broke: String = rules.Play(ember, slime)
	_check("a card that cannot be paid for answers cannot_afford", broke == "cannot_afford", broke)
	_check("and the word names no resource, because a card may be priced in any",
		not broke.contains("energy"), broke)
	rules.SetStat(player, "energy", energy)
	_check("and it was refused rather than half played", rules.GetZone(player, "hand").has(ember))

# Save answers the same dictionary LoadSave does, so a save button can say why it did nothing.
func _check_saving(rules: CantripRuntime) -> void:
	var saved: Dictionary = rules.Save()
	_check("a save comes back accepted, with the game under save", saved["accepted"] and saved["save"].length() > 0, str(saved.get("reason")))
	_check("and its reason is none", saved["reason"] == "none" and saved["message"] == "", str(saved))

	var loaded: Dictionary = rules.LoadSave(saved["save"])
	_check("which loads again", loaded["accepted"], str(loaded))

	var nonsense: Dictionary = rules.LoadSave("{ not a save")
	_check("while text that is not a save is refused as wrong_format",
		not nonsense["accepted"] and nonsense["reason"] == "wrong_format" and nonsense["message"].length() > 0, str(nonsense))

# What a run needs between battles, through the GDScript call path: names come back as an array of
# strings, a definition's text as the dictionary Describe gives, a removed card as a bool, and a new
# run leaves the node ready to set up again. Last, because the new run ends the game above.
func _check_between_battles(rules: CantripRuntime) -> void:
	var cards: Array = rules.GetDefinitions("card", "")
	_check("definitions are listed by name", cards == ["Ember", "Guard", "Sort"], str(cards))
	_check("and narrowed by a tag", rules.GetDefinitions("card", "skill") == ["Guard", "Sort"], str(rules.GetDefinitions("card", "skill")))

	var guard: Dictionary = rules.DescribeDefinition("Guard", "card")
	_check("a definition out of play describes itself", guard.get("plain", "") == "Gain 6 Block.", str(guard.get("plain")))
	_check("and a name nothing defines is empty", rules.DescribeDefinition("Nothing", "").is_empty())

	var spare: int = rules.AddCard("Guard", "draw")
	_check("a card is removed from the deck", rules.RemoveCard(spare) and not rules.GetZone(0, "draw").has(spare))
	_check("once", not rules.RemoveCard(spare))

	rules.Seed = 3
	rules.NewRun()
	_check("a new run has no player and no battle", rules.PlayerId() == 0 and not rules.IsInBattle() and rules.GetWon() == null)
	_check("and sets up again", rules.CreatePlayer("Player", 80, 3) != 0 and rules.SpawnEnemy("Slime", -1) != 0)

	# Every parameter, spelled out. StartBattleOn is a method of its own rather than a third
	# argument to StartBattle for exactly this reason: a C# default is not a default here, so
	# adding one would have been a parse error in every game that already calls StartBattle.
	rules.StartBattleOn("", false, false)
	_check("StartBattleOn keeps the board in play when no name is given", rules.IsInBattle())

	var leader: int = rules.PlayerId()
	_check("a stat is written and read back", rules.SetStat(leader, "gold", 9) == 9 and rules.GetStat(leader, "gold") == 9)
	_check("and changed by an amount", rules.ChangeStat(leader, "gold", -4) == -4 and rules.GetStat(leader, "gold") == 5)

	# The number a status bar wants, without building the whole entity dictionary per frame.
	var slime: int = rules.GetEnemies()[0]
	rules.Execute("apply Burn 3 to target", 0, slime)
	_check("a status counter is one call", rules.CounterOf(slime, "Burn") == 3, str(rules.CounterOf(slime, "Burn")))
	_check("and a status is still not a stat", rules.GetStat(slime, "Burn") == 0)


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
	_check("a request says which kind of question it is under mode, not kind",
		request["mode"] == "entities" and not request.has("kind"), str(request.keys()))

	var late: Dictionary = rules.AnswerChoice(first, [spare])
	_check("an answer to a request already dealt with is stale_request", late["reason"] == "stale_request", str(late))
	_check("and a refused answer still carries a result key, empty", late.get("result", null) == "", str(late))
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

# The party surface: the calls a game with more than one member makes, and the two keys an entity
# dictionary grew. A game with one member can call every one of these and cannot tell the answers
# from what it did before, which is the promise; this adds a second member so they differ.
func _check_party(rules: CantripRuntime) -> void:
	var leader: int = rules.PlayerId()
	var scout: int = rules.AddHero("Scout", 0)
	_check("AddHero answers an id of its own", scout != 0 and scout != leader, str(scout))
	_check("and a name no hero is declared under answers 0", rules.AddHero("Nothing", 0) == 0)

	rules.StartBattle(false, false)
	var party: Array = rules.GetParty()
	_check("the party is the leader and the hero, in the order they stand in", party == [leader, scout], str(party))
	_check("which is not the same as the allies", rules.GetAllies().size() == 2)

	_check("the leader is the one the engine would offer", rules.ActiveMemberId() == leader, str(rules.ActiveMemberId()))
	_check("and both may act, because turns: sides gives the party one turn", rules.CanAct(leader) and rules.CanAct(scout))
	_check("an id that names nobody cannot act", not rules.CanAct(999999))

	var view: Dictionary = rules.GetEntity(scout)
	_check("an entity says whether it is a party member and whether it has acted",
		view["party_member"] and not view["acted"], str(view.get("acted")))
	_check("and an enemy is neither", not rules.GetEntity(rules.GetEnemies()[0])["party_member"])

	rules.Pass(leader)
	_check("passing one member leaves the other's step", rules.CanAct(scout) and not rules.CanAct(leader))
	_check("the one that passed says it acted", rules.GetEntity(leader)["acted"], str(rules.GetEntity(leader)))
	_check("so the hero is now the one offered", rules.ActiveMemberId() == scout, str(rules.ActiveMemberId()))

	var snipe: int = rules.GetEntity(scout)["abilities"][0]
	_check("CanUse is true for an ability off cooldown", rules.CanUse(snipe))
	_check("and false for an id that is not an ability", not rules.CanUse(scout) and not rules.CanUse(999999))
	_check("using it works", rules.UseAbility(snipe, rules.GetEnemies()[0]) == "played")
	_check("and puts it on cooldown, which CanUse answers for", not rules.CanUse(snipe))

	var ember: int = rules.AddCard("Ember", "hand")
	_check("a card out of the party's hand can be played by a named member",
		rules.PlayBy(ember, rules.GetEnemies()[0], scout) == "played")
	_check("and a card named rather than held is looked for in that member's hand first",
		rules.PlayNamedBy("Guard", 0, scout) == "not_in_hand")
	_check("while Play itself still takes the two arguments every preview script wrote",
		rules.Play(rules.AddCard("Guard", "hand"), 0) == "played")

	var intent: Dictionary = rules.DescribeIntent(rules.GetEnemies()[0])
	_check("an intent names who it is telegraphed against", intent["target"] != 0, str(intent.get("target")))
	_check("by name as well as by id", party.has(intent["target"]) and not intent["target_name"].is_empty(), str(intent))
	_check("and reads as one line, move and target together",
		intent["line"].begins_with("Swipe") and intent["line"].contains(intent["target_name"]), intent["line"])

	rules.Execute("kill target", 0, scout)
	_check("a fallen member leaves the party", not rules.GetParty().has(scout), str(rules.GetParty()))
	_check("and turns up in the fallen, which nothing else lists", rules.GetFallen() == [scout], str(rules.GetFallen()))
	_check("but the battle goes on, because the leader still stands", rules.IsInBattle())
	_check("Revive brings it back, where a heal refuses", rules.Revive(scout, 5) and rules.GetStat(scout, "hp") == 5)
	_check("reviving the living answers false", not rules.Revive(scout, 5))
	_check("as does an id that names nobody", not rules.Revive(999999, 1))


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
