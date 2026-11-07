using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Claudalisk.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public static IServiceCollection AddPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.TryAddScoped<DbSession>();
        services.TryAddScoped<IScopeConnection>(provider => provider.GetRequiredService<DbSession>());
        services.TryAddScoped<IUnitOfWork, UnitOfWork>();
        services.TryAddScoped<DatabaseInitializer>();
        services.TryAddScoped<MigrationBaselines>();
        services.TryAddScoped<MigrationHistoryMoves>();
        return services;
    }

    public static IServiceCollection AddMigrationBaseline(this IServiceCollection services, string schema, string migration, IReadOnlyList<string> replaces)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(migration);
        ArgumentNullException.ThrowIfNull(replaces);
        if (replaces.Count == 0)
        {
            throw new ArgumentException("A baseline replaces at least one migration.", nameof(replaces));
        }

        if (!services.Any(descriptor => descriptor.ImplementationInstance is MigrationBaseline existing && existing.Schema == schema))
        {
            services.AddSingleton(new MigrationBaseline(schema, migration, [.. replaces]));
        }

        return services;
    }

    public static IServiceCollection AddMovedMigrationHistory(this IServiceCollection services, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(services);
        MigrationHistoryMoves.Quote(from);
        MigrationHistoryMoves.Quote(to);
        if (from == to)
        {
            throw new ArgumentException("A migration history moves to another schema.", nameof(to));
        }

        if (!services.Any(descriptor => descriptor.ImplementationInstance is MovedMigrationHistory existing && existing.To == to))
        {
            services.AddSingleton(new MovedMigrationHistory(from, to));
        }

        return services;
    }

    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (services.Any(descriptor => descriptor.ImplementationInstance is ModuleDbContextRegistration registration
            && registration.ContextType == typeof(TContext)))
        {
            return services;
        }

        services.AddDbContext<TContext>((provider, options) => options.UseModuleSchema(provider, schema));
        services.AddSingleton(new ModuleDbContextRegistration(typeof(TContext), schema));
        return services;
    }

    public static DbContextOptionsBuilder UseModuleSchema(this DbContextOptionsBuilder options, IServiceProvider services, string schema)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        DbSession session;
        try
        {
            session = services.GetRequiredService<DbSession>();
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"{ModuleContextGuard.PoolingMessage} Call AddPersistence first, and resolve module DbContexts from a scope.", exception);
        }

        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new ModuleContextGuard(schema));
        return options
            .UseNpgsql(session.Connection, npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, schema))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new EnlistInSessionTransaction(session));
    }
}
