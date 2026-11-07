using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Claudalisk.Scheduling.Migrations;

[DbContext(typeof(SchedulingDbContext))]
[Migration("20261026000000_InitialScheduling")]
internal sealed class InitialScheduling : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "scheduling");
        migrationBuilder.CreateTable(
            name: "schedules",
            schema: "scheduling",
            columns: table => new
            {
                name = table.Column<string>(type: "text", nullable: false),
                last_fired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("pk_schedules", x => x.name));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "schedules", schema: "scheduling");
}
