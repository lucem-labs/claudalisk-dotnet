using System.Text.Json.Serialization;
using Claudalisk.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Claudalisk.Jobs;

public sealed record JobProgressResponse(double? Fraction, string? Step);

public sealed record JobErrorResponse(string Type, string Title, string? Detail);

public sealed record JobResponse(
    Guid Id,
    string Module,
    string Kind,
    string Status,
    int Attempts,
    int MaxAttempts,
    JobProgressResponse Progress,
    JobErrorResponse? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    [property: JsonPropertyName(Hal.LinksMember)] HalLinks Links,
    [property: JsonPropertyName(Hal.ActionsMember)] HalActions Actions);

public static class JobsRouter
{
    public const string Tag = "Jobs";

    public static IEndpointRouteBuilder MapJobsRouter(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var jobs = endpoints.MapGroup("/api/jobs/{module}/{id:guid}").WithTags(Tag);
        jobs.MapGet("", async (string module, Guid id, JobsRegistry registry, IJobTransport transport, CancellationToken cancellationToken) =>
            registry.Find(module) is { } jobsModule && await transport.GetAsync(jobsModule, id, cancellationToken) is { } job
                ? Results.Ok(Represent(jobsModule, job))
                : NotFound(module, id))
            .WithName("GetJob")
            .Produces<JobResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        jobs.MapPost("/cancel", async (string module, Guid id, JobsRegistry registry, IJobTransport transport, CancellationToken cancellationToken) =>
        {
            if (registry.Find(module) is not { } jobsModule || await transport.GetAsync(jobsModule, id, cancellationToken) is not { } job)
            {
                return NotFound(module, id);
            }

            if (!CanCancel(job))
            {
                return Results.Problem(
                    type: "/problems/jobs/not-cancelable",
                    title: "The job can no longer be canceled.",
                    detail: $"The job is {Wire(job.Status)}.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var canceled = await transport.RequestCancelAsync(jobsModule, id, cancellationToken) ?? job;
            return Results.Ok(Represent(jobsModule, canceled));
        })
            .WithName("CancelJob")
            .Produces<JobResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return endpoints;
    }

    public static JobResponse Represent(JobsModule module, JobRecord job)
    {
        var self = $"/api/jobs/{module.Segment}/{job.Id}";
        return new JobResponse(
            job.Id,
            module.Name,
            job.Kind,
            Wire(job.Status),
            job.Attempts,
            job.MaxAttempts,
            new JobProgressResponse(job.ProgressFraction, job.ProgressStep),
            job.LastError is { } error ? new JobErrorResponse(error.Type, error.Title, error.Detail) : null,
            job.CreatedAt,
            job.StartedAt,
            job.FinishedAt,
            Hal.Self(self).WithIf(job.Status == JobStatus.Succeeded && job.ResultLink is not null, "result", job.ResultLink ?? self),
            Hal.Actions().WithIf(CanCancel(job), "cancel", $"{self}/cancel", "POST"));
    }

    public static string Wire(JobStatus status) => status.ToString().ToLowerInvariant();

    private static bool CanCancel(JobRecord job) =>
        job.Status == JobStatus.Queued || (job.Status == JobStatus.Running && !job.CancelRequested);

    private static IResult NotFound(string module, Guid id) =>
        Results.Problem(
            type: "/problems/jobs/job-not-found",
            title: "No such job.",
            detail: $"Module '{module}' has no job {id}.",
            statusCode: StatusCodes.Status404NotFound);
}
