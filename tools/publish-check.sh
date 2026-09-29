#!/usr/bin/env bash
# Publishes the reference game the two ways a shipped game is published, and runs what it built.
#
#   tools/publish-check.sh [<runtime identifier>]     default: this machine's
#
# docs/stability.md says Cantrip is usable from a published game. Nothing proved it until this
# script existed: the engine is a library, and every test in the repository ran it from a normal
# build with the whole framework present and a JIT underneath. Trimming and native AOT are where a
# rules engine usually dies -- reflection, `System.Text.Json` and dynamic dispatch are the usual
# casualties, and this one has all three within reach -- so the answer is checked rather than
# assumed, and checked by playing eight whole runs with saves and restores rather than by starting
# a process and watching it not crash.
#
# Trim and AOT analysis warnings are errors here. `reference/host` is the only application in this
# repository that publishes either way, so a call that cannot survive trimming has nowhere else to
# show up.
#
# Native AOT needs a platform linker: the C++ workload on Windows, `clang` and `zlib1g-dev` on
# Linux. Without one the AOT half is skipped, loudly, and the trimmed half still runs.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

rid="${1:-}"
if [ -z "$rid" ]; then
  case "$(uname -s)" in
    Linux*)  rid=linux-x64 ;;
    Darwin*) rid=osx-x64 ;;
    *)       rid=win-x64 ;;
  esac
fi

out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

host=reference/host/Cantrip.Reference.csproj
exe=cantrip-reference
case "$rid" in win-*) exe=cantrip-reference.exe ;; esac

publish() {
  local mode="$1" dir="$2"
  echo "== publishing the reference game: $mode, $rid"
  dotnet publish "$host" -c Release -r "$rid" \
    -p:CantripPublish="$mode" \
    -p:TreatWarningsAsErrors=true \
    -p:ILLinkTreatWarningsAsErrors=true \
    -p:IlcTreatWarningsAsErrors=true \
    -o "$dir"
}

publish trimmed "$out/trimmed"
echo "== running the trimmed build"
"$out/trimmed/$exe" --check
echo "trimmed: $(du -sh "$out/trimmed" | cut -f1)"

# The linker native AOT needs. `dotnet publish` reports a missing one as a build error after it has
# already compiled everything, which reads like a failure of the code rather than of the machine.
linker=no
case "$rid" in
  win-*)
    # The same question the SDK asks: is there a Visual Studio with the C++ tools in it. `link.exe`
    # on PATH is not the question -- Git for Windows ships one of its own that is a different
    # program entirely.
    vswhere="/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
    [ -x "$vswhere" ] \
      && [ -n "$("$vswhere" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2> /dev/null)" ] \
      && linker=yes
    ;;
  *)
    command -v clang > /dev/null 2>&1 && linker=yes
    ;;
esac

if [ "$linker" != yes ]; then
  echo
  echo "SKIPPED: native AOT, because this machine has no platform linker for $rid."
  echo "  Linux:   apt-get install clang zlib1g-dev"
  echo "  Windows: the Desktop Development for C++ workload in Visual Studio"
  echo "The trimmed half above covers the same trim analysis; only the native link is missing."
  exit 0
fi

publish aot "$out/aot"
echo "== running the native binary"
"$out/aot/$exe" --check
echo "aot: $(du -h "$out/aot/$exe" | cut -f1) for one file, no runtime beside it"
