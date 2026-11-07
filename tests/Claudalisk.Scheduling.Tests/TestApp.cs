using Claudalisk.Dispatching;
using Claudalisk.Modules;
using Claudalisk.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Claudalisk.Scheduling.Tests;

public sealed class CatalogContract;

public sealed record RefreshCatalogCommand(string Source);

public sealed record RebuildIndexCommand;

public sealed class Refreshes
{
    private readonly List<string> _sources = [];

    public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<string> Sources
    {
        get
        {
            lock (_sources)
            {
                return _sources.ToArray();
            }
        }
    }

    public void Add(string source)
    {
        lock (_sources)
        {
            _sources.Add(source);
        }

        First.TrySetResult();
    }
}

internal sealed class RefreshCatalogHandler(Refreshes refreshes) : IHandler<RefreshCatalogCommand, int>
{
    public Task<int> HandleAsync(RefreshCatalogCommand message, CancellationToken cancellationToken)
    {
        refreshes.Add(message.Source);
        return Task.FromResult(refreshes.Sources.Count);
    }
}

internal sealed class RebuildIndexHandler : IHandler<RebuildIndexCommand, int>
{
    public Task<int> HandleAsync(RebuildIndexCommand message, CancellationToken cancellationToken) => Task.FromResult(0);
}

public sealed class CatalogModule : IModule
{
    public Type Contract => typeof(CatalogContract);

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSchedulableCommand<RefreshCatalogCommand>("Catalog", "refresh-catalog");
        services.AddSchedulableCommand<RebuildIndexCommand>("Catalog", "rebuild-index");
    }
}

internal sealed record TestSchedule(string Name, string Cron, string Command = "refresh-catalog", string Module = "Catalog", string TimeZone = "UTC", string Arguments = """{"source":"nightly"}""");

internal static class TestApp
{
    public const string Image = "postgres:17-alpine";
    private const string TemplateDatabase = "scheduling_template";

    private static readonly Lazy<Task<string>> Server = new(StartAsync);

    public static DateTimeOffset Start { get; } = new(2026, 10, 26, 10, 30, 0, TimeSpan.Zero);

    public static async Task<string> CreateDatabaseAsync()
    {
        var admin = await Server.Value;
        var name = $"scheduling_{Guid.NewGuid():N}";
        await ExecuteAsync(admin, $"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDatabase}\"");
        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    public static ServiceProvider Build(string connectionString, FakeTimeProvider time, Refreshes refreshes, bool enabled, params TestSchedule[] schedules)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Scheduling:Enabled"] = enabled ? "true" : "false",
            ["Scheduling:TickInterval"] = "00:01:00",
        };
        for (var i = 0; i < schedules.Length; i++)
        {
            settings[$"Scheduling:Schedules:{i}:Name"] = schedules[i].Name;
            settings[$"Scheduling:Schedules:{i}:Cron"] = schedules[i].Cron;
            settings[$"Scheduling:Schedules:{i}:Command"] = schedules[i].Command;
            settings[$"Scheduling:Schedules:{i}:Module"] = schedules[i].Module;
            settings[$"Scheduling:Schedules:{i}:TimeZone"] = schedules[i].TimeZone;
            settings[$"Scheduling:Schedules:{i}:Arguments"] = schedules[i].Arguments;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(connectionString);
        services.AddModules(configuration, new CatalogModule());
        services.AddDispatching();
        services.AddScheduling(configuration);
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(refreshes);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

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
        await using (var app = Build(template, new FakeTimeProvider(Start), new Refreshes(), enabled: false))
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
