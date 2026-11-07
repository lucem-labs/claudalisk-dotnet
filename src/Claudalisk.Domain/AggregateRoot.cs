namespace Claudalisk.Domain;

public abstract class AggregateRoot
{
    public const long InitialVersion = 1;

    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot()
        : this(InitialVersion)
    {
    }

    protected AggregateRoot(long version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, InitialVersion);
        Version = version;
    }

    public long Version { get; private set; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public void IncrementVersion() => Version++;

    public IReadOnlyList<IDomainEvent> TakeDomainEvents()
    {
        var events = _domainEvents.ToArray();
        _domainEvents.Clear();
        return events;
    }

    protected void Record(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }
}
