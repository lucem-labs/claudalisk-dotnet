namespace Claudalisk.Domain.Tests;

internal sealed record OrderWasShipped(Guid OrderId) : IDomainEvent;

internal sealed record OrderWasCanceled(Guid OrderId) : IDomainEvent;

internal sealed class Order : AggregateRoot
{
    public Order()
    {
        Id = Guid.NewGuid();
    }

    public Order(Guid id, long version)
        : base(version)
    {
        Id = id;
    }

    public Guid Id { get; }

    public void Ship() => Record(new OrderWasShipped(Id));

    public void Cancel() => Record(new OrderWasCanceled(Id));

    public void RecordNothing() => Record(null!);
}

internal sealed class TestDomainException(
    string slug,
    string title,
    DomainErrorKind kind,
    IReadOnlyDictionary<string, object?>? extensions = null,
    Exception? innerException = null)
    : DomainException(slug, title, kind, extensions, innerException);
