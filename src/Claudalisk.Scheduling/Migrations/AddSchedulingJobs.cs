using Claudalisk.Jobs.PostgreSql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Claudalisk.Scheduling.Migrations;

[DbContext(typeof(SchedulingDbContext))]
[Migration("20261108000001_AddSchedulingJobs")]
internal sealed class AddSchedulingJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(JobsTable.CreateSql(SchedulingJobs.Schema));
        migrationBuilder.Sql(JobsTable.CreatePruneIndexSql(SchedulingJobs.Schema));
        migrationBuilder.Sql(JobsTable.CreateDedupeIndexSql(SchedulingJobs.Schema));
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.DropSql(SchedulingJobs.Schema));
}
