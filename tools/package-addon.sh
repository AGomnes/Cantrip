#!/usr/bin/env bash
# Packages the Godot addon as a zip whose root is addons/cantrip, which is the shape the
# Asset Library and every "unzip into your project" instruction expect.
#
#   tools/package-addon.sh [output-directory] [ref]
#
# The version comes from plugin.cfg, so the zip and the plugin can never disagree about it.
# Given a ref, a tag such as v0.1.0-preview.4, the addon README's links into the main branch of
# github.com/AGomnes/Cantrip point at that ref instead, so a user reads the docs of the version they
# installed. Without one, the README goes in as it is.

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
addon="$root/godot/Cantrip.Demo/addons/cantrip"
out="${1:-$root/artifacts}"
ref="${2:-}"

[ -d "$addon" ] || { echo "The addon is not where it should be: $addon" >&2; exit 1; }

if [ -n "$ref" ] && ! [[ "$ref" =~ ^[A-Za-z0-9][A-Za-z0-9._/-]*$ ]]; then
    echo "'$ref' is not a tag or branch name this script accepts." >&2
    exit 1
fi

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

if [ -n "$ref" ]; then
    readme="$staging/addons/cantrip/README.md"
    sed -E "s#(https://github\.com/AGomnes/Cantrip/(blob|tree))/main/#\1/$ref/#g" "$readme" > "$readme.new"
    mv "$readme.new" "$readme"
    if grep -q -E "AGomnes/Cantrip/(blob|tree)/main/" "$readme"; then
        echo "The addon README still links the main branch after pointing its links at $ref." >&2
        exit 1
    fi
fi

# .uid files are Godot's stable script ids: keeping them means a project that updates the addon
# does not lose the references its scenes already hold.
mkdir -p "$out"
out="$(cd "$out" && pwd)"   # zip runs from the staging folder, so a relative path would land there
zip="$out/cantrip-godot-$version.zip"
rm -f "$zip"
(cd "$staging" && zip -qr "$zip" addons)

echo "$zip"
unzip -l "$zip" | tail -n 3
