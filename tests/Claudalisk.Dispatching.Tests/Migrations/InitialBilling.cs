using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Claudalisk.Dispatching.Tests.Migrations;

[DbContext(typeof(BillingDbContext))]
[Migration("20261026000000_InitialBilling")]
internal sealed class InitialBilling : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "billing");
        migrationBuilder.CreateTable(
            name: "charges",
            schema: "billing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                amount = table.Column<decimal>(type: "numeric", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_charges", x => x.id);
                table.CheckConstraint("ck_charges_amount_positive", "amount > 0");
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "charges", schema: "billing");
}
