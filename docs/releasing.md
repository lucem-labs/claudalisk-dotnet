# Building and releasing

## Layout

- `Claudalisk.Dotnet.slnx` lists every project. A test fails when a project under `src/` or `tests/` is missing from it, because CI tests only what the solution lists.
- Each package lives in `src/<Package>/`. Its tests are in `tests/<Package>.Tests/`, or in the tests of the package that implements or uses it: the two abstractions packages and the PostgreSQL jobs transport have no test project of their own.
- `Directory.Build.props` targets `net10.0` and `net11.0` with nullable enabled and warnings as errors. `Directory.Packages.props` holds every package version, and each project commits its `packages.lock.json`.
- `tests/Claudalisk.Ci.Tests` checks the repository rules. The Persistence, Dispatching, Jobs and Scheduling tests run against real PostgreSQL in Testcontainers.

## Local build

Docker must be running: the PostgreSQL tests fail without it, and they never skip.

```bash
dotnet test --solution Claudalisk.Dotnet.slnx
```

## Packable packages

No project is packable by default. A package sets `<IsPackable>true</IsPackable>` in its project file in the pull request that gives it content. `Directory.Build.targets` fails `dotnet pack` with `CLAUDALISK0001` when a packable project has no source files. CI packs on every push, so a release never claims an empty package ID on nuget.org.

## Compatibility with the last release

CI and the `publish` workflow compare every package with the last release, so a breaking change is found before it ships.

- `scripts/baseline-version.mjs` finds the last release: the highest `v*` tag by Semantic Versioning precedence that has packages on nuget.org. In a release it looks only at tags lower than the one being released. It asks nuget.org which package IDs that release published and writes `CLAUDALISK_BASELINE_VERSION` and `CLAUDALISK_BASELINE_PACKAGES` to the job's environment. Nothing is edited by hand after a release.
- `Directory.Build.targets` sets `PackageValidationBaselineVersion` from those two variables for each packable project in the list. Restore downloads the released package, and `dotnet pack` fails with a `CP` error, such as `CP0001` for a removed type, when the new package breaks it. The download does not change `packages.lock.json`, so restore stays in locked mode.
- A package that is new since the last release is not in the list and has no baseline. Before the first release there is no baseline at all.
- Strict mode is off, so adding a public member is not an error.

To run the same check locally, in bash:

```bash
export $(node scripts/baseline-version.mjs)
dotnet pack Claudalisk.Dotnet.slnx --configuration Release --output artifacts
```

Without the two variables, a local pack compares nothing with the last release.

### An intended break

Record the break in the pull request that makes it. With the two variables set, run:

```bash
dotnet pack src/<Package> --configuration Release --output artifacts -p:ApiCompatGenerateSuppressionFile=true
```

This writes `src/<Package>/CompatibilitySuppressions.xml`. Delete the comment line at the top, because the no-comments check refuses it, and commit the file. Each entry with `IsBaselineSuppression` names one break against the last release. Do not suppress a break in any other way, such as `NoWarn`: the release check below reads only these files.

An entry stays valid after the release that ships the break. It can be removed once that release is the baseline.

## Releasing

The packages share one version, which follows Semantic Versioning ([CLA-0098](https://github.com/lucem-labs/claudalisk/blob/main/adr/0098-artifacts-follow-semantic-versioning.md)).

1. Make sure CI is green on `main`.
2. Choose the version from what changed since the last release. It must be higher than every earlier release. A `CompatibilitySuppressions.xml` entry added since the last release is a breaking change.
   - While the major version is 0, a breaking change bumps the minor version, and everything else bumps the patch.
   - A prerelease is `<version>-alpha.<n>`, `-beta.<n>` or `-rc.<n>`, such as `0.1.0-alpha.5`. It may break the one before it.
   - A change is breaking when an app must change to keep working: a removed or renamed public or protected member, a changed signature, a configuration key or a database object the package owns.
3. Tag the commit on `main` with `v<version>` and push the tag.
4. The `publish` workflow runs CI at the tag, checks the version, packs every packable package at that version against the last release, and pushes it to nuget.org.

A published version never changes. Never move or reuse a tag. A bad release is fixed by a new version, and the bad one is unlisted on nuget.org.

The workflow refuses a tag that is not a valid version, or that carries build metadata, or whose commit is not on `main`, and it fails when no package is packable.

`scripts/require-release-bump.mjs` refuses two more tags:

- A tag that is not higher than every other `v*` tag, by Semantic Versioning precedence.
- A tag that is too small a step for a break. When a baseline suppression was added since the last release, the version must be a breaking step from the last stable release: a new minor version while the major version is 0, and a new major version from 1.0.0 on. A prerelease of such a version passes, and so does any version before the first stable release.

The second check sees only breaks that package validation finds and that are recorded as suppressions. A changed configuration key, a changed database object or a change in behavior is still for the person who chooses the version to notice.

A refused tag is not moved or reused. Tag the next version instead.

The publish job runs in the `nuget` environment, where the owner can add protection rules. It signs in to nuget.org with Trusted Publishing: `NuGet/login` exchanges the workflow's GitHub OIDC token for a short-lived API key, so the repository stores no key. The nuget.org user name comes from the `NUGET_USER` repository variable.

Apps pin exact versions of these packages.
