using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Switchboard.Modules.Messaging.Domain;

internal sealed class OutboundSettingsConfig : IEntityTypeConfiguration<MsgOutboundSettings>
{
    public void Configure(EntityTypeBuilder<MsgOutboundSettings> e)
    {
        e.ToTable("MsgOutboundSettings");
        e.Property(x => x.Host).HasMaxLength(255);
        e.Property(x => x.Username).HasMaxLength(255);
        e.Property(x => x.PasswordProtected).HasMaxLength(2000);
        e.Property(x => x.FromName).HasMaxLength(200);
        e.Property(x => x.FromAddress).HasMaxLength(320);
        e.Property(x => x.EnvelopeFrom).HasMaxLength(320);
        e.Property(x => x.DkimDomain).HasMaxLength(255);
        e.Property(x => x.DkimSelector).HasMaxLength(100);
        e.Property(x => x.DkimKeyProtected).HasMaxLength(12000);
        e.Property(x => x.CaptureAddress).HasMaxLength(320);
    }
}

internal sealed class MailboxConfig : IEntityTypeConfiguration<MsgMailbox>
{
    public void Configure(EntityTypeBuilder<MsgMailbox> e)
    {
        e.ToTable("MsgMailboxes");
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Address).HasMaxLength(320).IsRequired();
        e.Property(x => x.ImapHost).HasMaxLength(255);
        e.Property(x => x.ImapUsername).HasMaxLength(255);
        e.Property(x => x.ImapPasswordProtected).HasMaxLength(2000);
        e.Property(x => x.ImapFolder).HasMaxLength(200);
        e.Property(x => x.WebhookSecretProtected).HasMaxLength(2000);
        e.Property(x => x.TrustedAuthServId).HasMaxLength(255);
        e.Property(x => x.LastError).HasMaxLength(1000);
        e.HasIndex(x => x.Address).IsUnique();
        e.HasIndex(x => x.WorkspaceId);
    }
}

internal sealed class MessageMapConfig : IEntityTypeConfiguration<MsgMessageMap>
{
    public void Configure(EntityTypeBuilder<MsgMessageMap> e)
    {
        e.ToTable("MsgMessageMap");
        e.Property(x => x.MessageId).HasMaxLength(450).IsRequired();
        e.HasIndex(x => x.MessageId).IsUnique();
        e.HasIndex(x => new { x.TicketId, x.CreatedAt });
    }
}

internal sealed class InboundMessageConfig : IEntityTypeConfiguration<MsgInboundMessage>
{
    public void Configure(EntityTypeBuilder<MsgInboundMessage> e)
    {
        e.ToTable("MsgInboundMessages");
        e.Property(x => x.MessageId).HasMaxLength(450).IsRequired();
        e.Property(x => x.FromAddress).HasMaxLength(320);
        e.Property(x => x.FromName).HasMaxLength(200);
        e.Property(x => x.Subject).HasMaxLength(500);
        e.Property(x => x.OutcomeDetail).HasMaxLength(1000);
        e.Property(x => x.RawStorageKey).HasMaxLength(200);
        e.HasIndex(x => x.ReceivedAt);
        e.HasIndex(x => x.MessageId);
        e.HasIndex(x => x.TicketId);
    }
}

internal sealed class DeliveryConfig : IEntityTypeConfiguration<MsgDelivery>
{
    public void Configure(EntityTypeBuilder<MsgDelivery> e)
    {
        e.ToTable("MsgDeliveries");
        e.Property(x => x.Recipient).HasMaxLength(320).IsRequired();
        e.Property(x => x.TemplateKey).HasMaxLength(100);
        e.Property(x => x.Subject).HasMaxLength(500);
        e.Property(x => x.Error).HasMaxLength(1000);
        e.Property(x => x.MessageId).HasMaxLength(450);
        // INV-NOT-1: one message per event, recipient and channel.
        e.HasIndex(x => new { x.EventId, x.Recipient, x.Channel }).IsUnique().HasFilter("[EventId] IS NOT NULL");
        e.HasIndex(x => new { x.TicketId, x.CreatedAt });
        e.HasIndex(x => new { x.Recipient, x.CreatedAt });
        e.HasIndex(x => x.MessageId);
    }
}

internal sealed class NotificationConfig : IEntityTypeConfiguration<MsgNotification>
{
    public void Configure(EntityTypeBuilder<MsgNotification> e)
    {
        e.ToTable("MsgNotifications");
        e.Property(x => x.Type).HasMaxLength(100).IsRequired();
        e.Property(x => x.Title).HasMaxLength(300).IsRequired();
        e.Property(x => x.Body).HasMaxLength(1000);
        e.Property(x => x.Link).HasMaxLength(500);
        e.Property(x => x.Reference).HasMaxLength(50);
        e.HasIndex(x => new { x.UserId, x.ReadAt, x.CreatedAt });
    }
}

internal sealed class TemplateConfig : IEntityTypeConfiguration<MsgTemplate>
{
    public void Configure(EntityTypeBuilder<MsgTemplate> e)
    {
        e.ToTable("MsgTemplates");
        e.Property(x => x.EventKey).HasMaxLength(100).IsRequired();
        e.Property(x => x.Subject).HasMaxLength(300).IsRequired();
        e.Property(x => x.BodyHtml).IsRequired();
        e.HasIndex(x => new { x.WorkspaceId, x.EventKey }).IsUnique();
    }
}

internal sealed class PreferenceConfig : IEntityTypeConfiguration<MsgPreference>
{
    public void Configure(EntityTypeBuilder<MsgPreference> e)
    {
        e.ToTable("MsgPreferences");
        e.HasKey(x => new { x.UserId, x.EventKey });
        e.Property(x => x.EventKey).HasMaxLength(100);
    }
}

internal sealed class AddressRuleConfig : IEntityTypeConfiguration<MsgAddressRule>
{
    public void Configure(EntityTypeBuilder<MsgAddressRule> e)
    {
        e.ToTable("MsgAddressRules");
        e.Property(x => x.Pattern).HasMaxLength(320).IsRequired();
        e.Property(x => x.Note).HasMaxLength(300);
        e.HasIndex(x => x.Pattern).IsUnique();
    }
}

internal sealed class BouncedAddressConfig : IEntityTypeConfiguration<MsgBouncedAddress>
{
    public void Configure(EntityTypeBuilder<MsgBouncedAddress> e)
    {
        e.ToTable("MsgBouncedAddresses");
        e.Property(x => x.Address).HasMaxLength(320).IsRequired();
        e.Property(x => x.Reason).HasMaxLength(500);
        e.HasIndex(x => x.Address).IsUnique();
    }
}
