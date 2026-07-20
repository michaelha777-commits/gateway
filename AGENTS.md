# HomeWatch Codex Guide

## Purpose

HomeWatch is a Windows-native companion dashboard for AdGuard Home. It turns raw DNS query logs into device-aware evidence timelines and grouped activity sessions.

## Runtime

- Target: Windows 10/11 with Windows PowerShell 5.1.
- No Node.js, Python, database server, or package installation is required on the target PC.
- Start with `Start-HomeWatch.cmd`.
- The dashboard listens only on `http://127.0.0.1:8765`.
- User data is stored outside the repository at `%LOCALAPPDATA%\HomeWatch`.
- AdGuard credentials are protected with Windows DPAPI and must never be committed.

## Architecture

- `HomeWatch.ps1`: local HTTP server, AdGuard API client, event normalization, classification, persistence, and session grouping.
- `web/index.html`: dashboard structure.
- `web/styles.css`: responsive light interface.
- `web/app.js`: browser rendering, filters, device aliases, refresh, and CSV export.
- `Install-Startup.*`: optional scheduled-task setup.

AdGuard Home is expected at `http://127.0.0.1` and uses HTTP Basic authentication for its `/control` API. The app reads `/control/querylog` and does not modify AdGuard settings.

## Product Rules

- Keep the interface bright and approachable; do not revert to a black or dark security-console theme.
- Treat DNS evidence conservatively. A direct adult domain plus a video CDN is stronger evidence than an isolated thumbnail or CDN lookup.
- Never claim DNS reveals complete HTTPS URLs, search terms, video titles, page contents, or definitive watch duration.
- Clearly distinguish direct visits, streaming delivery, thumbnails/profile assets, redirects/advertising, and encrypted-DNS bypass signals.
- Keep all monitoring data local by default.
- Do not add covert HTTPS interception, credential capture, or certificate installation features.

## Validation

Before committing:

1. Run `node --check web/app.js` when Node.js is available in the development environment.
2. Confirm the PowerShell script remains compatible with Windows PowerShell 5.1.
3. Start HomeWatch while AdGuard Home is running and verify `/api/status` and `/api/dashboard`.
4. Verify an isolated CDN request is lower-confidence than a direct-domain-plus-stream sequence.
5. Verify saved aliases and credentials remain under `%LOCALAPPDATA%\HomeWatch`, not in Git.

## Current Priorities

1. Improve device naming and DHCP-reservation guidance.
2. Expand domain classification without increasing false accusations.
3. Add clearer session explanations and bypass alerts.
4. Add reliable background collection and configurable retention.
5. Preserve the one-Ethernet, existing-Windows-computer deployment model.
