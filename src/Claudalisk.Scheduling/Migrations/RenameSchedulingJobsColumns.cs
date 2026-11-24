using Claudalisk.Jobs.PostgreSql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Claudalisk.Scheduling.Migrations;

[DbContext(typeof(SchedulingDbContext))]
[Migration("20261124000000_RenameSchedulingJobsColumns")]
internal sealed class RenameSchedulingJobsColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.RenameColumnsSql(SchedulingJobs.Schema));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.RestoreColumnsSql(SchedulingJobs.Schema));
}
