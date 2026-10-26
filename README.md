<p align="center">
  <img src="docs/assets/logo-wide.png" alt="Claudalisk" width="720">
</p>

# Claudalisk .NET

The .NET packages behind [Claudalisk](https://github.com/lucem-labs/claudalisk) apps: domain errors, module registration, the unit of work, a dispatcher for commands and queries, background jobs and a scheduler.

Claudalisk is an opinionated Claude Code plugin for building .NET + React apps. Its standards live as architecture decision records in the [Claudalisk repository](https://github.com/lucem-labs/claudalisk/tree/main/adr). This repository holds the infrastructure code those standards need, so that every app uses one tested implementation instead of its own copy.

> **Status: early.** Nothing is published yet. The design is tracked in the [issues](https://github.com/lucem-labs/claudalisk-dotnet/issues).

## Packages

This is the current plan. Names and boundaries can change as the packages are built (claudalisk CLA-0062).

| Package | What it holds | Standard | Issue |
|---|---|---|---|
| `Claudalisk.Domain` | `DomainException`, domain events and the aggregate base: the only package an app's Domain projects may reference | CLA-0048, CLA-0063, CLA-0068 | #3 |
| `Claudalisk.Modules` | Module registration with declared dependencies, and each module's controllers | CLA-0004 | #7 |
| `Claudalisk.Persistence.Abstractions` | `IUnitOfWork` | CLA-0007 | #8 |
| `Claudalisk.Persistence` | The data source, a connection per scope, the unit of work and module DbContext wiring | CLA-0007 | #8 |
| `Claudalisk.Dispatching.Abstractions` | `IDispatcher`, handler interfaces and `JobReference`, for implementation projects | CLA-0003, CLA-0067 | #1 |
| `Claudalisk.Dispatching` | The dispatcher and its pipeline: logging, validation and the unit of work | CLA-0049 | #1 |
| `Claudalisk.AspNetCore` | The Problem Details handler and health endpoints, registered by the Host | CLA-0001, CLA-0063 | #9 |
| `Claudalisk.Jobs` | Background jobs, batch jobs, event fan-out and the jobs router | CLA-0058, CLA-0059, CLA-0060, CLA-0068 | #4 |
| `Claudalisk.Jobs.PostgreSql` | The PostgreSQL transport for jobs | CLA-0058 | #4 |
| `Claudalisk.Scheduling` | The scheduler module, which sends commands on a cron schedule | CLA-0061 | #5 |

Interfaces projects in an app reference none of these (CLA-0003). An AWS SQS transport for jobs is planned. Apps pin exact package versions, and the packages are published to nuget.org with Trusted Publishing.

## Licence

[MIT](LICENSE)

## Not affiliated

Claudalisk is an independent project. It is not affiliated with, endorsed by, or sponsored by Anthropic (Claude) or Blizzard Entertainment (Hydralisk). All trademarks belong to their owners.
