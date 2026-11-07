using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Persistence;

internal sealed class ModuleContextGuard(string schema) : IDbContextOptionsExtension
{
    public const string PoolingMessage =
        "Module DbContexts must not be pooled (AddDbContextPool): each one needs its own scope's connection. Register them with AddModuleDbContext.";

    public const string RetryMessage =
        "Module DbContexts must not use EnableRetryOnFailure: EF Core retries do not work with the unit of work's transaction. Retry the whole IUnitOfWork.RunAsync call instead.";

    public string Schema { get; } = schema;

    public DbContextOptionsExtensionInfo Info => new GuardInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
        if (options.FindExtension<CoreOptionsExtension>()?.MaxPoolSize is not null)
        {
            throw new InvalidOperationException(PoolingMessage);
        }

        if (options.Extensions.OfType<RelationalOptionsExtension>().Any(extension => extension.ExecutionStrategyFactory is not null))
        {
            throw new InvalidOperationException(RetryMessage);
        }
    }

    private sealed class GuardInfo(ModuleContextGuard guard) : DbContextOptionsExtensionInfo(guard)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => $"ModuleSchema={guard.Schema} ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is GuardInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["Claudalisk:ModuleSchema"] = guard.Schema;
    }
}
