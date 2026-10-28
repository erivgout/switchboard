<img src="assets/icon.svg" width="64" height="64" alt="">

# Switchboard

Switchboard is an open-source, self-hosted IT service management platform: tickets, a self-service portal, a service catalog with approvals, a knowledge base, SLAs, automations, reporting, a REST API, and a built-in MCP server that lets your own AI agent work in Switchboard on your behalf. You run it and can change it to fit your organization.

It is built with .NET 10, ASP.NET Core Razor Pages, Bootstrap 5.3, htmx and SQL Server, and ships as a Docker image. License: [MIT](LICENSE).

## Run it

```bash
cp .env.example .env          # set a strong SWITCHBOARD_SA_PASSWORD and your SWITCHBOARD_BASE_URL
docker compose up -d --build
docker compose logs app       # copy the one-time setup token
```

Open `http://localhost:8080/setup`, paste the token, and create the first admin account. In production, put Switchboard behind a TLS-terminating proxy and set `Switchboard__TrustForwardedHeaders=true`. Keep the `appdata` volume: it holds attachments and the data-protection keys that encrypt stored secrets.

## Develop

```bash
docker compose -f docker-compose.dev.yml up -d      # SQL Server on :14333, Mailpit on :1025 (inbox at http://localhost:8025)
cd src/Switchboard.Web && dotnet run                # http://localhost:5080, with demo data
```

Sign in as `admin@switchboard.local`, `sam@switchboard.local` (agent) or `dana@switchboard.local` (requester), password `Switchboard!2026`. Run the tests with `dotnet test`. They create throwaway databases on the dev SQL Server.

## Read next

| Document | What it covers |
|---|---|
| [System requirements](docs/SYSTEM_REQUIREMENTS.md) | Everything Switchboard must do, and the safety rules and invariants it must keep |
| [Architecture](docs/ARCHITECTURE.md) | How the code fits together: modules, slots, events, security |
| [Design system](docs/DESIGN_SYSTEM.md) | The visual language and UI rules. Open `/styleguide` in development to see it |
| [Module guide](docs/MODULE_GUIDE.md) | How to build or change a module |
| [Roadmap](docs/ROADMAP.md) | What is built today and what comes next |
