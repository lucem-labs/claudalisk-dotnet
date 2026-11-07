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
