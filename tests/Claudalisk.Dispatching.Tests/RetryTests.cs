using Claudalisk.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Claudalisk.Dispatching.Tests;

public sealed class RetryTests
{
    [Fact]
    public async Task ACommandThatLosesTheRaceOnceSucceedsOnTheSecondAttemptInAnotherScope()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var id = Guid.NewGuid();

        var result = await SendAsync<Guid>(app, new FlakyCommand(id, FailAttempts: 1));

        Assert.Equal(id, result);
        var attempts = app.GetRequiredService<AttemptLog>().ScopeIds;
        Assert.Equal(2, attempts.Count);
        Assert.NotEqual(attempts[0], attempts[1]);
        Assert.Equal(1, await SendAsync<int>(app, new CountOrdersQuery()));
    }

    [Fact]
    public async Task ACommandThatAlwaysConflictsFailsAfterExactlyTheConfiguredAttempts()
    {
        await using var app = await PostgresServer.CreateAppAsync(services => services.AddDispatching(options => options.ConcurrencyAttempts = 4));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => SendAsync<Guid>(app, new FlakyCommand(Guid.NewGuid(), int.MaxValue)));

        Assert.Equal(4, app.GetRequiredService<AttemptLog>().ScopeIds.Count);
        Assert.Equal(0, await SendAsync<int>(app, new CountOrdersQuery()));
    }

    [Fact]
    public async Task OneAttemptMeansNoRetry()
    {
        await using var app = await PostgresServer.CreateAppAsync(services => services.AddDispatching(options => options.ConcurrencyAttempts = 1));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => SendAsync<Guid>(app, new FlakyCommand(Guid.NewGuid(), 1)));

        Assert.Single(app.GetRequiredService<AttemptLog>().ScopeIds);
    }

    [Fact]
    public async Task AQueryIsNeverRetried()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => SendAsync<int>(app, new ConflictingQuery()));

        Assert.Single(app.GetRequiredService<AttemptLog>().ScopeIds);
    }

    [Fact]
    public async Task ACommandSentInsideAUnitOfWorkJoinsItsScopeWithoutRetry()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var scope = app.CreateAsyncScope();
        var callerScope = scope.ServiceProvider.GetRequiredService<ScopeMarker>().Id;
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RunAsync<Guid>(
            token => dispatcher.SendAsync<Guid>(new FlakyCommand(Guid.NewGuid(), int.MaxValue), token),
            TestContext.Current.CancellationToken));

        Assert.Equal([callerScope], app.GetRequiredService<AttemptLog>().ScopeIds);
    }

    [Fact]
    public async Task ANestedCommandIsNotRetriedOnItsOwn()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => SendAsync<Guid>(app, new RelayCommand(Guid.NewGuid())));

        Assert.Equal(3, app.GetRequiredService<AttemptLog>().ScopeIds.Count);
    }

    [Fact]
    public async Task ZeroAttemptsIsRejected()
    {
        await using var app = await PostgresServer.CreateAppAsync(services => services.AddDispatching(options => options.ConcurrencyAttempts = 0));

        var error = Assert.Throws<OptionsValidationException>(() => app.GetRequiredService<IOptions<DispatchingOptions>>().Value);

        Assert.Contains("ConcurrencyAttempts", error.Message);
    }

    [Fact]
    public async Task ParallelSendsInAHandlerAreRejected()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync<int>(app, new FanOutQuery()));

        Assert.Contains("one at a time", error.Message);
    }

    [Fact]
    public async Task OfTwoParallelTopLevelSendsExactlyOneRuns()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var scope = app.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var gate = app.GetRequiredService<Gate>();
        using var start = new Barrier(2);
        Task<int> Send() => Task.Run(
            () =>
            {
                start.SignalAndWait(TimeSpan.FromSeconds(30));
                return dispatcher.SendAsync<int>(new GateQuery());
            },
            TestContext.Current.CancellationToken);

        var first = Send();
        var second = Send();
        var rejected = await Task.WhenAny(first, second).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => rejected);
        gate.Release.SetResult();

        Assert.Contains("one at a time", error.Message);
        var winner = ReferenceEquals(rejected, first) ? second : first;
        Assert.Equal(1, await winner.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(1, gate.Entries);
    }

    [Fact]
    public async Task AParentThatEndsBeforeItsNestedSendDoesNotWedgeTheScope()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var scope = app.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var background = app.GetRequiredService<Background>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.SendAsync<int>(new AbandonQuery(), TestContext.Current.CancellationToken));
        app.GetRequiredService<Gate>().Release.SetResult();
        Assert.Equal(1, await background.Task!.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.Equal(0, await dispatcher.SendAsync<int>(new CountOrdersQuery(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABackgroundTaskMaySendAfterTheSendThatStartedItFinished()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var scope = app.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var background = app.GetRequiredService<Background>();

        await dispatcher.SendAsync<int>(new BackgroundQuery(), TestContext.Current.CancellationToken);
        background.Resume.SetResult();

        Assert.Equal(0, await background.Task!.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAttemptScopeThatFailsToDisposeDoesNotFailTheCommandOrAbortTheRetry()
    {
        var log = new LogCollector();
        await using var app = await PostgresServer.CreateAppAsync(services =>
            services.AddLogging(logging => logging.AddProvider(log)));
        app.GetRequiredService<AttemptLog>().ThrowOnDispose = true;
        var id = Guid.NewGuid();

        var result = await SendAsync<Guid>(app, new FlakyCommand(id, FailAttempts: 1));

        Assert.Equal(id, result);
        Assert.Equal(2, app.GetRequiredService<AttemptLog>().ScopeIds.Count);
        var warnings = log.Entries.Where(entry => entry.Level == LogLevel.Warning && entry.Category == typeof(IDispatcher).FullName).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.All(warnings, warning => Assert.True(warning.HasException));
    }

    [Fact]
    public async Task AStepWithTheRetryOrderIsRejectedWhenTheDispatcherIsCreated()
    {
        await using var app = await PostgresServer.CreateAppAsync(services => services.AddScoped<IPipelineStep, RetrySlotStep>());
        await using var scope = app.CreateAsyncScope();

        var error = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IDispatcher>());

        Assert.Contains($"'{typeof(RetrySlotStep).FullName}' has Order {PipelineOrder.Retry}", error.Message);
    }

    private static async Task<T> SendAsync<T>(IServiceProvider app, object message)
    {
        await using var scope = app.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync<T>(message);
    }
}
