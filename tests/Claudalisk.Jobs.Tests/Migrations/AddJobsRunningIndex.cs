using Claudalisk.Jobs.PostgreSql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Claudalisk.Jobs.Tests.Migrations;

[DbContext(typeof(OrdersDbContext))]
[Migration("20261108000002_AddJobsRunningIndex")]
internal sealed class AddOrdersJobsRunningIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.CreateRunningIndexSql("orders"));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.DropRunningIndexSql("orders"));
}
