# HomeWatch Roadmap

## Immediate stability and performance
- Fix current build blockers and keep GitHub Actions green.
- Reduce main-page and evidence-timeline load times.
- Make acknowledge and ignore actions reliable.
- Ensure saves complete quickly and visibly.
- Keep newest sessions and events at the top.
- Display the deployed version/commit identifier prominently.

## Device intelligence
- Persist user-assigned names by MAC address.
- Improve ARP/NetBIOS discovery and device identification.
- Show MAC address, IP history and last-seen information.
- Provide focused per-device activity pages.
- Detect private/randomized MAC limitations and explain them.

## Session intelligence
- Complete adult-session lifecycle tracking.
- Add explainable confidence and severity.
- Detect meaningful escalation from streaming indicators, duration, repeated activity and multiple related domains.
- Persist alert-deduplication state across restarts.
- Improve session timeline and evidence summaries.
- Keep DNS limitations clear in the UI.

## Classification and reputation
- Improve domain categorization for major services such as TikTok, Amazon, Apple and Snapchat.
- Maintain updatable category lists.
- Cache VirusTotal and urlscan.io results.
- Add additional reputable intelligence providers where cost and API limits are acceptable.
- Let users correct, ignore or override classifications.

## Notifications
- Make ntfy delivery fast and reliable.
- Support test notifications and configuration validation.
- Use concise alerts with direct links into the relevant HomeWatch session.
- Preserve the one-start/one-escalation/no-end policy.
- Consider optional email or other providers later.

## User experience
- Replace the overloaded home page with menu-driven pages.
- Optimize phone navigation and touch targets.
- Show loading, success and failure states clearly.
- Add useful filters without hiding recent activity.
- Improve exports to CSV and JSON.

## Security and operations
- Keep external access behind Synology HTTPS reverse proxy on port 443.
- Add stronger authentication and session security.
- Add health checks for HomeWatch, AdGuard ingestion and integrations.
- Improve backup, restore and retention controls.
- Document safe upgrade and rollback procedures.

## Longer-term vision
- Behaviour baselines and anomaly detection
- AI-assisted explanations and investigation summaries
- Historical trends and household-level analytics
- Threat and malware detection
- Secure remote administration
- Optional gateway/proxy integration for richer evidence while respecting privacy and legal constraints