namespace Claudalisk.Domain.Tests;

public sealed class AggregateRootTests
{
    [Fact]
    public void ANewAggregateStartsAtVersionOne()
    {
        var order = new Order();

        Assert.Equal(1, order.Version);
        Assert.Equal(AggregateRoot.InitialVersion, order.Version);
    }

    [Fact]
    public void ALoadedAggregateKeepsItsStoredVersion()
    {
        var order = new Order(Guid.NewGuid(), 7);

        Assert.Equal(7, order.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void AVersionBelowOneIsRefused(long version)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Order(Guid.NewGuid(), version));
    }

    [Fact]
    public void IncrementVersionAddsOneOnEachSave()
    {
        var order = new Order(Guid.NewGuid(), 7);

        order.IncrementVersion();
        order.IncrementVersion();

        Assert.Equal(9, order.Version);
    }

    [Fact]
    public void IncrementVersionRefusesToOverflow()
    {
        var order = new Order(Guid.NewGuid(), long.MaxValue);

        Assert.Throws<OverflowException>(order.IncrementVersion);
        Assert.Equal(long.MaxValue, order.Version);
    }

    [Fact]
    public void ANewAggregateHasNoEvents()
    {
        Assert.Empty(new Order().DomainEvents);
    }

    [Fact]
    public void EventsAreRecordedInOrder()
    {
        var order = new Order();

        order.Ship();
        order.Cancel();

        Assert.Equal<IDomainEvent>(
            [new OrderWasShipped(order.Id), new OrderWasCanceled(order.Id)],
            order.DomainEvents);
    }

    [Fact]
    public void ClearDomainEventsRemovesEveryRecordedEvent()
    {
        var order = new Order();
        order.Ship();
        order.Cancel();

        order.ClearDomainEvents();

        Assert.Empty(order.DomainEvents);
    }

    [Fact]
    public void EventsRecordedAfterAClearAreKept()
    {
        var order = new Order();
        order.Ship();
        order.ClearDomainEvents();

        order.Cancel();

        Assert.Equal<IDomainEvent>([new OrderWasCanceled(order.Id)], order.DomainEvents);
    }

    [Fact]
    public void EventsReadBeforeAClearSurviveTheClear()
    {
        var order = new Order();
        order.Ship();

        var read = order.DomainEvents;
        order.ClearDomainEvents();

        Assert.Equal<IDomainEvent>([new OrderWasShipped(order.Id)], read);
    }

    [Fact]
    public void ClearingEventsLeavesTheVersionUnchanged()
    {
        var order = new Order(Guid.NewGuid(), 3);
        order.Ship();

        order.ClearDomainEvents();

        Assert.Equal(3, order.Version);
    }

    [Fact]
    public void RecordingANullEventIsRefused()
    {
        var order = new Order();

        Assert.Throws<ArgumentNullException>(order.RecordNothing);
        Assert.Empty(order.DomainEvents);
    }
}
