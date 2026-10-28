using Microsoft.AspNetCore.Authorization;
using Switchboard.Core.Security;
using Switchboard.Modules.Approvals.Services;
using Switchboard.Modules.Approvals.Web;

namespace Switchboard.Modules.Approvals.Areas.Approvals.Pages.Agent;

[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class DetailModel(IApprovalService approvals) : ApprovalDetailPageBase(approvals)
{
    public override string BasePath => "/approvals";
}
