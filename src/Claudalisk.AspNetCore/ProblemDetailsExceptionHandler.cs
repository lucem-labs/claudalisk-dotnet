using System.Globalization;
using Claudalisk.Dispatching;
using Claudalisk.Domain;
using Claudalisk.Modules;
using Claudalisk.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Claudalisk.AspNetCore;

internal sealed partial class ProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetails,
    IOptions<ClaudaliskWebOptions> options,
    ILogger<ProblemDetailsExceptionHandler> logger)
    : IExceptionHandler
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var mapping = ProblemFor(exception, httpContext.RequestServices.GetService<ModuleCatalog>(), options.Value.DefaultRetryAfter, options.Value.ProblemTypeBase);
        switch (mapping.Problem.Status)
        {
            case StatusCodes.Status500InternalServerError:
                LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
                break;
            case StatusCodes.Status503ServiceUnavailable:
                LogUnavailable(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
                break;
        }

        foreach (var (name, value) in mapping.Headers)
        {
            httpContext.Response.Headers[name] = value;
        }

        httpContext.Response.StatusCode = mapping.Problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = mapping.Problem,
            Exception = exception,
        });
    }

    internal static ProblemMapping ProblemFor(Exception exception, ModuleCatalog? catalog, TimeSpan defaultRetryAfter, Uri? typeBase)
    {
        var mapping = Map(exception, catalog, defaultRetryAfter);
        mapping.Problem.Type = ProblemTypes.Resolve(mapping.Problem.Type, typeBase);
        return mapping;
    }

    private static ProblemMapping Map(Exception exception, ModuleCatalog? catalog, TimeSpan defaultRetryAfter) => exception switch
    {
        DomainException domain => Plain(DomainProblem(domain, catalog)),
        MessageValidationException validation => Plain(new ValidationProblemDetails(validation.Errors.ToDictionary(error => error.Key, error => error.Value))
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more values are not valid.",
        }),
        StaleVersionException stale => Plain(new ProblemDetails
        {
            Status = StatusCodes.Status412PreconditionFailed,
            Type = "/problems/precondition-failed",
            Title = "The resource changed since you read it.",
            Detail = stale.Message,
        }),
        ConcurrencyConflictException => Plain(new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Type = "/problems/concurrency-conflict",
            Title = "Someone else changed this resource at the same time. Try again.",
        }),
        PreconditionRequiredException required => Plain(new ProblemDetails
        {
            Status = StatusCodes.Status428PreconditionRequired,
            Type = "/problems/precondition-required",
            Title = "This write needs If-Match.",
            Detail = required.Message,
        }),
        DependencyUnavailableException unavailable => new ProblemMapping(
            new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Type = "/problems/dependency-unavailable",
                Title = "A service this request needs is unavailable. Try again later.",
            },
            new Dictionary<string, string> { ["Retry-After"] = RetryAfterSeconds(unavailable.RetryAfter ?? defaultRetryAfter) }),
        BadHttpRequestException badRequest => Plain(new ProblemDetails
        {
            Status = badRequest.StatusCode,
            Title = "The request is not valid.",
            Detail = badRequest.Message,
        }),
        _ => Plain(new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
        }),
    };

    private static ProblemMapping Plain(ProblemDetails problem) => new(problem, NoHeaders);

    private static ProblemDetails DomainProblem(DomainException exception, ModuleCatalog? catalog)
    {
        var problem = new ProblemDetails
        {
            Status = exception.Kind switch
            {
                DomainErrorKind.NotFound => StatusCodes.Status404NotFound,
                DomainErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status409Conflict,
            },
            Type = ProblemTypes.For(exception, catalog),
            Title = exception.Title,
        };
        foreach (var (key, value) in exception.Extensions)
        {
            problem.Extensions[key] = value;
        }

        return problem;
    }

    internal static string RetryAfterSeconds(TimeSpan wait) =>
        ((long)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, PathString path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A dependency was unavailable for {Method} {Path}")]
    private static partial void LogUnavailable(ILogger logger, Exception exception, string method, PathString path);
}
