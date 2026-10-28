using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Api;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Api.Auth;
using Switchboard.Modules.Api.Http;

namespace Switchboard.Modules.Api.Endpoints;

/// <summary>Tickets, conversations, tasks, links and relationships (FR-INT-1).</summary>
internal static partial class TicketEndpoints
{
    private static readonly string[] Sorts = ["updated", "created", "due", "priority", "number", "lamp"];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapSwitchboardApi("tickets", "Tickets").WithApiConventions();

        g.MapGet("", ListAsync)
            .WithName("listTickets")
            .WithSummary("List or search tickets")
            .WithDescription("Filter with q (the queue filter language, e.g. `priority>=high is:lit -tag:vip`), sort (updated, created, due, priority, number, lamp; prefix - for descending), per_page (1-100) and page. Follow meta.nextCursor (or the Link header) with ?cursor= for the next page. Requesters, and agents with ?as=requester, get only tickets they can see in the portal.")
            .Produces<ListResponse<TicketListItemDto>>()
            .RequireScope(ApiScopes.TicketsRead);

        g.MapPost("", CreateAsync)
            .WithName("createTicket")
            .WithSummary("Create a ticket")
            .WithDescription("Requesters (or agents with ?as=requester) file as themselves and may set only subject, descriptionHtml/descriptionText, type, requestedForId, ccEmails, customFields and attachmentIds. Send Idempotency-Key to retry safely.")
            .Accepts<CreateTicketRequest>("application/json")
            .Produces<TicketDto>(StatusCodes.Status201Created)
            .RequireScope(ApiScopes.TicketsWrite);

        g.MapGet("/{id:guid}", GetAsync)
            .WithName("getTicket")
            .WithSummary("Get a ticket")
            .WithDescription("include=conversations,tasks,links embeds related records. Agents get an ETag to send back as If-Match on PATCH. Requesters only ever receive public content.")
            .Produces<TicketDto>()
            .RequireScope(ApiScopes.TicketsRead);

        g.MapPatch("/{id:guid}", PatchAsync)
            .WithName("updateTicket")
            .WithSummary("Update ticket fields")
            .WithDescription("Send only the fields to change; null clears a nullable field. Unknown or read-only fields are rejected. With If-Match, a stale ETag returns 412.")
            .Accepts<TicketPatchRequest>("application/json")
            .Produces<TicketDto>()
            .AgentsOnly().RequireScope(ApiScopes.TicketsWrite);

        g.MapDelete("/{id:guid}", async (Guid id, ITicketService tickets, CancellationToken ct) =>
            {
                await tickets.MoveToTrashAsync(id, ct);
                return Results.NoContent();
            })
            .WithName("trashTicket").WithSummary("Move a ticket to trash")
            .AgentsOnly().RequireScope(ApiScopes.TicketsDelete);

        g.MapPost("/{id:guid}/restore", (Guid id, HttpContext http, ITicketService tickets, ApiTicketReader reader, CancellationToken ct) =>
                AfterAsync(http, reader, id, () => tickets.RestoreAsync(id, ct), ct))
            .WithName("restoreTicket").WithSummary("Restore a ticket from trash")
            .Produces<TicketDto>().AgentsOnly().RequireScope(ApiScopes.TicketsDelete);

        g.MapPost("/{id:guid}/spam", (Guid id, HttpContext http, ITicketService tickets, ApiTicketReader reader, CancellationToken ct) =>
                AfterAsync(http, reader, id, () => tickets.SetSpamAsync(id, true, ct), ct))
            .WithName("markTicketSpam").WithSummary("Mark a ticket as spam")
            .Produces<TicketDto>().AgentsOnly().RequireScope(ApiScopes.TicketsDelete);

        g.MapPost("/{id:guid}/unspam", (Guid id, HttpContext http, ITicketService tickets, ApiTicketReader reader, CancellationToken ct) =>
                AfterAsync(http, reader, id, () => tickets.SetSpamAsync(id, false, ct), ct))
            .WithName("unmarkTicketSpam").WithSummary("Mark a ticket as not spam")
            .Produces<TicketDto>().AgentsOnly().RequireScope(ApiScopes.TicketsDelete);

        g.MapPost("/{id:guid}/merge", async (Guid id, HttpContext http, ITicketService tickets, ApiTicketReader reader, CancellationToken ct) =>
            {
                var body = await ApiJson.ReadAsync<MergeRequest>(http.Request, ct);
                var ids = ApiBodies.Ids(body.TicketIds, "ticketIds") ?? throw new ValidationException("ticketIds", "List the tickets to merge into this one.");
                return await AfterAsync(http, reader, id, () => tickets.MergeAsync(id, ids, ct), ct);
            })
            .WithName("mergeTickets").WithSummary("Merge other tickets into this one")
            .WithDescription("The tickets in ticketIds are closed and merged into the ticket in the path.")
            .Accepts<MergeRequest>("application/json").Produces<TicketDto>()
            .AgentsOnly().RequireScope(ApiScopes.TicketsWrite);

        MapChildren(g);
        MapLinks(g);
        MapConversations(g);
        MapTasks(g);
    }

    // ---------------------------------------------------------------- tickets

    private static async Task<IResult> ListAsync(HttpContext http, ITicketSearch search, IPermissionService permissions, CancellationToken ct)
    {
        var request = http.Request;
        ListCursor cursor;
        if (Paging.Text(request, "cursor", 8000) is { } raw)
        {
            cursor = ListCursor.Decode(raw);
        }
        else
        {
            var sort = Paging.Text(request, "sort", 20);
            if (sort is not null && !Sorts.Contains(sort.TrimStart('-', '+'), StringComparer.OrdinalIgnoreCase))
            {
                throw new ValidationException("sort", $"Sort by one of: {string.Join(", ", Sorts)}. Prefix - for descending.");
            }
            cursor = new ListCursor(
                Paging.Text(request, "q", 2000),
                sort,
                Paging.Int(request, "page", 1, 1, Paging.MaxPage),
                Paging.Int(request, "per_page", Paging.DefaultPerPage, 1, Paging.MaxPerPage),
                await ApiViews.RequesterModeAsync(request, permissions, ct));
        }
        if (!cursor.AsRequester && (await permissions.CurrentAsync(ct))?.IsAgent != true)
        {
            cursor = cursor with { AsRequester = true };
        }

        var result = await search.SearchAsync(new TicketSearchRequest
        {
            Query = cursor.Q,
            Sort = cursor.Sort,
            Page = cursor.Page,
            PageSize = cursor.PerPage,
            AsRequester = cursor.AsRequester,
        }, ct);
        var meta = Paging.Meta(http, result.Total, cursor);

        if (cursor.AsRequester)
        {
            var items = result.Items.Select(t => new RequesterTicketListItemDto(
                t.Id, t.Number, t.DisplayId, t.Type, t.Subject,
                new RequesterStatusDto(string.IsNullOrWhiteSpace(t.StatusRequesterLabel) ? t.StatusName : t.StatusRequesterLabel, t.StatusCategory),
                t.CreatedAt, t.UpdatedAt, t.ResolvedAt)).ToList();
            return ApiJson.Ok(new ListResponse<RequesterTicketListItemDto>(items, meta));
        }

        return ApiJson.Ok(new ListResponse<TicketListItemDto>(result.Items.Select(ToDto).ToList(), meta));
    }

    private static TicketListItemDto ToDto(TicketListItem t) => new()
    {
        Id = t.Id,
        Number = t.Number,
        DisplayId = t.DisplayId,
        Type = t.Type,
        WorkspaceId = t.WorkspaceId,
        Subject = t.Subject,
        Status = new StatusDto(t.StatusId, t.StatusName, t.StatusCategory),
        Priority = t.Priority,
        Group = t.GroupId is { } g ? new RefDto(g, t.GroupName ?? "") : null,
        Agent = t.AgentId is { } a ? new PersonRefDto(a, t.AgentName ?? "") : null,
        Requester = new PersonRefDto(t.RequesterId, t.RequesterName, t.RequesterEmail),
        Source = t.SourceKey,
        IsWaiting = t.IsLampLit,
        IsMajorIncident = t.IsMajorIncident,
        IsSpam = t.IsSpam,
        DeletedAt = t.DeletedAt,
        DueBy = t.DueBy,
        FirstResponseDueBy = t.FirstResponseDueBy,
        FirstRespondedAt = t.FirstRespondedAt,
        ResolvedAt = t.ResolvedAt,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
    };

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, ApiTicketReader reader, IPermissionService permissions, CancellationToken ct)
    {
        var include = ApiTicketReader.ParseIncludes(http.Request.Query["include"]);
        if (await ApiViews.RequesterModeAsync(http.Request, permissions, ct))
        {
            return ApiJson.Ok(await reader.GetForRequesterAsync(id, include, ct));
        }
        var (ticket, version) = await reader.GetForAgentAsync(id, include, ct);
        http.Response.Headers.ETag = ETags.Format(version);
        return ApiJson.Ok(ticket);
    }

    private static async Task<IResult> CreateAsync(HttpContext http, ITicketService tickets, ApiTicketReader reader, IPermissionService permissions, IHtmlContent html, CancellationToken ct)
    {
        var body = await ApiJson.ReadAsync<CreateTicketRequest>(http.Request, ct);
        var asRequester = await ApiViews.RequesterModeAsync(http.Request, permissions, ct);
        var ticket = await tickets.CreateAsync(ToCommand(body, asRequester, null, html), ct);
        return await CreatedAsync(http, reader, ticket, asRequester, ct);
    }

    private static async Task<IResult> PatchAsync(Guid id, HttpContext http, ITicketService tickets, ApiTicketReader reader, CancellationToken ct)
    {
        byte[]? expected = null;
        if (http.Request.Headers.IfMatch.Count > 0 && !ETags.TryParseIfMatch(http.Request.Headers.IfMatch.ToString(), out expected))
        {
            return ApiProblems.Result(http, 412, ApiProblems.TitleFor(412), "If-Match must be one ETag from a GET of this ticket, or *.");
        }
        using var doc = await ApiJson.ReadDocumentAsync(http.Request, ct);
        var patch = PatchReader.ReadTicketPatch(doc.RootElement);
        await tickets.UpdateAsync(id, patch, expected, ct);
        return await AgentViewAsync(http, reader, id, StatusCodes.Status200OK, ct);
    }

    internal static CreateTicketCommand ToCommand(CreateTicketRequest b, bool asRequester, Guid? parentId, IHtmlContent html)
    {
        if (asRequester && b.AgentOnlyFieldsSet().ToList() is { Count: > 0 } agentOnly)
        {
            throw new ValidationException(agentOnly.ToDictionary(f => f, _ => new[] { "Only agents can set this field." }));
        }
        return new CreateTicketCommand
        {
            WorkspaceId = b.WorkspaceId,
            Type = b.Type ?? TicketType.Incident,
            Subject = b.Subject ?? "",
            DescriptionHtml = ApiBodies.Html(html, b.DescriptionHtml, b.DescriptionText, "description"),
            RequesterId = b.RequesterId,
            RequesterEmail = b.RequesterEmail,
            RequesterName = b.RequesterName,
            RequestedForId = b.RequestedForId,
            CcEmails = b.CcEmails,
            StatusId = b.StatusId,
            Priority = b.Priority,
            Urgency = b.Urgency,
            Impact = b.Impact,
            SourceKey = TicketSourceKeys.Api,
            CategoryId = b.CategoryId,
            SubCategoryId = b.SubCategoryId,
            ItemCategoryId = b.ItemCategoryId,
            GroupId = b.GroupId,
            AgentId = b.AgentId,
            DepartmentId = b.DepartmentId,
            Tags = b.Tags,
            CustomFields = b.CustomFields,
            AttachmentIds = ApiBodies.Ids(b.AttachmentIds, "attachmentIds"),
            ParentId = parentId,
            IsMajorIncident = b.IsMajorIncident ?? false,
            PlannedStartAt = b.PlannedStartAt,
            PlannedEndAt = b.PlannedEndAt,
            PlannedEffortMinutes = b.PlannedEffortMinutes,
            AsRequester = asRequester,
        };
    }

    private static async Task<IResult> CreatedAsync(HttpContext http, ApiTicketReader reader, Ticket ticket, bool asRequester, CancellationToken ct)
    {
        http.Response.Headers.Location = $"{SwitchboardApi.Prefix}/tickets/{ticket.Id}";
        if (asRequester)
        {
            return ApiJson.Ok(await reader.GetForRequesterAsync(ticket.Id, TicketIncludes.None, ct), StatusCodes.Status201Created);
        }
        return await AgentViewAsync(http, reader, ticket.Id, StatusCodes.Status201Created, ct);
    }

    /// <summary>
    /// The agent view after a write. If the write moved the ticket out of the caller's ticket scope
    /// (e.g. reassigned to another group), the change still happened: 204 instead of a misleading 404.
    /// </summary>
    internal static async Task<IResult> AgentViewAsync(HttpContext http, ApiTicketReader reader, Guid id, int status, CancellationToken ct)
    {
        try
        {
            var (ticket, version) = await reader.GetForAgentAsync(id, TicketIncludes.None, ct);
            http.Response.Headers.ETag = ETags.Format(version);
            return ApiJson.Ok(ticket, status);
        }
        catch (NotFoundException)
        {
            return status == StatusCodes.Status201Created ? Results.StatusCode(StatusCodes.Status201Created) : Results.NoContent();
        }
    }

    private static async Task<IResult> AfterAsync(HttpContext http, ApiTicketReader reader, Guid id, Func<Task> action, CancellationToken ct)
    {
        await action();
        return await AgentViewAsync(http, reader, id, StatusCodes.Status200OK, ct);
    }

    // ---------------------------------------------------------------- children

    private static void MapChildren(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}/children", async (Guid id, IPermissionService permissions, CancellationToken ct) =>
            {
                var visible = await permissions.AgentVisibleTicketsAsync(ct);
                if (!await visible.AnyAsync(t => t.Id == id && t.PurgedAt == null, ct))
                {
                    throw new NotFoundException();
                }
                var children = await visible.AsNoTracking()
                    .Where(t => t.ParentId == id && t.PurgedAt == null)
                    .OrderBy(t => t.Number)
                    .Select(t => new TicketListItemDto
                    {
                        Id = t.Id,
                        Number = t.Number,
                        DisplayId = (t.Type == TicketType.Incident ? "INC-" : "SR-") + t.Number,
                        Type = t.Type,
                        WorkspaceId = t.WorkspaceId,
                        Subject = t.Subject,
                        Status = new StatusDto(t.StatusId, t.Status!.Name, t.Status.Category),
                        Priority = t.Priority,
                        Group = t.Group != null ? new RefDto(t.Group.Id, t.Group.Name) : null,
                        Agent = t.Agent != null ? new PersonRefDto(t.Agent.Id, t.Agent.DisplayName, null) : null,
                        Requester = new PersonRefDto(t.RequesterId, t.Requester!.DisplayName, t.Requester.Email),
                        Source = t.Source!.Key,
                        IsWaiting = t.AwaitingAgentSince != null
                            && (t.Status.Category == StatusCategory.Open || t.Status.Category == StatusCategory.Pending)
                            && t.DeletedAt == null && !t.IsSpam && t.MergedIntoId == null,
                        IsMajorIncident = t.IsMajorIncident,
                        IsSpam = t.IsSpam,
                        DeletedAt = t.DeletedAt,
                        DueBy = t.DueBy,
                        FirstResponseDueBy = t.FirstResponseDueBy,
                        FirstRespondedAt = t.FirstRespondedAt,
                        ResolvedAt = t.ResolvedAt,
                        CreatedAt = t.CreatedAt,
                        UpdatedAt = t.UpdatedAt,
                    })
                    .Take(500)
                    .ToListAsync(ct);
                return ApiJson.Ok(new ItemsResponse<TicketListItemDto>(children));
            })
            .WithName("listChildTickets").WithSummary("List child tickets")
            .Produces<ItemsResponse<TicketListItemDto>>().AgentsOnly().RequireScope(ApiScopes.TicketsRead);

        g.MapPost("/{id:guid}/children", async (Guid id, HttpContext http, ITicketService tickets, ApiTicketReader reader, IHtmlContent html, CancellationToken ct) =>
            {
                var body = await ApiJson.ReadAsync<CreateTicketRequest>(http.Request, ct);
                var child = await tickets.CreateAsync(ToCommand(body, false, id, html), ct);
                return await CreatedAsync(http, reader, child, false, ct);
            })
            .WithName("createChildTicket").WithSummary("Create a child ticket")
            .Accepts<CreateTicketRequest>("application/json").Produces<TicketDto>(StatusCodes.Status201Created)
            .AgentsOnly().RequireScope(ApiScopes.TicketsWrite);
    }

    // ---------------------------------------------------------------- links

    [GeneratedRegex("^[a-z][a-z0-9_]{0,49}$", RegexOptions.CultureInvariant)]
    private static partial Regex LinkWord();

    private static void MapLinks(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}/links", async (Guid id, ApiTicketReader reader, IPermissionService permissions, CancellationToken ct) =>
            {
                await EnsureAgentVisibleAsync(permissions, id, ct);
                return ApiJson.Ok(new ItemsResponse<LinkDto>(await reader.LinksAsync(id, ct)));
            })
            .WithName("listTicketLinks").WithSummary("List a ticket's links")
            .Produces<ItemsResponse<LinkDto>>().AgentsOnly().RequireScope(ApiScopes.TicketsRead);

        g.MapPost("/{id:guid}/links", async (Guid id, HttpContext http, ITicketService tickets, CancellationToken ct) =>
            {
                var body = await ApiJson.ReadAsync<LinkRequest>(http.Request, ct);
                var errors = new Dictionary<string, string[]>();
                if (body.TargetType is null || !LinkWord().IsMatch(body.TargetType))
                {
                    errors["targetType"] = ["Use a record type such as ticket, problem, change or asset."];
                }
                if (body.TargetId is null)
                {
                    errors["targetId"] = ["Give the id of the record to link."];
                }
                if (body.LinkType is not null && !LinkWord().IsMatch(body.LinkType))
                {
                    errors["linkType"] = ["Use a link type such as related, problem, change_causing or asset."];
                }
                if (errors.Count > 0)
                {
                    throw new ValidationException(errors);
                }
                var link = await tickets.LinkAsync(id, body.TargetType!, body.TargetId!.Value, body.LinkType ?? TicketLinkTypes.Related, ct);
                return ApiJson.Ok(new LinkDto(link.Id, link.TicketId, link.TargetType, link.TargetId, link.LinkType, link.CreatedAt), StatusCodes.Status201Created);
            })
            .WithName("createTicketLink").WithSummary("Link a ticket to another record")
            .Accepts<LinkRequest>("application/json").Produces<LinkDto>(StatusCodes.Status201Created)
            .AgentsOnly().RequireScope(ApiScopes.TicketsWrite);

        g.MapDelete("/{id:guid}/links/{linkId:guid}", async (Guid id, Guid linkId, SwitchboardDbContext db, ITicketService tickets, CancellationToken ct) =>
            {
                if (!await db.TicketLinks.AnyAsync(l => l.Id == linkId && l.TicketId == id, ct))
                {
                    throw new NotFoundException();
                }
                await tickets.UnlinkAsync(linkId, ct);
                return Results.NoContent();
            })
            .WithName("deleteTicketLink").WithSummary("Remove a link")
            .AgentsOnly().RequireScope(ApiScopes.TicketsWrite);
    }

    // ---------------------------------------------------------------- conversations

    private static void MapConversations(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}/conversations", async (Guid id, HttpContext http, ApiTicketReader reader, IPermissionService permissions, CancellationToken ct) =>
            {
                if (await ApiViews.RequesterModeAsync(http.Request, permissions, ct))
                {
                    var ticket = await reader.GetForRequesterAsync(id, TicketIncludes.Conversations, ct);
                    return ApiJson.Ok(new ItemsResponse<RequesterConversationDto>(ticket.Conversations ?? []));
                }
                await EnsureAgentVisibleAsync(permissions, id, ct);
                return ApiJson.Ok(new ItemsResponse<ConversationDto>(await reader.AgentConversationsAsync(id, ct)));
            })
            .WithName("listConversations").WithSummary("List a ticket's conversation")
            .WithDescription("Agents see replies, notes and forwards. Requesters see only public messages.")
            .Produces<ItemsResponse<ConversationDto>>().RequireScope(ApiScopes.TicketsRead);

        g.MapPost("/{id:guid}/reply", async (Guid id, HttpContext http, ITicketService tickets, ApiTicketReader reader, IHtmlContent html, CancellationToken ct) =>
            {
                var body = await ApiJson.ReadAsync<ReplyRequest>(http.Request, ct);
                var conversation = await tickets.ReplyAsync(id, new ReplyCommand
                {
                    BodyHtml = ApiBodies.Html(html, body.BodyHtml, body.BodyText),
                    Cc = body.Cc,
                    Bcc = body.Bcc,
                    AttachmentIds = ApiBodies.Ids(body.AttachmentIds, "attachmentIds"),
                    StatusId = body.StatusId,
                }, ct);
                return ApiJson.Ok(await reader.AgentConversationAsync(id, conversation.Id, ct), StatusCodes.Status201Created);
            })
            .WithName("replyToTicket").WithSummary("Reply to the requester")
            .Accepts<ReplyRequest>("application/json").Produces<ConversationDto>(StatusCodes.Status201Created)
            .AgentsOnly().RequireScope(ApiScopes.TicketsReply);

        g.MapPost("/{id:guid}/notes", async (Guid id, HttpContext http, ITicketService tickets, ApiTicketReader reader, IHtmlContent html, CancellationToken ct) =>
            {
                var body = await ApiJson.ReadAsync<NoteRequest>(http.Request, ct);
                var conversation = await tickets.AddNoteAsync(id, new NoteCommand
                {
                    BodyHtml = ApiBodies.Html(html, body.BodyHtml, body.BodyText),
                    IsPrivate = body.IsPrivate ?? true,
                    NotifyUserIds = ApiBodies.Ids(body.NotifyUserIds, "notifyUserIds"),
                    AttachmentIds = ApiBodies.Ids(body.AttachmentIds, "attachmentIds"),
                }, ct);
                return ApiJson.Ok(await reader.AgentConversationAsync(id, conversation.Id, ct), StatusCodes.Status201Created);
            })
            .WithName("addTicketNote").WithSummary("Add a note")
            .WithDescription("Private by default. A public note (isPrivate: false) is visible to the requester and needs the tickets:reply scope.")
            .Accepts<NoteRequest>("application/json").Produces<ConversationDto>(StatusCodes.Status201Created)
            .AgentsOnly().RequireScope(ApiScopes.TicketsWrite);

        g.MapPost("/{id:guid}/forward", async (Guid id, HttpContext http, ITicketService tickets, ApiTicketReader reader, IHtmlContent html, CancellationToken ct) =>
            {
                var body = await ApiJson.ReadAsync<ForwardRequest>(http.Request, ct);
                var conversation = await tickets.ForwardAsync(id, new ForwardCommand
                {
                    To = body.To ?? [],
                    Cc = body.Cc,
                    BodyHtml = ApiBodies.Html(html, body.BodyHtml, body.BodyText),
                    AttachmentIds = ApiBodies.Ids(body.AttachmentIds, "attachmentIds"),
                }, ct);
                return ApiJson.Ok(await reader.AgentConversationAsync(id, conversation.Id, ct), StatusCodes.Status201Created);
            })
            .WithName("forwardTicket").WithSummary("Forward a ticket to someone outside it")
            .Accepts<ForwardRequest>("application/json").Produces<ConversationDto>(StatusCodes.Status201Created)
            .AgentsOnly().RequireScope(ApiScopes.TicketsReply);

        g.MapPost("/{id:guid}/messages", async (Guid id, HttpContext http, ITicketService tickets, ApiTicketReader reader, IHtmlContent html, CancellationToken ct) =>
            {
                var body = await ApiJson.ReadAsync<RequesterMessageRequest>(http.Request, ct);
                var result = await tickets.AddRequesterMessageAsync(id, new RequesterMessageCommand
                {
                    BodyHtml = ApiBodies.Html(html, body.BodyHtml, body.BodyText),
                    AttachmentIds = ApiBodies.Ids(body.AttachmentIds, "attachmentIds"),
                }, ct);
                var conversation = await reader.RequesterConversationAsync(result.Ticket.Id, result.Conversation.Id, ct);
                return ApiJson.Ok(new RequesterMessageResultDto(result.Ticket.Id, result.CreatedFollowUp, conversation), StatusCodes.Status201Created);
            })
            .WithName("addRequesterMessage").WithSummary("Reply as the requester")
            .WithDescription("For requesters (and anyone the ticket is shared with) adding to their own ticket. Replying to a closed ticket may open a follow-up ticket instead; ticketId says which ticket got the message.")
            .Accepts<RequesterMessageRequest>("application/json").Produces<RequesterMessageResultDto>(StatusCodes.Status201Created)
            .RequireScope(ApiScopes.TicketsWrite);
    }

    // ---------------------------------------------------------------- tasks

    private static void MapTasks(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}/tasks", async (Guid id, ApiTicketReader reader, IPermissionService permissions, CancellationToken ct) =>
            {
                await EnsureAgentVisibleAsync(permissions, id, ct);
                return ApiJson.Ok(new ItemsResponse<TaskDto>(await reader.TasksAsync(id, ct)));
            })
            .WithName("listTicketTasks").WithSummary("List a ticket's tasks")
            .Produces<ItemsResponse<TaskDto>>().AgentsOnly().RequireScope(ApiScopes.TicketsRead);

        g.MapPost("/{id:guid}/tasks", async (Guid id, HttpContext http, ITicketService tickets, CancellationToken ct) =>
            {
                var body = await ApiJson.ReadAsync<CreateTaskRequest>(http.Request, ct);
                var task = await tickets.AddTaskAsync(id, new TaskCommand
                {
                    Title = body.Title ?? "",
                    Description = body.Description,
                    AssigneeId = body.AssigneeId,
                    GroupId = body.GroupId,
                    DueAt = body.DueAt,
                    NotifyBeforeMinutes = body.NotifyBeforeMinutes,
                    PlannedStartAt = body.PlannedStartAt,
                    PlannedEndAt = body.PlannedEndAt,
                    PlannedEffortMinutes = body.PlannedEffortMinutes,
                }, ct);
                return ApiJson.Ok(ApiTicketReader.ToDto(task), StatusCodes.Status201Created);
            })
            .WithName("createTicketTask").WithSummary("Add a task")
            .Accepts<CreateTaskRequest>("application/json").Produces<TaskDto>(StatusCodes.Status201Created)
            .AgentsOnly().RequireScope(ApiScopes.TicketsWrite);

        g.MapPatch("/{id:guid}/tasks/{taskId:guid}", async (Guid id, Guid taskId, HttpContext http, SwitchboardDbContext db, ITicketService tickets, CancellationToken ct) =>
            {
                await EnsureTaskAsync(db, id, taskId, ct);
                using var doc = await ApiJson.ReadDocumentAsync(http.Request, ct);
                var task = await tickets.UpdateTaskAsync(taskId, PatchReader.ReadTaskPatch(doc.RootElement), ct);
                return ApiJson.Ok(ApiTicketReader.ToDto(task));
            })
            .WithName("updateTicketTask").WithSummary("Update a task")
            .Accepts<TaskPatchRequest>("application/json").Produces<TaskDto>()
            .AgentsOnly().RequireScope(ApiScopes.TicketsWrite);

        g.MapDelete("/{id:guid}/tasks/{taskId:guid}", async (Guid id, Guid taskId, SwitchboardDbContext db, ITicketService tickets, CancellationToken ct) =>
            {
                await EnsureTaskAsync(db, id, taskId, ct);
                await tickets.DeleteTaskAsync(taskId, ct);
                return Results.NoContent();
            })
            .WithName("deleteTicketTask").WithSummary("Delete a task")
            .AgentsOnly().RequireScope(ApiScopes.TicketsWrite);
    }

    private static async Task EnsureTaskAsync(SwitchboardDbContext db, Guid ticketId, Guid taskId, CancellationToken ct)
    {
        // Visibility is checked by the service; this keeps a task from being reached through another ticket's URL.
        if (!await db.TicketTasks.AnyAsync(t => t.Id == taskId && t.TicketId == ticketId, ct))
        {
            throw new NotFoundException();
        }
    }

    internal static async Task EnsureAgentVisibleAsync(IPermissionService permissions, Guid ticketId, CancellationToken ct)
    {
        var visible = await permissions.AgentVisibleTicketsAsync(ct);
        if (!await visible.AnyAsync(t => t.Id == ticketId && t.PurgedAt == null, ct))
        {
            throw new NotFoundException();
        }
    }
}
