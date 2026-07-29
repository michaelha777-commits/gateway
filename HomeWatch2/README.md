# HomeWatch 2

HomeWatch 2 is the ASP.NET Core and SQLite implementation of the HomeWatch local network monitoring application.

## Current architecture

- ASP.NET Core web server on port `8920`
- SQLite database through Entity Framework Core
- Background AdGuard Home query-log importer
- Device, event, session, alert, and discovery storage
- Adult-domain intelligence and user safe overrides
- ntfy notification integration
- Responsive desktop and phone dashboard
- Runtime settings, backup, export, and network-discovery endpoints
- Visible Git commit identifier in the application header

## Database

The runtime database is created as `homewatch.db`.

Core tables:

- `Devices`
- `Events`
- `Sessions`
- `Alerts`
- `DeviceDiscovery`

The current model indexes event timestamps and device-plus-timestamp activity. HomeWatch currently uses `Database.EnsureCreated()`, so future schema changes require a versioned migration or schema-upgrade mechanism rather than relying on model changes alone.

## Activity history

HomeWatch already persists imported activity. The current historical browsing limitation comes from fixed API/UI windows and hard result caps, not from the absence of storage.

The complete source review, performance findings, and implementation plan are documented in:

- [`../docs/ACTIVITY_HISTORY_ARCHITECTURE.md`](../docs/ACTIVITY_HISTORY_ARCHITECTURE.md)

Key next changes:

1. keyset-paginated `/api/activity` evidence endpoint,
2. separate database-computed activity summaries,
3. custom date ranges and repeated `Load older` navigation,
4. SQL aggregation for device statistics,
5. reliable AdGuard import checkpoints and backfill,
6. consistent online SQLite backups,
7. schema migrations or a versioned schema upgrader.

## Run locally

1. Install the .NET 8 SDK.
2. Open the `HomeWatch2` folder.
3. Run `Start-HomeWatch2.cmd`.
4. Open `http://127.0.0.1:8920`.

PowerShell alternative:

```powershell
dotnet restore
dotnet run
```

## Service installation

Use `Install-HomeWatch-Service.ps1` from an elevated PowerShell session. The installed Windows service name is `HomeWatch`.

## Current source version

The application reports version `2.0.0-alpha.17` and displays the current Git commit when Git is available or `HOMEWATCH_COMMIT` is configured.

## Backup note

Do not treat a normal file copy of an actively written SQLite database as a guaranteed consistent backup. A supported backup path should use SQLite's online backup API or stop writes and checkpoint the WAL before copying the database.

## Unlimited activity history

HomeWatch retains imported DNS evidence in SQLite without an application-imposed age cutoff. The dashboard and device views support 24-day/year presets, custom UTC-backed date ranges, and **All History**. Results are fetched in bounded pages with a stable keyset cursor; use **Load Older** or scroll to the end of a device timeline to continue. Counts always describe the complete selected range, not merely the rows currently rendered.

The importer stores its AdGuard high-water checkpoint in SQLite and pages backward after downtime until it reaches that checkpoint. `AdGuard:BatchSize` controls each AdGuard request and `AdGuard:MaxRecoveryPages` bounds work in one polling cycle (backfill resumes on the next cycle). Event fingerprints and a unique SQLite index make replay idempotent.

See [`../docs/ACTIVITY_API.md`](../docs/ACTIVITY_API.md) and [`../docs/ACTIVITY_HISTORY_ARCHITECTURE.md`](../docs/ACTIVITY_HISTORY_ARCHITECTURE.md) for API and design details.
