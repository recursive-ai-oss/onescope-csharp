# Building and releasing

## What a build needs

Everything the app, samples and tests reference comes from nuget.org: onegame's packages, the libraries the bundled modules sit on, and the modules themselves.
`tools/check-packages.sh` reads the pinned versions (`Directory.Packages.props` and the tests' `VersionOverride` attributes) for `RecursiveAI.OnePhysics`,
`RecursiveAI.OnePhysics.Scope`, `RecursiveAI.OnePath` and `RecursiveAI.OnePath.Scope` and asks nuget.org whether each exists. When one is missing it fails with a single
message naming it, so a missing package is not mistaken for a restore problem. Both workflows run it first.

To build before those packages are published, put them in a folder and point `ONESCOPE_FEEDS` at it (several folders are separated by `;`):

```
export ONESCOPE_FEEDS=/path/to/feed
dotnet build OneScope.slnx
```

The SDK, probe and attach projects have no package references at all.

## Package validation

When `ONESCOPE_FEEDS` is set, packing the SDK, probe and attach packages is checked against the last 0.x preview (`0.3.0-preview.1`), so a binary break for a module built on it is
listed. The breaks intended for 1.0 are recorded in `src/OneScope.Sdk/CompatibilitySuppressions.xml`; any new one fails the pack. Once 1.0 is published, point
`PackageValidationBaselineVersion` at it and delete the suppressions file.

## Continuous integration

`.github/workflows/ci.yml` builds and runs the three test projects on Linux, macOS and Windows. Golden images come from the lavapipe software renderer: the Linux job installs
it and compares pixels exactly; macOS and Windows draw on the machine's renderer, which differs in the last bits, so there the tests check that three runs of each capture are
byte-identical and skip the comparison with the goldens.

## Releasing

`.github/workflows/release.yml` runs when a tag `v<version>` is pushed, where the version is `VersionPrefix` and `VersionSuffix` from `Directory.Build.props` (for example `v1.0.0-rc.1`). It
checks the tag against the props, builds, tests, packs `RecursiveAI.OneScope.Sdk`, `.Probe`, `.Attach` and `.Tool`, and pushes them with a short-lived key from nuget.org's trusted publishing
(`NuGet/login`); no API key is stored. One-time setup, done by an owner of the nuget.org account:

1. On nuget.org, add a trusted publishing policy for this repository and the workflow file `release.yml` (and the `release` environment).
2. In the repository, create an environment named `release` and a variable `NUGET_USER` holding the nuget.org user name.
