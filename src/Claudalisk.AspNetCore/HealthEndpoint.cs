using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace Claudalisk.AspNetCore;

public sealed record HealthResponse(string Status, string Database, string? Environment, string DeployedSha, string DeployedRef);

public static class HealthEndpoint
{
    public const string Path = "/api/health";

    public static RouteHandlerBuilder MapClaudaliskHealth(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Path, async (IDatabaseHealthProbe database, IConfiguration configuration, CancellationToken cancellationToken) =>
            await database.CanConnectAsync(cancellationToken)
                ? Results.Ok(new HealthResponse(
                    "ok",
                    "connected",
                    Normalize(configuration["APP_ENVIRONMENT"])?.ToLowerInvariant(),
                    Normalize(configuration["DEPLOYED_SHA"]) ?? "unknown",
                    Normalize(configuration["DEPLOYED_REF"]) ?? "unknown"))
                : Results.Problem("Database is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable))
            .WithName("GetHealth")
            .WithTags(ModuleTags.HostTag)
            .Produces<HealthResponse>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
