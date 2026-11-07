namespace Claudalisk.Domain;

public abstract class AggregateRoot
{
    public const long InitialVersion = 1;

    private readonly List<IDomainEvent> recordedEvents = [];

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

    public IReadOnlyList<IDomainEvent> DomainEvents => [.. recordedEvents];

    public void IncrementVersion() => Version = checked(Version + 1);

    public void ClearDomainEvents() => recordedEvents.Clear();

    protected void Record(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        recordedEvents.Add(domainEvent);
    }
}
