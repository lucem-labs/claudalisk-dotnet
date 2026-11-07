using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudalisk.Persistence;

public sealed record MigrationBaseline(string Schema, string Migration, IReadOnlyList<string> Replaces);

public sealed class MigrationBaselineException(string message) : InvalidOperationException(message);

public sealed partial class MigrationBaselines(IServiceProvider services)
{
    private readonly ILogger logger = services.GetService<ILogger<MigrationBaselines>>() ?? NullLogger<MigrationBaselines>.Instance;

    private readonly MigrationBaseline[] baselines = services.GetServices<MigrationBaseline>().ToArray();

    public async Task AdoptAsync(CancellationToken cancellationToken)
    {
        if (baselines.Length == 0)
        {
            return;
        }

        var adopt = await PlanAsync(cancellationToken);
        if (adopt.Count == 0)
        {
            return;
        }

        await services.GetRequiredService<IUnitOfWork>().RunAsync(async token =>
        {
            foreach (var (baseline, applied) in adopt)
            {
                await using var context = HistoryContext(baseline.Schema);
                var history = context.GetService<IHistoryRepository>();
                foreach (var migration in applied)
                {
                    await context.Database.ExecuteSqlRawAsync(history.GetDeleteScript(migration), token);
                }

                await context.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(baseline.Migration, ProductInfo.GetVersion())), token);
            }
        }, cancellationToken);

        foreach (var (baseline, _) in adopt)
        {
            LogRecorded(logger, baseline.Migration, baseline.Schema, baseline.Replaces.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded the baseline {Migration} for schema {Schema} in place of {Count} migrations.")]
    private static partial void LogRecorded(ILogger logger, string migration, string schema, int count);

    private async Task<List<(MigrationBaseline Baseline, List<string> Applied)>> PlanAsync(CancellationToken cancellationToken)
    {
        var adopt = new List<(MigrationBaseline, List<string>)>();
        foreach (var baseline in baselines)
        {
            await using var context = HistoryContext(baseline.Schema);
            var applied = await AppliedAsync(context, cancellationToken);
            if (applied is not { Count: > 0 })
            {
                await EnsureFreshAsync(context, baseline, cancellationToken);
                continue;
            }

            if (applied.Contains(baseline.Migration, StringComparer.Ordinal))
            {
                continue;
            }

            var missing = baseline.Replaces.Except(applied, StringComparer.Ordinal).ToArray();
            var unknown = applied.Except(baseline.Replaces, StringComparer.Ordinal).ToArray();
            if (missing.Length > 0 || unknown.Length > 0)
            {
                throw new MigrationBaselineException(
                    $"Schema {baseline.Schema} is not at the state the baseline {baseline.Migration} replaces, so nothing was changed. "
                    + $"Missing: {List(missing)}. Unknown: {List(unknown)}. "
                    + "Deploy the last version before the baseline first, so the database has every migration it replaces, then this one.");
            }

            adopt.Add((baseline, applied));
        }

        return adopt;
    }

    private DbContext HistoryContext(string schema) =>
        new(new DbContextOptionsBuilder<DbContext>().UseModuleSchema(services, schema).Options);

    private static async Task<List<string>?> AppliedAsync(DbContext context, CancellationToken cancellationToken)
    {
        var history = context.GetService<IHistoryRepository>();
        if (!await history.ExistsAsync(cancellationToken))
        {
            return null;
        }

        return (await history.GetAppliedMigrationsAsync(cancellationToken)).Select(row => row.MigrationId).ToList();
    }

    private static async Task EnsureFreshAsync(DbContext context, MigrationBaseline baseline, CancellationToken cancellationToken)
    {
        var tables = await context.Database
            .SqlQuery<long>($"SELECT count(*)::bigint AS \"Value\" FROM information_schema.tables WHERE table_schema = {baseline.Schema} AND table_name <> {PersistenceServiceCollectionExtensions.MigrationsHistoryTable}")
            .SingleAsync(cancellationToken);
        if (tables > 0)
        {
            throw new MigrationBaselineException(
                $"Schema {baseline.Schema} has tables but no migration history, so the baseline {baseline.Migration} can be neither applied nor recorded. Nothing was changed.");
        }
    }

    private static string List(string[] migrations) => migrations.Length == 0 ? "none" : string.Join(", ", migrations);
}
