using Claudalisk.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Claudalisk.Jobs.Tests;

internal static class PostgresServer
{
    public const string Image = "postgres:17-alpine";
    private const string TemplateDatabase = "jobs_template";

    private static readonly Lazy<Task<string>> Server = new(StartAsync);

    public static async Task<string> CreateDatabaseAsync()
    {
        var admin = await Server.Value;
        var name = $"jobs_{Guid.NewGuid():N}";
        await ExecuteAsync(admin, $"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDatabase}\"");
        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    public static async Task<ServiceProvider> CreateAppAsync(Action<JobsOptions>? configure = null) =>
        new ServiceCollection().AddTestApp(await CreateDatabaseAsync(), configure).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

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

    private static async Task<string> StartAsync()
    {
        var container = new PostgreSqlBuilder(Image).Build();
        await StartOrExplainAsync(() => container.StartAsync());
        AppDomain.CurrentDomain.ProcessExit += (_, _) => container.DisposeAsync().AsTask().GetAwaiter().GetResult();

        var admin = container.GetConnectionString();
        await ExecuteAsync(admin, $"CREATE DATABASE \"{TemplateDatabase}\"");
        var template = new NpgsqlConnectionStringBuilder(admin) { Database = TemplateDatabase }.ConnectionString;
        await using (var app = new ServiceCollection().AddTestApp(template).BuildServiceProvider())
        {
            await using var scope = app.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().MigrateAsync(default);
        }

        NpgsqlConnection.ClearAllPools();
        await ExecuteAsync(admin, $"ALTER DATABASE \"{TemplateDatabase}\" WITH IS_TEMPLATE true ALLOW_CONNECTIONS false");
        return admin;
    }

    internal static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
