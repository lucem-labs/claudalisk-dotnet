using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudalisk.Persistence;

public sealed record MigrationBaseline(string Schema, string Migration, IReadOnlyList<string> Replaces);

public sealed class MigrationBaselineException : InvalidOperationException
{
    public MigrationBaselineException()
    {
    }

    public MigrationBaselineException(string message)
        : base(message)
    {
    }

    public MigrationBaselineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed partial class MigrationBaselines(IServiceProvider services)
{
    private readonly ILogger logger = services.GetService<ILogger<MigrationBaselines>>() ?? NullLogger<MigrationBaselines>.Instance;

    private readonly MigrationBaseline[] baselines = services.GetServices<MigrationBaseline>().ToArray();

    internal const long AdvisoryLockKey = 7370120260101;

    public async Task AdoptAsync(CancellationToken cancellationToken = default)
    {
        if (baselines.Length == 0)
        {
            return;
        }

        var adopted = new List<MigrationBaseline>();
        await services.GetRequiredService<IUnitOfWork>().RunAsync(async token =>
        {
            var locking = HistoryContext(baselines[0].Schema);
            await using (locking.ConfigureAwait(false))
            {
                await locking.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", token).ConfigureAwait(false);
            }

            var adopt = await PlanAsync(token).ConfigureAwait(false);
            foreach (var (baseline, applied) in adopt)
            {
                var context = HistoryContext(baseline.Schema);
                await using var contextDisposal = context.ConfigureAwait(false);
                var history = context.GetService<IHistoryRepository>();
                foreach (var migration in applied)
                {
                    await context.Database.ExecuteSqlRawAsync(history.GetDeleteScript(migration), token).ConfigureAwait(false);
                }

                await context.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(baseline.Migration, ProductInfo.GetVersion())), token).ConfigureAwait(false);
                adopted.Add(baseline);
            }
        }, cancellationToken).ConfigureAwait(false);

        foreach (var baseline in adopted)
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
            var context = HistoryContext(baseline.Schema);
            await using var contextDisposal = context.ConfigureAwait(false);
            var applied = await AppliedAsync(context, baseline.Schema, cancellationToken).ConfigureAwait(false);
            if (applied is not { Count: > 0 })
            {
                await EnsureFreshAsync(context, baseline, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (applied.Contains(baseline.Migration, StringComparer.Ordinal))
            {
                var replaced = applied.Intersect(baseline.Replaces, StringComparer.Ordinal).ToArray();
                var stray = UnknownMigrations(baseline, applied);
                if (replaced.Length > 0 || stray.Length > 0)
                {
                    throw new MigrationBaselineException(
                        $"Schema {baseline.Schema} records the baseline {baseline.Migration} next to migrations it should not, so nothing was changed. "
                        + $"Replaced: {List(replaced)}. Unknown: {List(stray)}. Remove them from the history by hand once the schema is known to match the baseline.");
                }

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

    private string[] UnknownMigrations(MigrationBaseline baseline, List<string> applied)
    {
        var registration = services.GetServices<ModuleDbContextRegistration>().FirstOrDefault(candidate => candidate.Schema == baseline.Schema);
        if (registration is null)
        {
            return [];
        }

        var context = (DbContext)services.GetRequiredService(registration.ContextType);
        var known = context.GetService<IMigrationsAssembly>().Migrations.Keys;
        return applied
            .Where(id => id != baseline.Migration && !baseline.Replaces.Contains(id, StringComparer.Ordinal) && !known.Contains(id, StringComparer.Ordinal))
            .ToArray();
    }

    private DbContext HistoryContext(string schema) =>
        new(new DbContextOptionsBuilder<DbContext>().UseModuleSchema(services, schema).Options);

    private static async Task<List<string>?> AppliedAsync(DbContext context, string schema, CancellationToken cancellationToken)
    {
        var table = $"{MigrationHistoryMoves.Quote(schema)}.\"{PersistenceServiceCollectionExtensions.MigrationsHistoryTable}\"";
        var exists = await context.Database
            .SqlQuery<bool>($"SELECT to_regclass({table}) IS NOT NULL AS \"Value\"")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            return null;
        }

        var history = context.GetService<IHistoryRepository>();
        return (await history.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).Select(row => row.MigrationId).ToList();
    }

    private static async Task EnsureFreshAsync(DbContext context, MigrationBaseline baseline, CancellationToken cancellationToken)
    {
        var tables = await context.Database
            .SqlQuery<long>($"SELECT count(*)::bigint AS \"Value\" FROM pg_catalog.pg_class AS c JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace WHERE n.nspname = {baseline.Schema} AND c.relkind IN ('r', 'p', 'v', 'm', 'f') AND c.relname <> {PersistenceServiceCollectionExtensions.MigrationsHistoryTable}")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (tables > 0)
        {
            throw new MigrationBaselineException(
                $"Schema {baseline.Schema} has tables but no migration history, so the baseline {baseline.Migration} can be neither applied nor recorded. Nothing was changed.");
        }
    }

    private static string List(string[] migrations) => migrations.Length == 0 ? "none" : string.Join(", ", migrations);
}
