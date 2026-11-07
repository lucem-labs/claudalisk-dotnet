using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;

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
    public async Task AValidationFailureBecomes400WithErrorsByProperty()
    {
        await using var app = await TestApp.StartAsync();

        var (response, body) = await PostAsync(app, "/api/orders/fail/validation");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Reference is required.", body.GetProperty("errors").GetProperty("reference")[0].GetString());
    }

    [Fact]
    public async Task AConcurrencyConflictBecomes412()
    {
        await using var app = await TestApp.StartAsync();

        var (response, body) = await PostAsync(app, "/api/orders/fail/stale");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("/problems/precondition-failed", body.GetProperty("type").GetString());
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
