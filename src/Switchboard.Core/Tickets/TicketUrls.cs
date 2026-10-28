namespace Switchboard.Core.Tickets;

/// <summary>
/// Canonical paths to a ticket. Use these instead of hard-coding routes, so links stay right
/// when a page moves. Prefix with <c>SwitchboardOptions.BaseUrl</c> for links in email.
/// </summary>
public static class TicketUrls
{
    /// <summary>The agent ticket page (ServiceDesk).</summary>
    public static string Agent(int number) => $"/tickets/{number}";

    /// <summary>The requester's view in the portal.</summary>
    public static string Requester(int number) => $"/portal/requests/{number}";
}
