using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Persistence;

public sealed class DatabaseInitializer(IServiceProvider services, IEnumerable<ModuleDbContextRegistration> registrations)
{
    public IReadOnlyList<ModuleDbContextRegistration> Contexts { get; } = registrations.ToArray();

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (services.GetRequiredService<IScopeConnection>().Transaction is not null)
        {
            throw new InvalidOperationException(
                "DatabaseInitializer.MigrateAsync cannot run inside a unit of work, because migrations manage their own transactions. Call it outside IUnitOfWork.RunAsync.");
        }

        await services.GetRequiredService<MigrationHistoryMoves>().MoveAsync(cancellationToken);
        await services.GetRequiredService<MigrationBaselines>().AdoptAsync(cancellationToken);
        foreach (var registration in Contexts)
        {
            await Context(registration).Database.MigrateAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<string>> GetPendingMigrationsAsync(CancellationToken cancellationToken = default)
    {
        var pending = new List<string>();
        foreach (var registration in Contexts)
        {
            var migrations = await Context(registration).Database.GetPendingMigrationsAsync(cancellationToken);
            pending.AddRange(migrations.Select(migration => $"{registration.Schema}: {migration}"));
        }

        return pending;
    }

    private DbContext Context(ModuleDbContextRegistration registration) =>
        (DbContext)services.GetRequiredService(registration.ContextType);
}
