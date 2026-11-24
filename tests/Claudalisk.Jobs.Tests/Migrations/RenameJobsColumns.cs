using Claudalisk.Jobs.PostgreSql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Claudalisk.Jobs.Tests.Migrations;

[DbContext(typeof(OrdersDbContext))]
[Migration("20261124000000_RenameJobsColumns")]
internal sealed class RenameOrdersJobsColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.RenameColumnsSql("orders"));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.RestoreColumnsSql("orders"));
}

[DbContext(typeof(BillingDbContext))]
[Migration("20261124000000_RenameJobsColumns")]
internal sealed class RenameBillingJobsColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.RenameColumnsSql("billing"));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.RestoreColumnsSql("billing"));
}

[DbContext(typeof(MailDbContext))]
[Migration("20261124000000_RenameJobsColumns")]
internal sealed class RenameMailJobsColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.RenameColumnsSql("mail"));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.RestoreColumnsSql("mail"));
}
