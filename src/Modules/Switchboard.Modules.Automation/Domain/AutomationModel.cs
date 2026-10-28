using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Switchboard.Core.Domain;

namespace Switchboard.Modules.Automation.Domain;

internal sealed class AutWorkflowConfiguration : IEntityTypeConfiguration<AutWorkflow>
{
    public void Configure(EntityTypeBuilder<AutWorkflow> e)
    {
        e.ToTable("AutWorkflows");
        e.Property(x => x.Module).HasMaxLength(32).IsRequired();
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Description).HasMaxLength(1000);
        e.Property(x => x.PublishedTrigger).HasMaxLength(64);
        e.Property(x => x.DisabledReason).HasMaxLength(1000);
        e.Property(x => x.LastRunStatus).HasConversion<string>().HasMaxLength(16);
        e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.WorkspaceId, x.Module, x.Order });
        e.HasIndex(x => new { x.WorkspaceId, x.Name }).IsUnique();
        e.HasIndex(x => new { x.WorkspaceId, x.PublishedTrigger, x.IsEnabled });
    }
}

internal sealed class AutWorkflowVersionConfiguration : IEntityTypeConfiguration<AutWorkflowVersion>
{
    public void Configure(EntityTypeBuilder<AutWorkflowVersion> e)
    {
        e.ToTable("AutWorkflowVersions");
        e.Property(x => x.Note).HasMaxLength(500);
        e.Property(x => x.CreatedByName).HasMaxLength(200);
        e.HasOne<AutWorkflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.WorkflowId, x.Number }).IsUnique();
    }
}

internal sealed class AutRunConfiguration : IEntityTypeConfiguration<AutRun>
{
    public void Configure(EntityTypeBuilder<AutRun> e)
    {
        e.ToTable("AutRuns");
        e.Property(x => x.WorkflowName).HasMaxLength(200);
        e.Property(x => x.RunKey).HasMaxLength(200).IsRequired();
        e.Property(x => x.TriggerKey).HasMaxLength(64);
        e.Property(x => x.TriggerSummary).HasMaxLength(500);
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        e.Property(x => x.Message).HasMaxLength(2000);
        // INV-AUT-1: one run per triggering event and workflow.
        e.HasIndex(x => x.RunKey).IsUnique();
        // INV-AUT-2: a workflow runs at most once per record within one cascade.
        e.HasIndex(x => new { x.WorkflowId, x.TicketId, x.CascadeRootId })
            .IsUnique()
            .HasFilter("[WorkflowId] IS NOT NULL AND [TicketId] IS NOT NULL AND [Status] <> 'Blocked'");
        e.HasIndex(x => new { x.WorkflowId, x.StartedAt });
        e.HasIndex(x => new { x.TicketId, x.StartedAt });
        e.HasIndex(x => new { x.WorkflowId, x.TicketId, x.Anchor }).HasFilter("[Anchor] IS NOT NULL");
        e.HasIndex(x => x.StartedAt);
        e.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AutRunStepConfiguration : IEntityTypeConfiguration<AutRunStep>
{
    public void Configure(EntityTypeBuilder<AutRunStep> e)
    {
        e.ToTable("AutRunSteps");
        e.Property(x => x.Kind).HasMaxLength(64);
        e.Property(x => x.Title).HasMaxLength(500);
        e.Property(x => x.Error).HasMaxLength(4000);
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        e.HasIndex(x => new { x.RunId, x.Order });
        e.HasIndex(x => new { x.Status, x.CreatedAt }).HasFilter("[IsExternal] = 1");
    }
}

internal sealed class AutScenarioConfiguration : IEntityTypeConfiguration<AutScenario>
{
    public void Configure(EntityTypeBuilder<AutScenario> e)
    {
        e.ToTable("AutScenarios");
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Description).HasMaxLength(1000);
        e.Property(x => x.Visibility).HasConversion<string>().HasMaxLength(16);
        e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.WorkspaceId, x.Visibility });
        e.HasIndex(x => x.OwnerUserId);
    }
}

internal sealed class AutClosureRuleConfiguration : IEntityTypeConfiguration<AutClosureRule>
{
    public void Configure(EntityTypeBuilder<AutClosureRule> e)
    {
        e.ToTable("AutClosureRules");
        e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.TicketType).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.FieldKey).HasMaxLength(100);
        e.Property(x => x.Message).HasMaxLength(500);
        e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => x.WorkspaceId);
    }
}

internal sealed class AutScheduleConfiguration : IEntityTypeConfiguration<AutSchedule>
{
    public void Configure(EntityTypeBuilder<AutSchedule> e)
    {
        e.ToTable("AutSchedules");
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Frequency).HasConversion<string>().HasMaxLength(16);
        e.Property(x => x.TimeZone).HasMaxLength(64);
        e.Property(x => x.LastError).HasMaxLength(1000);
        e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.IsEnabled, x.NextRunAt });
    }
}

internal sealed class AutScheduleOccurrenceConfiguration : IEntityTypeConfiguration<AutScheduleOccurrence>
{
    public void Configure(EntityTypeBuilder<AutScheduleOccurrence> e)
    {
        e.ToTable("AutScheduleOccurrences");
        e.HasOne<AutSchedule>().WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.ScheduleId, x.OccurrenceAt }).IsUnique();
    }
}

internal sealed class AutTicketClockConfiguration : IEntityTypeConfiguration<AutTicketClock>
{
    public void Configure(EntityTypeBuilder<AutTicketClock> e)
    {
        e.ToTable("AutTicketClocks");
        e.HasKey(x => x.TicketId);
        e.HasIndex(x => x.StatusSince);
    }
}

internal sealed class AutEventLineageConfiguration : IEntityTypeConfiguration<AutEventLineage>
{
    public void Configure(EntityTypeBuilder<AutEventLineage> e)
    {
        e.ToTable("AutEventLineage");
        e.HasKey(x => x.EventId);
        e.HasIndex(x => x.CreatedAt);
    }
}

internal sealed class AutQueuedEventConfiguration : IEntityTypeConfiguration<AutQueuedEvent>
{
    public void Configure(EntityTypeBuilder<AutQueuedEvent> e)
    {
        e.ToTable("AutQueuedEvents");
        e.Property(x => x.EventType).HasMaxLength(500).IsRequired();
        e.HasIndex(x => x.EventId).IsUnique();
        e.HasIndex(x => new { x.WorkspaceId, x.OccurredAt });
    }
}

internal sealed class AutAlertConfiguration : IEntityTypeConfiguration<AutAlert>
{
    public void Configure(EntityTypeBuilder<AutAlert> e)
    {
        e.ToTable("AutAlerts");
        e.Property(x => x.Key).HasMaxLength(200).IsRequired();
        e.Property(x => x.Message).HasMaxLength(2000);
        e.HasIndex(x => x.Key).IsUnique();
    }
}
