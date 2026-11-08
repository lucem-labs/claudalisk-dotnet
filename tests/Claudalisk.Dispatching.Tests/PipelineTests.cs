using Claudalisk.Modules;
using Claudalisk.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Claudalisk.Dispatching.Tests;

public sealed class PipelineTests
{
    [Fact]
    public async Task StepsRunInTheirOrderWhateverTheRegistrationOrder()
    {
        await using var app = await PostgresServer.CreateAppAsync(Prepend);

        await SendAsync<Guid>(app, new PlaceOrderCommand(Guid.NewGuid(), "A-1"));

        Assert.Equal(
            [("AfterLoggingStep", false), ("AfterRetryStep", false), ("AfterUnitOfWorkStep", true)],
            app.GetRequiredService<StepTrace>().Entries);
    }

    [Fact]
    public async Task AStepAfterValidationDoesNotRunForAnInvalidMessageButAStepBeforeItDoes()
    {
        await using var app = await PostgresServer.CreateAppAsync(Prepend);

        await Assert.ThrowsAsync<MessageValidationException>(() => SendAsync<Guid>(app, new PlaceOrderCommand(Guid.NewGuid(), "")));

        Assert.Equal([("AfterLoggingStep", false)], app.GetRequiredService<StepTrace>().Entries);
    }

    [Fact]
    public void AStepWithTheRetryOrderFailsAtStartup()
    {
        var services = new ServiceCollection().AddTestApp("Host=localhost;Database=unused");
        services.AddScoped<IPipelineStep, RetrySlotStep>();

        var error = Assert.Throws<InvalidOperationException>(() => services.BuildServiceProvider().ValidateDispatching());

        Assert.Contains($"'{typeof(RetrySlotStep).FullName}' has Order {PipelineOrder.Retry}", error.Message);
    }

    [Fact]
    public async Task TheOuterPipelineRunsValidationBeforeTheInnerWork()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var scope = app.CreateAsyncScope();
        var ran = false;

        var error = await Assert.ThrowsAsync<MessageValidationException>(() => scope.ServiceProvider.GetRequiredService<IOuterPipeline>().RunAsync(
            new PlaceOrderCommand(Guid.NewGuid(), ""),
            MessageKind.Command,
            _ =>
            {
                ran = true;
                return Task.FromResult(1);
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(typeof(PlaceOrderCommand), error.MessageType);
        Assert.False(ran);
    }

    [Fact]
    public async Task TheOuterPipelineLogsButRunsNoUnitOfWorkAndNoInnerSteps()
    {
        var log = new LogCollector();
        await using var app = await PostgresServer.CreateAppAsync(services =>
        {
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(log));
            Prepend(services);
        });
        await using var scope = app.CreateAsyncScope();
        bool? active = null;

        var result = await scope.ServiceProvider.GetRequiredService<IOuterPipeline>().RunAsync(
            new PlaceOrderCommand(Guid.NewGuid(), "A-1"),
            MessageKind.Command,
            _ =>
            {
                active = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().IsActive;
                return Task.FromResult(5);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(5, result);
        Assert.False(active);
        Assert.Equal([("AfterLoggingStep", false)], app.GetRequiredService<StepTrace>().Entries);
        Assert.Empty(app.GetRequiredService<SendLog>().Sends);
        Assert.Contains(log.Entries, entry => entry.Message == "Handling Command PlaceOrderCommand");
        Assert.Contains(log.Entries, entry => entry.Message == "Handled Command PlaceOrderCommand");
    }

    [Fact]
    public async Task TheOuterPipelineRunsAnEventThroughItsValidators()
    {
        var log = new LogCollector();
        await using var app = await PostgresServer.CreateAppAsync(services =>
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(log)));
        await using var scope = app.CreateAsyncScope();
        var outer = scope.ServiceProvider.GetRequiredService<IOuterPipeline>();

        await Assert.ThrowsAsync<MessageValidationException>(
            () => outer.RunAsync(new InvoiceIssuedEvent(""), MessageKind.Event, _ => Task.FromResult(1), TestContext.Current.CancellationToken));
        var result = await outer.RunAsync(new InvoiceIssuedEvent("INV-1"), MessageKind.Event, _ => Task.FromResult(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, result);
        Assert.Contains(log.Entries, entry => entry.Message == "Handled Event InvoiceIssuedEvent");
    }

    [Fact]
    public async Task SendsFromInsideTheOuterPipelineAreNestedSends()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var scope = app.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var result = await scope.ServiceProvider.GetRequiredService<IOuterPipeline>().RunAsync(
            new InvoiceIssuedEvent("INV-1"),
            MessageKind.Event,
            token => dispatcher.SendAsync<int>(new CountOrdersQuery(), token).AsTask(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task AValidatorForABaseTypeOrAnInterfaceDoesNotRun()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        var result = await SendAsync<Guid>(app, new DerivedCommand(""));

        Assert.Equal(Guid.Empty, result);
    }

    [Fact]
    public async Task AModuleAddedAfterAddDispatchingHasItsHandlerAndValidatorUsed()
    {
        await using var app = await PostgresServer.CreateAppAsync(services =>
            services.AddClaudaliskModules(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new LateModule.LateModule()));

        var result = await SendAsync<string>(app, new LateModule.LateCommand("late"));
        var error = await Assert.ThrowsAsync<MessageValidationException>(() => SendAsync<string>(app, new LateModule.LateCommand("")));

        Assert.Equal("late", result);
        Assert.Equal(["late"], app.GetRequiredService<LateModule.LateLog>().Handled);
        Assert.Equal(["name"], error.Errors.Keys);
    }

    [Fact]
    public async Task ALateModulesHandlerAndValidatorAreDisposedWhenTheSendCompletes()
    {
        await using var app = await PostgresServer.CreateAppAsync(services =>
            services.AddClaudaliskModules(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new LateModule.LateModule()));

        await SendAsync<string>(app, new LateModule.LateDisposableCommand("late"));

        Assert.Equal(["handler", "validator"], app.GetRequiredService<LateModule.LateLog>().Disposed.Order(StringComparer.Ordinal));
    }

    private static void Prepend(IServiceCollection services)
    {
        services.Insert(0, ServiceDescriptor.Scoped<IPipelineStep, AfterUnitOfWorkStep>());
        services.Insert(0, ServiceDescriptor.Scoped<IPipelineStep, AfterRetryStep>());
        services.Insert(0, ServiceDescriptor.Scoped<IPipelineStep, AfterLoggingStep>());
    }

    private static async Task<T> SendAsync<T>(IServiceProvider app, object message)
    {
        await using var scope = app.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync<T>(message);
    }
}
