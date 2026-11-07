# Building and releasing

## Layout

- `Claudalisk.Dotnet.slnx` lists every project. A test fails when a project under `src/` or `tests/` is missing from it, because CI tests only what the solution lists.
- Each package lives in `src/<Package>/`, and its tests in `tests/<Package>.Tests/`.
- `Directory.Build.props` targets .NET 10 with nullable enabled and warnings as errors. `Directory.Packages.props` holds every package version, and each project commits its `packages.lock.json`.
- `tests/Claudalisk.Ci.Tests` checks the repository rules and proves that CI can run Testcontainers tests against real PostgreSQL.

## Local build

Docker must be running: the PostgreSQL tests fail without it, and they never skip.

```bash
dotnet test --solution Claudalisk.Dotnet.slnx
```

## Packable packages

Every project is not packable by default. A package sets `<IsPackable>true</IsPackable>` in its project file in the pull request that gives it content. A test fails when a packable project under `src/` has no source files, so a release never claims an empty package ID on nuget.org.

## Releasing

1. Make sure CI is green on `main`.
2. Tag the commit on `main` with `v<major>.<minor>.<patch>`, optionally with a prerelease suffix such as `v0.1.0-alpha.1`, and push the tag.
3. The `publish` workflow runs CI at the tag, packs every packable package at that version, and pushes it to nuget.org.

The workflow refuses a tag that is not a valid version, and it fails when no package is packable. It signs in to nuget.org with Trusted Publishing: `NuGet/login` exchanges the workflow's GitHub OIDC token for a short-lived API key, so the repository stores no key. The nuget.org user name comes from the `NUGET_USER` repository variable.

Apps pin exact versions of these packages.
