using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Claudalisk.Dispatching.Tests;

public sealed class DispatcherTests
{
    [Fact]
    public async Task ACommandAcrossTwoModulesCommitsBoth()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = Guid.NewGuid();

        await SendAsync<Guid>(app, new CheckOutCommand(orderId, "A-1", 10m, FailAfterBoth: false));

        Assert.Equal((1, 1), await CountAsync(app));
    }

    [Fact]
    public async Task ADatabaseErrorInTheSecondModuleRollsBackTheFirst()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        await Assert.ThrowsAsync<DbUpdateException>(() => SendAsync<Guid>(app, new CheckOutCommand(Guid.NewGuid(), "A-1", -5m, FailAfterBoth: false)));

        Assert.Equal((0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task TheUnitOfWorkStepOfTheOuterCommandRollsBackItsNestedSends()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync<Guid>(app, new CheckOutCommand(Guid.NewGuid(), "A-1", 10m, FailAfterBoth: true)));

        Assert.Equal((0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task AQueryRunsWithoutAUnitOfWork()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync<int>(app, new PeekOrdersQuery(Guid.NewGuid())));

        Assert.Equal((1, 0), await CountAsync(app));
    }

    [Fact]
    public async Task AnInvalidMessageFailsValidationBeforeItsHandlerOrAnyStepInsideRuns()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        var error = await Assert.ThrowsAsync<MessageValidationException>(() => SendAsync<Guid>(app, new PlaceOrderCommand(Guid.NewGuid(), "")));

        Assert.Equal(typeof(PlaceOrderCommand), error.MessageType);
        Assert.Equal(["reference"], error.Errors.Keys);
        Assert.Empty(app.GetRequiredService<SendLog>().Sends);
        Assert.Equal((0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task ANestedInvalidMessageRollsBackTheOuterCommand()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        var error = await Assert.ThrowsAsync<MessageValidationException>(
            () => SendAsync<Guid>(app, new CheckOutCommand(Guid.NewGuid(), "far-too-long-reference", 10m, FailAfterBoth: false)));

        Assert.Equal(typeof(PlaceOrderCommand), error.MessageType);
        Assert.Equal((0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task AnUntypedSendRunsTheSamePipelineAndReturnsTheHandlersResult()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = Guid.NewGuid();
        await using (var scope = app.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync((object)new PlaceOrderCommand(orderId, "A-1"), TestContext.Current.CancellationToken);

            Assert.Equal(orderId, result);
        }

        Assert.Equal(["PlaceOrderCommand"], app.GetRequiredService<SendLog>().Sends);
        Assert.Equal((1, 0), await CountAsync(app));
    }

    [Fact]
    public async Task SendingForTheWrongResultTypeNamesBothTypes()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync<string>(app, new PlaceOrderCommand(Guid.NewGuid(), "A-1")));

        Assert.Contains(typeof(string).FullName!, error.Message);
        Assert.Contains(typeof(Guid).FullName!, error.Message);
        Assert.Equal((0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task AMessageThatIsNeitherACommandNorAQueryCannotBeSent()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync<Guid>(app, new OrderPlaced()));

        Assert.Contains("Command or Query", error.Message);
    }

    [Fact]
    public async Task HandlersAreResolvedFromTheCallersScope()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var scope = app.CreateAsyncScope();

        var handled = await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync<Guid>(new WhichScopeQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(scope.ServiceProvider.GetRequiredService<ScopeMarker>().Id, handled);
    }

    [Fact]
    public async Task SendsInOneScopeRunOneAtATime()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var scope = app.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.WhenAll(dispatcher.SendAsync<int>(new CountOrdersQuery(), TestContext.Current.CancellationToken), dispatcher.SendAsync<int>(new CountChargesQuery(), TestContext.Current.CancellationToken)));

        Assert.Contains("one at a time", error.Message);
    }

    [Fact]
    public async Task AppStepsRunInsideTheBuiltInStepsForEverySend()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        await SendAsync<Guid>(app, new CheckOutCommand(Guid.NewGuid(), "A-1", 10m, FailAfterBoth: false));

        Assert.Equal(["CheckOutCommand", "PlaceOrderCommand", "ChargeOrderCommand"], app.GetRequiredService<SendLog>().Sends);
    }

    [Fact]
    public async Task TheLoggingStepLogsEachSendAndItsFailure()
    {
        var log = new LogCollector();
        await using var app = await PostgresServer.CreateAppAsync(services =>
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(log)));

        await SendAsync<Guid>(app, new WhichScopeQuery());
        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync<int>(app, new PeekOrdersQuery(Guid.NewGuid())));

        Assert.Equal(
            [
                (LogLevel.Debug, "Handling Query WhichScopeQuery", false),
                (LogLevel.Debug, "Handled Query WhichScopeQuery", false),
                (LogLevel.Debug, "Handling Query PeekOrdersQuery", false),
                (LogLevel.Information, "Query PeekOrdersQuery failed: The query failed after it wrote.", true),
            ],
            log.Entries.Where(entry => entry.Category == typeof(IDispatcher).FullName).Select(entry => (entry.Level, entry.Message, entry.HasException)));
    }

    [Fact]
    public async Task ACanceledSendLogsAtDebugWithoutTheException()
    {
        var log = new LogCollector();
        await using var app = await PostgresServer.CreateAppAsync(services =>
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(log)));
        using var cancellation = new CancellationTokenSource();
        await using var scope = app.CreateAsyncScope();
        var gate = app.GetRequiredService<Gate>();

        var send = scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync<int>(new GateQuery(), cancellation.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

        Assert.Contains(
            log.Entries,
            entry => entry.Category == typeof(IDispatcher).FullName && entry is { Level: LogLevel.Debug, HasException: false, Message: "Query GateQuery was canceled" });
        Assert.DoesNotContain(log.Entries, entry => entry.Category == typeof(IDispatcher).FullName && entry.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task AValidationFailureLogsAtInformationWithoutTheException()
    {
        var log = new LogCollector();
        await using var app = await PostgresServer.CreateAppAsync(services =>
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(log)));

        await Assert.ThrowsAsync<MessageValidationException>(() => SendAsync<Guid>(app, new PlaceOrderCommand(Guid.NewGuid(), "")));

        var rejected = Assert.Single(log.Entries, entry => entry.Category == typeof(IDispatcher).FullName && entry.Level == LogLevel.Information);
        Assert.False(rejected.HasException);
        Assert.StartsWith("Command PlaceOrderCommand was rejected: ", rejected.Message);
    }

    private sealed record OrderPlaced;

    private static async Task<T> SendAsync<T>(IServiceProvider app, object message)
    {
        await using var scope = app.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync<T>(message);
    }

    private static async Task<(int Orders, int Charges)> CountAsync(IServiceProvider app) =>
        (await SendAsync<int>(app, new CountOrdersQuery()), await SendAsync<int>(app, new CountChargesQuery()));
}
