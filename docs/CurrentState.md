# HomeWatch Current State

Last documentation refresh: 2026-07-28

## Deployment
- Production URL: `https://teamelevation.synology.me/`
- External HTTPS port: `443`
- Synology NAS provides the public reverse proxy and TLS termination.
- HomeWatch runs on a Windows computer as an ASP.NET Core application.
- Internal application port: `8920`.

## Application capabilities under development
- AdGuard Home query-log ingestion
- Device discovery and persistent device naming
- Per-device activity history
- Domain categorization and explanations
- Adult-session detection, grouping and timelines
- Encrypted-DNS and Apple Private Relay detection
- Alert acknowledgement and ignore controls
- ntfy mobile notifications
- VirusTotal and urlscan.io intelligence
- CSV/JSON exports
- Responsive phone interface

## Current priorities
1. Improve performance of the main page, refreshes, evidence timelines and saves.
2. Make acknowledgement and ignore actions reliable and immediate.
3. Improve per-device activity and MAC-based identity.
4. Make adult-session analysis more intelligent and explainable.
5. Implement the agreed start/escalation-only notification policy.
6. Keep the version identifier visible so deployed code can be matched to a commit.

## Adult-session design state
The intended lifecycle is:

`candidate -> started -> escalated (optional) -> completed`

Notification behaviour:
- Candidate becoming started: notify once.
- Meaningful escalation: notify once with reasons.
- Completion after inactivity: no notification.

## Known technical issue
A previous build failed because a background-service class attempted to use `RootDomain`, a local helper declared in top-level statements in `Program.cs`. Shared domain helpers must be moved into a normal reusable class, such as `DomainHelpers`, so worker classes can call them.

## Known forensic baseline
A prior AdGuard-log investigation confirmed an adult session from device `192.168.1.128`, approximately 03:16:35 to 03:34:42. Evidence showed an adult homepage, another adult site, HLS streaming infrastructure, later MP4 CDN activity and continued browsing. This example is useful for testing session grouping and escalation logic. It did not reveal exact titles, searches, categories or exact video counts.

## Documentation maintenance
Update this file whenever any of the following changes:
- Production URL, port, proxy or server
- Main branch/deployment workflow
- Current implementation priority
- Major feature status
- Known blocking build issue
- Notification behaviour
- Data-source or evidence limitations