using Microsoft.EntityFrameworkCore;

namespace Claudalisk.Scheduling;

internal sealed class ScheduleRow
{
    public required string Name { get; set; }

    public DateTimeOffset? LastFiredAt { get; set; }
}

internal sealed class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options) : DbContext(options)
{
    public const string Schema = "scheduling";

    public DbSet<ScheduleRow> Schedules => Set<ScheduleRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<ScheduleRow>(schedule =>
        {
            schedule.ToTable("schedules");
            schedule.HasKey(row => row.Name);
        });
    }
}
