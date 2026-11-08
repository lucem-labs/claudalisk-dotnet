using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Claudalisk.AspNetCore.Tests;

public sealed class HostEndpointsTests
{
    [Fact]
    public async Task HealthReportsTheEnvironmentAndTheDeployedCommit()
    {
        await using var app = await TestApp.StartAsync(settings: new()
        {
            ["APP_ENVIRONMENT"] = " Dev ",
            ["DEPLOYED_SHA"] = "abc1234",
            ["DEPLOYED_REF"] = "main",
        });

        var body = await app.GetTestClient().GetFromJsonAsync<JsonElement>("/api/health", TestContext.Current.CancellationToken);

        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal("connected", body.GetProperty("database").GetString());
        Assert.Equal("dev", body.GetProperty("environment").GetString());
        Assert.Equal("abc1234", body.GetProperty("deployedSha").GetString());
        Assert.Equal("main", body.GetProperty("deployedRef").GetString());
    }

    [Fact]
    public async Task HealthDefaultsUnsetValues()
    {
        await using var app = await TestApp.StartAsync();

        var body = await app.GetTestClient().GetFromJsonAsync<JsonElement>("/api/health", TestContext.Current.CancellationToken);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("environment").ValueKind);
        Assert.Equal("unknown", body.GetProperty("deployedSha").GetString());
        Assert.Equal("unknown", body.GetProperty("deployedRef").GetString());
    }

    [Fact]
    public async Task HealthIsUnavailableWithoutADatabase()
    {
        await using var app = await TestApp.StartAsync(connected: false);

        var response = await app.GetTestClient().GetAsync("/api/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AnUnavailableDatabaseIsLoggedAtWarningWithoutAnException()
    {
        var log = new LogCollector();
        await using var app = await TestApp.StartAsync(connected: false, configureServices: services => services.AddLogging(logging => logging.AddProvider(log)));

        await app.GetTestClient().GetAsync("/api/health", TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries, entry => entry.Category == typeof(HealthEndpoint).FullName);
        Assert.Equal((LogLevel.Warning, false), (entry.Level, entry.HasException));
    }

    [Fact]
    public async Task TheOpenAPIDocumentTagsEachOperationWithItsModuleAndHealthWithHost()
    {
        await using var app = await TestApp.StartAsync();

        var document = await app.GetTestClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(["OrderBooking"], Tags(document, "/api/orders/{id}", "get"));
        Assert.Equal(["OrderBooking"], Tags(document, "/api/orders/{id}/cancel", "post"));
        Assert.Equal(["Host"], Tags(document, "/api/health", "get"));
        var tags = document.GetProperty("tags").EnumerateArray().ToDictionary(tag => tag.GetProperty("name").GetString()!, tag => tag.GetProperty("description").GetString());
        Assert.Equal("Placing, changing and cancelling orders.", tags["OrderBooking"]);
        Assert.Equal(ModuleTags.HostDescription, tags["Host"]);
    }

    [Fact]
    public async Task AnAppCanMapControllersToModulesWhileTheyStillLiveElsewhere()
    {
        await using var app = await TestApp.StartAsync(configure: options =>
            options.TagFor = controller => controller == typeof(OrdersController) ? "Billing" : null);

        var document = await app.GetTestClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(["Billing"], Tags(document, "/api/orders/{id}", "get"));
        Assert.Equal(["Host"], Tags(document, "/api/health", "get"));
    }

    [Fact]
    public async Task AnAppCanDescribeATagThatNoModuleOwnsWithoutChangingAModulesDescription()
    {
        await using var app = await TestApp.StartAsync(configure: options =>
        {
            options.TagFor = controller => controller == typeof(OrdersController) ? "Billing" : null;
            options.TagDescriptions["Billing"] = "Invoices and payments.";
            options.TagDescriptions["Host"] = "Not used.";
        });

        var document = await app.GetTestClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json", TestContext.Current.CancellationToken);
        var tags = document.GetProperty("tags").EnumerateArray().ToDictionary(tag => tag.GetProperty("name").GetString()!, tag => tag.GetProperty("description").GetString());

        Assert.Equal("Invoices and payments.", tags["Billing"]);
        Assert.Equal(ModuleTags.HostDescription, tags["Host"]);
    }

    [Fact]
    public async Task TheUnavailableHealthResponseHasTheDependencyProblemTypeAndRetryAfter()
    {
        await using var app = await TestApp.StartAsync(connected: false, configure: options => options.DefaultRetryAfter = TimeSpan.FromSeconds(45));

        var response = await app.GetTestClient().GetAsync("/api/health", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal("/problems/dependency-unavailable", body.GetProperty("type").GetString());
        Assert.Equal(503, body.GetProperty("status").GetInt32());
        Assert.Equal("45", Assert.Single(response.Headers.GetValues("Retry-After")));
    }

    [Fact]
    public async Task EveryOperationListsTheProblemTypesOfTheWebPackage()
    {
        await using var app = await TestApp.StartAsync();

        var document = await app.GetTestClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json", TestContext.Current.CancellationToken);
        var operations = document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(operation => (Name: $"{operation.Name} {path.Name}", operation.Value)))
            .ToList();

        Assert.True(operations.Count > 1);
        Assert.Contains(operations, operation => operation.Name.StartsWith("get ", StringComparison.Ordinal));
        Assert.Contains(operations, operation => operation.Name == "get /api/health");
        foreach (var (name, operation) in operations)
        {
            var responses = operation.GetProperty("responses");
            foreach (var (status, type) in new[] { ("409", "/problems/concurrency-conflict"), ("412", "/problems/precondition-failed"), ("428", "/problems/precondition-required"), ("503", "/problems/dependency-unavailable") })
            {
                Assert.True(responses.TryGetProperty(status, out var response), $"{name} lists no {status}");
                var description = response.GetProperty("description").GetString()!;
                if (description.Contains("Problem type", StringComparison.Ordinal))
                {
                    Assert.Contains(type, description, StringComparison.Ordinal);
                    var schema = response.GetProperty("content").GetProperty("application/problem+json").GetProperty("schema");
                    Assert.True(schema.GetProperty("properties").TryGetProperty("title", out _));
                }
            }
        }
    }

    [Fact]
    public async Task ADeclaredResponseIsNotOverwritten()
    {
        await using var app = await TestApp.StartAsync();

        var document = await app.GetTestClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json", TestContext.Current.CancellationToken);
        var responses = document.GetProperty("paths").GetProperty("/api/health").GetProperty("get").GetProperty("responses");

        Assert.DoesNotContain("Problem type", responses.GetProperty("503").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.True(responses.TryGetProperty("409", out _));
    }

    [Fact]
    public async Task ARepresentationCarriesLinksActionsAndAnETag()
    {
        await using var app = await TestApp.StartAsync();
        var id = Guid.NewGuid();

        var open = await app.GetTestClient().GetAsync($"/api/orders/{id}", TestContext.Current.CancellationToken);
        var body = await open.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var shipped = await app.GetTestClient().GetFromJsonAsync<JsonElement>($"/api/orders/{id}?shipped=true", TestContext.Current.CancellationToken);

        Assert.Equal("\"7\"", open.Headers.ETag?.ToString());
        Assert.Equal($"/api/orders/{id}", body.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.Equal("/api/orders", body.GetProperty("_links").GetProperty("collection").GetProperty("href").GetString());
        Assert.Equal("POST", body.GetProperty("_actions").GetProperty("cancel").GetProperty("method").GetString());
        Assert.Equal("PATCH", body.GetProperty("_actions").GetProperty("edit").GetProperty("method").GetString());
        Assert.False(shipped.GetProperty("_actions").TryGetProperty("cancel", out _));
    }

    [Fact]
    public async Task NumbersAreNumbersInTheOpenAPIDocument()
    {
        await using var app = await TestApp.StartAsync();

        var document = await app.GetTestClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json", TestContext.Current.CancellationToken);
        var version = document.GetProperty("components").GetProperty("schemas").GetProperty("OrderResponse").GetProperty("properties").GetProperty("version");

        Assert.Equal("integer", version.GetProperty("type").GetString());
    }

    private static string[] Tags(JsonElement document, string path, string method) =>
        document.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!).ToArray();
}
