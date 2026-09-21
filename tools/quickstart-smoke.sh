#!/usr/bin/env bash
# Follows docs/quickstart.md word for word in an empty folder, against packages built from this
# repository instead of nuget.org, and fails if any step does. A quickstart that nobody runs goes
# stale without anyone noticing; this is what notices.
#
#   tools/quickstart-smoke.sh <folder with Cantrip.Core and Cantrip.Cli .nupkg files>
#
# The quickstart marks what to run with HTML comments, which Markdown does not render:
#   <!-- smoke: run -->          the next code block is shell commands, run in order
#   <!-- smoke: file <path> -->  the next code block is written to <path>
# Everything the reader copies is marked; nothing else runs.
set -euo pipefail

feed="$(cd "${1:?usage: quickstart-smoke.sh <package folder>}" && pwd)"
root="$(cd "$(dirname "$0")/.." && pwd)"
doc="$root/docs/quickstart.md"

ls "$feed"/Cantrip.Core.*.nupkg "$feed"/Cantrip.Cli.*.nupkg > /dev/null

# Git Bash on Windows spells paths /c/Users/...; dotnet there needs C:\Users\...
feed_path="$feed"
command -v cygpath > /dev/null && feed_path="$(cygpath -w "$feed")"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Only the local feed, and a private package cache: a package with the same version already on
# nuget.org, or cached from an earlier run, must not stand in for the one just built.
cat > "$work/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed_path" />
  </packageSources>
</configuration>
EOF
export NUGET_PACKAGES="$work/packages"
# Local tools are found through a resolver cache under the .NET home folder; a stale entry from an
# earlier run, pointing into a package folder since deleted, would make `dotnet cantrip` fail.
export DOTNET_CLI_HOME="$work/home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

script="$work/quickstart.sh"
{
  echo 'set -euo pipefail'
  awk '
    /^<!-- smoke: run -->/ { mode = "run"; next }
    /^<!-- smoke: file /   { mode = "file"; path = $4; next }
    /^```/ {
      if (infence) {
        if (capturing && mode == "file") print "SMOKE_EOF"
        infence = 0; capturing = 0; mode = ""
      } else {
        infence = 1
        if (mode != "") {
          capturing = 1
          if (mode == "file") print "cat > \"" path "\" <<'"'"'SMOKE_EOF'"'"'"
        }
      }
      next
    }
    capturing { print }
  ' "$doc"
} > "$script"

grep -q "dotnet add package Cantrip.Core" "$script" || { echo "No marked steps found in $doc." >&2; exit 1; }

echo "== Following docs/quickstart.md in $work"
cd "$work"
bash "$script"

echo "== Checking the result"
cd "$work/HexDuel"
dotnet cantrip test content | tee "$work/test.txt"
grep -q "3 passed, 0 failed" "$work/test.txt"

# The first compile of the Program.cs the reader pastes in: its errors must reach the log.
dotnet build --nologo -v quiet

# The game is interactive: play card 1 three times and end the turn, until someone falls. The
# result can land on the same line as the last prompt, so match the end of the line, and check the
# game ended before the input ran out, because Program.cs also says "You fall." when it does.
rounds=30
for _ in $(seq 1 $rounds); do printf '1\n1\n1\n\n'; done | dotnet bin/Debug/net*/HexDuel.dll > "$work/game.txt"
last="$(tail -n 1 "$work/game.txt")"
echo "$last"
case "$last" in
  *"The Ghoul falls."|*"You fall.") ;;
  *) echo "The game did not end." >&2; exit 1 ;;
esac
[ "$(grep -o 'Play a card by number' "$work/game.txt" | wc -l)" -le $((rounds * 4)) ] || { echo "The game did not end before the input ran out." >&2; exit 1; }

echo "== The quickstart works as written."
