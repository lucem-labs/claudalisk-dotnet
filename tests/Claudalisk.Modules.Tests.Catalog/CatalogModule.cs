using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Modules.Tests.Catalog;

public sealed class CatalogModule : IModule
{
    public Type Contract => typeof(CatalogContract);

    public IReadOnlyCollection<Type> Dependencies => [];

    public void Register(ModuleRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        registration.Services.AddSingleton<CatalogPrices>();
    }
}
