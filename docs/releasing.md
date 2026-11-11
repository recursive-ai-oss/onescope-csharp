# Building and releasing

## What a build needs

Everything the app, samples and tests reference comes from nuget.org: onegame's packages, the libraries the bundled modules sit on, and the modules themselves.
`tools/check-packages.sh` reads the pinned versions (`Directory.Packages.props` and the tests' `VersionOverride` attributes) for `RecursiveAI.OnePhysics`,
`RecursiveAI.OnePhysics.Scope`, `RecursiveAI.OnePath` and `RecursiveAI.OnePath.Scope` and asks nuget.org whether each exists. When one is missing it fails with a single
message naming it, so a missing package is not mistaken for a restore problem. The CI workflow runs it first; the release workflow uses it to decide whether the tool can be built.

To build before those packages are published, put them in a folder and point `ONESCOPE_FEEDS` at it (several folders are separated by `;`):

```
export ONESCOPE_FEEDS=/path/to/feed
dotnet build OneScope.slnx
```

The SDK, probe and attach projects have no package references at all.

## The A/B tests

Playing one replay through two builds of a module in separate load contexts is tested with builds that need nothing but nuget.org's final packages. The test module is built twice with one rule changed
(`tests/OneScope.TestModule.RuleB`): the two builds agree up to the step where the rule differs, the compare operation names that step, and the diff names the first differing entity and field.
The physics module is loaded twice from two folders, each its own context, and both reproduce the library's recorded hash at every step. Adding a physics build that differs from the current one means
another published version of the module, and only the final release of each is on nuget.org.

## Package validation

When `ONESCOPE_FEEDS` is set, packing the SDK, probe and attach packages is checked against the last 0.x preview (`0.3.0-preview.1`), so a binary break for a module built on it is
listed. The breaks intended for 1.0 are recorded in `src/OneScope.Sdk/CompatibilitySuppressions.xml`; any new one fails the pack. Once 1.0 is published, point
`PackageValidationBaselineVersion` at it and delete the suppressions file.

## Continuous integration

`.github/workflows/ci.yml` builds and runs the three test projects on Linux, macOS and Windows. Golden images come from the lavapipe software renderer: the Linux job installs
it and compares pixels exactly; macOS and Windows draw on the machine's renderer, which differs in the last bits, so there the tests check that three runs of each capture are
byte-identical and skip the comparison with the goldens.

## Releasing

`CHANGELOG.md` keeps an `Unreleased` section; a release adds a `## [<version>] - <date>` section above it, and that section becomes the GitHub release's notes.
`tools/release-checks.sh` holds the checks the workflow makes on a tag, so they can be run by hand: `tag-form` (`v1.2.3` or `v1.2.3-rc.1`), `changelog` (a section for the version),
`version` (the tag equals the version the four projects build as) and `notes` (prints the section).

`.github/workflows/release.yml` runs when a tag `v<version>` is pushed, where the version is `VersionPrefix` and `VersionSuffix` from `Directory.Build.props` (for example `v1.0.0-rc.1`), in three jobs:

1. **verify**: the tag is well formed, its commit is on `main`, `ci.yml` passed on that commit, and the changelog has the version.
2. **publish** (environment `nuget`): the version check, then build and test, then pack `RecursiveAI.OneScope.Sdk`, `.Probe`, `.Attach` and `.Tool` with `.snupkg` symbols, a provenance attestation, and a push to nuget.org
   with a short-lived key from trusted publishing (`NuGet/login`, user `recursiveai.org`); no API key is stored. Pushes use `--skip-duplicate`.
3. **release**: a GitHub release with the changelog section as notes and the packages attached; a pre-release when the version has a suffix.

Every action is pinned by commit, with the version in a comment, because the publish job can obtain a publishing key.

The tool carries the physics and navigation modules, so it can only be built once those are on nuget.org. The publish job asks `tools/check-packages.sh`; when a module is missing it publishes the three libraries only
(the modules are built on them) and warns. Once the modules are published, re-run the workflow for the same tag: the libraries are skipped as duplicates, the app is tested, and the tool is pushed and added to the release.

One-time setup, done by an owner of the nuget.org account: a trusted publishing policy for this repository, the workflow file `release.yml` and the environment `nuget`; and a repository environment named `nuget`.
