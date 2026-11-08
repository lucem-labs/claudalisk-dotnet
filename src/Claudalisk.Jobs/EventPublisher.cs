using System.Text.Json;
using Claudalisk.Domain;

namespace Claudalisk.Jobs;

internal sealed class AggregateTracker : IAggregateTracker
{
    private readonly List<AggregateRoot> _aggregates = [];

    public void Track(AggregateRoot aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (!_aggregates.Contains(aggregate))
        {
            _aggregates.Add(aggregate);
        }
    }

    public IReadOnlyList<IDomainEvent> TakeEvents()
    {
        var events = _aggregates.SelectMany(aggregate => aggregate.TakeDomainEvents()).ToArray();
        _aggregates.Clear();
        return events;
    }

    public bool HasTracked => _aggregates.Count > 0;

    public void Forget() => _aggregates.Clear();
}

internal sealed class EventPublisher(JobsRegistry registry, IJobWriter writer, AggregateTracker tracker) : IEventPublisher
{
    public async Task PublishAsync(object integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        foreach (var subscription in registry.Subscriptions.Where(subscription => subscription.EventType == integrationEvent.GetType()))
        {
            var module = registry.Find(subscription.Module)!;
            await writer.InsertAsync(
                module,
                new NewJob(subscription.Kind, JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), JobQueue.Json), new JobOptions(Lane: subscription.Lane)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task PublishTrackedAsync(CancellationToken cancellationToken)
    {
        foreach (var domainEvent in tracker.TakeEvents())
        {
            await PublishAsync(domainEvent, cancellationToken).ConfigureAwait(false);
            if (registry.Map(domainEvent) is { } integrationEvent)
            {
                await PublishAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
