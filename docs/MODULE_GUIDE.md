# Working on a module

Several modules are built at the same time, in the same working tree. These rules keep that from breaking. Read [ARCHITECTURE.md](ARCHITECTURE.md) and [DESIGN_SYSTEM.md](DESIGN_SYSTEM.md) first.

## What you may change

- **Only the paths you own:** your module project(s) under `src/Modules/` and your test project `tests/Switchboard.Modules.<Name>.Tests/`.
- **Read only, for everyone except the integrator:**
  - `src/Switchboard.Core`;
  - `src/Switchboard.UI`;
  - `src/Switchboard.Web`;
  - `tests/Switchboard.Testing`;
  - `docs/`;
  - `Switchboard.slnx`, `Directory.Build.props`;
  - Docker files;
  - every other module.
- **Need a shared change?** A missing Core service, a new slot, a UI component, a field on a Core entity: don't make it. Work around it inside your module if you reasonably can. Then list the change under "Requested shared changes" in your report: file, the exact change, and why.
- **Git: read-only commands only** (`status`, `diff`, `log`, `show`). Never `commit`, `stash`, `checkout`, `reset`, `clean`, `restore` or `rebase`. Other people's uncommitted work is in this tree.
- **Never stop or restart the SQL Server or Mailpit containers.** Others are using them.

## Build and run in isolation

Use your slot name, module list, database and port everywhere. With `slot=knowledge`, `modules=Knowledge` and `port=5104`:

```bash
# Git Bash
export SWITCHBOARD_BUILD_SLOT=knowledge SWITCHBOARD_MODULES="Knowledge"
dotnet build src/Switchboard.Web
cd src/Switchboard.Web && ASPNETCORE_ENVIRONMENT=Development \
  ConnectionStrings__Default="Server=localhost,14333;Database=Switchboard_knowledge;User Id=sa;Password=Switchboard-Dev-2026!;TrustServerCertificate=True;Encrypt=True" \
  Switchboard__BaseUrl=http://localhost:5104 \
  dotnet run --no-launch-profile --urls http://localhost:5104
```

```powershell
# PowerShell
$env:SWITCHBOARD_BUILD_SLOT='knowledge'; $env:SWITCHBOARD_MODULES='Knowledge'
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:ConnectionStrings__Default='Server=localhost,14333;Database=Switchboard_knowledge;User Id=sa;Password=Switchboard-Dev-2026!;TrustServerCertificate=True;Encrypt=True'
$env:Switchboard__BaseUrl='http://localhost:5104'
dotnet run --project src/Switchboard.Web --no-launch-profile --urls http://localhost:5104
```

How it works:

- **`SWITCHBOARD_BUILD_SLOT`** puts your `bin`/`obj` under `.artifacts/<slot>/`, so your builds never fight over files with other builds. Always set it, for `dotnet test` too.
- **`SWITCHBOARD_MODULES`** builds the host with only your modules, so someone else's half-finished module can't break your build. You can add a module you depend on if it currently builds, but don't rely on it.
- **Your own database:** created on first run, with demo data. After changing your entities, run once with `Switchboard__ResetDatabase=true` to recreate it. Tests create their own databases.
- **Stop your server when you're done.** Leave no processes running.
- **Memory is shared.** About ten builds run on one machine. Always build with `-m:2` (e.g. `dotnet build src/Switchboard.Web -m:2`), set `MSBUILDDISABLENODEREUSE=1`, and stop your server whenever you're not actively testing it.

To sign in, use the demo accounts in ARCHITECTURE.md (password `Switchboard!2026`).

## Dependencies

- **Licenses:** NuGet and front-end packages must be MIT, Apache-2.0, BSD, ISC or similarly permissive. Fonts may be OFL. No GPL/LGPL/AGPL, and no packages that went commercial (FluentAssertions 8+, AutoMapper 15+, MediatR 13+, Moq 4.20+). Prefer the framework.
- **Placement:** add packages to your own `.csproj` only. Use `10.0.14` for Microsoft.* packages, to match Core.
- **Front-end libraries:** vendor them into your module's `wwwroot/lib/` (no CDNs; the CSP blocks them and instances may be air-gapped). Prefer none. Bootstrap, Bootstrap Icons, htmx and the SignalR client are already loaded.

## Tests

- **Project:** `tests/Switchboard.Modules.<Name>.Tests` (xUnit). It references `tests/Switchboard.Testing` and your module; see `tests/Switchboard.Core.Tests` for the pattern. `SwitchboardTestHost.CreateAsync(new YourModule())` gives you Core plus your module on a throwaway database.
- **Coverage:** the invariants and permission boundaries your module owns (who can see and do what), plus the main flows.
- **Run:** `SWITCHBOARD_BUILD_SLOT=<slot> dotnet test tests/Switchboard.Modules.<Name>.Tests`.

## UI review

Every page you add should:

- render in light and dark themes and at 390px wide, with no browser console errors;
- use the shared components and tag helpers (see `/styleguide`);
- follow the writing rules in DESIGN_SYSTEM.md.

Screenshot your pages and critique them:

```bash
node tools/ui-snap/snap.mjs --base http://localhost:<port> --user sam@switchboard.local --out .artifacts/snaps/<slot> /your/page
```

Open the PNGs with your image-reading tool, fix what looks wrong, and repeat.

## Review with sol-loop

When your module works, review it with the **sol-loop** skill (load it from your agent's skills and follow it).

**Scope.** Your owned paths only. All of your files are untracked, so the base is `HEAD`. The change is `git status --short -- <your paths>` plus the contents of the untracked files. Tell the reviewer exactly which paths to read.

**Starting Sol.** Load the T3 tools with ToolSearch: `select:mcp__t3-code__orchestrator_capabilities,mcp__t3-code__delegate_task,mcp__t3-code__task_cancel`. Call `orchestrator_capabilities` and use the newest Sol model listed: provider `codex`, model id containing `sol`, highest version.

**Delegating.** Call `delegate_task` with:

- `role: "review"`;
- `target: { providerInstanceId: "codex", model: "<sol id>" }`;
- `isolation: "shared"`.

Because you must finish within your own run, use `mode: "wait"` with `timeoutMs: 1800000` instead of async. Use a new `clientRequestId` each round.

**Round brief.** Use the template in the skill. Add: "Other agents are editing other paths in this tree; ignore them."

**Budget.** 3 rounds, or 5 for auth, permissions or data-loss issues. Fix agreed findings with the smallest change; decline nits in one line. Keep the ledger.

**If no route runs Sol**, say so in your report. Don't substitute another model.

## Definition of done

1. Your projects build with zero warnings, and your tests pass.
2. The host runs with your modules, and every page you added works when signed in as the right demo users (agent, supervisor, requester, admin). Permission boundaries hold when you try them as the wrong user.
3. Screenshots reviewed in light, dark and mobile.
4. sol-loop finished: approved, or stuck with both sides written down.
5. Your server is stopped.

## Your final report

Keep it short and factual:

- **Built:** features, with SRS requirement IDs.
- **Not built:** gaps within your scope.
- **Where to look:** URLs to try, and which demo user to sign in as.
- **Tests:** what they cover, and the pass/fail counts from the last run.
- **sol-loop:** rounds, model, fixed and declined findings, final verdict.
- **Requested shared changes:** file, exact change, reason.
- **Integration notes:** anything the integrator must know (slots you render or fill, events you raise or handle, settings keys, table names).
