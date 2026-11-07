using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Security", "CA2100", Scope = "namespaceanddescendants", Target = "~N:Claudalisk.Jobs.Tests", Justification = "Test helper that runs fixed SQL and DDL against a throwaway test database; database names and identifiers cannot be parameters. Until 2027-01-26.")]
