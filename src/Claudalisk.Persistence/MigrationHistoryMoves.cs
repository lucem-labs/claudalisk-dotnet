using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudalisk.Persistence;

public sealed record MovedMigrationHistory(string From, string To);

public sealed class MigrationHistoryMoveException : InvalidOperationException
{
    public MigrationHistoryMoveException()
    {
    }

    public MigrationHistoryMoveException(string message)
        : base(message)
    {
    }

    public MigrationHistoryMoveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed partial class MigrationHistoryMoves(IServiceProvider services)
{
    private readonly ILogger logger = services.GetService<ILogger<MigrationHistoryMoves>>() ?? NullLogger<MigrationHistoryMoves>.Instance;

    private readonly MovedMigrationHistory[] moves = services.GetServices<MovedMigrationHistory>().ToArray();

    public async Task MoveAsync(CancellationToken cancellationToken = default)
    {
        if (moves.Length == 0)
        {
            return;
        }

        var moved = new List<MovedMigrationHistory>();
        await services.GetRequiredService<IUnitOfWork>().RunAsync(async token =>
        {
            var locking = new DbContext(new DbContextOptionsBuilder<DbContext>().UseClaudaliskModuleSchema(services, moves[0].To).Options);
            await using (locking.ConfigureAwait(false))
            {
                await locking.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({MigrationBaselines.AdvisoryLockKey})", token).ConfigureAwait(false);
            }

            foreach (var move in moves)
            {
                var context = new DbContext(new DbContextOptionsBuilder<DbContext>().UseClaudaliskModuleSchema(services, move.To).Options);
                await using var contextDisposal = context.ConfigureAwait(false);
                var from = await HistoryExistsAsync(context, move.From, token).ConfigureAwait(false);
                var to = await HistoryExistsAsync(context, move.To, token).ConfigureAwait(false);
                if (from && to)
                {
                    throw new MigrationHistoryMoveException(
                        $"Both schema {move.From} and schema {move.To} have a migration history, so it cannot be moved. Nothing was changed.");
                }

                if (!from)
                {
                    continue;
                }

                var sql = MoveSql(move);
                await context.Database.ExecuteSqlRawAsync(sql, token).ConfigureAwait(false);
                moved.Add(move);
            }
        }, cancellationToken).ConfigureAwait(false);

        foreach (var move in moved)
        {
            LogMoved(logger, move.From, move.To);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved the migration history from schema {From} to schema {To}.")]
    private static partial void LogMoved(ILogger logger, string from, string to);

    private static Task<bool> HistoryExistsAsync(DbContext context, string schema, CancellationToken cancellationToken)
    {
        var table = $"{Quote(schema)}.\"{PersistenceServiceCollectionExtensions.MigrationsHistoryTable}\"";
        return context.Database
            .SqlQuery<bool>($"SELECT to_regclass({table}) IS NOT NULL AS \"Value\"")
            .SingleAsync(cancellationToken);
    }

    private static string MoveSql(MovedMigrationHistory move) =>
        $"CREATE SCHEMA IF NOT EXISTS {Quote(move.To)}; "
        + $"ALTER TABLE {Quote(move.From)}.\"{PersistenceServiceCollectionExtensions.MigrationsHistoryTable}\" SET SCHEMA {Quote(move.To)};";

    internal static string Quote(string schema) =>
        SchemaPattern().IsMatch(schema)
            ? $"\"{schema}\""
            : throw new ArgumentException($"A schema is a lower-case identifier; got '{schema}'.", nameof(schema));

    [GeneratedRegex(@"^[a-z_][a-z0-9_]*\z")]
    private static partial Regex SchemaPattern();
}
