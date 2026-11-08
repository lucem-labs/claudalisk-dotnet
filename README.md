<p align="center">
  <img src="docs/assets/logo-wide.png" alt="Claudalisk" width="720">
</p>

# Claudalisk .NET

The .NET packages behind [Claudalisk](https://github.com/lucem-labs/claudalisk) apps: domain errors, a dispatcher for commands and queries, persistence, background jobs and a scheduler.

Claudalisk is an opinionated Claude Code plugin for building .NET + React apps. Its standards live as architecture decision records in the [Claudalisk repository](https://github.com/lucem-labs/claudalisk/tree/main/adr). This repository holds the infrastructure code those standards need, so that every app uses one tested implementation instead of its own copy.

> **Status: early.** Alpha versions are on [nuget.org](https://www.nuget.org/packages/Claudalisk.Domain), starting at `0.1.0-alpha.1`, and the API can still change. The design is tracked in the [issues](https://github.com/lucem-labs/claudalisk-dotnet/issues).

## Packages

| Package | What it holds | Standard |
|---|---|---|
| [`Claudalisk.Domain`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Domain/README.md) | `AggregateRoot` with its version, domain events, `DomainException` and `DependencyUnavailableException` | CLA-0063 |
| [`Claudalisk.Dispatching.Abstractions`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Dispatching.Abstractions/README.md) | Commands, queries and handler interfaces, the pipeline contracts and the message exceptions; the only dispatching package an Interfaces project may reference | CLA-0003, CLA-0067 |
| [`Claudalisk.Dispatching`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Dispatching/README.md) | The dispatcher and its pipeline: logging, validation, retry on concurrency conflicts, explicit step order and the unit of work | CLA-0049 |
| [`Claudalisk.Persistence.Abstractions`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Persistence.Abstractions/README.md) | `IUnitOfWork` and `ConcurrencyConflictException`, without EF Core types | CLA-0007, CLA-0056 |
| [`Claudalisk.Persistence`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Persistence/README.md) | EF Core on PostgreSQL for module contexts: the scope's connection and transaction, the unit of work and the migration runner | CLA-0007, CLA-0008, CLA-0010 |
| [`Claudalisk.AspNetCore`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.AspNetCore/README.md) | The Problem Details handler, conditional writes, HAL members, the health endpoint and module tags in OpenAPI | CLA-0045, CLA-0046, CLA-0063 |
| [`Claudalisk.Jobs`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Jobs/README.md) | Background jobs, batch jobs, event jobs, rebuilds and the jobs router | CLA-0058, CLA-0059, CLA-0060 |
| [`Claudalisk.Jobs.PostgreSql`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Jobs.PostgreSql/README.md) | The PostgreSQL transport for jobs | CLA-0058 |
| [`Claudalisk.Modules`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Modules/README.md) | `IModule`, `AddClaudaliskModules` and the module catalog: dependency-ordered module registration | CLA-0004 |
| [`Claudalisk.Scheduling`](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/src/Claudalisk.Scheduling/README.md) | The scheduler, which queues commands as jobs in its own jobs module on a cron schedule; it depends on the jobs library and a jobs transport | CLA-0061 |

An AWS SQS transport for jobs is planned. Apps pin exact package versions.

## Licence

[MIT](LICENSE)

## Not affiliated

Claudalisk is an independent project. It is not affiliated with, endorsed by, or sponsored by Anthropic (Claude) or Blizzard Entertainment (Hydralisk). All trademarks belong to their owners.
