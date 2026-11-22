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

## Releasing

The packages share one version, which follows Semantic Versioning ([CLA-0098](https://github.com/lucem-labs/claudalisk/blob/main/adr/0098-artifacts-follow-semantic-versioning.md)).

1. Make sure CI is green on `main`.
2. Choose the version from what changed since the last release. It must be higher than every earlier release.
   - While the major version is 0, a breaking change bumps the minor version, and everything else bumps the patch.
   - A prerelease is `<version>-alpha.<n>`, `-beta.<n>` or `-rc.<n>`, such as `0.1.0-alpha.5`. It may break the one before it.
   - A change is breaking when an app must change to keep working: a removed or renamed public or protected member, a changed signature, a configuration key or a database object the package owns.
3. Tag the commit on `main` with `v<version>` and push the tag.
4. The `publish` workflow runs CI at the tag, packs every packable package at that version, and pushes it to nuget.org.

A published version never changes. Never move or reuse a tag. A bad release is fixed by a new version, and the bad one is unlisted on nuget.org.

The workflow refuses a tag that is not a valid version, or that carries build metadata, or whose commit is not on `main`, and it fails when no package is packable. The publish job runs in the `nuget` environment, where the owner can add protection rules. It signs in to nuget.org with Trusted Publishing: `NuGet/login` exchanges the workflow's GitHub OIDC token for a short-lived API key, so the repository stores no key. The nuget.org user name comes from the `NUGET_USER` repository variable.

Apps pin exact versions of these packages.
