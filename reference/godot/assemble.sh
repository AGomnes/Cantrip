#!/usr/bin/env bash
# Puts the Godot project together from the one copy of everything it needs.
#
# Godot finds scripts by path inside the project, so `addons/cantrip` has to be a real folder in
# the project: it cannot be referenced, linked or packaged. A game outside this repository gets
# it by unzipping a release into the project, which is the same copy by another name. Neither
# the addon nor the content is in the repository twice, so this script is how they get here.
#
#   bash reference/godot/assemble.sh
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"

rm -rf "$here/addons" "$here/content"
mkdir -p "$here/addons"
cp -r "$root/godot/Cantrip.Demo/addons/cantrip" "$here/addons/cantrip"
cp -r "$root/reference/content" "$here/content"
cp "$root/LICENSE" "$here/addons/cantrip/LICENSE"
echo "Assembled addons/cantrip and content/ into $here"
