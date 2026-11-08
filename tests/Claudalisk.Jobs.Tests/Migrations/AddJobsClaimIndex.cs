using Claudalisk.Jobs.PostgreSql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Claudalisk.Jobs.Tests.Migrations;

[DbContext(typeof(OrdersDbContext))]
[Migration("20261108000009_AddJobsClaimIndex")]
internal sealed class AddOrdersJobsClaimIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.CreateClaimIndexSql("orders"));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.DropClaimIndexSql("orders"));
}
