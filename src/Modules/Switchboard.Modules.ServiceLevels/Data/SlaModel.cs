using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Switchboard.Core.Domain;
using Switchboard.Modules.ServiceLevels.Domain;

namespace Switchboard.Modules.ServiceLevels.Data;

internal sealed class SlaCalendarConfiguration : IEntityTypeConfiguration<SlaCalendar>
{
    public void Configure(EntityTypeBuilder<SlaCalendar> e)
    {
        e.ToTable("SlaCalendars");
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Description).HasMaxLength(1000);
        e.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
        e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        e.HasMany(x => x.Hours).WithOne().HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.Cascade);
        e.HasMany(x => x.Holidays).WithOne().HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.WorkspaceId, x.DeletedAt });
    }
}

internal sealed class SlaWorkingHoursConfiguration : IEntityTypeConfiguration<SlaWorkingHours>
{
    public void Configure(EntityTypeBuilder<SlaWorkingHours> e)
    {
        e.ToTable("SlaWorkingHours");
        e.HasIndex(x => x.CalendarId);
    }
}

internal sealed class SlaHolidayConfiguration : IEntityTypeConfiguration<SlaHoliday>
{
    public void Configure(EntityTypeBuilder<SlaHoliday> e)
    {
        e.ToTable("SlaHolidays");
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.HasIndex(x => new { x.CalendarId, x.Date }).IsUnique();
    }
}

internal sealed class SlaPolicyConfiguration : IEntityTypeConfiguration<SlaPolicy>
{
    public void Configure(EntityTypeBuilder<SlaPolicy> e)
    {
        e.ToTable("SlaPolicies");
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Description).HasMaxLength(1000);
        e.Ignore(x => x.Conditions);
        e.Ignore(x => x.Targets);
        e.Ignore(x => x.Reminders);
        e.Ignore(x => x.Escalations);
        e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<SlaCalendar>().WithMany().HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.NoAction);
        e.HasIndex(x => new { x.WorkspaceId, x.DeletedAt, x.Order });
    }
}

internal sealed class SlaTicketStateConfiguration : IEntityTypeConfiguration<SlaTicketState>
{
    public void Configure(EntityTypeBuilder<SlaTicketState> e)
    {
        e.ToTable("SlaTicketStates");
        e.HasKey(x => x.TicketId);
        e.Property(x => x.PolicyName).HasMaxLength(200);
        e.Property(x => x.SelectionReason).HasMaxLength(1000);
        e.Property(x => x.OverrideReason).HasMaxLength(1000);
        e.Ignore(x => x.Pauses);
        e.HasOne<Ticket>().WithOne().HasForeignKey<SlaTicketState>(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<SlaPolicy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.NoAction);
        e.HasOne<SlaCalendar>().WithMany().HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.NoAction);
        e.HasIndex(x => new { x.WorkspaceId, x.PausedSince });
        e.HasIndex(x => x.NextResponseDueBy);
    }
}

internal sealed class SlaMonitorCursorConfiguration : IEntityTypeConfiguration<SlaMonitorCursor>
{
    public void Configure(EntityTypeBuilder<SlaMonitorCursor> e)
    {
        e.ToTable("SlaMonitorCursors");
        e.HasKey(x => x.TicketId);
        e.HasOne<Ticket>().WithOne().HasForeignKey<SlaMonitorCursor>(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => x.NextCheckAt);
    }
}

internal sealed class SlaEventConfiguration : IEntityTypeConfiguration<SlaEvent>
{
    public void Configure(EntityTypeBuilder<SlaEvent> e)
    {
        e.ToTable("SlaEvents");
        e.Property(x => x.Summary).HasMaxLength(1000);
        e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        // INV-SLA-4: the database refuses a second firing, even from another instance.
        e.HasIndex(x => new { x.TicketId, x.Cycle, x.Kind, x.Target, x.Level, x.Sequence }).IsUnique();
        e.HasIndex(x => new { x.WorkspaceId, x.FiredAt });
    }
}
