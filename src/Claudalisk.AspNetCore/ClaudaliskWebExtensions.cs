using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Claudalisk.AspNetCore;

public static class ClaudaliskWebExtensions
{
    public static IServiceCollection AddClaudaliskWeb(this IServiceCollection services, Action<ClaudaliskWebOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (configure is not null)
        {
            services.Configure(configure);
        }

        if (services.Any(descriptor => descriptor.ServiceType == typeof(ModuleTags)))
        {
            return services;
        }

        services.AddOptions<ClaudaliskWebOptions>();
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options => options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict);
        services.AddProblemDetails();
        services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
        services.TryAddSingleton<ModuleTags>();
        services.AddOpenApi(options =>
        {
            options.AddOperationTransformer<ModuleTags>();
            options.AddDocumentTransformer<ModuleTags>();
        });
        return services;
    }

    public static WebApplication UseClaudaliskWeb(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.MapOpenApi();
        app.MapClaudaliskHealth();
        return app;
    }
}
