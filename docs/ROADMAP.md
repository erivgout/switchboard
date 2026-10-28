# Roadmap

What exists today, what is missing inside the built modules, and what comes next. Requirement IDs refer to [SYSTEM_REQUIREMENTS.md](SYSTEM_REQUIREMENTS.md).

## Built: first release (P0 plus parts of P1)

| Area | Module | Highlights |
|---|---|---|
| Foundation | Core, UI, Web | One permission engine with agent and requester visibility; ticket service with merge, child tickets, trash and spam, and reopen rules; filter language; activity entry per change; events delivered after commit through an outbox; hash-chained audit log; Argon2id passwords and two-step verification; first-run setup token; PWA; live updates; design system |
| Agent workspace | ServiceDesk | Queue with saved views, Kanban, bulk jobs and CSV export; ticket page with composer, collision detection, tasks, links and activity; canned responses; ticket templates; global search |
| Requester portal | Portal | Home, search, report a problem with article suggestions, my requests (own, shared, department), request page showing only public content |
| Service catalog and approvals | Catalog, Approvals | Form builder, requests on someone's behalf, multi-level approvals (any, everyone, majority) with delegation and immutable decisions, resubmit after rejection |
| Knowledge | Knowledge | Folder visibility, versions with diff, approval to publish, feedback, announcements, deflection, optional public knowledge base |
| Email and notifications | Messaging | SMTP out; IMAP or webhook in with threading, loop and spoofing protection; in-app notifications; templates; delivery log |
| Service levels and reporting | ServiceLevels, Insights | Business calendars with DST and holidays; SLA policies with pause, override, reminders and escalations; dashboard, team dashboard, reports |
| Automation | Automation | Workflows (event and time triggers) with test runs, exactly-once execution and loop limits; scenarios; closure rules; scheduled tickets; round-robin and least-busy assignment |
| Administration | Admin | People (invite, deactivate, merge, erase), roles, groups, departments, locations, statuses, categories, custom fields, settings, feature toggles, audit log viewer |
| Integrations | Api | REST API with API keys, idempotency, ETags and OpenAPI; signed webhooks with SSRF protection |
| AI agents | Mcp | OAuth 2.1 (PKCE, dynamic registration, client metadata documents), MCP server with 24 core tools plus module tools, out-of-band confirmation for risky actions, untrusted-content labelling, audit, kill switches |

636 automated tests across 13 suites run against SQL Server.

## Gaps inside the built modules

- **Tickets:**
  - time tracking (FR-TKT-16);
  - the major-incident workspace (FR-INC-3/4/5);
  - XLSX export;
  - inserting a whole article or inline images in the composer;
  - automatic signatures.
- **Portal:** request on behalf of someone else, cancel (as distinct from close), language preference.
- **Catalog:**
  - stock and license fulfilment;
  - conditional form fields;
  - several items per request;
  - delivery-time target feeding SLAs (FR-SLA-8).
- **Approvals:** parallel levels; deciding from Slack, Teams or mobile.
- **Knowledge:** translations (FR-KB-4), external sources (FR-KB-8), review reminders, video embeds.
- **Email:**
  - Microsoft 365 and Gmail OAuth mailboxes;
  - per-language templates;
  - IMAP has only been tested through the shared pipeline, not against a real server.
- **Service levels:** OLAs (FR-SLA-7), XLAs (FR-SLA-9).
- **Reports:** scheduled delivery, PDF/XLSX, custom dashboards.
- **Automation:**
  - drag-and-drop canvas;
  - delay, approval and lookup steps;
  - manual trigger from the API or MCP;
  - skill-based routing.
- **Admin:**
  - per-role field visibility (FR-IAM-12);
  - usage limits dashboard (FR-ORG-8).
  - **Not yet enforced by sign-in:** the saved session, password and IP-range settings.
- **API:** OAuth for REST, `RateLimit-*` headers, field selection, keyset paging.
- **MCP:**
  - usage quotas;
  - anomaly throttling;
  - undo and the agent activity timeline;
  - resource subscriptions.
  - Sessions are in memory, so run one instance or use sticky sessions.
- **Deployment:** data-protection keys are stored unencrypted on the app volume. Add an option to protect them with a certificate or key vault.

## Shared follow-ups

- **User lifecycle events:** add `UserDeactivated`, `UsersMerged` and `UserForgotten`, so the API, MCP and other modules revoke and re-point their own data.
- **Core additions:**
  - a requester close (`CloseAsRequesterAsync`);
  - a spam-changed event, so SLA clocks pause at once;
  - a module demo-data hook after demo users exist;
  - module-declared default role grants;
  - removing ticket links by target.
- **Shared UI:**
  - move the people picker into the UI library (Approvals and Portal each have one);
  - add a per-nav-item count, such as pending approvals.
- **Agent tools:** argument-dependent action classes, a progress reporter, and scope hints on privileges.
- **Notifications:** have Messaging send SLA, knowledge-review and approval-request notifications, then turn off the SLA module's own emails (`sla.email_notifications`).

## Next phases

These modules follow the same pattern (see [MODULE_GUIDE.md](MODULE_GUIDE.md)):

1. **Problem, change and release management:**
   - CAB and the change calendar;
   - maintenance and blackout windows (§3.7–3.9).
2. **Assets and CMDB:**
   - relationships and impact analysis;
   - discovery built on osquery plus a network probe and cloud adapters (§3.15);
   - software and SaaS (§3.16);
   - contracts (§3.17), procurement (§3.18).
3. **IT operations:** alerts, on-call schedules and paging, service health, status pages (§3.19).
4. **Employee lifecycle:** onboarding, offboarding, journeys, document generation, e-signature (§3.21).
5. **AI:**
   - model provider adapters;
   - an AI agent for the portal;
   - copilot for agents;
   - insights (§3.27).
6. **Platform:**
   - custom objects (§3.28);
   - config-as-code and sandbox promotion (FR-ADM-5/6);
   - the Freshservice importer (FR-ADM-7);
   - Slack and Teams apps (FR-CH-6);
   - Web Push for the PWA;
   - projects (§3.20).
