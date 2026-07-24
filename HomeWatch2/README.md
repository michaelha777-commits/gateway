# HomeWatch 2

HomeWatch 2 is a clean rebuild of the original PowerShell application.

## Foundation included

- ASP.NET Core web server
- SQLite database through Entity Framework Core
- Indexed device, event, session, and alert tables
- Fast `/api/status` and `/api/dashboard` endpoints
- Responsive phone and desktop dashboard
- Visible version identifier
- Alert acknowledgement endpoint
- One-click Windows launcher

## Run locally

1. Install the .NET 8 SDK.
2. Open the `HomeWatch2` folder.
3. Double-click `Start-HomeWatch2.cmd`.
4. Open `http://127.0.0.1:8920`.

You can also run:

```powershell
dotnet restore
dotnet run
```

## Current version

`2.0.0-alpha.1`

The database is created automatically as `homewatch.db` the first time the app starts.

## Next build step

Add a background AdGuard Home importer that stores query-log entries in SQLite without delaying dashboard requests.
