using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Claudalisk.Persistence.Tests;

internal static class PostgresServer
{
    public const string Image = "postgres:17-alpine";
    private const string TemplateDatabase = "persistence_template";

    private static readonly Lazy<Task<string>> Server = new(StartAsync);
    private static PostgreSqlContainer? _container;

    public static async Task<ServiceProvider> CreateAppAsync(Action<IServiceCollection>? configure = null) =>
        Build(await CreateDatabaseAsync(TemplateDatabase), configure);

    public static async Task<ServiceProvider> CreateEmptyAppAsync(Action<IServiceCollection>? configure = null) =>
        Build(await CreateDatabaseAsync("template0"), configure);

    public static Task<string> CreateEmptyDatabaseAsync() => CreateDatabaseAsync("template0");

    public static Task<string> CreateMigratedDatabaseAsync() => CreateDatabaseAsync(TemplateDatabase);

    public static ServiceProvider CreateApp(string connectionString, Action<IServiceCollection>? configure = null) => Build(connectionString, configure);

    public static async Task StartOrExplainAsync(Func<Task> start)
    {
        try
        {
            await start();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The database tests need Docker: Testcontainers could not start {Image}. Start Docker and run the tests again.",
                exception);
        }
    }

    private static ServiceProvider Build(string connectionString, Action<IServiceCollection>? configure)
    {
        var services = new ServiceCollection().AddTestModules(connectionString);
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<string> CreateDatabaseAsync(string template)
    {
        var admin = await Server.Value;
        var name = $"persistence_{Guid.NewGuid():N}";
        await ExecuteAsync(admin, $"CREATE DATABASE \"{name}\" TEMPLATE \"{template}\"");
        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    public static ValueTask DisposeContainerAsync() =>
        Interlocked.Exchange(ref _container, null) is { } container ? container.DisposeAsync() : ValueTask.CompletedTask;

    private static async Task<string> StartAsync()
    {
        var container = new PostgreSqlBuilder(Image).Build();
        _container = container;
        await StartOrExplainAsync(() => container.StartAsync());

        var admin = container.GetConnectionString();
        await ExecuteAsync(admin, $"CREATE DATABASE \"{TemplateDatabase}\"");
        var template = new NpgsqlConnectionStringBuilder(admin) { Database = TemplateDatabase }.ConnectionString;
        await using (var app = new ServiceCollection().AddTestModules(template).BuildServiceProvider())
        {
            await using var scope = app.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().MigrateAsync(default);
        }

        NpgsqlConnection.ClearAllPools();
        await ExecuteAsync(admin, $"ALTER DATABASE \"{TemplateDatabase}\" WITH IS_TEMPLATE true ALLOW_CONNECTIONS false");
        return admin;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
