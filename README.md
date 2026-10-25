<p align="center">
  <img src="docs/assets/logo-wide.png" alt="Claudalisk" width="720">
</p>

# Claudalisk .NET

The .NET packages behind [Claudalisk](https://github.com/lucem-labs/claudalisk) apps: domain errors, a dispatcher for commands and queries, background jobs and a scheduler.

Claudalisk is an opinionated Claude Code plugin for building .NET + React apps. Its standards live as architecture decision records in the [Claudalisk repository](https://github.com/lucem-labs/claudalisk/tree/main/adr). This repository holds the infrastructure code those standards need, so that every app uses one tested implementation instead of its own copy.

> **Status: early.** Nothing is published yet. The design is tracked in the [issues](https://github.com/lucem-labs/claudalisk-dotnet/issues).

## Packages

| Package | What it holds | Standard |
|---|---|---|
| `Claudalisk.Domain` | `DomainException` and the handler that turns errors into Problem Details | CLA-0063 |
| `Claudalisk.Dispatching.Abstractions` | Commands, queries and handler interfaces, the only package an Interfaces project may reference | CLA-0003, CLA-0067 |
| `Claudalisk.Dispatching` | The dispatcher and its pipeline: logging, validation and the unit of work | CLA-0049 |
| `Claudalisk.Jobs` | Background jobs, batch jobs and the jobs router | CLA-0058, CLA-0059, CLA-0060 |
| `Claudalisk.Jobs.PostgreSql` | The PostgreSQL transport for jobs | CLA-0058 |
| `Claudalisk.Scheduling` | The scheduler module, which sends commands on a cron schedule | CLA-0061 |

An AWS SQS transport for jobs is planned. Apps pin exact package versions, and the packages will be published to nuget.org.

## Licence

[MIT](LICENSE)

## Not affiliated

Claudalisk is an independent project. It is not affiliated with, endorsed by, or sponsored by Anthropic (Claude) or Blizzard Entertainment (Hydralisk). All trademarks belong to their owners.
