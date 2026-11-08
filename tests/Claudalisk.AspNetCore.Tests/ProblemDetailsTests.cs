using Claudalisk.Modules;
using Claudalisk.Dispatching;
using Claudalisk.Domain;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Claudalisk.AspNetCore.Tests;

public sealed class ProblemDetailsTests
{
    [Theory]
    [InlineData("conflict", HttpStatusCode.Conflict, "/problems/order-booking/already-shipped", "The order has already shipped.")]
    [InlineData("not-found", HttpStatusCode.NotFound, "/problems/order-booking/order-not-found", "No such order.")]
    [InlineData("forbidden", HttpStatusCode.Forbidden, "/problems/order-booking/not-your-order", "This order belongs to someone else.")]
    public async Task ADomainErrorBecomesItsKindsStatusWithAModuleType(string kind, HttpStatusCode status, string type, string title)
    {
        await using var app = await TestApp.StartAsync();

        var (response, body) = await PostAsync(app, $"/api/orders/fail/{kind}");

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(type, body.GetProperty("type").GetString());
        Assert.Equal(title, body.GetProperty("title").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task ADomainErrorCarriesItsExtensionMembers()
    {
        await using var app = await TestApp.StartAsync();

        var (_, body) = await PostAsync(app, "/api/orders/fail/conflict");

        Assert.Equal(3, body.GetProperty("shipped").GetInt32());
    }

    [Fact]
    public async Task ADomainErrorHasNoDetailThatRepeatsItsTitle()
    {
        await using var app = await TestApp.StartAsync();

        var (_, body) = await PostAsync(app, "/api/orders/fail/conflict");

        Assert.False(body.TryGetProperty("detail", out _));
    }

    [Theory]
    [InlineData("https://errors.example.test/docs/")]
    [InlineData("https://errors.example.test/docs")]
    public async Task TheProblemTypeBaseMakesEveryTypeAbsolute(string typeBase)
    {
        await using var app = await TestApp.StartAsync(configure: options => options.ProblemTypeBase = new Uri(typeBase));

        var (_, domain) = await PostAsync(app, "/api/orders/fail/conflict");
        var (_, unavailable) = await PostAsync(app, "/api/orders/fail/unavailable");

        Assert.Equal("https://errors.example.test/docs/problems/order-booking/already-shipped", domain.GetProperty("type").GetString());
        Assert.Equal("https://errors.example.test/docs/problems/dependency-unavailable", unavailable.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("ftp://errors.example.test/")]
    [InlineData("https://errors.example.test/?a=1")]
    public async Task AnUnusableProblemTypeBaseFailsAtStartup(string typeBase)
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(
            () => TestApp.StartAsync(configure: options => options.ProblemTypeBase = new Uri(typeBase, UriKind.RelativeOrAbsolute)));

        Assert.Contains("ProblemTypeBase", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidationFailureBecomes400WithErrorsByProperty()
    {
        await using var app = await TestApp.StartAsync();

        var (response, body) = await PostAsync(app, "/api/orders/fail/validation");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Reference is required.", body.GetProperty("errors").GetProperty("reference")[0].GetString());
    }

    [Fact]
    public async Task AStaleVersionBecomes412()
    {
        await using var app = await TestApp.StartAsync();

        var (response, body) = await PostAsync(app, "/api/orders/fail/stale");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("/problems/precondition-failed", body.GetProperty("type").GetString());
        Assert.Equal("The resource changed since you read it.", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task AConcurrencyConflictBecomes409()
    {
        await using var app = await TestApp.StartAsync();

        var (response, body) = await PostAsync(app, "/api/orders/fail/race");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("/problems/concurrency-conflict", body.GetProperty("type").GetString());
        Assert.Equal(409, body.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task AStaleIfMatchThroughTheDispatcherBecomes412AndTheHandlerRunsOnce()
    {
        await using var app = await TestApp.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/orders/{Guid.NewGuid()}");
        request.Headers.TryAddWithoutValidation("If-Match", "\"7\"");

        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("/problems/precondition-failed", body.GetProperty("type").GetString());
        Assert.Equal(1, app.Services.GetRequiredService<HandlerRuns>().Revisions);
    }

    [Fact]
    public async Task ACurrentIfMatchThroughTheDispatcherSucceeds()
    {
        await using var app = await TestApp.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/orders/{Guid.NewGuid()}");
        request.Headers.TryAddWithoutValidation("If-Match", "\"8\"");

        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ARaceLostOnEveryAttemptBecomes409AfterExactlyTheConfiguredAttempts()
    {
        await using var app = await TestApp.StartAsync(configureServices: services => services.AddDispatching(options => options.ConcurrencyAttempts = 4));

        var (response, body) = await PostAsync(app, $"/api/orders/{Guid.NewGuid()}/contest");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("/problems/concurrency-conflict", body.GetProperty("type").GetString());
        Assert.Equal(4, app.Services.GetRequiredService<HandlerRuns>().Contests);
    }

    [Theory]
    [InlineData("unavailable", "30")]
    [InlineData("unavailable-for-a-while", "90")]
    public async Task AnUnavailableDependencyBecomes503WithRetryAfter(string kind, string retryAfter)
    {
        await using var app = await TestApp.StartAsync();

        var (response, body) = await PostAsync(app, $"/api/orders/fail/{kind}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("/problems/dependency-unavailable", body.GetProperty("type").GetString());
        Assert.Equal(503, body.GetProperty("status").GetInt32());
        Assert.Equal(retryAfter, Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.DoesNotContain("hunter2", body.GetRawText(), StringComparison.Ordinal);
        Assert.False(body.TryGetProperty("detail", out _));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(0.001, "1")]
    [InlineData(60, "60")]
    public void RetryAfterIsTheWaitInWholeSecondsRoundedUp(double seconds, string retryAfter)
    {
        Assert.Equal(retryAfter, ProblemDetailsExceptionHandler.RetryAfterSeconds(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task TheRetryAfterComesFromTheOptionsWhenTheExceptionCarriesNone()
    {
        await using var app = await TestApp.StartAsync(configure: options => options.DefaultRetryAfter = TimeSpan.FromSeconds(120));

        var (response, _) = await PostAsync(app, "/api/orders/fail/unavailable");

        Assert.Equal("120", Assert.Single(response.Headers.GetValues("Retry-After")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ANonPositiveDefaultRetryAfterFailsAtStartup(int seconds)
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(
            () => TestApp.StartAsync(configure: options => options.DefaultRetryAfter = TimeSpan.FromSeconds(seconds)));

        Assert.Contains("DefaultRetryAfter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMappingCarriesTheRetryAfterOfTheException()
    {
        var withWait = ProblemDetailsExceptionHandler.ProblemFor(new DependencyUnavailableException("down", TimeSpan.FromSeconds(89.2)), null, TimeSpan.FromSeconds(30), null);
        var withoutWait = ProblemDetailsExceptionHandler.ProblemFor(new DependencyUnavailableException("down"), null, TimeSpan.FromSeconds(30), null);
        var other = ProblemDetailsExceptionHandler.ProblemFor(new InvalidOperationException("bug"), null, TimeSpan.FromSeconds(30), null);

        Assert.Equal(503, withWait.Problem.Status);
        Assert.Equal("90", withWait.Headers["Retry-After"]);
        Assert.Equal("30", withoutWait.Headers["Retry-After"]);
        Assert.Empty(other.Headers);
    }

    [Fact]
    public async Task AnUnavailableDependencyIsLoggedAtWarningWithTheException()
    {
        var log = new LogCollector();
        await using var app = await TestApp.StartAsync(configureServices: services => services.AddLogging(logging => logging.AddProvider(log)));

        await PostAsync(app, "/api/orders/fail/unavailable");

        var entry = Assert.Single(log.Entries, entry => entry.Category == typeof(ProblemDetailsExceptionHandler).FullName);
        Assert.Equal((LogLevel.Warning, true), (entry.Level, entry.HasException));
    }

    [Fact]
    public async Task AnUnexpectedErrorIsLoggedAtError()
    {
        var log = new LogCollector();
        await using var app = await TestApp.StartAsync(configureServices: services => services.AddLogging(logging => logging.AddProvider(log)));

        await PostAsync(app, "/api/orders/fail/bug");

        var entry = Assert.Single(log.Entries, entry => entry.Category == typeof(ProblemDetailsExceptionHandler).FullName);
        Assert.Equal((LogLevel.Error, true), (entry.Level, entry.HasException));
    }

    [Fact]
    public async Task AMalformedIfMatchIsNotEchoedInTheProblem()
    {
        await using var app = await TestApp.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{Guid.NewGuid()}/cancel");
        request.Headers.TryAddWithoutValidation("If-Match", "\"zebra-4711\"");

        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("zebra-4711", text, StringComparison.Ordinal);
        Assert.Contains("If-Match must be one strong version tag", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyOtherExceptionBecomes500WithoutInternalDetails()
    {
        await using var app = await TestApp.StartAsync();

        var (response, body) = await PostAsync(app, "/api/orders/fail/bug");
        var text = body.GetRawText();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("An unexpected error occurred.", body.GetProperty("title").GetString());
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("InvalidOperationException", text);
        Assert.False(body.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task AWriteWithoutIfMatchBecomes428()
    {
        await using var app = await TestApp.StartAsync();

        var (response, body) = await PostAsync(app, $"/api/orders/{Guid.NewGuid()}/cancel");

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Equal("/problems/precondition-required", body.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("\"7\"", HttpStatusCode.OK)]
    [InlineData("W/\"7\"", HttpStatusCode.BadRequest)]
    [InlineData("\"seven\"", HttpStatusCode.BadRequest)]
    [InlineData("\"0\"", HttpStatusCode.BadRequest)]
    public async Task IfMatchMustBeOneStrongVersion(string ifMatch, HttpStatusCode status)
    {
        await using var app = await TestApp.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{Guid.NewGuid()}/cancel");
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(status, response.StatusCode);
        if (status == HttpStatusCode.OK)
        {
            Assert.Equal(7, (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("version").GetInt64());
        }
    }

    [Theory]
    [InlineData("Orders", "orders")]
    [InlineData("OrderBooking", "order-booking")]
    [InlineData("HTTPClients", "http-clients")]
    [InlineData("Cards", "cards")]
    public void ModuleNamesBecomeKebabCaseInTheType(string module, string kebab)
    {
        Assert.Equal(kebab, ModuleNames.Kebab(module));
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> PostAsync(Microsoft.AspNetCore.Builder.WebApplication app, string path)
    {
        var response = await app.GetTestClient().PostAsync(path, null, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken));
    }
}
