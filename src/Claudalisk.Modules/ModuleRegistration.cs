using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Modules;

public sealed class ModuleRegistration
{
    internal ModuleRegistration(IServiceCollection services, IConfiguration configuration, Type contract)
    {
        Services = services;
        Configuration = configuration;
        Contract = contract;
    }

    public IServiceCollection Services { get; }

    public IConfiguration Configuration { get; }

    public Type Contract { get; }
}
