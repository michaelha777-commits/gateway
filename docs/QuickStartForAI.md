# HomeWatch Quick Start for a New Chat

Read this file first, then read `docs/CurrentState.md`.

## What HomeWatch is
HomeWatch is Michael Hani's home-network intelligence application. It analyzes AdGuard Home DNS logs, tracks devices, classifies domains, detects adult-content and encrypted-DNS activity, groups evidence into sessions, and sends alerts through ntfy.

## Canonical environment
- GitHub repository: `michaelha777-commits/gateway`
- Application: `HomeWatch2`
- Production URL: `https://teamelevation.synology.me/`
- External port: `443`
- Public edge: Synology NAS reverse proxy with HTTPS termination
- Internal application endpoint: Windows HomeWatch server on port `8920`
- The Windows service should not be exposed directly to the internet.

## Owner preferences
- Implement rather than over-discuss.
- Make direct GitHub commits when write access is available.
- Avoid ZIP files unless specifically requested.
- Be precise about what was actually committed and verified.
- Ask one question at a time only when information is genuinely required.
- Keep the UI wording accurate and understandable.
- Optimize for phone use and fast load times.

## Key integrations
- AdGuard Home: DNS query logs
- ntfy: phone alerts
- VirusTotal: reputation lookup
- urlscan.io: domain/URL intelligence

## Important product decisions
- Most recent sessions and activity should appear first.
- A version or commit identifier should always be visible.
- Use menus; do not overload the home page.
- Device names should persist by MAC address where possible.
- Classifications and escalations must explain their reasoning.
- Ignore actions and acknowledgements must work immediately and leave an audit trail where appropriate.

## Adult-session alert policy
1. Send one alert when a probable session starts.
2. Send one additional alert only if the session meaningfully escalates.
3. Explain the escalation reason.
4. Never repeatedly alert for the same escalation.
5. Do not alert when the session ends.
6. Silently mark the session completed after inactivity.

Potential escalation evidence includes streaming/CDN indicators, HLS or media-delivery domains, prolonged duration, repeated adult-domain requests, and multiple related adult domains.

## DNS evidence boundary
Never overstate what DNS proves. It can show domains, timestamps, frequency, probable services and some streaming infrastructure. It usually cannot reveal exact page content, search terms, video titles, exact content categories, or exact video counts.

## Known implementation context
`Program.cs` registers services including `AdultSessionMonitor`, `AdGuardImportWorker`, and `NtfyNotifier`. Adult events imported from AdGuard are passed to the adult-session monitor. Shared domain-root logic should live in a reusable helper instead of top-level local functions that background-service classes cannot call.

## How to resume work
1. Read this file.
2. Read `docs/CurrentState.md`.
3. Inspect the current repository branch and recent commits before changing code.
4. Check GitHub Actions after committing.
5. Update `docs/CurrentState.md` when architecture, deployment, behaviour, or priorities change.

## Never forget
The canonical external HomeWatch address is `https://teamelevation.synology.me/` over HTTPS port `443`.