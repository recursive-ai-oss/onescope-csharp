#!/usr/bin/env bash
# Fails with one clear message when a library package the build needs is not on nuget.org yet.
# The bundled modules (RecursiveAI.OnePhysics.Scope, RecursiveAI.OnePath.Scope) and the libraries under them
# (RecursiveAI.OnePhysics, RecursiveAI.OnePath) are pinned in Directory.Packages.props and in the tests' VersionOverride
# attributes. Without this check a missing one shows up as a restore error that does not say what to do.
# Run from the repository root: bash tools/check-packages.sh [--warn]
# With --warn a missing package is a warning and the exit status is still 1, for a workflow step that only decides what to do next.
set -euo pipefail

# One "Id Version" line per pin, from the central pins and from any project that overrides one.
pins=$(
  { grep -h 'RecursiveAI\.One\(Physics\|Path\)' Directory.Packages.props; \
    find src tests samples tools -name '*.csproj' -exec grep -h 'RecursiveAI\.One\(Physics\|Path\)' {} + || true; } \
  | sed -n 's/.*Include="\(RecursiveAI\.One\(Physics\|Path\)[A-Za-z.]*\)".*Version\(Override\)\{0,1\}="\([^"]*\)".*/\1 \4/p' \
  | sort -u
)

missing=()
count=0
while read -r id version; do
  [ -n "$id" ] || continue
  count=$((count + 1))
  lower=$(printf '%s' "$id" | tr '[:upper:]' '[:lower:]')
  want=$(printf '%s' "$version" | tr '[:upper:]' '[:lower:]')
  code=$(curl -sS -o index.json.tmp -w '%{http_code}' "https://api.nuget.org/v3-flatcontainer/$lower/index.json" || echo 000)
  if [ "$code" = 404 ]; then
    missing+=("$id $version")
  elif [ "$code" != 200 ]; then
    echo "Could not ask nuget.org about $id (HTTP $code)." >&2
    rm -f index.json.tmp
    exit 2
  elif ! grep -q "\"$want\"" index.json.tmp; then
    missing+=("$id $version")
  fi
  rm -f index.json.tmp
done <<< "$pins"

if [ "${#missing[@]}" -gt 0 ]; then
  list=$(IFS=,; echo "${missing[*]}")
  level=error
  [ "${1:-}" = "--warn" ] && level=warning
  echo "::$level title=Packages missing from nuget.org::The build needs these packages and nuget.org does not have them yet: ${list//,/, }. Publish them first (or build locally with ONESCOPE_FEEDS pointing at a folder that holds them). This check exists so a missing package is not reported as a confusing restore error."
  exit 1
fi

echo "All $count pinned library packages are on nuget.org."
