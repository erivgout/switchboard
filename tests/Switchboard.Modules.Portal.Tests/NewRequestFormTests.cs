using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Portal.Services;

namespace Switchboard.Modules.Portal.Tests;

/// <summary>The "Report a problem" form with a required requester field (own database: the field applies to every request).</summary>
public sealed class NewRequestFormTests(PortalFixture f) : IClassFixture<PortalFixture>
{
    [Fact]
    public async Task Every_problem_is_reported_at_once_and_a_complete_form_files_the_request()
    {
        await f.Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            db.CustomFieldDefinitions.Add(new CustomFieldDefinition
            {
                EntityType = Ticket.Family,
                Key = "affected",
                Label = "Impact",
                RequesterLabel = "Who is affected?",
                FieldType = CustomFieldType.Dropdown,
                OptionsJson = """["Just me","My team"]""",
                VisibleToRequesters = true,
                EditableByRequesters = true,
                RequiredForRequesters = true,
            });
            await db.SaveChangesAsync();
            return true;
        });
        var dana = await f.RequesterAsync("Dana Form");

        var ex = await Assert.ThrowsAsync<ValidationException>(() => f.AsRequester(dana, p => p.CreateAsync(new NewPortalRequest
        {
            Subject = " ",
            CcEmails = ["not-an-email"],
            Category = "c:not-a-guid",
        })));
        Assert.Equal(["category", "cc", "custom.affected", "subject"], ex.Errors.Keys.Order());
        Assert.Equal("Answer “Who is affected?”.", ex.Errors["custom.affected"].Single());

        var bad = await Assert.ThrowsAsync<ValidationException>(() => f.AsRequester(dana, p => p.CreateAsync(new NewPortalRequest
        {
            Subject = "Printer offline",
            CustomFields = new Dictionary<string, string[]> { ["affected"] = ["Everyone"] },
        })));
        Assert.True(bad.Errors.ContainsKey("custom.affected"));

        var ticket = await f.AsRequester(dana, p => p.CreateAsync(new NewPortalRequest
        {
            Subject = "Printer offline",
            CustomFields = new Dictionary<string, string[]> { ["affected"] = ["My team"] },
        }));
        var view = await f.AsRequester(dana, p => p.GetAsync(ticket.Number));
        Assert.Contains(view.Fields, x => x.Label == "Who is affected?" && x.Text == "My team");
    }
}
