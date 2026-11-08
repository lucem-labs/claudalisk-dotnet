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
        var existing = services.Select(descriptor => descriptor.ImplementationInstance).OfType<PersistenceRegistration>().SingleOrDefault();
        if (existing is not null)
        {
            if (existing.ConnectionString != connectionString)
            {
                throw new InvalidOperationException(
                    "AddPersistence was already called with another connection string. A host has one database, so call AddPersistence once.");
            }

            return services;
        }

        services.AddSingleton(new PersistenceRegistration(connectionString));
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

        var existing = services.Select(descriptor => descriptor.ImplementationInstance).OfType<MigrationBaseline>()
            .SingleOrDefault(baseline => baseline.Schema == schema);
        if (existing is null)
        {
            services.AddSingleton(new MigrationBaseline(schema, migration, [.. replaces]));
            return services;
        }

        if (existing.Migration != migration || !existing.Replaces.SequenceEqual(replaces))
        {
            throw new InvalidOperationException(
                $"The schema '{schema}' already has the baseline {Describe(existing.Migration, existing.Replaces)} and cannot also have {Describe(migration, replaces)}.");
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

        var existing = services.Select(descriptor => descriptor.ImplementationInstance).OfType<MovedMigrationHistory>()
            .SingleOrDefault(move => move.To == to || move.From == from);
        if (existing is null)
        {
            services.AddSingleton(new MovedMigrationHistory(from, to));
            return services;
        }

        if (existing.From != from || existing.To != to)
        {
            throw new InvalidOperationException(
                $"The migration history move from '{existing.From}' to '{existing.To}' is already registered and conflicts with the move from '{from}' to '{to}'.");
        }

        return services;
    }

    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        var registrations = services.Select(descriptor => descriptor.ImplementationInstance).OfType<ModuleDbContextRegistration>().ToList();
        var existing = registrations.SingleOrDefault(registration => registration.ContextType == typeof(TContext));
        if (existing is not null)
        {
            if (existing.Schema != schema)
            {
                throw new InvalidOperationException(
                    $"{typeof(TContext).Name} is already registered with the schema '{existing.Schema}' and cannot be registered again with the schema '{schema}'.");
            }

            return services;
        }

        var owner = registrations.SingleOrDefault(registration => registration.Schema == schema);
        if (owner is not null)
        {
            throw new InvalidOperationException(
                $"The schema '{schema}' is already registered for {owner.ContextType.Name}, so {typeof(TContext).Name} cannot use it. Each module context needs its own schema.");
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

        if (services.GetService<IServiceProviderIsService>()?.IsService(typeof(DbSession)) == false)
        {
            throw new InvalidOperationException(MissingPersistenceMessage);
        }

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

    internal const string MissingPersistenceMessage =
        "Module DbContexts need the scope's connection from AddPersistence. Call AddPersistence when registering services.";

    private static string Describe(string migration, IEnumerable<string> replaces) =>
        $"{migration} replacing {string.Join(", ", replaces)}";

    private sealed record PersistenceRegistration(string ConnectionString);
}
