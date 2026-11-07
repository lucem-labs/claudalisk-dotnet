using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Claudalisk.Dispatching.Tests.Migrations;

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
            },
            constraints: table => table.PrimaryKey("pk_orders", x => x.id));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "orders", schema: "orders");
}
