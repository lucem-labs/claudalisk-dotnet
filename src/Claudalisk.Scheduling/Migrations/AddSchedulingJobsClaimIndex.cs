using Claudalisk.Jobs.PostgreSql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Claudalisk.Scheduling.Migrations;

[DbContext(typeof(SchedulingDbContext))]
[Migration("20261108000009_AddSchedulingJobsClaimIndex")]
internal sealed class AddSchedulingJobsClaimIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.CreateClaimIndexSql(SchedulingJobs.Schema));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.DropClaimIndexSql(SchedulingJobs.Schema));
}
