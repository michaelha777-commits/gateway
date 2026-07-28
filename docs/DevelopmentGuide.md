# HomeWatch Development Guide

## Working style
- Inspect the current repository state before modifying code.
- Implement changes directly when the requirement is clear.
- Keep commits focused and use descriptive commit messages.
- Never claim a commit succeeded without the returned commit SHA.
- Check GitHub Actions after code changes.
- Keep `main` stable.

## Code organization
- Shared logic must live in normal reusable classes, not top-level local functions when background services also need it.
- Keep ingestion, classification, session analysis, notification and presentation concerns separate.
- Prefer dependency injection for services and external clients.
- Make background processing cancellation-aware and resilient to transient failures.

## Reliability rules
- Imports must be idempotent.
- Notification sends must be deduplicated.
- External intelligence failures must not stop DNS ingestion.
- Persist session state required to avoid duplicate alerts after restart.
- Log failures with enough context to diagnose them without exposing secrets.

## Data and UI rules
- Store raw evidence separately from derived classifications.
- Display clear confidence and explanation for inferred activity.
- Sort recent activity and sessions newest first.
- Use asynchronous/background enrichment instead of delaying page loads.
- Paginate large result sets.
- Make action buttons visibly confirm success or failure.

## Device identity
- Prefer MAC address as the durable identity key when available.
- Keep current and historical IP addresses as attributes.
- Preserve manually assigned names across IP changes.

## Adult-session implementation contract
A session should store at least:
- Device identity
- Start and last-seen timestamps
- Current lifecycle state
- Evidence events
- Normalized domains
- Confidence/severity
- Start-notification sent flag
- Escalation-notification sent flag
- Escalation reasons
- Completion timestamp

## Documentation contract
Update documentation when changing:
- Public URL or ports
- Deployment topology
- Notification behaviour
- Session lifecycle
- Integrations
- Major priorities
- Important known limitations

At minimum, update `docs/CurrentState.md` after a significant work session.