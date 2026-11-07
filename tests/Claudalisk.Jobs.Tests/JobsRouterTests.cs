using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Claudalisk.AspNetCore;
using Claudalisk.Dispatching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Jobs.Tests;

public sealed class JobsRouterTests
{
    [Fact]
    public async Task The_router_shows_a_queued_job_with_its_cancel_action()
    {
        await using var app = await StartAsync();
        var job = await JobsTests.SendAsync<JobReference>(app.Services, new PlaceOrderCommand(Guid.NewGuid(), "A-1", FailAfterQueueing: false));

        var body = await app.GetTestClient().GetFromJsonAsync<JsonElement>(job.Url);

        Assert.Equal(job.Id, body.GetProperty("id").GetGuid());
        Assert.Equal("Orders", body.GetProperty("module").GetString());
        Assert.Equal("ship-order", body.GetProperty("kind").GetString());
        Assert.Equal("queued", body.GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("attempts").GetInt32());
        Assert.Equal(job.Url, body.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.False(body.GetProperty("_links").TryGetProperty("result", out _));
        Assert.Equal($"{job.Url}/cancel", body.GetProperty("_actions").GetProperty("cancel").GetProperty("href").GetString());
        Assert.Equal("POST", body.GetProperty("_actions").GetProperty("cancel").GetProperty("method").GetString());
    }

    [Fact]
    public async Task A_finished_job_links_its_result_and_can_no_longer_be_canceled()
    {
        await using var app = await StartAsync();
        var orderId = Guid.NewGuid();
        var job = await JobsTests.SendAsync<JobReference>(app.Services, new PlaceOrderCommand(orderId, "A-1", FailAfterQueueing: false));
        await JobsTests.Runner(app.Services).RunUntilIdleAsync("Orders");

        var body = await app.GetTestClient().GetFromJsonAsync<JsonElement>(job.Url);
        var cancel = await app.GetTestClient().PostAsync($"{job.Url}/cancel", null);

        Assert.Equal("succeeded", body.GetProperty("status").GetString());
        Assert.Equal(1.0, body.GetProperty("progress").GetProperty("fraction").GetDouble());
        Assert.Equal($"/api/orders/{orderId}", body.GetProperty("_links").GetProperty("result").GetProperty("href").GetString());
        Assert.False(body.GetProperty("_actions").TryGetProperty("cancel", out _));
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Equal("/problems/jobs/not-cancelable", (await cancel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Canceling_through_the_router_cancels_a_queued_job()
    {
        await using var app = await StartAsync();
        var job = await JobsTests.SendAsync<JobReference>(app.Services, new PlaceOrderCommand(Guid.NewGuid(), "A-1", FailAfterQueueing: false));

        var response = await app.GetTestClient().PostAsync($"{job.Url}/cancel", null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("canceled", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("_actions").TryGetProperty("cancel", out _));
    }

    [Fact]
    public async Task A_failed_job_shows_its_last_error_as_problem_details()
    {
        await using var app = await StartAsync();
        var job = await JobsTests.EnqueueAsync(app.Services, new StrictCommand(""));
        await JobsTests.Runner(app.Services).RunUntilIdleAsync("Orders");

        var body = await app.GetTestClient().GetFromJsonAsync<JsonElement>(job.Url);

        Assert.Equal("failed", body.GetProperty("status").GetString());
        Assert.Equal("/problems/jobs/invalid", body.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("The job's input is not valid.", body.GetProperty("error").GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("/api/jobs/unknown-module/0b0c9f5e-7c69-4b1a-9a4b-8d3f8e6a2b11")]
    [InlineData("/api/jobs/orders/0b0c9f5e-7c69-4b1a-9a4b-8d3f8e6a2b11")]
    public async Task An_unknown_module_or_job_is_404(string path)
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("/problems/jobs/job-not-found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task The_router_reads_every_module_by_its_name()
    {
        await using var app = await StartAsync();
        var job = await JobsTests.EnqueueAsync(app.Services, new FlakyCommand(0));

        var byName = await app.GetTestClient().GetAsync($"/api/jobs/Orders/{job.Id}");
        var bySegment = await app.GetTestClient().GetAsync($"/api/jobs/orders/{job.Id}");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (byName.StatusCode, bySegment.StatusCode));
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddTestApp(await PostgresServer.CreateDatabaseAsync());
        builder.Services.AddClaudaliskWeb();
        builder.Services.AddSingleton<IDatabaseHealthProbe, AlwaysUp>();
        var app = builder.Build();
        app.UseClaudaliskWeb();
        app.MapJobsRouter();
        await app.StartAsync();
        return app;
    }

    private sealed class AlwaysUp : IDatabaseHealthProbe
    {
        public Task<bool> CanConnectAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
