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

namespace Claudalisk.AspNetCore;

internal sealed partial class ProblemDetailsExceptionHandler(IProblemDetailsService problemDetails, ILogger<ProblemDetailsExceptionHandler> logger)
    : IExceptionHandler
{
    internal static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problem = ProblemFor(exception, httpContext.RequestServices.GetService<ModuleCatalog>());
        if (problem.Status == StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }

        if (exception is DependencyUnavailableException unavailable)
        {
            httpContext.Response.Headers.RetryAfter = RetryAfterSeconds(unavailable.RetryAfter ?? DefaultRetryAfter);
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    internal static ProblemDetails ProblemFor(Exception exception, ModuleCatalog? catalog) => exception switch
    {
        DomainException domain => DomainProblem(domain, catalog),
        MessageValidationException validation => new ValidationProblemDetails(validation.Errors.ToDictionary(error => error.Key, error => error.Value))
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more values are not valid.",
        },
        StaleVersionException stale => new ProblemDetails
        {
            Status = StatusCodes.Status412PreconditionFailed,
            Type = "/problems/precondition-failed",
            Title = "The resource changed since you read it.",
            Detail = stale.Message,
        },
        ConcurrencyConflictException => new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Type = "/problems/concurrency-conflict",
            Title = "Someone else changed this resource at the same time. Try again.",
        },
        PreconditionRequiredException required => new ProblemDetails
        {
            Status = StatusCodes.Status428PreconditionRequired,
            Type = "/problems/precondition-required",
            Title = "This write needs If-Match.",
            Detail = required.Message,
        },
        DependencyUnavailableException => new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Type = "/problems/dependency-unavailable",
            Title = "A service this request needs is unavailable. Try again later.",
        },
        BadHttpRequestException badRequest => new ProblemDetails
        {
            Status = badRequest.StatusCode,
            Title = "The request is not valid.",
            Detail = badRequest.Message,
        },
        _ => new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
        },
    };

    private static ProblemDetails DomainProblem(DomainException exception, ModuleCatalog? catalog)
    {
        var module = ModuleNames.ModuleOf(exception.GetType(), catalog);
        var problem = new ProblemDetails
        {
            Status = exception.Kind switch
            {
                DomainErrorKind.NotFound => StatusCodes.Status404NotFound,
                DomainErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status409Conflict,
            },
            Type = module is null ? $"/problems/{exception.Slug}" : $"/problems/{ModuleNames.Kebab(module)}/{exception.Slug}",
            Title = exception.Title,
            Detail = exception.Title,
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
}
