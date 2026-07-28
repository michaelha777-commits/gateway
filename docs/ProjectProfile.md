# HomeWatch Project Profile

## Purpose
HomeWatch is a home-network intelligence and family-safety platform. It ingests DNS activity from AdGuard Home, associates activity with devices, classifies domains, detects adult-content and encrypted-DNS behaviour, groups evidence into sessions, and sends actionable alerts.

## Canonical production details
- External URL: https://teamelevation.synology.me/
- External protocol and port: HTTPS on TCP 443
- Reverse proxy: Synology NAS
- Internal HomeWatch service: Windows PC, ASP.NET Core, port 8920
- Repository: michaelha777-commits/gateway
- Primary application directory: HomeWatch2

## Request flow
Internet -> Synology HTTPS reverse proxy:443 -> Windows HomeWatch server:8920 -> HomeWatch application -> AdGuard Home and external intelligence services

The Windows service must not be exposed directly to the public internet. Public traffic should enter through the Synology reverse proxy.

## Current integrations
- AdGuard Home for DNS query-log ingestion
- ntfy for mobile push notifications
- VirusTotal for reputation intelligence
- urlscan.io for domain and URL intelligence

## Product principles
- Fast, mobile-friendly interface
- Most recent activity first
- Visible application version/commit identifier
- Menu-based navigation rather than an overloaded home page
- Explainable classifications and alerts
- Device identity should be associated with MAC address where possible, not only IP address
- Avoid notification noise

## Adult-session notification policy
- Notify once when a probable adult session starts.
- Notify once if the same session intelligently escalates.
- Do not send repeated escalation alerts.
- Do not notify when a session ends.
- Ended sessions silently transition to completed.
- Escalations must state why confidence or severity increased, such as streaming indicators, duration, multiple related domains, or repeated activity.

## Evidence limitations
HomeWatch primarily sees DNS-level evidence. DNS data can confirm domains, timing, recurrence, likely service categories, and some streaming infrastructure. It generally cannot prove page titles, search terms, exact video titles, exact content categories, or the exact number of videos watched unless additional inspection technology is added.

## Working agreement
- Prefer direct, verified GitHub commits over ZIP files or manual copy/paste.
- Keep main stable.
- Do not claim a change was committed unless the resulting commit is confirmed.
- Read docs/QuickStartForAI.md and docs/CurrentState.md at the beginning of a new HomeWatch conversation.
- Update CurrentState.md after significant implementation or deployment changes.

## Long-term vision
HomeWatch should evolve into a reliable home-network security and family-safety platform with device history, intelligent session analysis, threat detection, explainable evidence, smart notifications, historical analytics, mobile administration, and secure remote access.