#!/usr/bin/env bash
# Regenerates docs/api/ from the XML documentation comments in the sources.
#
#   tools/api-docs.sh              write docs/api/
#   tools/api-docs.sh --check      fail if what is on disk is not what the sources say
#
# The reference is generated because the surface it describes is frozen for the whole 1.x line and
# a hand-written reference for a frozen surface drifts the first time anybody touches a signature.
# CI runs --check, so it cannot.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

dotnet build tools/Cantrip.ApiDoc/Cantrip.ApiDoc.csproj -c Release --nologo -v quiet
dotnet tools/Cantrip.ApiDoc/bin/Release/net9.0/cantrip-apidoc.dll --root "$root" "$@"
