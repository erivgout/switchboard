using Switchboard.Modules.Approvals.Services;
using Switchboard.Modules.Approvals.Web;

namespace Switchboard.Modules.Approvals.Areas.Approvals.Pages.Portal;

public sealed class DetailModel(IApprovalService approvals) : ApprovalDetailPageBase(approvals)
{
    public override string BasePath => "/portal/approvals";
}
