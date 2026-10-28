# Switchboard design system

Every screen is built from these parts. The tokens and components live in
`src/Switchboard.UI/wwwroot/css/switchboard.css`. Run the app in Development and open
**`/styleguide`** to see every component, in both themes.

## The idea

A manual telephone exchange: enamel-painted cabinets, brass jacks, and lamps that light when a line needs an operator. Switchboard routes requests to the people who can help, so the interface borrows that vocabulary.

- **One bold element: the lamp.** A small round indicator next to each ticket.
  - **Lit (brass):** the ticket is waiting on an agent.
  - **Red:** waiting and overdue.
  - **Hollow ring:** not waiting.

  Lamps differ by fill as well as color, so they read without color vision. The top bar's "N waiting" counter counts lit lamps on your lines. Use the lamp only for this meaning. Never decorate with it.
- **Everything else is quiet.** Flat panes separated by 1px rules. Shadows only on overlays (menus, modals, toasts). No gradients, no decorative cards, no hover animation on every element.
- **One orchestrated motion.** A lamp lighting when a new request arrives live (`sb-lamp--arriving`). Everything else is instant, or a short transition that shows what changed.

## Tokens

All colors are CSS custom properties, with light and dark values. Never hard-code a hex value in a page.

| Token | Light | Use |
|---|---|---|
| `--sb-panel` | `#e9eeeb` | App background (cabinet grey-green) |
| `--sb-surface` | `#ffffff` | Panes, inputs, table rows |
| `--sb-surface-2` | `#f4f7f5` | Table headers, inset areas |
| `--sb-ink` / `--sb-ink-muted` / `--sb-ink-faint` | `#1b2422` / `#5b6763` / `#8a9692` | Text / secondary text / decoration only (not for text) |
| `--sb-rule` / `--sb-rule-strong` | `#d3dbd7` / `#b3bfba` | Borders |
| `--sb-cord` | `#0b6e79` | Primary actions, links, focus, selection |
| `--sb-brass`, `--sb-lamp` | `#a47a2c`, `#d29a2d` | Lamps, active-nav marker, private-note label |
| `--sb-danger` / `--sb-warning` / `--sb-success` | `#b42318` / `#8a5300` / `#1e7a4c` | States, each with a `-tint` background |
| `--sb-note` | `#f8f2e2` | Private-note paper |
| `--sb-rail` | `#16201e` | Agent rail, portal header, sign-in panel |

Bootstrap's variables (`--bs-primary`, `--bs-body-bg`, `--bs-border-color`, …) are mapped to these, so standard Bootstrap classes pick up the palette. Dark mode is `data-bs-theme="dark"` on `<html>`; users switch it from the user menu.

## Type

- **Atkinson Hyperlegible Next** for everything users read and type. It was designed for legibility and keeps `I l 1` and `O 0` distinct, which matters for IDs, asset tags and email addresses. Base size: 15px in the agent console, 16px in the portal.
- **Big Shoulders** (industrial signage) is used **only** for:
  - page titles (`.sb-title`);
  - ticket and record IDs (`.sb-stamp`, e.g. `INC-1042`);
  - big numbers (`.sb-num`);
  - the wordmark.

  Never use it for body text, labels, or anything a user typed.
- Sentence case everywhere. **No all-caps labels**, no small caps, no letter-spaced eyebrows above headings.
- Times and counts use tabular numerals (`.tabular`; already on tables and `time`).

## Layouts

| Layout | Use for | Notes |
|---|---|---|
| `_AgentLayout` | Agent console | Dark rail with grouped nav, top bar (search with `/` shortcut, waiting counter, New menu, notifications slot, user menu), content area |
| `_AdminLayout` | Admin pages | Agent layout plus a grouped settings menu on the left. Forms max 47.5rem wide (`.sb-form-narrow`) |
| `_PortalLayout` | Requester portal | Dark header band with the organization name, centered 70rem container, friendlier 16px type |
| `_AuthLayout` | Sign-in, setup, password pages | Lamp field on the left, form on the right |

Page skeleton:

```cshtml
<sb-page-header title="Knowledge" subtitle="Answers your team and requesters can find on their own.">
    <a class="btn btn-primary" href="/kb/new"><i class="bi bi-plus-lg" aria-hidden="true"></i> New article</a>
</sb-page-header>

<div class="sb-pane">
    <div class="sb-pane-header"><h2 class="sb-pane-title">Recently updated</h2></div>
    <div class="sb-pane-body">…</div>
</div>
```

Two-column record page: `<div class="sb-split">main column … <aside class="sb-sticky">sidebar</aside></div>` (21.25rem sidebar; stacks below 992px).

## Components

| Need | Use |
|---|---|
| Waiting indicator | `<sb-lamp lit="@x.IsLampLit" alert="@overdue" />` |
| Status | `<sb-status name="@x.StatusName" category="@x.StatusCategory" />` (portal: add `for-requester="true" requester-label="…"`) |
| Priority | `<sb-priority value="@x.Priority" />` (signal bars plus text) |
| SLA due | `<sb-due due="@x.DueBy" category="@x.StatusCategory" resolved-at="@x.ResolvedAt" />` (live countdown) |
| Time | `<sb-time value="@x.CreatedAt" />` (relative; `mode="datetime"` for absolute; shown in the viewer's timezone) |
| Person | `<sb-avatar name="@name" size="sm" />` plus the name as text next to it |
| Record ID | `<span class="sb-stamp">@x.DisplayId</span>` |
| Tag | `<span class="sb-tag">vip</span>`. VIP marker: `<span class="sb-vip">VIP</span>` |
| Empty state | `<sb-empty icon="bi-inbox" title="No tickets are waiting"><p class="sb-empty-text">…</p><div class="sb-empty-actions">…</div></sb-empty>` |
| List of records | `<div class="sb-pane sb-queue-wrap"><table class="sb-queue">…</table></div>` (sticky header; **never** wrap it in `overflow-hidden`). Lamp in `td.sb-col-lamp`, checkbox in `td.sb-col-check`, `tr.is-lit` for bold subjects, `tr[data-sb-href]` makes the row clickable, `.sb-hide-sm` hides minor columns on phones |
| Saved views / sub-navigation | `<ul class="sb-views"><li><a aria-current="page">…<span class="sb-count">12</span></a></li></ul>` |
| Conversation | `.sb-thread` of `article.sb-msg`. Variants: requester (default), `.sb-msg--agent` (cord rule), `.sb-msg--note` with inner `.sb-msg-card` (paper; always shows `.sb-note-label` with a lock icon), `.sb-msg--system` (one muted line) |
| Composer | `.sb-composer`, `.sb-composer-modes` (buttons with `aria-pressed`), textarea, `.sb-composer-foot`. Add `.is-note` in private-note mode: a private note must never look like a reply |
| Properties | `<dl class="sb-props"><div><dt>Status</dt><dd>…</dd></div></dl>`; selects inside look plain until hovered |
| Activity | `<ul class="sb-timeline"><li>…</li></ul>` |
| Kanban | `.sb-board` > `.sb-board-col` > `.sb-board-head` + `a.sb-card` |
| Headline numbers | `.sb-metrics` > `.sb-metric` > `.sb-num` + `.sb-metric-label` |
| Section label | `<p class="sb-section-label">Assignment</p>` (sentence case, muted) |
| Keyboard hint | `<span class="sb-kbd">/</span>` |
| Toast after redirect | `TempData.Toast("Reply sent")` (or `ToastKind.Error`) |
| Toast after htmx | `Response.HxToast("Saved")` |

### Buttons

- **`btn-primary`:** the one main action on a screen. "Send reply", "Create ticket", "Save changes".
- **`btn-outline-secondary`:** other actions.
- **`btn-ghost`:** toolbar and low-emphasis actions.
- **`btn-danger`:** destructive confirmation only.
- **`btn-icon`:** icon-only buttons. These need an `aria-label`.
- Sizes: default, or `btn-sm` in dense toolbars.

### Icons

Bootstrap Icons only: `<i class="bi bi-inbox" aria-hidden="true"></i>`. An icon never replaces a visible label, except in icon buttons that have an `aria-label`.

## Interaction patterns

- **Forms post and redirect** (POST/redirect/GET) and show a toast. Show validation errors next to fields and in an `alert-danger` summary, in plain language: what's wrong and how to fix it.
- **htmx for in-place updates.** Examples: changing a property in the sidebar, filtering a list, paging, loading a tab. Return a partial (`Partial("_Row", model)`) and set `Response.HxToast(...)` when an action needs confirming. Never put JavaScript in attributes (`hx-on` is disabled by CSP); add behavior in a module `.js` file.
- **No inline scripts.** CSP is `script-src 'self'`. Put page scripts in your module's `wwwroot/js` and load them with `@section Scripts { <script src="~/_content/Switchboard.Modules.X/js/x.js" asp-append-version="true"></script> }`. Pass data through `data-` attributes.
- **Confirmation:**
  - Destructive actions: `data-sb-confirm="Delete 3 tickets? You can restore them from trash for 30 days."` on the form or button, or `hx-confirm`.
  - Bulk actions say the count.
  - Permanent deletes say they can't be undone.
- **Live updates.** Listen for `sb:live` events, and update or reload the affected part. When someone else changed the record you're editing, say so: "Priya changed this ticket. Reload to see the update."
- **Loading.** Use `htmx-indicator` spinners (`spinner-border spinner-border-sm`) only for actions slower than about 300ms.

## Writing

- Name things by what users understand, not by how the system works. "Notifications", not "webhook config". "Waiting on you", not "awaiting_agent".
- Buttons say exactly what happens, and the same name follows through: "Merge tickets" leads to the toast "Tickets merged".
- Sentence case. Plain verbs. No exclamation marks.
- Don't join metadata with middle dots (`A · B · C`) or append `→` to links.
- Errors say what happened and what to do, without apologizing: "That email is already used by Dana Whitfield."
- Empty states invite the next step: "No articles yet. Write the first one so requesters can help themselves."
- Requesters never see internal words: internal status names (use the requester label), group names (unless allowed), "agent", "SLA".

## Accessibility (WCAG 2.2 AA)

- Every control is keyboard reachable, with visible focus (the CSS provides it; don't remove outlines).
- Every form control has a `<label>`. Icon buttons have `aria-label`. Decorative icons have `aria-hidden="true"`.
- Status is never conveyed by color alone. Lamps differ by fill, priority uses bars plus text, and due states use an icon plus text.
- Use landmarks (`main`, `nav`, `aside`) and one `h1` per page (the page header). Headings go in order.
- Targets are at least 24×24px. Check everything in dark mode and at 390px wide.
- Respect reduced motion: global CSS turns animation off; don't add motion that bypasses it.

## Reviewing your screens

With your instance running:

```bash
node tools/ui-snap/snap.mjs --base http://localhost:<port> --user sam@switchboard.local /your/page
node tools/ui-snap/snap.mjs --base http://localhost:<port> --user sam@switchboard.local --theme dark /your/page
node tools/ui-snap/snap.mjs --base http://localhost:<port> --user dana@switchboard.local --mobile /portal/your-page
```

Open the PNGs it prints and critique them against this page before you call a screen done. Then remove one thing.
