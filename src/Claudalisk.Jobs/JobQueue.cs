using System.Text.Json;
using Claudalisk.Dispatching;

namespace Claudalisk.Jobs;

internal sealed class JobQueue(JobsRegistry registry, IJobWriter writer) : IJobQueue
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<JobReference> EnqueueAsync(object command, JobOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        options ??= new JobOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1);
        var module = registry.ModuleFor(command.GetType());
        var kind = module.KindFor(command.GetType())!;
        if (kind.Lane is { } fixedLane && options.Lane != fixedLane)
        {
            options = options.Lane == JobOptions.DefaultLane
                ? options with { Lane = fixedLane }
                : throw new InvalidOperationException($"Job kind '{kind.Name}' of module '{module.Name}' runs only on lane '{fixedLane}'.");
        }

        if (!module.Lanes.ContainsKey(options.Lane))
        {
            throw new InvalidOperationException($"Module '{module.Name}' has no job lane '{options.Lane}'.");
        }

        var id = await writer.InsertAsync(module, new NewJob(kind.Name, JsonSerializer.Serialize(command, command.GetType(), Json), options), cancellationToken).ConfigureAwait(false);
        return Reference(module, id);
    }

    internal static JobReference Reference(JobsModule module, Guid id) => new(module.Name, id, $"/api/jobs/{module.Segment}/{id}");
}
