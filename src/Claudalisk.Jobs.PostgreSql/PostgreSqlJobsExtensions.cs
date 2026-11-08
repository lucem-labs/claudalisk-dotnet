using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Claudalisk.Jobs.PostgreSql;

public static class PostgreSqlJobsExtensions
{
    public static IServiceCollection AddClaudaliskPostgreSqlJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IJobTransport, PostgreSqlJobTransport>();
        services.TryAddScoped<IJobWriter, PostgreSqlJobWriter>();
        return services;
    }
}
