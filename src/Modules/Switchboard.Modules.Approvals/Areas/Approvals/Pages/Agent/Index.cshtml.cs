using Microsoft.AspNetCore.Authorization;
using Switchboard.Core.Security;
using Switchboard.Modules.Approvals.Services;
using Switchboard.Modules.Approvals.Web;

namespace Switchboard.Modules.Approvals.Areas.Approvals.Pages.Agent;

[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class IndexModel(IApprovalService approvals, IApprovalDelegationService delegation, IActorAccessor actors)
    : ApprovalIndexPageBase(approvals, delegation, actors)
{
    public override string BasePath => "/approvals";
}
