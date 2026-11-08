using Claudalisk.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Claudalisk.AspNetCore;

public sealed record HealthResponse(string Status, string Database, string? Environment, string DeployedSha, string DeployedRef);

public static class HealthEndpoint
{
    public const string Path = "/api/health";

    public static RouteHandlerBuilder MapClaudaliskHealth(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Path, async (
                HttpContext httpContext,
                IDatabaseHealthProbe database,
                IConfiguration configuration,
                IOptions<ClaudaliskWebOptions> options,
                CancellationToken cancellationToken) =>
            {
                if (await database.CanConnectAsync(cancellationToken))
                {
                    return Results.Ok(new HealthResponse(
                        "ok",
                        "connected",
                        Normalize(configuration["APP_ENVIRONMENT"])?.ToLowerInvariant(),
                        Normalize(configuration["DEPLOYED_SHA"]) ?? "unknown",
                        Normalize(configuration["DEPLOYED_REF"]) ?? "unknown"));
                }

                var mapping = ProblemDetailsExceptionHandler.ProblemFor(
                    new DependencyUnavailableException("The database is unavailable."),
                    null,
                    options.Value.DefaultRetryAfter);
                foreach (var (name, value) in mapping.Headers)
                {
                    httpContext.Response.Headers[name] = value;
                }

                return Results.Problem(mapping.Problem);
            })
            .WithName("GetHealth")
            .WithTags(ModuleTags.HostTag)
            .Produces<HealthResponse>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
