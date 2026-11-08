using Claudalisk.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using static Claudalisk.Jobs.Tests.JobsTests;

namespace Claudalisk.Jobs.Tests;

public sealed class OuterStepsTests
{
    private sealed class Messages : ILoggerProvider, ILogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
            {
                _lines.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public async Task ABatchCommandWithInvalidInputFailsWithoutRetryAndRunsNoBatch()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new ImportOrdersCommand(Total: 0, CrashWhenDone: -1));

        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));

        var failed = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Failed, 1, "/problems/jobs/invalid", null), (failed.Status, failed.Attempts, failed.LastError!.Type, failed.Checkpoint));
        Assert.Equal(0L, await CountAsync(app, "orders.orders"));
    }

    [Fact]
    public async Task AValidBatchCommandIsLoggedByTheLoggingStepOnceForTheWholeRun()
    {
        var messages = new Messages();
        await using var app = new ServiceCollection()
            .AddTestApp(await PostgresServer.CreateDatabaseAsync())
            .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(messages))
            .BuildServiceProvider();
        await EnqueueAsync(app, new ImportOrdersCommand(Total: 5, CrashWhenDone: -1));

        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Single(messages.Lines, line => line == "Handling Command ImportOrdersCommand");
        Assert.Single(messages.Lines, line => line == "Handled Command ImportOrdersCommand");
    }

    [Fact]
    public async Task AnEventThatFailsValidationIsNotRetriedAndItsHandlerDoesNotRun()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await PublishAsync(app, new OrderCanceled(Guid.Empty));

        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Billing", "refunds", TestContext.Current.CancellationToken));

        Assert.Equal(1L, await CountAsync(app, "billing.jobs WHERE status = 'failed' AND attempts = 1 AND last_error->>'type' = '/problems/jobs/invalid'"));
        Assert.Equal(0L, await CountAsync(app, "billing.refunds"));
    }

    [Fact]
    public async Task ARebuildCommandWithInvalidInputFailsWithoutRetryAndChangesNothing()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new RebuildRefundsCommand(Guid.Empty));

        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Billing", "refunds", TestContext.Current.CancellationToken));

        var failed = (await JobAsync(app, "Billing", job.Id))!;
        Assert.Equal((JobStatus.Failed, 1, "/problems/jobs/invalid"), (failed.Status, failed.Attempts, failed.LastError!.Type));
    }

    private static async Task PublishAsync(IServiceProvider app, object integrationEvent)
    {
        await using var scope = app.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .RunAsync(token => scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(integrationEvent, token), TestContext.Current.CancellationToken);
    }

    private static async Task<long> CountAsync(IServiceProvider app, string from)
    {
        await using var connection = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {from}", connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
