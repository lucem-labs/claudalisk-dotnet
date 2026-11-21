using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Claudalisk.AspNetCore;

internal sealed class ValidationProblemWriter(IOptions<JsonOptions> json, IOptions<ProblemDetailsOptions> problemOptions) : IProblemDetailsWriter
{
    private static readonly MediaTypeHeaderValue Json = new("application/json");
    private static readonly MediaTypeHeaderValue ProblemJson = new("application/problem+json");

    public bool CanWrite(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ProblemDetails is not HttpValidationProblemDetails)
        {
            return false;
        }

        var accept = context.HttpContext.Request.GetTypedHeaders().Accept;
        return accept.Count == 0 || accept.Any(type => Json.IsSubsetOf(type) || ProblemJson.IsSubsetOf(type));
    }

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var httpContext = context.HttpContext;
        var problem = context.ProblemDetails;
        problem.Status ??= httpContext.Response.StatusCode;
        if (!problem.Extensions.ContainsKey("traceId"))
        {
            problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        }

        problemOptions.Value.CustomizeProblemDetails?.Invoke(context);
        return new ValueTask(httpContext.Response.WriteAsJsonAsync(
            problem,
            problem.GetType(),
            json.Value.SerializerOptions,
            "application/problem+json",
            httpContext.RequestAborted));
    }
}
