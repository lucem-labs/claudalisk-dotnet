namespace Claudalisk.Domain.Tests;

public sealed class AggregateRootTests
{
    [Fact]
    public void ANewAggregateStartsAtVersionOneWithNoEvents()
    {
        var order = new Order();

        Assert.Equal(1, order.Version);
        Assert.Equal(AggregateRoot.InitialVersion, order.Version);
        Assert.Empty(order.DomainEvents);
    }

    [Fact]
    public void ALoadedAggregateKeepsItsStoredVersion()
    {
        Assert.Equal(7, new Order(7).Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AVersionBelowOneIsRefused(long version)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Order(version));
    }

    [Fact]
    public void EachSaveIncrementsTheVersionByOne()
    {
        var order = new Order(3);

        order.IncrementVersion();
        order.IncrementVersion();

        Assert.Equal(5, order.Version);
    }

    [Fact]
    public void ChangingTheAggregateRecordsItsEventsInOrder()
    {
        var order = new Order();

        order.Ship();
        order.Cancel("damaged");

        Assert.Equal<IDomainEvent>([new OrderWasShipped(), new OrderWasCanceled("damaged")], order.DomainEvents);
    }

    [Fact]
    public void TakingTheEventsReturnsThemOnceAndClearsThem()
    {
        var order = new Order();
        order.Ship();

        var taken = order.TakeDomainEvents();
        order.Cancel("late");

        Assert.Equal<IDomainEvent>([new OrderWasShipped()], taken);
        Assert.Equal<IDomainEvent>([new OrderWasCanceled("late")], order.DomainEvents);
        Assert.Equal<IDomainEvent>([new OrderWasCanceled("late")], order.TakeDomainEvents());
        Assert.Empty(order.DomainEvents);
    }

    [Fact]
    public void RecordingDoesNotChangeTheVersion()
    {
        var order = new Order(2);

        order.Ship();

        Assert.Equal(2, order.Version);
    }

    [Fact]
    public void ANullEventIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new Order().RecordNothing());
    }

    private sealed record OrderWasShipped : IDomainEvent;

    private sealed record OrderWasCanceled(string Reason) : IDomainEvent;

    private sealed class Order : AggregateRoot
    {
        public Order()
        {
        }

        public Order(long version)
            : base(version)
        {
        }

        public void Ship() => Record(new OrderWasShipped());

        public void Cancel(string reason) => Record(new OrderWasCanceled(reason));

        public void RecordNothing() => Record(null!);
    }
}
