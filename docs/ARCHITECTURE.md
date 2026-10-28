# Switchboard architecture

This is how the code is put together. [SYSTEM_REQUIREMENTS.md](SYSTEM_REQUIREMENTS.md) says *what* to build; this page says *where* and *how*. UI rules are in [DESIGN_SYSTEM.md](DESIGN_SYSTEM.md); the rules for working on a module in parallel with others are in [MODULE_GUIDE.md](MODULE_GUIDE.md).

## Stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10, ASP.NET Core |
| UI | Razor Pages, Bootstrap 5.3, Bootstrap Icons, htmx 2 for partial updates, SignalR for live updates |
| Data | EF Core 10 on SQL Server 2025 |
| Auth | ASP.NET Core Identity (cookies), Argon2id hashing, TOTP two-step verification |
| Deployment | Docker image plus `docker-compose.yml` (app + SQL Server) |
| License | MIT |

## Repository layout

```
src/
  Switchboard.Core/      Domain model, EF context, permissions, events, ticket services. No UI.
  Switchboard.UI/        Razor class library: layouts, design-system CSS/JS, tag helpers, shell context.
  Switchboard.Web/       The host: Program.cs, sign-in/setup/profile pages, SignalR hub, files, PWA.
  Modules/
    Switchboard.Modules.<Name>/   One feature module each (Razor class library).
tests/
  Switchboard.Testing/   Integration test host (throwaway SQL Server database per test class).
  Switchboard.Core.Tests/
  Switchboard.Modules.<Name>.Tests/   (per module)
tools/ui-snap/           Headless screenshots of pages, for reviewing UI work.
docs/
```

## Modules

A module is a Razor class library with exactly one `ISwitchboardModule` implementation (`src/Switchboard.Core/Modules/ISwitchboardModule.cs`). The host finds every `Switchboard.Modules.*` assembly it references and calls:

| Hook | Use it to |
|---|---|
| `ConfigureServices` | Register services, nav items, UI slots, privileges, event handlers, hosted services |
| `ConfigureModel` | Add entities to the EF model: `modelBuilder.ApplyConfigurationsFromAssembly(typeof(MyModule).Assembly)` |
| `MapEndpoints` | Map minimal APIs or hubs. Razor Pages are discovered automatically |
| `ConfigureApp` | Add middleware (rare) |
| `SeedAsync` | Idempotent defaults; runs on every startup as the system actor |

**Pages** live in `Areas/<ModuleName>/Pages/` and declare an absolute route, so URLs stay clean:

```cshtml
@page "/kb/{id:guid}"
```

Each area has its own `_ViewImports.cshtml` and `_ViewStart.cshtml` (layout `_AgentLayout`, `_PortalLayout` or `_AdminLayout`).

**Navigation** comes from `services.AddNavItem(new NavItem(NavArea.Agent, "kb", "Knowledge", "bi-journal-text", "/kb", Order: 40))`. Admin items take a `Group` (shown as a heading in the admin menu) and normally a `Privilege`.

**Slots** let one module render inside another module's page without a compile-time dependency. A page renders `<sb-slot name="ticket.sidebar" model="Model.Ticket" />`; any module registers a view component for it: `services.AddSlot<SlaSidebarViewComponent>("ticket.sidebar", order: 20)`. The view component has one `InvokeAsync` parameter, typed as the slot model (`Ticket`, `Guid`, `string`…) or `object?`. It receives the model itself, or `null` for null-model slots. Slots in use:

| Slot | Rendered by | Model |
|---|---|---|
| `topbar.end` | agent layout, before the user menu | `ShellModel` |
| `topbar.new` | the "New" menu (add `<li>` items) | `ShellModel` |
| `usermenu.items` | user menu (add `dropdown-item` links) | `ShellModel` |
| `profile.sections` | `/profile` (add `sb-pane` sections) | `Guid` user id |
| `ticket.header` | ticket page, under the title | `Ticket` |
| `ticket.actions` | ticket page "More" menu: rendered inside `<ul class="dropdown-menu">`, so add `<li>` items | `Ticket` |
| `ticket.sidebar` | ticket page, right column | `Ticket` |
| `ticket.tabs` | ticket page tabs: each top-level `<section data-sb-tab-title="…">` you render becomes a tab | `Ticket` |
| `ticket.list.badges` | each queue row, after the subject | `TicketListItem` |
| `portal.home` | portal home, below the main paths | `null` |
| `portal.home.attention` | portal home "Waiting on you" pane: render one `a.prt-attention-item` (e.g. pending approvals with count) | `null` |
| `portal.search` | portal search results page | `string` query |
| `portal.newticket.aside` | portal "Report a problem" form, beside the fields. The subject input carries `data-sb-deflect-source` | `null` |
| `portal.ticket` | requester's ticket page sidebar | `Ticket` |
| `dashboard.widgets` | agent dashboard | `null` |
| `search.results` | global agent search page | `string` query |
| `reports.catalog` | `/reports` list of reports: add `<li><a href><i class="bi …"></i><strong>Title</strong><span>Description</span></a></li>` | `null` |
| `approval.subject` | approval detail and approval email pages: describe the record being approved | `AprRequest` |

Add a new slot by rendering `<sb-slot>` in a page you own and listing it in this table.

**Privileges** are declared with `services.AddPrivileges(new PrivilegeDefinition("kb.manage", "Manage the knowledge base", "Knowledge"))` and checked through `IPermissionService`. Built-in roles pick up module privileges ending in `.view` or `.use` automatically.

## Data

- One `SwitchboardDbContext` (`src/Switchboard.Core/Data`). Modules add entities in `ConfigureModel` and query them with `db.Set<T>()`.
- Entities derive from `Entity` (time-ordered GUID `Id`). Use `DateTimeOffset` in UTC, money as integer minor units plus currency (INV-MON-1).
- **Table names must be unique across modules.** Prefix every module table: Knowledge `Kb…`, Catalog `Cat…`, Approvals `Apr…`, Service levels `Sla…`, Automation `Aut…`, Messaging `Msg…`, Portal `Prt…`, Admin `Adm…`, Insights `Ins…`, API `Api…`, MCP `Mcp…`, Service desk `Sd…`.
- No migrations yet. In development the schema comes from `EnsureCreated`, so after you change your entities, restart once with `Switchboard__ResetDatabase=true` to recreate your database. Migrations are generated once all modules are integrated.
- Custom field values live in the owner's `CustomFieldsJson` column; definitions are `CustomFieldDefinition` rows. Validate with `ICustomFieldService`.

`SaveChangesAsync` does four things in one transaction:

1. Runs pre-commit event handlers.
2. Stamps `CreatedAt`/`UpdatedAt`.
3. Writes one `ActivityEntry` per changed `IActivitySubject` (INV-HIS-1). Call `db.Activity(entity, action, summary)` first to give the entry a readable sentence.
4. Writes raised events to the outbox.

## Writing tickets

Every ticket write goes through `ITicketService` (`src/Switchboard.Core/Tickets/TicketService.cs`): create, update (`TicketPatch`), reply, note, forward, requester message, merge, link, trash, spam, purge, tasks, sharing. It checks permissions, enforces the invariants and raises events. Don't set `Ticket` properties directly, except in a pre-commit handler that owns that field (for example the service-levels module setting `DueBy`).

Read tickets with:

- `ITicketSearch`: filter language plus permission trimming, with projections to `TicketListItem`.
- `ITicketService.GetForAgentAsync` / `GetForRequesterAsync`: one ticket, or `NotFoundException`.
- `IPermissionService.AgentVisibleTicketsAsync()` / `RequesterVisibleTicketsAsync()`: an `IQueryable<Ticket>` for custom queries.

The filter language (`TicketQueryLanguage`) is shared by the queue, API and MCP, for example `priority>=high group:mine is:lit -tag:vip created>-7d "vpn"`. The full field list is in `TicketQueryLanguage.Fields`.

## Events

Core raises `TicketCreated`, `TicketUpdated` (with field changes), `TicketStatusChanged`, `TicketAssigned`, `TicketConversationAdded`, `AgentsMentioned`, `TicketsMerged`, `TicketDeleted`, `TicketRestored` and `TicketTaskChanged` (`src/Switchboard.Core/Events/DomainEvents.cs`).

| Kind | Interface | When it runs | Use for |
|---|---|---|---|
| Pre-commit | `IPreCommitHandler<T>` (`services.AddPreCommitHandler<TEvent, THandler>()`) | Inside `SaveChanges`, same transaction | Keeping derived data consistent, e.g. SLA due dates |
| After commit | `IEventHandler<T>` (`services.AddEventHandler<TEvent, THandler>()`) | Via the outbox, at least once, as the system actor, retried with backoff | Email, notifications, automations, webhooks. Must be idempotent |

Modules may define their own events (records deriving from `DomainEvent`) and raise them with `db.Raise(...)`. `DomainEvent.CascadeDepth` tells automations how deep a chain is (SR-AUT-1).

## Security

- **Deny by default.** Every endpoint requires a signed-in user (fallback policy). Mark public pages `[AllowAnonymous]`.
- **Agent pages:** `[Authorize(Policy = SwitchboardPolicies.Agent)]`. Admin pages: `[Authorize(Policy = "priv:<privilege>")]` or `SwitchboardPolicies.OrgAdmin`.
- **Server-side checks.** Check privileges in services with `IPermissionService.DemandAsync(privilege, workspaceId)`, never only in the UI.
- **The actor.** `IActorAccessor.Current` says who is acting and through which channel. Background work wraps itself in `actors.BeginScope(Actor.System("…"))`.
- **Untrusted HTML.** Sanitize every piece of user HTML with `IHtmlContent.Sanitize` before storing it. Render stored HTML with `@Html.Raw` only if it was sanitized on the way in.
- **Files.** Upload with `POST /uploads` (multipart, antiforgery header) to get a pending attachment id. Attach it through a service (`ClaimPendingAsync`), and link to it as `/files/{id}/{name}`. Register an `IAttachmentAccessPolicy` for any new owner type.
- **Antiforgery.** Razor forms include the token automatically. htmx and `sb.fetch()` send it as a header.
- **Audit.** Security and configuration changes call `IAuditLog.Record(action, objectType, objectId, details)` in the same unit of work.
- **Exceptions.** Throw `ValidationException`, `ForbiddenException`, `NotFoundException` or `ConflictException` from services. The host turns them into status pages, or into problem+json for API, MCP and htmx calls.

## REST API and AI-agent tools

Both are extension points, so every module can expose its own data without depending on the API or MCP modules:

- **REST:** `endpoints.MapSwitchboardApi("kb", "Knowledge")` (`src/Switchboard.Core/Api/ApiConventions.cs`) returns a route group under `/api/v1/kb`. It authenticates with personal API keys or OAuth bearer tokens (scheme `SwitchboardApi`, registered by the API module), applies the `api` rate limit and appears in the OpenAPI document. Browser cookies are not accepted on API routes. Return problem details through the domain exceptions.
- **AI-agent tools (MCP):** implement `IAgentTool` (`src/Switchboard.Core/Agents/AgentTools.cs`) and register it with `services.AddAgentTool<T>()`. Each tool has:
  - a stable `snake_case` name;
  - a model-facing description, which must contain no user content;
  - a JSON input schema;
  - an OAuth scope;
  - an action class (R/W/X/A/D, SRS §4.6).

  Tools run as the delegating user through the same services as the UI. Return user-generated text wrapped with `AgentContent.Untrusted(...)`. The MCP module discovers all `IAgentTool` registrations and handles scopes, confirmations, rate limits and audit. The tool catalog is SRS Appendix B.

## Real time

`ILiveNotifier` pushes messages to users, ticket viewers or workspace agents. `ToTicketAsync` reaches **agents** watching a ticket (internal payloads allowed); `ToTicketRequestersAsync` reaches requesters watching it in the portal (requester-safe payloads only: no private content, agent names or presence, SR-AUTHZ-7). In the browser, listen with `document.addEventListener("sb:live", e => …)`; `e.detail` holds `{type, payload}`. Ticket pages set `ViewData["WatchTicket"] = ticket.Id` to join the ticket's room; agents also drive the collision-detection presence events (`ticket.presence`), which requesters never receive. Message types in use: `ticket.created`, `ticket.updated`, `ticket.conversation`, `ticket.presence`, `notification.created`, `agent_action.pending`. Name new types `area.event`.

## Settings and feature toggles

`ISettingsService` stores JSON values per instance or per workspace. Well-known keys are in `SettingKeys`. Feature toggles are `features.<module>` (default on); check them with `IsFeatureEnabledAsync("knowledge")`.

## Running locally

```bash
docker compose -f docker-compose.dev.yml up -d   # SQL Server on :14333, Mailpit SMTP :1025 / inbox http://localhost:8025
cd src/Switchboard.Web && dotnet run             # http://localhost:5080, demo data, admin@switchboard.local / Switchboard!2026
```

| Variable | Effect |
|---|---|
| `SWITCHBOARD_BUILD_SLOT=<name>` | Separate `bin`/`obj` under `.artifacts/<name>`, so parallel builds don't collide |
| `SWITCHBOARD_MODULES=ServiceDesk;Knowledge` | Build the host with only those modules |
| `ConnectionStrings__Default=…;Database=<name>` | Use your own database |
| `Switchboard__ResetDatabase=true` | Drop and recreate the development database on start |

Demo accounts (development only, password `Switchboard!2026`):

| Account | Role |
|---|---|
| `admin@switchboard.local` | Organization admin |
| `sam@switchboard.local` | Agent, global scope, Service Desk group |
| `priya@switchboard.local` | Supervisor, Service Desk and Network groups |
| `jordan@switchboard.local` | Agent, group scope, Hardware group |
| `dana@switchboard.local` | Requester, Finance, department head |

## Testing

Integration tests use `Switchboard.Testing.SwitchboardTestHost`, which creates and drops a database on the dev SQL Server. See `tests/Switchboard.Core.Tests/TicketServiceTests.cs` for the pattern. Run with:

```bash
SWITCHBOARD_BUILD_SLOT=tests dotnet test tests/Switchboard.Core.Tests
```

## Links between modules

Never hard-code another module's routes. Ticket pages: `TicketUrls.Agent(number)` (`/tickets/{number}`) and `TicketUrls.Requester(number)` (`/portal/requests/{number}`). Prefix with `SwitchboardOptions.BaseUrl` in email.
