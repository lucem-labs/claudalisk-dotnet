<p align="center">
  <img src="docs/assets/logo-wide.png" alt="Claudalisk" width="720">
</p>

# Claudalisk .NET

The .NET packages behind [Claudalisk](https://github.com/lucem-labs/claudalisk) apps: domain errors, a dispatcher for commands and queries, persistence, background jobs and a scheduler.

Claudalisk is an opinionated Claude Code plugin for building .NET + React apps. Its standards live as architecture decision records in the [Claudalisk repository](https://github.com/lucem-labs/claudalisk/tree/main/adr). This repository holds the infrastructure code those standards need, so that every app uses one tested implementation instead of its own copy.

> **Status: early.** Alpha versions are on [nuget.org](https://www.nuget.org/packages/Claudalisk.Domain), and the API can still change from one alpha to the next. See [Versions](#versions).

## Packages

| Package | What it holds | Standard |
|---|---|---|
| [`Claudalisk.Domain`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Domain/README.md) | `AggregateRoot` with its version, domain events, `DomainException`, `DependencyUnavailableException` and `StaleVersionException` | CLA-0063 |
| [`Claudalisk.Dispatching.Abstractions`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Dispatching.Abstractions/README.md) | The dispatcher, handler and pipeline contracts and `MessageValidationException`, with no dependencies; the only dispatching package an Interfaces project may reference | CLA-0003 |
| [`Claudalisk.Dispatching`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Dispatching/README.md) | The dispatcher and its pipeline: logging, validation, retry on concurrency conflicts, explicit step order and the unit of work | CLA-0049 |
| [`Claudalisk.Persistence.Abstractions`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Persistence.Abstractions/README.md) | `IUnitOfWork` and `ConcurrencyConflictException`, without EF Core types | CLA-0007, CLA-0056 |
| [`Claudalisk.Persistence`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Persistence/README.md) | EF Core on PostgreSQL for module contexts: the scope's connection and transaction, the unit of work, and the migration runner with its baselines | CLA-0007, CLA-0008, CLA-0010 |
| [`Claudalisk.AspNetCore`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.AspNetCore/README.md) | The Problem Details handler, conditional writes, HAL members, the health endpoint and module tags in OpenAPI | CLA-0045, CLA-0046, CLA-0063 |
| [`Claudalisk.Jobs`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Jobs/README.md) | Background jobs, batch jobs, event jobs, rebuilds and the jobs router | CLA-0058, CLA-0059, CLA-0060 |
| [`Claudalisk.Jobs.PostgreSql`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Jobs.PostgreSql/README.md) | The PostgreSQL transport for jobs | CLA-0058 |
| [`Claudalisk.Modules`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Modules/README.md) | `IModule`, `AddClaudaliskModules` and the module catalog: dependency-ordered module registration | CLA-0004 |
| [`Claudalisk.Scheduling`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Scheduling/README.md) | The scheduler, which queues commands as jobs in its own jobs module on a cron schedule; it depends on the jobs library and its PostgreSQL transport | CLA-0061 |

An AWS SQS transport for jobs is planned.

## Versions

- Every package targets `net10.0` and `net11.0`.
- The packages share one version, and each release publishes all of them.
- Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html), as [CLA-0098](https://github.com/lucem-labs/claudalisk/blob/main/adr/0098-artifacts-follow-semantic-versioning.md) sets out. While the major version is 0, a breaking change bumps the minor version and everything else bumps the patch.
- A prerelease is `<version>-alpha.<n>`, `-beta.<n>` or `-rc.<n>`, and it may break the one before it.
- A published version never changes. A bad release is fixed by a new version.
- Apps pin exact package versions.

[Building and releasing](docs/releasing.md) describes how a release is made.

## Licence

[MIT](LICENSE)

## Not affiliated

Claudalisk is an independent project. It is not affiliated with, endorsed by, or sponsored by Anthropic (Claude) or Blizzard Entertainment (Hydralisk). All trademarks belong to their owners.
