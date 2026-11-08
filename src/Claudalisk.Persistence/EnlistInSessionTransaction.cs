using System.Data.Common;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Claudalisk.Persistence;

internal sealed class EnlistInSessionTransaction(DbSession session) : SaveChangesInterceptor, IDbCommandInterceptor
{
    public InterceptionResult<DbCommand> CommandCreating(CommandCorrelatedEventData eventData, InterceptionResult<DbCommand> result)
    {
        Enlist(eventData.Context);
        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Enlist(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Enlist(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult ThrowingConcurrencyException(ConcurrencyExceptionEventData eventData, InterceptionResult result) =>
        throw Conflict(eventData);

    public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
        throw Conflict(eventData);

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => RethrowConflict(eventData.Exception);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        RethrowConflict(eventData.Exception);
        return Task.CompletedTask;
    }

    private void Enlist(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        ModuleContextGuard.Check(context.GetService<IDbContextOptions>());
        if (session.Transaction is null)
        {
            return;
        }

        var current = context.Database.CurrentTransaction?.GetDbTransaction();
        if (!ReferenceEquals(current, session.Transaction))
        {
            context.Database.UseTransaction(session.Transaction);
        }

        if (!session.EnlistedContexts.Contains(context))
        {
            session.EnlistedContexts.Add(context);
        }
    }

    private static void RethrowConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConcurrencyConflictException conflict)
            {
                ExceptionDispatchInfo.Capture(conflict).Throw();
            }
        }
    }

    private static ConcurrencyConflictException Conflict(ConcurrencyExceptionEventData eventData) =>
        new(ConflictMessage, new DbUpdateConcurrencyException($"{ConflictMessage} Entities: {string.Join(", ", eventData.Entries.Select(entry => entry.Metadata.DisplayName()))}."));

    private const string ConflictMessage = "The data was changed by someone else since it was read. Read it again and retry.";
}
