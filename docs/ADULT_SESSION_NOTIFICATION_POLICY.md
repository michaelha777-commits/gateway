# Adult Session Notification and Escalation Policy

## Required behavior

HomeWatch must use a two-notification lifecycle for each adult-content session.

### Notification 1 — Session started

Send one ntfy notification when a new adult session is confirmed.

The notification should include:

- Device name and IP address
- Session start time
- Initial confidence/severity
- Primary adult domain
- A link to the live session when available

### Notification 2 — Session escalated

Send at most one escalation notification per session. The notification is sent only when meaningful new evidence makes the session substantially more serious.

Escalation evidence may include:

- Confirmed video delivery or playback infrastructure, including HLS or MP4 adult CDN domains
- Session duration reaching the configured threshold
- Multiple distinct adult root domains
- Continued page navigation or a significant increase in adult evidence
- Confidence increasing from medium to high

The escalation notification must explain why it was sent. It should include:

- Device name and IP address
- Current duration
- Current confidence/severity
- Human-readable escalation reasons
- A link to the live session when available

An escalation notification must never be sent more than once for the same session.

### Session ended

Do not send an ntfy notification when:

- The adult session ends
- The inactivity timeout closes the session
- The session is acknowledged
- The session is archived

The session should be completed silently in the HomeWatch UI.

## Session lifecycle

1. New
2. Active — notification 1 sent once
3. Escalated — notification 2 sent once
4. Completed — no notification
5. Archived — no notification

## UI requirements

For each adult session, display:

- Current lifecycle state
- Current severity
- Start time
- Last activity time
- Live or final duration
- Adult root domains
- Evidence count
- Streaming evidence
- Escalation reasons
- Escalation timestamp
- A chronological escalation timeline

Suggested severity labels:

- Medium — adult site confirmed
- High — streaming or sustained adult activity confirmed
- Critical — extended or strongly corroborated activity

Severity changes must not automatically generate more than the single allowed escalation notification.

## Deduplication

Persist notification state with the session. At minimum, store the equivalent of:

- StartNotificationSentAt
- EscalationNotificationSentAt
- EscalatedAt
- EscalationReasons

Do not rely only on in-memory flags because HomeWatch may restart while a session is active.

## Recommended default escalation rule

Escalate when at least one strong trigger is present, or when multiple supporting triggers cross a configured score threshold.

Strong trigger:

- Confirmed adult video streaming/CDN evidence

Supporting triggers:

- Session active for at least 10 minutes
- At least two distinct adult root domains
- Repeated adult page-navigation evidence
- High confidence
- Material increase in evidence count

The system must avoid treating A, AAAA, and HTTPS queries for the same hostname at the same timestamp as three independent user actions.

## Acceptance criteria

- Exactly one start notification can be sent per session.
- Exactly one escalation notification can be sent per session.
- No end-of-session ntfy notification is sent.
- Restarting HomeWatch does not duplicate either notification.
- Escalation notification states the reasons for escalation.
- A/AAAA/HTTPS DNS-query bundles are deduplicated for scoring.
- Session completion remains visible in the UI and audit history.
- Existing ntfy test functionality continues to work.
