#!/usr/bin/env bash
# Checks the release workflow runs on a version tag, so they can also be run by hand.
#
# There are two tag forms, because the packages are released in an order (docs/releasing.md):
#   v1.2.3 or v1.2.3-rc.1         the libraries: RecursiveAI.OneScope.Sdk, .Probe and .Attach
#   tool-v1.2.3 or tool-v1.2.3-rc.1   the tool: RecursiveAI.OneScope.Tool
#
#   tools/release-checks.sh kind      <tag>        prints "libraries" or "tool"
#   tools/release-checks.sh tag-form  <tag>        the tag has one of the two forms
#   tools/release-checks.sh changelog <tag>        CHANGELOG.md has a section for it: "## [1.2.3]" or "## [tool 1.2.3]"
#   tools/release-checks.sh version   <tag>        the tag says the same as the version the packages it publishes build as
#   tools/release-checks.sh notes     <tag>        prints the changelog section (fails when empty)
#   tools/release-checks.sh ci        <tag> <sha>  CI passed on the commit: the libraries jobs for a library tag, the whole run for a tool tag
#                                                  (needs the gh command and GH_TOKEN; GITHUB_REPOSITORY names the repository)
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

cmd="${1:-}"; tag="${2:-}"
[[ -n "$cmd" && -n "$tag" ]] || { echo "usage: $0 kind|tag-form|changelog|version|notes|ci <tag> [sha]" >&2; exit 2; }

form_error() { echo "::error::$tag is not a release tag: use v1.2.3 (libraries) or tool-v1.2.3 (tool), with an optional suffix such as -rc.1"; exit 1; }

if [[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  kind=libraries; version="${tag#v}"; heading="## [$version]"
elif [[ "$tag" =~ ^tool-v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  kind=tool; version="${tag#tool-v}"; heading="## [tool $version]"
else
  form_error
fi

case "$cmd" in
  kind) echo "$kind" ;;
  tag-form) ;;
  changelog)
    awk -v heading="$heading" 'index($0, heading) == 1 { found = 1 } END { exit !found }' CHANGELOG.md \
      || { echo "::error::CHANGELOG.md has no \"$heading\" section"; exit 1; }
    ;;
  version)
    # The libraries share one version from Directory.Build.props and so does the tool; the tag has to say the same as what it publishes.
    if [[ "$kind" == libraries ]]; then projects="src/OneScope.Sdk src/OneScope.Probe src/OneScope.Attach"; else projects="src/OneScope.App"; fi
    for project in $projects; do
      built="$(dotnet msbuild "$project" -getProperty:PackageVersion | tr -d '[:space:]')"
      if [[ "$built" != "$version" ]]; then
        echo "::error::tag $tag does not match the package version $built of $project"
        exit 1
      fi
    done
    ;;
  notes)
    # From the section's heading up to the next version heading or the link definitions at the end of the file.
    out="$(awk -v heading="$heading" '
      index($0, heading) == 1 { on = 1; next }
      on && (index($0, "## [") == 1 || $0 ~ /^\[[^]]+\]: /) { exit }
      on { print }
    ' CHANGELOG.md)"
    if [[ -z "${out//[[:space:]]/}" ]]; then echo "::error::the changelog section \"$heading\" is empty"; exit 1; fi
    printf '%s\n' "$out"
    ;;
  ci)
    sha="${3:-}"
    [[ -n "$sha" ]] || { echo "usage: $0 ci <tag> <sha>" >&2; exit 2; }
    repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is not set}"
    if [[ "$kind" == tool ]]; then
      # The tool needs the whole run green: the libraries and the app, on every system.
      passed=$(gh api "repos/$repo/actions/workflows/ci.yml/runs?head_sha=$sha&per_page=100" \
        --jq '[.workflow_runs[] | select(.conclusion == "success")] | length')
      [[ "$passed" -ge 1 ]] || { echo "::error::no fully green ci run for $sha; the tool needs every job, app included. Wait for CI on this commit and push the tag again"; exit 1; }
    else
      # The libraries need their own jobs green, on every system, in one run. The app jobs may still be red or running.
      run_ids=$(gh api "repos/$repo/actions/workflows/ci.yml/runs?head_sha=$sha&per_page=100" --jq '.workflow_runs[].id')
      for run in $run_ids; do
        ok=$(gh api "repos/$repo/actions/runs/$run/jobs?per_page=100" \
          --jq '[.jobs[] | select((.name | startswith("libraries (")) and .conclusion == "success")] | length')
        if [[ "$ok" -ge 3 ]]; then echo "libraries jobs passed in run $run"; exit 0; fi
      done
      echo "::error::no ci run for $sha has all three libraries jobs (ubuntu, macos, windows) green. Wait for CI on this commit and push the tag again"
      exit 1
    fi
    ;;
  *) echo "unknown check: $cmd" >&2; exit 2 ;;
esac
