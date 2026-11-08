using Claudalisk.Dispatching;
using Claudalisk.Jobs;
using Claudalisk.Jobs.PostgreSql;
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
    public ValueTask<int> HandleAsync(RefreshCatalogCommand message, CancellationToken cancellationToken)
    {
        refreshes.Add(message.Source);
        return ValueTask.FromResult(refreshes.Sources.Count);
    }
}

internal sealed class RebuildIndexHandler : IHandler<RebuildIndexCommand, int>
{
    public ValueTask<int> HandleAsync(RebuildIndexCommand message, CancellationToken cancellationToken) => ValueTask.FromResult(0);
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

internal sealed record TestSettings(bool? Enabled = true, string TickInterval = "00:01:00", bool Workers = false);

internal static class TestApp
{
    public const string Image = "postgres:17-alpine";
    private const string TemplateDatabase = "scheduling_template";

    private static readonly Lazy<Task<string>> Server = new(StartAsync);
    private static PostgreSqlContainer? _container;

    public static DateTimeOffset Start { get; } = new(2026, 10, 26, 10, 30, 0, TimeSpan.Zero);

    public static async Task<string> CreateEmptyDatabaseAsync()
    {
        var admin = await Server.Value;
        var name = $"scheduling_{Guid.NewGuid():N}";
        await ExecuteAsync(admin, $"CREATE DATABASE \"{name}\"");
        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    public static async Task<string> CreateDatabaseAsync()
    {
        var admin = await Server.Value;
        var name = $"scheduling_{Guid.NewGuid():N}";
        await ExecuteAsync(admin, $"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDatabase}\"");
        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    public static ServiceProvider Build(string connectionString, FakeTimeProvider time, Refreshes refreshes, bool enabled, params TestSchedule[] schedules) =>
        Services(connectionString, time, refreshes, new TestSettings(enabled), schedules).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    public static ServiceCollection Services(string connectionString, FakeTimeProvider time, Refreshes refreshes, TestSettings settings, params TestSchedule[] schedules)
    {
        var values = new Dictionary<string, string?>
        {
            ["Scheduling:TickInterval"] = settings.TickInterval,
        };
        if (settings.Enabled is { } enabled)
        {
            values["Scheduling:Enabled"] = enabled ? "true" : "false";
        }

        for (var i = 0; i < schedules.Length; i++)
        {
            values[$"Scheduling:Schedules:{i}:Name"] = schedules[i].Name;
            values[$"Scheduling:Schedules:{i}:Cron"] = schedules[i].Cron;
            values[$"Scheduling:Schedules:{i}:Command"] = schedules[i].Command;
            values[$"Scheduling:Schedules:{i}:Module"] = schedules[i].Module;
            values[$"Scheduling:Schedules:{i}:TimeZone"] = schedules[i].TimeZone;
            values[$"Scheduling:Schedules:{i}:Arguments"] = schedules[i].Arguments;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddClaudaliskPersistence(connectionString);
        services.AddClaudaliskModules(configuration, new CatalogModule());
        services.AddClaudaliskDispatching();
        services.AddClaudaliskJobs(options => options.WorkersEnabled = settings.Workers);
        services.AddClaudaliskPostgreSqlJobs();
        services.AddClaudaliskScheduling(configuration);
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(refreshes);
        return services;
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
