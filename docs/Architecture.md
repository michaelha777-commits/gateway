# HomeWatch Architecture

## High-level topology

```text
Remote browser or phone
        |
        | HTTPS TCP 443
        v
teamelevation.synology.me
        |
        v
Synology NAS reverse proxy / TLS termination
        |
        | HTTP to internal Windows host, port 8920
        v
HomeWatch2 ASP.NET Core application
        |
        +--> AdGuard Home query logs
        +--> Local device and session data
        +--> VirusTotal
        +--> urlscan.io
        +--> ntfy notifications
```

## Security boundary
The Synology NAS is the public entry point. The Windows HomeWatch service should remain private on the LAN and should not have its internal port forwarded directly from the internet.

## Main logical components

### Web application
Provides dashboards, menus, device views, sessions, evidence timelines, settings, acknowledgement, ignore controls, exports and mobile access.

### AdGuard import worker
Periodically reads AdGuard Home DNS events, normalizes client and domain data, classifies events and records evidence for further analysis.

### Device identity
Device identity should prioritize stable MAC association. IP addresses can change and should be treated as current network attributes rather than permanent identity.

### Domain intelligence
Combines local classification rules and external intelligence providers. Results should include category, confidence, evidence and an understandable explanation.

### Adult-session monitor
Groups related DNS evidence by device and time window. It tracks session lifecycle, avoids duplicate notifications and records the reasons for escalation.

### Notification service
Uses ntfy to deliver concise alerts. Notification logic must be idempotent so restarting the service or reprocessing evidence does not create duplicate alerts.

## Data-flow principles
- Normalize domains before classification.
- Keep raw evidence separate from derived conclusions.
- Preserve timestamps and source information.
- Make every classification and escalation explainable.
- Design imports so re-reading the same log entry is safe.
- Keep expensive external lookups cached and off the critical UI path.

## Performance principles
- Do not block page loads on external reputation APIs.
- Paginate long histories and evidence timelines.
- Load summary data before detailed evidence.
- Index frequently queried timestamps, device identifiers, session identifiers and normalized domains.
- Use background processing for enrichment.

## Evidence model
HomeWatch is primarily DNS-based. The architecture should distinguish:
- Observed facts: device, domain, query time, response status and source.
- Derived signals: category, streaming indicator, private-relay indicator and reputation.
- Session conclusions: probable activity, confidence, severity and escalation reasons.

This distinction prevents the UI from presenting inferences as direct observations.