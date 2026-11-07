using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Claudalisk.Modules.Tests;

internal static class TestHosts
{
    public static WebApplication Build(Action<WebApplicationBuilder> compose)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        compose(builder);
        return builder.Build();
    }

    public static WebApplication Build(params IModule[] modules) =>
        Build(builder => builder.Services.AddModules(builder.Configuration, modules));

    public static async Task<HttpClient> StartAsync(WebApplication app)
    {
        app.MapControllers();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = ((IApplicationBuilder)app).ServerFeatures.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address) };
    }
}
