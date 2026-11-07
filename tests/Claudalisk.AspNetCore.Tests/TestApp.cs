using System.Text.Json.Serialization;
using Claudalisk.Dispatching;
using Claudalisk.Domain;
using Claudalisk.Modules;
using Claudalisk.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.AspNetCore.Tests;

public sealed class OrderBookingContract;

public sealed class OrderBookingModule : IModule
{
    public Type Contract => typeof(OrderBookingContract);

    public string Description => "Placing, changing and cancelling orders.";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
    }
}

public sealed class AlreadyShipped(int shipped)
    : DomainException("already-shipped", "The order has already shipped.", DomainErrorKind.Conflict, new Dictionary<string, object?> { ["shipped"] = shipped });

public sealed class OrderNotFound() : DomainException("order-not-found", "No such order.", DomainErrorKind.NotFound);

public sealed class NotYourOrder() : DomainException("not-your-order", "This order belongs to someone else.", DomainErrorKind.Forbidden);

public sealed record OrderResponse(
    Guid Id,
    long Version,
    [property: JsonPropertyName(Hal.LinksMember)] HalLinks Links,
    [property: JsonPropertyName(Hal.ActionsMember)] HalActions Actions);

[ApiController]
[Route("api/orders")]
public sealed class OrdersController : ControllerBase
{
    [HttpGet("{id:guid}")]
    public OrderResponse Get(Guid id, bool shipped = false)
    {
        Response.SetETag(7);
        return new OrderResponse(
            id,
            7,
            Hal.Self($"/api/orders/{id}").With("collection", "/api/orders"),
            Hal.Actions().WithIf(!shipped, "cancel", $"/api/orders/{id}/cancel", "post").With("edit", $"/api/orders/{id}", "PATCH"));
    }

    [HttpPost("{id:guid}/cancel")]
    public IActionResult Cancel(Guid id) => Ok(new { version = Request.RequiredIfMatchVersion() });

    [HttpPost("fail/{kind}")]
    public IActionResult Fail(string kind) => kind switch
    {
        "conflict" => throw new AlreadyShipped(3),
        "not-found" => throw new OrderNotFound(),
        "forbidden" => throw new NotYourOrder(),
        "validation" => throw new MessageValidationException(typeof(OrdersController), new Dictionary<string, string[]> { ["reference"] = ["Reference is required."] }),
        "stale" => throw new ConcurrencyConflictException("The order changed."),
        "unavailable" => throw new DependencyUnavailableException("Secret upstream detail: api key hunter2"),
        "unavailable-for-a-while" => throw new DependencyUnavailableException("The carrier is down.", TimeSpan.FromSeconds(89.2)),
        "bug" => throw new InvalidOperationException("Secret internal detail: connection string Host=db;Password=hunter2"),
        _ => NotFound(),
    };
}

public sealed class FakeDatabase(bool connected) : IDatabaseHealthProbe
{
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken) => Task.FromResult(connected);
}

internal static class TestApp
{
    public static async Task<WebApplication> StartAsync(
        bool connected = true,
        Dictionary<string, string?>? settings = null,
        Action<ClaudaliskWebOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings ?? []);
        builder.Services.AddModules(builder.Configuration, new OrderBookingModule());
        builder.Services.AddClaudaliskWeb(configure);
        builder.Services.AddSingleton<IDatabaseHealthProbe>(new FakeDatabase(connected));
        var app = builder.Build();
        app.UseClaudaliskWeb();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }
}
