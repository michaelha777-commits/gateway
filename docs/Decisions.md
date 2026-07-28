# HomeWatch Architecture and Product Decisions

## ADR-001: Public access through Synology reverse proxy
**Decision:** Use `https://teamelevation.synology.me/` on external port 443. Synology terminates TLS and forwards to the private Windows HomeWatch service on port 8920.

**Reason:** Centralizes HTTPS, certificate management and public exposure while keeping the Windows application off the direct internet edge.

## ADR-002: GitHub is the source of truth
**Decision:** Prefer direct, verified commits to `michaelha777-commits/gateway`. Avoid ZIP-based delivery unless explicitly requested.

**Reason:** Preserves history, reduces manual file mistakes and makes deployments traceable.

## ADR-003: DNS evidence must not be overstated
**Decision:** Separate observed DNS facts from inferred activity.

**Reason:** DNS can establish domains and timing but usually cannot establish exact page content, search terms, titles or exact video counts.

## ADR-004: Adult alerts are event-based, not noisy
**Decision:** Alert once at session start and once at meaningful escalation. Do not alert at completion and do not repeat escalation alerts.

**Reason:** The owner wants timely awareness without notification fatigue.

## ADR-005: Escalation must be explainable
**Decision:** Every escalation records machine-readable reasons and user-facing explanations.

**Reason:** A severity label without evidence is not trustworthy or actionable.

## ADR-006: Device identity should survive DHCP changes
**Decision:** Associate user-defined names and history with MAC address where possible, while retaining IP history.

**Reason:** IP-only identity breaks when addresses change.

## ADR-007: Mobile speed is a first-class requirement
**Decision:** Summaries load before detailed evidence; long lists are paginated; enrichment occurs in the background.

**Reason:** The application is frequently accessed from a phone and has previously loaded too slowly.

## ADR-008: Visible deployment identity
**Decision:** Display a version or commit identifier at the top of the UI.

**Reason:** It must be obvious whether the running instance contains the latest commit.

## ADR-009: Main page should not contain everything
**Decision:** Use menu-based navigation and focused pages.

**Reason:** The original home page became too busy and slow.

## ADR-010: Secrets do not belong in Git
**Decision:** API tokens, credentials and certificate private material remain outside the repository.

**Reason:** Security and safe repository sharing.