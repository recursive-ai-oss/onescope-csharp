#!/usr/bin/env bash
# Checks the release workflow runs on a version tag, so they can also be run by hand.
#
#   tools/release-checks.sh tag-form  <tag>      the tag looks like v1.2.3 or v1.2.3-rc.1
#   tools/release-checks.sh changelog <tag>      CHANGELOG.md has a "## [<version>]" heading
#   tools/release-checks.sh version   <tag>      the tag says the same as the version the packages build as
#   tools/release-checks.sh notes     <tag>      prints the changelog section of the version (fails when empty)
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

cmd="${1:-}"; tag="${2:-}"
[[ -n "$cmd" && -n "$tag" ]] || { echo "usage: $0 tag-form|changelog|version|notes <tag>" >&2; exit 2; }
version="${tag#v}"

case "$cmd" in
  tag-form)
    [[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] \
      || { echo "::error::$tag is not a version tag such as v1.2.3 or v1.2.3-rc.1"; exit 1; }
    ;;
  changelog)
    awk -v heading="## [$version]" 'index($0, heading) == 1 { found = 1 } END { exit !found }' CHANGELOG.md \
      || { echo "::error::CHANGELOG.md has no \"## [$version]\" section"; exit 1; }
    ;;
  version)
    # All four packages share one version from Directory.Build.props; the tag has to say the same.
    for project in src/OneScope.Sdk src/OneScope.Probe src/OneScope.Attach src/OneScope.App; do
      built="$(dotnet msbuild "$project" -getProperty:PackageVersion | tr -d '[:space:]')"
      if [[ "v$built" != "$tag" ]]; then
        echo "::error::tag $tag does not match the package version $built of $project"
        exit 1
      fi
    done
    ;;
  notes)
    # From the version's heading up to the next version heading or the link definitions at the end of the file.
    out="$(awk -v heading="## [$version]" '
      index($0, heading) == 1 { on = 1; next }
      on && (index($0, "## [") == 1 || $0 ~ /^\[[^]]+\]: /) { exit }
      on { print }
    ' CHANGELOG.md)"
    if [[ -z "${out//[[:space:]]/}" ]]; then echo "::error::the changelog section for $version is empty"; exit 1; fi
    printf '%s\n' "$out"
    ;;
  *) echo "unknown check: $cmd" >&2; exit 2 ;;
esac
