using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Persistence.Tests;

internal sealed class Order
{
    public Guid Id { get; set; }

    public required string Reference { get; set; }

    public long Version { get; set; }
}

internal sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public const string Schema = "orders";

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<Order>(order =>
        {
            order.ToTable("orders");
            order.HasIndex(o => o.Reference).IsUnique();
            order.Property(o => o.Version).IsConcurrencyToken();
        });
    }
}

internal sealed class OrderStore(OrdersDbContext db)
{
    public async Task PlaceAsync(Guid id, string reference, CancellationToken cancellationToken)
    {
        db.Orders.Add(new Order { Id = id, Reference = reference, Version = 1 });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task PlaceManyAsync(IReadOnlyList<(Guid Id, string Reference)> orders, CancellationToken cancellationToken)
    {
        db.Orders.AddRange(orders.Select(order => new Order { Id = order.Id, Reference = order.Reference, Version = 1 }));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RenameAsync(Guid id, string reference, long readVersion, CancellationToken cancellationToken)
    {
        var order = await db.Orders.SingleAsync(o => o.Id == id, cancellationToken);
        db.Entry(order).Property(o => o.Version).OriginalValue = readVersion;
        order.Reference = reference;
        order.Version = readVersion + 1;
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<int> CountAsync(CancellationToken cancellationToken) => db.Orders.CountAsync(cancellationToken);
}

internal sealed class Charge
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public decimal Amount { get; set; }
}

internal sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public const string Schema = "billing";

    public DbSet<Charge> Charges => Set<Charge>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<Charge>(charge => charge.ToTable("charges", table => table.HasCheckConstraint("ck_charges_amount_positive", "amount > 0")));
    }
}

internal sealed class BillingStore(BillingDbContext db)
{
    public async Task ChargeAsync(Guid orderId, decimal amount, CancellationToken cancellationToken)
    {
        db.Charges.Add(new Charge { Id = Guid.NewGuid(), OrderId = orderId, Amount = amount });
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task EnqueueReceiptAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO billing.jobs (kind, order_id) VALUES ('receipt', {orderId})", cancellationToken);

    public Task<int> CountChargesAsync(CancellationToken cancellationToken) => db.Charges.CountAsync(cancellationToken);

    public Task<int> CountQueuedAsync(CancellationToken cancellationToken) =>
        db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM billing.jobs").SingleAsync(cancellationToken);
}

internal static class TestModulesRegistration
{
    public static IServiceCollection AddTestModules(this IServiceCollection services, string connectionString) =>
        services
            .AddClaudaliskPersistence(connectionString)
            .AddModuleDbContext<OrdersDbContext>(OrdersDbContext.Schema)
            .AddModuleDbContext<BillingDbContext>(BillingDbContext.Schema)
            .AddScoped<OrderStore>()
            .AddScoped<BillingStore>();
}
