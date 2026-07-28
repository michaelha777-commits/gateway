# HomeWatch stabilization

This branch is the controlled stabilization line for HomeWatch.

## Rules

- No generated search-and-replace installers.
- Source files are changed directly and reviewed as complete files.
- Every PowerShell change must pass parser validation.
- Every JavaScript change must pass syntax validation.
- Startup, listener, API, dashboard, and background collection changes are committed separately.
- `main` remains the last known production line until the stabilization branch is verified.

## Immediate goals

1. Keep one documented default port.
2. Add startup diagnostics and a `/health` endpoint.
3. Separate background data collection from HTTP request handling.
4. Serve cached dashboard data so browser requests never trigger long-running collection work.
5. Split the monolithic script into focused modules without changing the UI or stored data format.

## Verification gate

A change is ready only when:

- `HomeWatch.ps1` parses without errors.
- `web/app.js` passes JavaScript syntax validation.
- Static files load locally.
- `/api/status`, `/health`, and `/api/dashboard` return within their timeout.
- Localhost and LAN access are tested independently.
- The process can be stopped and restarted without changing URL reservations or ports.
