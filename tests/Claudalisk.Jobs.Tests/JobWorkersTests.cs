using Claudalisk.Dispatching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Claudalisk.Jobs.Tests;

public sealed class JobWorkersTests
{
    private sealed class FailingTransport : IJobTransport
    {
        public TaskCompletionSource SecondClaim { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstClaim { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Pruned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _claims;

        public Task<ClaimedJob?> ClaimAsync(JobsModule module, string lane, TimeSpan staleAfter, CancellationToken cancellationToken)
        {
            var claims = Interlocked.Increment(ref _claims);
            if (claims == 1)
            {
                FirstClaim.SetResult();
            }

            if (claims == 2)
            {
                SecondClaim.SetResult();
            }

            throw new InvalidOperationException("The database is not there.");
        }

        public Task WaitForWorkAsync(JobsModule module, string lane, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new ObjectDisposedException(nameof(FailingTransport));

        public Task<bool> HeartbeatAsync(JobsModule module, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReportProgressAsync(JobsModule module, Guid id, double fraction, string? step, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RetryOrFailAsync(JobsModule module, Guid id, JobError error, TimeSpan? retryAfter, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MarkCanceledAsync(JobsModule module, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RequeueAsync(JobsModule module, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobRecord?> GetAsync(JobsModule module, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobRecord?> RequestCancelAsync(JobsModule module, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> IsCancelRequestedAsync(JobsModule module, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobHistoryPage> ListAsync(JobsModule module, JobHistoryQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> DeleteFinishedBeforeAsync(JobsModule module, TimeSpan retention, CancellationToken cancellationToken)
        {
            Pruned.TrySetResult();
            return Task.FromResult(0);
        }
    }

    private sealed class FailingAtShutdownTransport : IJobTransport
    {
        public TaskCompletionSource Claimed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ClaimedJob?> ClaimAsync(JobsModule module, string lane, TimeSpan staleAfter, CancellationToken cancellationToken)
        {
            var stopped = new TaskCompletionSource();
            await using var registration = cancellationToken.Register(stopped.SetResult);
            Claimed.TrySetResult();
            await stopped.Task;
            throw new InvalidOperationException("The connection closed while the host stopped.");
        }

        public Task WaitForWorkAsync(JobsModule module, string lane, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> HeartbeatAsync(JobsModule module, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReportProgressAsync(JobsModule module, Guid id, double fraction, string? step, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RetryOrFailAsync(JobsModule module, Guid id, JobError error, TimeSpan? retryAfter, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MarkCanceledAsync(JobsModule module, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RequeueAsync(JobsModule module, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobRecord?> GetAsync(JobsModule module, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobRecord?> RequestCancelAsync(JobsModule module, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> IsCancelRequestedAsync(JobsModule module, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobHistoryPage> ListAsync(JobsModule module, JobHistoryQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> DeleteFinishedBeforeAsync(JobsModule module, TimeSpan retention, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact(Timeout = 60_000)]
    public async Task AWorkerWhoseTransportKeepsFailingRetriesAfterThePollIntervalAndStopsCleanly()
    {
        var transport = new FailingTransport();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDispatching();
        services.AddSingleton<IJobTransport>(transport);
        services.AddJobs(options => options.PollInterval = TimeSpan.FromMilliseconds(20));
        services.AddModuleJobs("Orders", "orders", _ => { });
        await using var provider = services.BuildServiceProvider();
        var workers = provider.GetServices<IHostedService>().OfType<BackgroundService>().Single();

        await workers.StartAsync(TestContext.Current.CancellationToken);
        await Task.WhenAny(transport.SecondClaim.Task, workers.ExecuteTask!);
        await workers.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(workers.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheWorkersPruneFinishedJobsWhenTheClockPassesThePruneInterval()
    {
        var transport = new FailingTransport();
        var time = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddDispatching();
        services.AddSingleton<IJobTransport>(transport);
        services.AddJobs(options => options.PruneInterval = TimeSpan.FromHours(1));
        services.AddModuleJobs("Orders", "orders", _ => { });
        await using var provider = services.BuildServiceProvider();
        var workers = provider.GetServices<IHostedService>().OfType<BackgroundService>().Single();

        await workers.StartAsync(TestContext.Current.CancellationToken);
        await transport.FirstClaim.Task;
        Assert.False(transport.Pruned.Task.IsCompleted);
        time.Advance(TimeSpan.FromHours(1));
        await transport.Pruned.Task;
        await workers.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(workers.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact(Timeout = 60_000)]
    public async Task AWorkerWhoseClaimFailsAfterTheHostStartsStoppingStopsCleanly()
    {
        var transport = new FailingAtShutdownTransport();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDispatching();
        services.AddSingleton<IJobTransport>(transport);
        services.AddJobs();
        services.AddModuleJobs("Orders", "orders", _ => { });
        await using var provider = services.BuildServiceProvider();
        var workers = provider.GetServices<IHostedService>().OfType<BackgroundService>().Single();

        await workers.StartAsync(TestContext.Current.CancellationToken);
        await transport.Claimed.Task;
        await workers.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(workers.ExecuteTask!.IsCompletedSuccessfully);
    }
}
