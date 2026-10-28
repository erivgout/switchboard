using System.Text.Json;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;

namespace Switchboard.Core.Data;

/// <summary>
/// The single EF Core context. Modules add their own entities through
/// <see cref="ISwitchboardModule.ConfigureModel"/> and access them with <c>Set&lt;T&gt;()</c>.
/// </summary>
/// <remarks>
/// <see cref="SaveChangesAsync(bool, CancellationToken)"/> does four things in one transaction:
/// stamps timestamps, runs pre-commit event handlers, writes one activity entry per changed
/// <see cref="IActivitySubject"/> (INV-HIS-1), and persists raised events to the outbox.
/// </remarks>
public class SwitchboardDbContext : IdentityUserContext<User, Guid>
{
    private static readonly HashSet<string> ActivityIgnoredProperties = new(StringComparer.Ordinal)
    {
        nameof(IHasTimestamps.UpdatedAt),
        nameof(Ticket.RowVersion),
        nameof(Ticket.DescriptionText),
        nameof(TicketConversation.BodyText),
        nameof(Ticket.LastRequesterActivityAt),
        nameof(Ticket.LastAgentActivityAt),
        nameof(Ticket.AwaitingAgentSince),
    };

    private readonly List<DomainEvent> _pendingEvents = [];
    private readonly Dictionary<object, ActivityHint> _activityHints = new(ReferenceEqualityComparer.Instance);
    private readonly IServiceProvider? _services;
    private readonly IActorAccessor? _actorAccessor;
    private readonly TimeProvider _time;

    public SwitchboardDbContext(
        DbContextOptions<SwitchboardDbContext> options,
        ModuleCatalog? modules = null,
        IActorAccessor? actorAccessor = null,
        IServiceProvider? services = null,
        TimeProvider? time = null)
        : base(options)
    {
        Modules = modules ?? ModuleCatalog.Empty;
        _actorAccessor = actorAccessor;
        _services = services;
        _time = time ?? TimeProvider.System;
    }

    public ModuleCatalog Modules { get; }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<UserEmail> UserEmails => Set<UserEmail>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<WorkspaceMembership> WorkspaceMemberships => Set<WorkspaceMembership>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<AgentGroup> AgentGroups => Set<AgentGroup>();
    public DbSet<AgentGroupMember> AgentGroupMembers => Set<AgentGroupMember>();
    public DbSet<RequesterGroup> RequesterGroups => Set<RequesterGroup>();
    public DbSet<RequesterGroupMember> RequesterGroupMembers => Set<RequesterGroupMember>();
    public DbSet<UserDelegation> UserDelegations => Set<UserDelegation>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketStatus> TicketStatuses => Set<TicketStatus>();
    public DbSet<TicketSource> TicketSources => Set<TicketSource>();
    public DbSet<TicketCategory> TicketCategories => Set<TicketCategory>();
    public DbSet<TicketTag> TicketTags => Set<TicketTag>();
    public DbSet<TicketShare> TicketShares => Set<TicketShare>();
    public DbSet<TicketConversation> TicketConversations => Set<TicketConversation>();
    public DbSet<TicketTask> TicketTasks => Set<TicketTask>();
    public DbSet<TicketLink> TicketLinks => Set<TicketLink>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<ActivityEntry> ActivityEntries => Set<ActivityEntry>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<CustomFieldDefinition> CustomFieldDefinitions => Set<CustomFieldDefinition>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<DisplayIdCounter> DisplayIdCounters => Set<DisplayIdCounter>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<OutboxHandlerReceipt> OutboxHandlerReceipts => Set<OutboxHandlerReceipt>();

    /// <summary>Queue a domain event; it is dispatched on the next save.</summary>
    public void Raise(DomainEvent domainEvent) => _pendingEvents.Add(domainEvent);

    /// <summary>
    /// Describe the change to <paramref name="entity"/> for its activity timeline. Without a hint the
    /// recorder writes a generic entry with raw field diffs.
    /// </summary>
    public void Activity(object entity, string action, string summary, IReadOnlyList<FieldChange>? changes = null)
        => _activityHints[entity] = new ActivityHint(action, summary, changes);

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, ModuleAwareModelCacheKeyFactory>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        CoreModel.Configure(builder);
        foreach (var module in Modules.Modules)
        {
            module.ConfigureModel(builder);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var actor = _actorAccessor?.Current ?? Actor.System();
        var now = _time.GetUtcNow();

        var dispatched = await RunPreCommitHandlersAsync(actor, now, cancellationToken);
        StampTimestamps(now);
        RecordActivity(actor, now);
        WriteOutbox(dispatched, now);

        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        _activityHints.Clear();

        if (dispatched.Count > 0)
        {
            _services?.GetService(typeof(IOutboxSignal)).As<IOutboxSignal>()?.Notify();
        }

        return result;
    }

    private async Task<List<DomainEvent>> RunPreCommitHandlersAsync(Actor actor, DateTimeOffset now, CancellationToken ct)
    {
        var all = new List<DomainEvent>();
        // Handlers can raise more events; bound the loop so a buggy handler can't spin forever.
        for (var round = 0; round < 8 && _pendingEvents.Count > 0; round++)
        {
            var batch = _pendingEvents
                .Select(e => e with
                {
                    OccurredAt = e.OccurredAt == default ? now : e.OccurredAt,
                    Actor = e.Actor ?? actor.Snapshot(),
                    CascadeDepth = Math.Max(e.CascadeDepth, actor.CascadeDepth),
                })
                .ToList();
            _pendingEvents.Clear();
            all.AddRange(batch);

            if (_services is null)
            {
                continue;
            }

            foreach (var domainEvent in batch)
            {
                await EventDispatch.InvokeAsync(_services, typeof(IPreCommitHandler<>), domainEvent, ct);
            }
        }

        if (_pendingEvents.Count > 0)
        {
            throw new InvalidOperationException("Pre-commit event handlers kept raising events; aborting to avoid a loop.");
        }

        return all;
    }

    private void StampTimestamps(DateTimeOffset now)
    {
        foreach (var entry in ChangeTracker.Entries<IHasTimestamps>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAt == default)
                {
                    entry.Entity.CreatedAt = now;
                }
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added)
            {
                continue;
            }
            var created = entry.Metadata.FindProperty("CreatedAt");
            if (created is not null && created.ClrType == typeof(DateTimeOffset)
                && (DateTimeOffset)entry.Property("CreatedAt").CurrentValue! == default)
            {
                entry.Property("CreatedAt").CurrentValue = now;
            }
        }
    }

    private void RecordActivity(Actor actor, DateTimeOffset now)
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.Entity is IActivitySubject && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        foreach (var entry in entries)
        {
            var subject = (IActivitySubject)entry.Entity;
            _activityHints.TryGetValue(entry.Entity, out var hint);

            IReadOnlyList<FieldChange> changes = hint?.Changes ?? (entry.State == EntityState.Modified ? Diff(entry) : []);
            if (hint is null && entry.State == EntityState.Modified && changes.Count == 0)
            {
                continue;
            }

            var entityType = entry.Metadata.ClrType.Name;
            var action = hint?.Action ?? entry.State switch
            {
                EntityState.Added => "created",
                EntityState.Deleted => "deleted",
                _ => "updated",
            };
            var summary = hint?.Summary ?? entry.State switch
            {
                EntityState.Added => $"{Humanize(entityType)} created",
                EntityState.Deleted => $"{Humanize(entityType)} deleted",
                _ => $"Updated {string.Join(", ", changes.Select(c => Humanize(c.Field).ToLowerInvariant()))}",
            };

            ActivityEntries.Add(new ActivityEntry
            {
                SubjectType = subject.ActivitySubjectType,
                SubjectId = subject.ActivitySubjectId,
                WorkspaceId = subject.ActivityWorkspaceId,
                EntityType = entityType,
                EntityId = entry.Entity is Entity e ? e.Id : subject.ActivitySubjectId,
                ActorKind = actor.Kind,
                ActorUserId = actor.UserId,
                ActorName = actor.DisplayName,
                Channel = actor.Channel,
                ClientName = actor.ClientName,
                Action = action,
                Summary = summary,
                ChangesJson = JsonSerializer.Serialize(changes, JsonDefaults.Options),
                CreatedAt = now,
            });
        }
    }

    private static List<FieldChange> Diff(EntityEntry entry)
    {
        var changes = new List<FieldChange>();
        foreach (var property in entry.Properties)
        {
            if (!property.IsModified || ActivityIgnoredProperties.Contains(property.Metadata.Name))
            {
                continue;
            }
            var from = Format(property.OriginalValue);
            var to = Format(property.CurrentValue);
            if (from != to)
            {
                changes.Add(new FieldChange(property.Metadata.Name, from, to));
            }
        }
        return changes;
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        DateTimeOffset d => d.ToString("O"),
        System.Collections.IEnumerable list and not string => JsonSerializer.Serialize(list, JsonDefaults.Options),
        _ => value.ToString(),
    };

    private void WriteOutbox(List<DomainEvent> events, DateTimeOffset now)
    {
        foreach (var domainEvent in events)
        {
            var type = domainEvent.GetType();
            OutboxMessages.Add(new OutboxMessage
            {
                Id = domainEvent.EventId,
                EventType = $"{type.FullName}, {type.Assembly.GetName().Name}",
                PayloadJson = JsonSerializer.Serialize(domainEvent, type, JsonDefaults.Options),
                OccurredAt = domainEvent.OccurredAt,
                CascadeDepth = domainEvent.CascadeDepth,
                NextAttemptAt = now,
            });
        }
    }

    private static string Humanize(string pascal)
    {
        var chars = new List<char>(pascal.Length + 8);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(pascal[i - 1]))
            {
                chars.Add(' ');
                chars.Add(char.ToLowerInvariant(c));
            }
            else
            {
                chars.Add(c);
            }
        }
        var s = new string(chars.ToArray());
        return s.EndsWith(" id", StringComparison.Ordinal) ? s[..^3] : s;
    }

    private sealed record ActivityHint(string Action, string Summary, IReadOnlyList<FieldChange>? Changes);
}

/// <summary>Wakes the outbox processor after a commit that produced events.</summary>
public interface IOutboxSignal
{
    void Notify();
}

internal sealed class ModuleAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is SwitchboardDbContext sb
            ? (context.GetType(), sb.Modules.Fingerprint, designTime)
            : (context.GetType(), designTime);
}

internal static class ObjectExtensions
{
    public static T? As<T>(this object? value) where T : class => value as T;
}
