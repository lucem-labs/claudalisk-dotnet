using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Security", "CA2100", Scope = "type", Target = "~T:Claudalisk.Jobs.PostgreSql.PostgreSqlJobTransport", Justification = "Only the jobs table's schema, which the module registers at startup and JobsTable.Quote quotes, is interpolated; every value is a parameter. Until 2027-01-26.")]
[assembly: SuppressMessage("Security", "CA2100", Scope = "type", Target = "~T:Claudalisk.Jobs.PostgreSql.PostgreSqlJobWriter", Justification = "Only the jobs table's schema, which the module registers at startup and JobsTable.Quote quotes, is interpolated; every value is a parameter. Until 2027-01-26.")]
