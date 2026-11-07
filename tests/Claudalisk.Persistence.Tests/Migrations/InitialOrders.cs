using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Claudalisk.Persistence.Tests.Migrations;

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
                version = table.Column<long>(type: "bigint", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_orders", x => x.id));
        migrationBuilder.CreateIndex(name: "ix_orders_reference", schema: "orders", table: "orders", column: "reference", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "orders", schema: "orders");
}
