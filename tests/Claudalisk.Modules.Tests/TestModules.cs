using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Modules.Tests;

public sealed class StorageContract;

public sealed class OrdersContract;

public sealed class BillingContract;

public interface INotAMarker;

public sealed class RegistrationLog
{
    public List<string> Modules { get; } = [];
}

public sealed class StorageService;

public sealed class OrdersService(StorageService storage)
{
    public StorageService Storage { get; } = storage;
}

public abstract class TestModule(Type? contract, params Type[] dependencies) : IModule
{
    public Type? Contract { get; } = contract;

    public IReadOnlyCollection<Type> Dependencies { get; } = dependencies;

    public virtual void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var log = (RegistrationLog?)services.SingleOrDefault(descriptor => descriptor.ServiceType == typeof(RegistrationLog))?.ImplementationInstance;
        if (log is null)
        {
            log = new RegistrationLog();
            services.AddSingleton(log);
        }

        log.Modules.Add(GetType().Name);
    }
}

public sealed class StorageModule() : TestModule(typeof(StorageContract)), IModule
{
    public string Description => "Where everything is stored.";

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        base.ConfigureServices(services, configuration);
        services.AddSingleton<StorageService>();
    }
}

public sealed class OrdersModule() : TestModule(typeof(OrdersContract), typeof(StorageContract))
{
    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        base.ConfigureServices(services, configuration);
        services.AddScoped<OrdersService>();
    }
}

public sealed class BillingModule() : TestModule(typeof(BillingContract), typeof(OrdersContract), typeof(StorageContract));

public sealed class OtherStorageModule() : TestModule(typeof(StorageContract));

public sealed class CircularOrdersModule() : TestModule(typeof(OrdersContract), typeof(BillingContract));

public sealed class CircularBillingModule() : TestModule(typeof(BillingContract), typeof(OrdersContract));

public sealed class InterfaceDependencyModule() : TestModule(null, typeof(INotAMarker));

[ApiController]
[Route("orders")]
public sealed class OrdersController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok("orders");
}
