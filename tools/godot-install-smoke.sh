#!/usr/bin/env bash
# Installs the Godot addon the way docs/godot.md tells a stranger to, into a blank Godot C# project
# outside this repository, then runs the guide's first battle exactly as written, its functions for
# between battles and for a call that fails, and a few checks of its own from GDScript. The demo
# project inside the repository inherits the repository's build settings, so it cannot show what a
# user's own project sees; this can. It fails on any build warning, on a plugin that does not load,
# on a first battle that reports an error or prints something other than the guide shows, and on
# any failed check.
#
# docs/godot.md marks what to take with HTML comments, which Markdown does not render:
#   <!-- smoke: file <path> -->      the next code block is written to <path> in the project
#   <!-- smoke: prints <script> -->  the next code block is how that script's output begins
#
#   tools/godot-install-smoke.sh <godot binary> <addon zip> <folder with the Cantrip.Core .nupkg>
set -euo pipefail

godot="${1:?usage: godot-install-smoke.sh <godot binary> <addon zip> <package folder>}"
zip="$(cd "$(dirname "${2:?addon zip}")" && pwd)/$(basename "$2")"
feed="$(cd "${3:?package folder}" && pwd)"
root="$(cd "$(dirname "$0")/.." && pwd)"

package="$(ls "$feed"/Cantrip.Core.*.nupkg | grep -v '\.snupkg$' | head -1)"
version="$(basename "$package" .nupkg)"
version="${version#Cantrip.Core.}"

# The project must be built with the Godot .NET SDK that matches the engine running it.
# Godot prints 4.6.2.stable.mono..., but 4.6.stable... for a .0 release and 4.7.beta1... for a
# preview; the SDK is published as 4.6.2, 4.6.0 and 4.7.0-beta.1.
sdk="$("$godot" --headless --version | head -1 | awk -F. '{
  if ($3 ~ /^[0-9]+$/) { patch = $3; status = $4 } else { patch = 0; status = $3 }
  v = $1 "." $2 "." patch
  if (status != "stable") { kind = status; n = status; sub(/[0-9]+$/, "", kind); sub(/^[a-z]+/, "", n); v = v "-" kind "." n }
  print v }')"
echo "== Godot $sdk, Cantrip.Core $version"

feed_path="$feed"
command -v cygpath > /dev/null && feed_path="$(cygpath -w "$feed")"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
project="$work/BlankGame"
mkdir -p "$project"
export NUGET_PACKAGES="$work/packages" DOTNET_CLI_HOME="$work/home" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

# What Godot writes for a new C# project, plus the plugin switched on, which step 4 of the guide
# does by hand in Project Settings.
cat > "$project/project.godot" <<'EOF'
config_version=5

[application]
config/name="BlankGame"

[dotnet]
project/assembly_name="BlankGame"

[editor_plugins]
enabled=PackedStringArray("res://addons/cantrip/plugin.cfg")
EOF
cat > "$project/BlankGame.csproj" <<EOF
<Project Sdk="Godot.NET.Sdk/$sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
</Project>
EOF

# Cantrip packages from the folder given, everything else (the Godot SDK) from nuget.org, so the
# package just built is the one tested even when the same version is already published.
cat > "$work/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="local" value="$feed_path" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="Cantrip.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
EOF

echo "== Step 1: copy addons/cantrip into the project (from the release zip)"
unzip -q "$zip" -d "$project"
[ -f "$project/addons/cantrip/plugin.cfg" ] || { echo "The zip has no addons/cantrip/plugin.cfg at its root." >&2; exit 1; }

echo "== Step 2: the command docs/godot.md gives"
pin="dotnet add package Cantrip.Core --version $version"
grep -q -F "$pin" "$root/docs/godot.md" || { echo "docs/godot.md does not say: $pin" >&2; exit 1; }
(cd "$project" && $pin > "$work/add.log" 2>&1) || { cat "$work/add.log" >&2; echo "'$pin' failed in the blank project." >&2; exit 1; }

# The code block after a marker line in a document, without carriage returns.
block() {
  awk -v want="$2" '{ sub(/\r$/, "") } $0 == want { f = 1; next } f && /^```/ { if (inb) exit; inb = 1; next } f && inb' "$1"
}

echo "== The first battle in docs/godot.md: its content and its script, as written"
mkdir -p "$project/content"
for file in content/game.cantrip content/sift.cantrip first_battle.gd; do
  block "$root/docs/godot.md" "<!-- smoke: file $file -->" > "$project/$file"
  [ -s "$project/$file" ] || { echo "docs/godot.md has no code block marked <!-- smoke: file $file -->." >&2; exit 1; }
done
block "$root/docs/godot.md" "<!-- smoke: prints first_battle.gd -->" > "$work/first_battle.expected"
[ -s "$work/first_battle.expected" ] || { echo "docs/godot.md has no code block marked <!-- smoke: prints first_battle.gd -->." >&2; exit 1; }
printf '[gd_scene format=3]\n\n[ext_resource type="Script" path="res://first_battle.gd" id="1"]\n\n[node name="FirstBattle" type="Node"]\nscript = ExtResource("1")\n' > "$project/first_battle.tscn"

# The guide says its content is the quickstart's, so keep that true.
block "$root/docs/quickstart.md" "<!-- smoke: file content/game.cantrip -->" > "$work/quickstart.cantrip"
if ! cmp -s "$work/quickstart.cantrip" "$project/content/game.cantrip"; then
  diff "$work/quickstart.cantrip" "$project/content/game.cantrip" >&2 || true
  echo "The content/game.cantrip in docs/godot.md is no longer the one in docs/quickstart.md." >&2
  exit 1
fi

echo "== Between battles and When a call fails in docs/godot.md: their content and functions, as written"
# The guide gives functions for a script that holds `rules`, so they go into one with a run of its
# own below them. The first battle loads the content too, and never uses it.
block "$root/docs/godot.md" "<!-- smoke: file content/rewards.cantrip -->" > "$project/content/rewards.cantrip"
block "$root/docs/godot.md" "<!-- smoke: file between_battles.gd -->" > "$work/between_battles.part"
block "$root/docs/godot.md" "<!-- smoke: file spawn.gd -->" > "$work/spawn.part"
for part in "$project/content/rewards.cantrip" "$work/between_battles.part" "$work/spawn.part"; do
  [ -s "$part" ] || { echo "docs/godot.md has no code block marked for $(basename "$part" .part)." >&2; exit 1; }
done
{
  printf 'extends Node\n\nvar rules: CantripRuntime\n\n'
  cat "$work/between_battles.part"
  printf '\n'
  cat "$work/spawn.part"
  cat <<'EOF'

# The run the guide's functions are checked by.
var _failures := 0

func _ready() -> void:
	call_deferred("_run")

func _check(what: String, ok: bool, detail: String = "") -> void:
	print(("BETWEEN: ok   " if ok else "BETWEEN: FAIL ") + what + ("" if ok or detail == "" else " (" + detail + ")"))
	if not ok:
		_failures += 1

func _names(ids: Array) -> Array:
	var names: Array = []
	for id in ids:
		names.append(rules.GetEntity(id)["name"])
	names.sort()
	return names

func _first_in_deck(card_name: String) -> int:
	for id in deck():
		if rules.GetEntity(id)["name"] == card_name:
			return id
	return 0

func _run() -> void:
	rules = CantripRuntime.new()
	rules.AutoLoad = false
	add_child(rules)
	var problems: Array = rules.LoadContent("res://content")
	_check("content loads", problems.is_empty(), str(problems))

	new_run(5)
	_check("a run starts with its deck", deck().size() == 8, str(_names(deck())))
	var ghoul: int = spawn("Ghoul")
	rules.StartBattle(true, true)
	rules.Execute("deal 99 to target", 0, ghoul)
	_check("the battle is won, and the whole deck is back in the draw pile",
		rules.GetWon() == true and deck().size() == 8, str(_names(deck())))

	print("BETWEEN: spawning an enemy no content defines; the error it reports next is expected.")
	_check("a failed call is seen in an untyped variable", spawn("Nobody") == 0)

	_check("a Strike upgrades", upgrade_card(_first_in_deck("Strike")))
	_check("a Defend has no upgrade, so it stays", not upgrade_card(_first_in_deck("Defend")))
	remove_card(_first_in_deck("Curse"))
	_check("the deck is as the player left it",
		_names(deck()) == ["Curse", "Defend", "Defend", "Sift", "Strike", "Strike", "Strike+"], str(_names(deck())))

	var rng := RandomNumberGenerator.new()
	rng.seed = 5
	var offer: Array = offer_rewards(rng)
	var offered: Array = offer.duplicate()
	offered.sort()
	_check("three reward cards are offered, and no upgraded one", offered == ["Brace", "Cleave", "Flurry"], str(offer))
	rules.AddCard(offer[0], "draw")

	var next_ghoul: int = spawn("Ghoul")
	rules.StartBattle(true, true)
	_check("the next battle is dealt from the deck as it now is",
		rules.GetHand().size() + deck().size() == 8, str(_names(rules.GetHand())))

	print("BETWEEN: playing a card whose content fails; the error it reports next is expected.")
	var result: String = rules.Play(rules.AddCard("Broken", "hand"), next_ghoul)
	_check("a failed call's null in a typed variable raises no second error, and == null does not see it",
		result != "played" and not (result == null))

	new_run(6)
	_check("a new run starts over",
		rules.GetWon() == null and not rules.IsInBattle() and deck().size() == 8 and not _names(deck()).has("Strike+"), str(_names(deck())))

	get_tree().quit(1 if _failures > 0 else 0)
EOF
} > "$project/between_battles.gd"
printf '[gd_scene format=3]\n\n[ext_resource type="Script" path="res://between_battles.gd" id="1"]\n\n[node name="BetweenBattles" type="Node"]\nscript = ExtResource("1")\n' > "$project/between_battles.tscn"

# For the checks of its own below: cards that discover, so an offer goes through the node. The first
# battle loads them too, and never uses them.
cat > "$project/content/offers.cantrip" <<'EOF'
card Spark
  cost 0
  tags spell

card Flare
  cost 0
  tags spell

card Glint
  cost 0
  tags spell

card Scholar
  cost 0
  effect:
    discover 3 cards where tag:spell as found
    create found into hand
EOF

# And a card whose content fails when it is played, for what When a call fails says about a typed
# variable.
cat > "$project/content/broken.cantrip" <<'EOF'
card Broken
  cost 0
  target enemy
  effect:
    deal nonsense to target
EOF

cat > "$project/main.gd" <<'EOF'
extends Node

var _failures := 0

func _ready() -> void:
	call_deferred("_run")

func _check(what: String, ok: bool, detail: String = "") -> void:
	print(("INSTALL: ok   " if ok else "INSTALL: FAIL ") + what + ("" if ok or detail == "" else " (" + detail + ")"))
	if not ok:
		_failures += 1

func _run() -> void:
	var rules := CantripRuntime.new()
	rules.AutoLoad = false
	add_child(rules)

	var problems: Array = rules.LoadContent("res://content")
	_check("content loads", problems.is_empty(), str(problems))

	rules.CreatePlayer("Player", 40, 3)
	var ghoul: int = rules.SpawnEnemy("Ghoul", 0)
	rules.StartBattle(false, false)

	var strike: int = rules.AddCard("Strike", "hand")
	_check("a card plays", rules.Play(strike, ghoul) == "played")
	_check("and hits", rules.GetStat(ghoul, "hp") == 24, str(rules.GetStat(ghoul, "hp")))

	# A lambda, which C#'s Callable cannot carry, answers content; and the game still exits cleanly
	# with it registered, which the status check below sees.
	var bonus := 2
	rules.RegisterName("bonus", func(_context: Dictionary) -> Variant: return bonus)
	rules.Execute("deal bonus to target", 0, ghoul)
	_check("a lambda answers content", rules.GetStat(ghoul, "hp") == 22, str(rules.GetStat(ghoul, "hp")))

	var scholar: int = rules.AddCard("Scholar", "hand")
	_check("discover asks the player", rules.Play(scholar, 0) == "pending")
	var choice: Dictionary = rules.GetPendingChoice()
	_check("as an offer of three cards", choice.get("kind") == "offer" and choice.get("options", []).size() == 3, str(choice))
	var outside: Dictionary = rules.AnswerChoice(choice["id"], [3])
	_check("a position the offer does not have is turned away as unknown_option",
		not outside["accepted"] and outside["reason"] == "unknown_option", str(outside))
	var picked: String = choice["options"][1]["name"]
	var answer: Dictionary = rules.AnswerChoice(choice["id"], [1])
	_check("answering by position finishes the card", answer.get("result") == "played", str(answer))
	var names: Array = []
	for id in rules.GetHand():
		names.append(rules.GetEntity(id)["name"])
	_check("and creates the card picked", names == [picked], str(names))

	get_tree().quit(1 if _failures > 0 else 0)
EOF
printf '[gd_scene load_steps=2 format=3]\n\n[ext_resource type="Script" path="res://main.gd" id="1"]\n\n[node name="Main" type="Node"]\nscript = ExtResource("1")\n' > "$project/main.tscn"

echo "== Step 3: build before enabling the plugin"
for configuration in Debug ExportRelease; do
  (cd "$project" && dotnet build -c "$configuration" --nologo -v quiet 2>&1) | tee "$work/build.log"
  if grep -q -E "warning [A-Z]+[0-9]+" "$work/build.log"; then
    echo "The addon builds with warnings in a blank project ($configuration)." >&2
    exit 1
  fi
done

echo "== Step 4: the plugin loads and imports the content"
timeout 300 "$godot" --headless --path "$project" --import > "$work/import.log" 2>&1 || true
grep "Cantrip:" "$work/import.log" || true
grep -q "Cantrip: dock ready" "$work/import.log" || { cat "$work/import.log" >&2; echo "The plugin did not load." >&2; exit 1; }

echo "== Your first battle, as docs/godot.md gives it"
# Like a game, the script never quits by itself, so Godot stops after a few frames; the whole battle
# is played in the first. Everything Godot says is kept, so a script error or a C# exception shows
# in the log when it fails, and anything it reports as an error fails the run.
status=0
timeout 300 "$godot" --headless --path "$project" res://first_battle.tscn --quit-after 30 > "$work/first_battle.log" 2>&1 || status=$?
tr -d '\r' < "$work/first_battle.log" > "$work/first_battle.out"
# What it printed, from the line the guide's output starts with: Godot's own banner comes first.
awk -v first="$(head -n 1 "$work/first_battle.expected")" -v lines="$(wc -l < "$work/first_battle.expected")" \
  'seen || $0 == first { seen = 1; if (printed++ < lines) print }' "$work/first_battle.out" > "$work/first_battle.start"
problem=""
if [ "$status" -ne 0 ]; then problem="Godot exited with $status."
elif grep -q "ERROR" "$work/first_battle.out"; then problem="It reported an error."
elif ! cmp -s "$work/first_battle.expected" "$work/first_battle.start"; then problem="It does not begin as the guide shows."
elif ! grep -q -x "The Ghoul falls." "$work/first_battle.out" || ! grep -q "^Turn 3\. " "$work/first_battle.out" || grep -q "^Turn 4\. " "$work/first_battle.out"; then
  problem="The guide says the Ghoul loses on turn 3."
fi
if [ -n "$problem" ]; then
  cat "$work/first_battle.log" >&2
  diff "$work/first_battle.expected" "$work/first_battle.start" >&2 || true
  echo "The first battle in docs/godot.md failed. $problem" >&2
  exit 1
fi
cat "$work/first_battle.out"

echo "== Between battles and When a call fails, as docs/godot.md gives them"
# The run quits by itself; --quit-after only stops one that a script error cut short, which the
# missing last check then shows.
status=0
timeout 300 "$godot" --headless --path "$project" res://between_battles.tscn --quit-after 60 > "$work/between.log" 2>&1 || status=$?
tr -d '\r' < "$work/between.log" > "$work/between.out"
grep -E "^(BETWEEN|Brace|Cleave|Flurry)" "$work/between.out" || true
if [ "$status" -ne 0 ] || grep -q -E "BETWEEN: FAIL|SCRIPT ERROR" "$work/between.out" || ! grep -q "BETWEEN: ok   a new run starts over" "$work/between.out" \
  || ! grep -q -x "Cleave: Deal 8 damage to ALL enemies." "$work/between.out" || ! grep -q -x "Flurry: Deal 4 damage. Deal 4 damage." "$work/between.out"; then
  cat "$work/between.log" >&2
  echo "The functions under Between battles or When a call fails in docs/godot.md failed (exit $status)." >&2
  exit 1
fi

echo "== Checks of its own: a battle, a lambda callback and a discover offer from GDScript"
status=0
timeout 300 "$godot" --headless --path "$project" res://main.tscn > "$work/run.log" 2>&1 || status=$?
grep "INSTALL:" "$work/run.log" || true
if [ "$status" -ne 0 ] || grep -q "INSTALL: FAIL" "$work/run.log" || ! grep -q "INSTALL: ok   and creates the card picked" "$work/run.log"; then
  cat "$work/run.log" >&2
  [ "$status" -eq 124 ] && echo "Godot timed out: a script error probably stopped the scene before it quit." >&2
  echo "The GDScript battle failed (exit $status)." >&2
  exit 1
fi

echo "== The addon installs into a blank project and works as docs/godot.md says."
