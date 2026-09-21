#!/usr/bin/env bash
# Installs the Godot addon the way docs/godot.md tells a stranger to, into a blank Godot C# project
# outside this repository, and plays a battle from GDScript. The demo project inside the repository
# inherits the repository's build settings, so it cannot show what a user's own project sees; this
# can. It fails on any build warning, on a plugin that does not load, and on any failed check.
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
sdk="$("$godot" --headless --version | head -1 | cut -d. -f1-3)"
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
(cd "$project" && $pin > /dev/null)

# Content: the quickstart's game, plus a card that discovers, so an offer goes through the node.
mkdir -p "$project/content"
awk '/<!-- smoke: file content\/game.cantrip -->/ { f = 1; next } f && /^```/ { if (inb) exit; inb = 1; next } f && inb' \
  "$root/docs/quickstart.md" > "$project/content/game.cantrip"
cat >> "$project/content/game.cantrip" <<'EOF'

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

	var scholar: int = rules.AddCard("Scholar", "hand")
	_check("discover asks the player", rules.Play(scholar, 0) == "pending")
	var choice: Dictionary = rules.GetPendingChoice()
	_check("as an offer of three cards", choice.get("kind") == "offer" and choice.get("options", []).size() == 3, str(choice))
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

echo "== Step 5: a battle from GDScript"
timeout 300 "$godot" --headless --path "$project" res://main.tscn 2>&1 | tee "$work/run.log" | grep "INSTALL:"
[ "${PIPESTATUS[0]}" -eq 0 ] || { echo "The GDScript battle failed." >&2; exit 1; }
! grep -q "INSTALL: FAIL" "$work/run.log"

echo "== The addon installs into a blank project and works as docs/godot.md says."
