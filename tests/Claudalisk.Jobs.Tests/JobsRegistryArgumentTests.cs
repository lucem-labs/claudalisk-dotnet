namespace Claudalisk.Jobs.Tests;

public sealed class JobsRegistryArgumentTests
{
    [Fact]
    public void FindingAModuleNeedsANameOrSegment()
    {
        var failure = Assert.Throws<ArgumentNullException>(() => new JobsRegistry().Find(null!));

        Assert.Equal("nameOrSegment", failure.ParamName);
    }

    [Fact]
    public void FindingTheModuleOfAPayloadNeedsAPayloadType()
    {
        var failure = Assert.Throws<ArgumentNullException>(() => new JobsRegistry().ModuleFor(null!));

        Assert.Equal("payloadType", failure.ParamName);
    }

    [Fact]
    public void RepresentingAJobNeedsAModuleAndAJob()
    {
        Assert.Equal("module", Assert.Throws<ArgumentNullException>(() => JobsRouter.Represent(null!, null!)).ParamName);
        Assert.Equal("job", Assert.Throws<ArgumentNullException>(() => JobsRouter.Represent(new JobsRegistry().GetOrAdd("Orders", "orders"), null!)).ParamName);
    }
}
