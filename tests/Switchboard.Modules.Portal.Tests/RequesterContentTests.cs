using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Portal.Services;

namespace Switchboard.Modules.Portal.Tests;

/// <summary>INV-TKT-7 and SR-AUTHZ-7: the requester page carries public content only.</summary>
public sealed class RequesterContentTests(PortalFixture f) : IClassFixture<PortalFixture>
{
    [Fact]
    public async Task Request_page_never_includes_private_notes_forwards_hidden_messages_or_their_files()
    {
        var dana = await f.RequesterAsync("Dana Requester");
        var ticket = await f.FileAsync(dana);

        var publicFile = await f.UploadAsync(f.Agent, "steps.txt", "public steps");
        var noteFile = await f.UploadAsync(f.Agent, "internal-notes.txt", "SECRET-NOTE-FILE");
        var forwardFile = await f.UploadAsync(f.Agent, "vendor-quote.txt", "SECRET-FORWARD-FILE");
        await f.AsAgent(async sp =>
        {
            var tickets = sp.GetRequiredService<ITicketService>();
            await tickets.AddNoteAsync(ticket.Id, new NoteCommand { BodyHtml = "<p>SECRET-PRIVATE-NOTE</p>", IsPrivate = true, AttachmentIds = [noteFile] });
            await tickets.ForwardAsync(ticket.Id, new ForwardCommand { To = ["vendor@example.com"], BodyHtml = "<p>SECRET-FORWARD</p>", AttachmentIds = [forwardFile] });
            await tickets.ReplyAsync(ticket.Id, new ReplyCommand { BodyHtml = "<p>Try these steps.</p>", AttachmentIds = [publicFile] });
            await tickets.AddNoteAsync(ticket.Id, new NoteCommand { BodyHtml = "<p>Parts are on order.</p>", IsPrivate = false });
            var hidden = await tickets.ReplyAsync(ticket.Id, new ReplyCommand { BodyHtml = "<p>SECRET-HIDDEN-REPLY</p>" });
            await tickets.HideConversationAsync(hidden.Id);
        });

        var view = await f.AsRequester(dana, p => p.GetAsync(ticket.Number));

        Assert.Equal(["<p>Try these steps.</p>", "<p>Parts are on order.</p>"], view.Messages.Select(m => m.BodyHtml));
        Assert.All(view.Messages, m => Assert.Equal(PortalMessageKind.Support, m.Kind));
        Assert.DoesNotContain(view.Messages, m => m.BodyHtml.Contains("SECRET"));
        var files = view.Messages.SelectMany(m => m.Files).Concat(view.Files).Select(x => x.Id).ToList();
        Assert.Equal([publicFile], files);
    }

    [Fact]
    public async Task Agent_only_fields_never_appear_and_requester_labels_are_used()
    {
        var dana = await f.RequesterAsync("Dana Fields");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        await AddFieldsAsync(suffix);

        var fields = await f.AsRequester(dana, p => p.RequesterFieldsAsync());
        var mine = fields.Where(x => x.Key.EndsWith(suffix)).ToList();
        var editable = Assert.Single(mine);
        Assert.Equal($"asset_{suffix}", editable.Key);
        Assert.Equal("Your asset tag", editable.Label);

        // A requester can't set a read-only field even by posting it.
        var ticket = await f.AsRequester(dana, p => p.CreateAsync(new NewPortalRequest
        {
            Subject = "Laptop fan is loud",
            CustomFields = new Dictionary<string, string[]>
            {
                [$"asset_{suffix}"] = ["LT-0042"],
                [$"cost_{suffix}"] = ["999"],
                [$"internal_{suffix}"] = ["tampered"],
            },
        }));
        var stored = CustomFieldService.Parse((await f.LoadAsync(ticket.Id)).CustomFieldsJson);
        Assert.Equal("LT-0042", stored[$"asset_{suffix}"]?.GetValue<string>());
        Assert.False(stored.ContainsKey($"cost_{suffix}"));
        Assert.False(stored.ContainsKey($"internal_{suffix}"));

        await f.AsAgent(sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch
        {
            CustomFields = new Dictionary<string, System.Text.Json.JsonElement?>
            {
                [$"cost_{suffix}"] = System.Text.Json.JsonSerializer.SerializeToElement("1200"),
                [$"internal_{suffix}"] = System.Text.Json.JsonSerializer.SerializeToElement("Escalate to vendor"),
            },
        }));

        var view = await f.AsRequester(dana, p => p.GetAsync(ticket.Number));
        Assert.Contains(view.Fields, x => x.Label == "Your asset tag" && x.Text == "LT-0042");
        Assert.Contains(view.Fields, x => x.Label == "Estimated cost" && x.Text == "1200");
        Assert.DoesNotContain(view.Fields, x => x.Text == "Escalate to vendor" || x.Label == "Internal triage");
    }

    [Fact]
    public async Task Agent_names_follow_the_setting()
    {
        var dana = await f.RequesterAsync("Dana Names");
        var ticket = await f.FileAsync(dana);
        await f.AsAgent(async sp =>
        {
            await sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { AgentId = f.Agent.Id });
            await sp.GetRequiredService<ITicketService>().ReplyAsync(ticket.Id, new ReplyCommand { BodyHtml = "<p>On it.</p>" });
        });

        var shown = await f.AsRequester(dana, p => p.GetAsync(ticket.Number));
        Assert.Equal("Sam Agent", shown.Messages.Single().AuthorName);
        Assert.Equal("Sam Agent", shown.HandledBy);

        await f.SetAsync(SettingKeys.ShowAgentNamesToRequesters, false);
        try
        {
            var hidden = await f.AsRequester(dana, p => p.GetAsync(ticket.Number));
            Assert.Equal(PortalRequests.SupportName, hidden.Messages.Single().AuthorName);
            Assert.Null(hidden.Messages.Single().AvatarName);
            Assert.Null(hidden.HandledBy);
        }
        finally
        {
            await f.SetAsync(SettingKeys.ShowAgentNamesToRequesters, true);
        }
    }

    [Fact]
    public async Task Portal_search_treats_filter_syntax_as_text()
    {
        var dana = await f.RequesterAsync("Dana Search");
        var other = await f.RequesterAsync("Olive Search");
        var vpn = await f.FileAsync(dana, "VPN drops on hotel wifi");
        await f.FileAsync(other, "VPN drops at home");
        await f.AsAgent(async sp =>
        {
            var network = await sp.GetRequiredService<SwitchboardDbContext>().AgentGroups.AsNoTracking().FirstAsync(g => g.Name == "Network");
            await sp.GetRequiredService<ITicketService>().UpdateAsync(vpn.Id, new TicketPatch { GroupId = network.Id });
        });

        var found = await f.AsRequester(dana, p => p.SearchAsync("vpn drops"));
        Assert.Equal([vpn.Id], found.Select(r => r.Id));

        var byNumber = await f.AsRequester(dana, p => p.SearchAsync(vpn.DisplayId));
        Assert.Equal([vpn.Id], byNumber.Select(r => r.Id));

        // Internal fields can't be probed through the portal search.
        Assert.Empty(await f.AsRequester(dana, p => p.SearchAsync("group:Network")));
        Assert.Empty(await f.AsRequester(dana, p => p.SearchAsync("vpn OR printer")));
    }

    private Task AddFieldsAsync(string suffix) =>
        f.Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            db.CustomFieldDefinitions.AddRange(
                new CustomFieldDefinition { EntityType = Ticket.Family, Key = $"asset_{suffix}", Label = "Asset tag", RequesterLabel = "Your asset tag", FieldType = CustomFieldType.Text, VisibleToRequesters = true, EditableByRequesters = true, Order = 1 },
                new CustomFieldDefinition { EntityType = Ticket.Family, Key = $"cost_{suffix}", Label = "Estimated cost", FieldType = CustomFieldType.Text, VisibleToRequesters = true, EditableByRequesters = false, Order = 2 },
                new CustomFieldDefinition { EntityType = Ticket.Family, Key = $"internal_{suffix}", Label = "Internal triage", FieldType = CustomFieldType.Text, VisibleToRequesters = false, EditableByRequesters = false, Order = 3 });
            await db.SaveChangesAsync();
            return true;
        });
}
