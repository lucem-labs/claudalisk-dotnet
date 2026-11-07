using Claudalisk.Jobs.PostgreSql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Claudalisk.Jobs.Tests.Migrations;

[DbContext(typeof(OrdersDbContext))]
[Migration("20261026000000_InitialOrders")]
internal sealed class InitialOrders : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "orders");
        migrationBuilder.CreateTable(
            name: "orders",
            schema: "orders",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                reference = table.Column<string>(type: "text", nullable: false),
                shipped = table.Column<bool>(type: "boolean", nullable: false),
                canceled = table.Column<bool>(type: "boolean", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_orders", x => x.id));
        migrationBuilder.CreateIndex(name: "ix_orders_reference", schema: "orders", table: "orders", column: "reference", unique: true);
        migrationBuilder.Sql(JobsTable.CreateSql("orders"));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(JobsTable.DropSql("orders"));
        migrationBuilder.DropTable(name: "orders", schema: "orders");
    }
}

[DbContext(typeof(BillingDbContext))]
[Migration("20261026000000_InitialBilling")]
internal sealed class InitialBilling : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "billing");
        migrationBuilder.CreateTable(
            name: "refunds",
            schema: "billing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_refunds", x => x.id));
        migrationBuilder.Sql(JobsTable.CreateSql("billing"));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(JobsTable.DropSql("billing"));
        migrationBuilder.DropTable(name: "refunds", schema: "billing");
    }
}

[DbContext(typeof(MailDbContext))]
[Migration("20261026000000_InitialMail")]
internal sealed class InitialMail : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.CreateSql("mail"));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.DropSql("mail"));
}
