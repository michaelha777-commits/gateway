# HomeWatch Project Profile

## Production URL
- https://teamelevation.synology.me/
- HTTPS (443)

## Repository
- michaelha777-commits/gateway

## Architecture
Internet -> Synology Reverse Proxy -> Windows HomeWatch Server -> ASP.NET Core -> AdGuard Home

## Internal Service
- Windows server
- Port 8920

## Key Principles
- Reverse proxy terminates HTTPS on Synology.
- Windows server is not directly exposed.
- Commit directly to GitHub whenever possible.
- Keep main stable.

## Current Integrations
- AdGuard Home
- ntfy
- VirusTotal
- urlscan.io

## UI Goals
- Fast
- Mobile friendly
- Version visible
- Menu-based navigation

## Long-Term Vision
HomeWatch will evolve into a comprehensive home network intelligence and security platform with device monitoring, AI-assisted analysis, historical analytics, and smart notifications.
