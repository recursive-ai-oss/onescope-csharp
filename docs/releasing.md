# Building and releasing

## What a build needs

Everything the app, samples and tests reference comes from nuget.org: onegame's packages, the libraries the bundled modules sit on, and the modules themselves.
`tools/check-packages.sh` reads the pinned versions (`Directory.Packages.props` and the tests' `VersionOverride` attributes) for `RecursiveAI.OnePhysics`,
`RecursiveAI.OnePhysics.Scope`, `RecursiveAI.OnePath` and `RecursiveAI.OnePath.Scope` and asks nuget.org whether each exists. When one is missing it fails with a single
message naming it, so a missing package is not mistaken for a restore problem. The `app` jobs of the CI workflow and the tool release run it first. The three libraries (SDK, probe and attach) and their tests need none of these packages.

To build against unpublished builds of those packages, put them in a folder and point `ONESCOPE_FEEDS` at it (several folders are separated by `;`):

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

Packing the SDK, probe and attach packages is checked against the last published release (`PackageValidationBaselineVersion`, restored from nuget.org), so a binary break for a module built on it fails the pack. After each release, point the baseline at the version just published.

## Continuous integration

`.github/workflows/ci.yml` has two groups of jobs, each on Linux, macOS and Windows:

- **`libraries`**: restores, builds and tests only the SDK, probe and attach test projects, then packs the three libraries. They need nothing but the .NET SDK and nuget.org's test packages, so
  these jobs go green on a clean runner before any module package exists.
- **`app`**: `tools/check-packages.sh` (strict), then the whole solution, the app's tests and, on Linux, the tool pack. It can only pass once the physics and navigation modules are on nuget.org.

Golden images come from the lavapipe software renderer: the Linux `app` job installs it and compares pixels exactly (within two steps per channel on a different llvmpipe build); macOS and Windows draw on the machine's renderer,
which differs in the last bits, so there the tests check that three runs of each capture are byte-identical and skip the comparison with the goldens.
The hosted Windows runner has the Vulkan loader but no driver, so onegame draws the shell on its CPU path (`CaptureResult.Renderer` is `cpu`), which has no scene views. The tests that look at scene pixels
(`Harness.RequireGraphicsDevice`) skip there, and only there: on Linux and macOS a missing device fails them. Everything else, the live attach, hashes and operations included, runs on Windows.

## Releasing

The packages are released in an order, because each step builds on the one before it:

| Step | What | How | Needs |
|---|---|---|---|
| 1 | `RecursiveAI.OneScope.Sdk`, `.Probe`, `.Attach` | tag `v<version>` | the `libraries` jobs of `ci.yml` green on the commit |
| 2 | `RecursiveAI.OnePath` | the onepath repository | step 1 is on nuget.org |
| 3 | `RecursiveAI.OnePath.Scope` and `RecursiveAI.OnePhysics.Scope` | their own repositories, each built on the SDK from step 1 | steps 1 and 2 are on nuget.org |
| 4 | `RecursiveAI.OneScope.Tool` | tag `tool-v<version>` | step 3 is on nuget.org, and the whole `ci.yml` run green on the commit (`app` jobs and `check-packages.sh` included) |

Because the app's pins must name published packages, the commit that gets the `tool-v<version>` tag is later than the one that got `v<version>`: pin the final module versions, wait for CI, then tag.

`CHANGELOG.md` keeps an `Unreleased` section. A library release adds `## [<version>] - <date>` above it, a tool release `## [tool <version>] - <date>`; the section becomes that GitHub release's notes.
`tools/release-checks.sh` holds the checks the workflow makes on a tag, so they can be run by hand: `kind` (`libraries` or `tool`), `tag-form` (`v1.2.3` or `tool-v1.2.3`, with an optional suffix such as `-rc.1`),
`changelog` (the section for the tag), `version` (the tag equals the version of the packages it publishes), `notes` (prints the section) and `ci <tag> <sha>` (the CI requirement above, through the `gh` command).

`.github/workflows/release.yml` runs on either tag form, in three jobs:

1. **verify**: the tag is well formed, its commit is on `main`, CI is green as the table says (for a library tag: the three `libraries` jobs, by job, even while the app jobs are red; for a tool tag: the whole run), and the changelog has the version.
2. **publish** (environment `nuget`): the version check, then (tool only) `check-packages.sh`, build and test of what the tag publishes, pack with `.snupkg` symbols, a provenance attestation, and a push to nuget.org
   with a short-lived key from trusted publishing (`NuGet/login`, user `recursiveai.org`); no API key is stored. Pushes use `--skip-duplicate`.
3. **release**: a GitHub release named after the tag, with the changelog section as notes and the packages attached; a pre-release when the version has a suffix.

Every action is pinned by commit, with the version in a comment, because the publish job can obtain a publishing key.

One-time setup, done by an owner of the nuget.org account: a trusted publishing policy for this repository, the workflow file `release.yml` and the environment `nuget`; and a repository environment named `nuget`.
