extends Node

# What CI checks about the Godot front end: that the addon's node plays this game's content, that
# a party under `turns: initiative` behaves the way the C# host says it does, and that a save
# taken part-way down a run restores into a node that has only loaded its content.

var _failures := 0
var rules: CantripRuntime

func _ready() -> void:
	call_deferred("_run")

func _check(what: String, ok: bool, detail: String = "") -> void:
	print(("CHAPEL: ok   " if ok else "CHAPEL: FAIL ") + what
		+ ("" if ok or detail == "" else " (" + detail + ")"))
	if not ok:
		_failures += 1

func _counter(id: int, status: String) -> int:
	for entry in rules.GetEntity(id)["statuses"]:
		if entry["name"] == status: return entry["counter"]
	return 0

func _named(ids: Array, name: String) -> int:
	for id in ids:
		if rules.GetEntity(id)["name"] == name: return id
	return 0

func _run() -> void:
	rules = CantripRuntime.new()
	rules.AutoLoad = false
	rules.Seed = 7
	add_child(rules)

	var report: Dictionary = rules.LoadContent("res://content")
	_check("the chapel's content loads with nothing to report",
		report["ok"] and report["errors"] == 0 and report["warnings"] == 0, str(report))

	var leader: int = rules.CreatePlayer("Acolyte", 40, 3)
	rules.Execute("speed = 6", 0, 0)
	rules.Execute("grant Invocation", 0, 0)
	var warden: int = rules.AddHero("Warden", 0)
	var cantor: int = rules.AddHero("Cantor", 0)
	_check("AddHero returns a member for each hero declaration", warden != 0 and cantor != 0)

	rules.AddDeck(["Censer", "Censer", "Litany", "Wade", "Pike"])
	var acolyte: int = rules.SpawnEnemy("Drowned Acolyte", -1)
	rules.StartBattle(true, true)

	# The board. Both sides fill the first aisle before the second.
	_check("the party stands on the board", rules.GetEntity(warden)["rank"] == 1
		and rules.GetEntity(cantor)["lane"] == 1, str(rules.GetEntity(warden)))
	_check("GetParty counts three, GetAllies the same, and the leader is a member",
		rules.GetParty().size() == 3 and rules.GetAllies().size() == 3
		and rules.GetEntity(leader)["party_member"], str(rules.GetParty()))

	# Initiative: the leader has speed 6 and goes first, which is also when the party draws.
	_check("the fastest member is up first", rules.ActiveMemberId() == leader,
		str(rules.GetEntity(rules.ActiveMemberId())["name"]))
	_check("and it is the leader that holds the hand",
		rules.GetZone(leader, "hand").size() == 5, str(rules.GetZone(leader, "hand").size()))
	_check("a member with no pile of its own holds nothing",
		rules.GetZone(warden, "hand").is_empty())
	# Under `turns: initiative` CanAct is the active member and nobody else, which is what the
	# UI greys out by. Under `turns: sides` it would be true for every member still waiting.
	_check("CanAct is the active member alone under initiative",
		rules.CanAct(leader) and not rules.CanAct(warden))

	# An intent that names its target, and a taunt that re-aims it with no second roll.
	var intent: Dictionary = rules.DescribeIntent(acolyte)
	_check("the enemy telegraphs a move and a member", intent["name"] == "Clutch"
		and intent["target"] != 0, str(intent))
	_check("and the intent line names them both", intent["line"].contains("->")
		or intent["line"].contains("→"), intent["line"])
	var aimed_at: int = intent["target"]

	rules.Pass(leader)
	_check("passing runs the order on to the next of ours", rules.ActiveMemberId() == warden,
		str(rules.GetEntity(rules.ActiveMemberId())["name"]))
	var bulwark: int = _named(rules.GetEntity(warden)["abilities"], "Bulwark")
	_check("the Warden was granted its declared abilities", bulwark != 0)
	rules.UseAbility(bulwark, warden)
	_check("a taunt re-aims the telegraph with no new roll",
		rules.DescribeIntent(acolyte)["target"] == warden and aimed_at != warden,
		"%d then %d" % [aimed_at, rules.DescribeIntent(acolyte)["target"]])
	_check("and the ability is on cooldown afterwards", not rules.CanUse(bulwark))

	# Reach, from the node's own targeting question rather than from a play that fails.
	var censer: int = _named(rules.GetZone(leader, "hand"), "Censer")
	if censer == 0:
		censer = rules.AddCard("Censer", "hand")
	_check("a card names what it is aimed at", rules.GetTargetMode(censer) == "enemy")
	_check("and the node answers which enemies are in reach",
		rules.GetLegalTargets(censer).size() == 1, str(rules.GetLegalTargets(censer)))

	# A card played by a named member: the cost leaves the leader's pool and the rest is the
	# performer's.
	var energy_before: int = rules.GetStat(leader, "energy")
	var litany: int = rules.AddCard("Litany", "hand")
	_check("a member performs a play out of the leader's hand",
		rules.PlayBy(litany, warden, warden) == "played")
	_check("the cost came out of the leader's pool",
		rules.GetStat(leader, "energy") == energy_before - 1,
		"%d then %d" % [energy_before, rules.GetStat(leader, "energy")])
	# GetStat does not read a status counter, although `Warden.Fervour` does in content and
	# CounterOf does in C#. From GDScript the only way to a status number is the `statuses`
	# array in the entity dictionary. See reference/FINDINGS.md.
	_check("and the Fervour went to whoever spoke the words",
		_counter(warden, "Fervour") == 1 and _counter(leader, "Fervour") == 0,
		"warden %d leader %d, and GetStat says %d" % [_counter(warden, "Fervour"),
			_counter(leader, "Fervour"), rules.GetStat(warden, "Fervour")])

	# Saving, part-way through, into a node that has only loaded its content.
	var hash_before: String = rules.StateHash()
	var saved: Dictionary = rules.Save()
	_check("a save is accepted between actions", saved["accepted"], str(saved.get("message", "")))

	var second := CantripRuntime.new()
	second.AutoLoad = false
	second.Seed = 7
	add_child(second)
	second.LoadContent("res://content")
	var loaded: Dictionary = second.LoadSave(saved["save"])
	_check("and restores into a fresh node with no player of its own",
		loaded["accepted"], str(loaded.get("message", "")))
	_check("the restored game is the same game, hash for hash",
		second.StateHash() == hash_before, "%s vs %s" % [hash_before, second.StateHash()])
	_check("its ids are the ones the run wrote down",
		second.GetEntity(warden)["name"] == "Warden", str(second.GetEntity(warden)))
	_check("and the member whose step it is came back with it",
		second.ActiveMemberId() == rules.ActiveMemberId())

	# The one thing content cannot do, done by the node, because the run kept the id.
	second.Execute("deal 99 to target", 0, cantor)
	_check("a fallen member leaves the party", second.GetParty().size() == 2,
		str(second.GetParty().size()))
	_check("and is not in GetAllies either", not second.GetAllies().has(cantor))
	_check("Revive is the only way back, and it takes an id",
		second.Revive(cantor, 9) and second.GetStat(cantor, "hp") == 9,
		str(second.GetStat(cantor, "hp")))
	_check("the battle is not lost while anyone stands", second.GetWon() == null)

	# Rules text with live values, for a card frame.
	var described: Dictionary = second.Describe(censer, acolyte)
	_check("a card describes itself with its live numbers",
		described["plain"].contains("damage") and not described["empty"], str(described["plain"]))
	_check("and a definition describes itself before anything is in play",
		second.DescribeDefinition("Requiem", "card")["plain"].contains("12"),
		second.DescribeDefinition("Requiem", "card")["plain"])

	get_tree().quit(1 if _failures > 0 else 0)
