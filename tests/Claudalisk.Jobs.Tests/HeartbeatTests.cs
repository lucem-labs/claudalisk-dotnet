using Claudalisk.Jobs.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Claudalisk.Jobs.Tests;

public sealed class HeartbeatTests
{
    private sealed class FirstHeartbeatFailsTransport(IJobTransport inner) : IJobTransport
    {
        private int _beats;

        public TaskCompletionSource FirstBeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondBeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> HeartbeatAsync(JobsModule module, Guid id, CancellationToken cancellationToken)
        {
            switch (Interlocked.Increment(ref _beats))
            {
                case 1:
                    FirstBeat.SetResult();
                    throw new InvalidOperationException("The connection dropped during the first heartbeat.");
                case 2:
                    var result = await inner.HeartbeatAsync(module, id, cancellationToken);
                    SecondBeat.SetResult();
                    return result;
                default:
                    return await inner.HeartbeatAsync(module, id, cancellationToken);
            }
        }

        public Task<ClaimedJob?> ClaimAsync(JobsModule module, string lane, TimeSpan staleAfter, CancellationToken cancellationToken) =>
            inner.ClaimAsync(module, lane, staleAfter, cancellationToken);

        public Task WaitForWorkAsync(JobsModule module, string lane, TimeSpan timeout, CancellationToken cancellationToken) =>
            inner.WaitForWorkAsync(module, lane, timeout, cancellationToken);

        public Task<bool> IsCancelRequestedAsync(JobsModule module, Guid id, CancellationToken cancellationToken) =>
            inner.IsCancelRequestedAsync(module, id, cancellationToken);

        public Task ReportProgressAsync(JobsModule module, Guid id, double fraction, string? step, CancellationToken cancellationToken) =>
            inner.ReportProgressAsync(module, id, fraction, step, cancellationToken);

        public Task RetryOrFailAsync(JobsModule module, Guid id, JobError error, TimeSpan? retryAfter, CancellationToken cancellationToken) =>
            inner.RetryOrFailAsync(module, id, error, retryAfter, cancellationToken);

        public Task MarkCanceledAsync(JobsModule module, Guid id, CancellationToken cancellationToken) =>
            inner.MarkCanceledAsync(module, id, cancellationToken);

        public Task RequeueAsync(JobsModule module, Guid id, CancellationToken cancellationToken) =>
            inner.RequeueAsync(module, id, cancellationToken);

        public Task<JobRecord?> GetAsync(JobsModule module, Guid id, CancellationToken cancellationToken) =>
            inner.GetAsync(module, id, cancellationToken);

        public Task<JobRecord?> RequestCancelAsync(JobsModule module, Guid id, CancellationToken cancellationToken) =>
            inner.RequestCancelAsync(module, id, cancellationToken);

        public Task<JobHistoryPage> ListAsync(JobsModule module, JobHistoryQuery query, CancellationToken cancellationToken) =>
            inner.ListAsync(module, query, cancellationToken);

        public Task<int> DeleteFinishedBeforeAsync(JobsModule module, TimeSpan retention, CancellationToken cancellationToken) =>
            inner.DeleteFinishedBeforeAsync(module, retention, cancellationToken);
    }

    [Fact(Timeout = 60_000)]
    public async Task AFailedHeartbeatDoesNotStopTheNextOnes()
    {
        var time = new FakeTimeProvider();
        FirstHeartbeatFailsTransport? created = null;
        await using var app = new ServiceCollection()
            .AddTestApp(await PostgresServer.CreateDatabaseAsync())
            .AddSingleton<TimeProvider>(time)
            .AddSingleton<IJobTransport>(provider =>
                created = new FirstHeartbeatFailsTransport(new PostgreSqlJobTransport(provider.GetRequiredService<NpgsqlDataSource>())))
            .BuildServiceProvider();
        var key = Guid.NewGuid();
        var job = await JobsTests.EnqueueAsync(app, new WaitCommand(key, 0.5));
        var signals = app.GetRequiredService<Signals>().For(key);
        var running = JobsTests.Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken);
        await signals.Started.Task;
        var beating = created!;

        time.Advance(TimeSpan.FromMilliseconds(20));
        await beating.FirstBeat.Task;
        time.Advance(TimeSpan.FromMilliseconds(20));
        await beating.SecondBeat.Task;
        signals.Release.SetResult();
        await running;

        Assert.Equal(JobStatus.Succeeded, (await JobsTests.JobAsync(app, "Orders", job.Id))!.Status);
    }
}
