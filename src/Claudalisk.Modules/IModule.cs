using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Modules;

public interface IModule
{
    Type? Contract => null;

    string? Description => null;

    IReadOnlyCollection<Type> Dependencies => [];

    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}
