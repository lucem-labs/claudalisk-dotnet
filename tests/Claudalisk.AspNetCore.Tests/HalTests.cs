using System.Text.Json;

namespace Claudalisk.AspNetCore.Tests;

public sealed class HalTests
{
    [Fact]
    public void LinksAndActionsSerializeAsPlainJsonObjects()
    {
        var links = new HalLinks { { "self", "/orders/1" } };
        var actions = new HalActions { { "cancel", "/orders/1/cancel", "post" } };

        Assert.Equal("""{"self":{"href":"/orders/1"}}""", JsonSerializer.Serialize(links, JsonSerializerOptions.Web));
        Assert.Equal("""{"cancel":{"href":"/orders/1/cancel","method":"POST"}}""", JsonSerializer.Serialize(actions, JsonSerializerOptions.Web));
    }

    [Fact]
    public void TheMembersCannotBeChangedExceptThroughTheBuilderMethods()
    {
        Assert.False(typeof(HalLinks).IsAssignableTo(typeof(IDictionary<string, HalLink>)));
        Assert.False(typeof(HalActions).IsAssignableTo(typeof(IDictionary<string, HalAction>)));
    }

    [Fact]
    public void ALaterLinkWithTheSameRelationReplacesTheEarlierOne()
    {
        var links = Hal.Self("/first").With("self", "/second");

        Assert.Equal("/second", Assert.Single(links).Value.Href);
    }
}
