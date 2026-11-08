using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Claudalisk.AspNetCore;

internal sealed class ProblemResponses(IOptions<ClaudaliskWebOptions> options) : IOpenApiOperationTransformer
{
    internal const string MediaType = "application/problem+json";

    internal static readonly IReadOnlyList<(int Status, string Type, string Description)> Declared =
    [
        (StatusCodes.Status409Conflict, "/problems/concurrency-conflict", "Someone else changed the resource at the same time, even after retries. Send the request again."),
        (StatusCodes.Status412PreconditionFailed, "/problems/precondition-failed", "The If-Match version is stale. Read the resource again."),
        (StatusCodes.Status428PreconditionRequired, "/problems/precondition-required", "The write needs an If-Match header."),
        (StatusCodes.Status503ServiceUnavailable, "/problems/dependency-unavailable", "A service the request needs is unavailable. Retry after the time in the Retry-After header."),
    ];

    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        operation.Responses ??= [];
        IOpenApiSchema? schema = null;
        foreach (var (status, type, description) in Declared)
        {
            var key = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (operation.Responses.ContainsKey(key))
            {
                continue;
            }

            schema ??= await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), null, cancellationToken).ConfigureAwait(false);
            operation.Responses[key] = new OpenApiResponse
            {
                Description = $"{description} Problem type: {ProblemTypes.Resolve(type, options.Value.ProblemTypeBase)}",
                Content = new Dictionary<string, OpenApiMediaType> { [MediaType] = new OpenApiMediaType { Schema = schema } },
            };
        }
    }
}
