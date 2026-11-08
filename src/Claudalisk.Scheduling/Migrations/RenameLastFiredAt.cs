using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Claudalisk.Scheduling.Migrations;

[DbContext(typeof(SchedulingDbContext))]
[Migration("20261108000002_RenameLastFiredAt")]
internal sealed class RenameLastFiredAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.RenameColumn(name: "last_fired_at", schema: "scheduling", table: "schedules", newName: "last_fired_timestamp");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.RenameColumn(name: "last_fired_timestamp", schema: "scheduling", table: "schedules", newName: "last_fired_at");
}
