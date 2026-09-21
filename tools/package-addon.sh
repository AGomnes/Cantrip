#!/usr/bin/env bash
# Packages the Godot addon as a zip whose root is addons/cantrip, which is the shape the
# Asset Library and every "unzip into your project" instruction expect.
#
#   tools/package-addon.sh [output-directory]
#
# The version comes from plugin.cfg, so the zip and the plugin can never disagree about it.

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
addon="$root/godot/Cantrip.Demo/addons/cantrip"
out="${1:-$root/artifacts}"

[ -d "$addon" ] || { echo "The addon is not where it should be: $addon" >&2; exit 1; }

command -v zip >/dev/null || {
    echo "This needs the 'zip' command, which Git Bash on Windows does not ship." >&2
    echo "On Windows run tools/package-addon.ps1 instead." >&2
    exit 1
}

version="$(sed -n 's/^version="\(.*\)"$/\1/p' "$addon/plugin.cfg")"
[ -n "$version" ] || { echo "plugin.cfg has no version." >&2; exit 1; }

staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

mkdir -p "$staging/addons"
cp -r "$addon" "$staging/addons/cantrip"
cp "$root/LICENSE" "$staging/addons/cantrip/LICENSE"

# .uid files are Godot's stable script ids: keeping them means a project that updates the addon
# does not lose the references its scenes already hold.
mkdir -p "$out"
zip="$out/cantrip-godot-$version.zip"
rm -f "$zip"
(cd "$staging" && zip -qr "$zip" addons)

echo "$zip"
unzip -l "$zip" | tail -n 3
