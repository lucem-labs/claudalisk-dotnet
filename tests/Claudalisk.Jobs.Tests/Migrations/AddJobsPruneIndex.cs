using Claudalisk.Jobs.PostgreSql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Claudalisk.Jobs.Tests.Migrations;

[DbContext(typeof(OrdersDbContext))]
[Migration("20261108000000_AddJobsPruneIndex")]
internal sealed class AddOrdersJobsPruneIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.CreatePruneIndexSql("orders"));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.DropPruneIndexSql("orders"));
}
