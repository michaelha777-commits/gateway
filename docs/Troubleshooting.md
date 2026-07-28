# HomeWatch Troubleshooting

## External URL does not load
Canonical external address: `https://teamelevation.synology.me/` on port `443`.

Check in order:
1. HomeWatch process is running on the Windows host.
2. Port 8920 responds locally on the Windows host.
3. Windows Firewall permits the intended LAN access.
4. Synology can reach the Windows host on port 8920.
5. Synology reverse-proxy source is HTTPS/443 for the hostname.
6. Reverse-proxy destination points to the correct internal host and port 8920.
7. The certificate is valid for `teamelevation.synology.me`.
8. Router/NAT and ISP permit inbound 443 as configured.

## GitHub Actions build fails with RootDomain error
A known failure pattern is:

`Cannot use local variable or local function 'RootDomain' declared in a top-level statement in this context.`

Cause: a class such as `AdGuardImportWorker` attempted to call a helper declared as a local function in top-level `Program.cs` statements.

Fix: move domain-root logic to a normal static helper class such as `DomainHelpers` and call it from both top-level configuration and worker classes. Remove unused duplicate helpers after the shared implementation is adopted.

## UI is slow
Investigate:
- Expensive queries on the request path
- Missing indexes
- Loading full evidence histories instead of summaries
- Repeated external API calls
- Excessive synchronous saving
- Unbounded lists
- Polling too frequently

Preferred corrections:
- Background enrichment
- Caching
- Pagination
- Summary-first endpoints
- Database indexes
- Debounced refreshes

## Acknowledge or Ignore does nothing
Verify:
- Button sends the expected request
- API returns a success status
- Database update commits
- UI updates optimistically or refreshes the affected item
- Ignore rules use normalized domains/device identifiers
- Audit event is recorded when required
- Errors are visible to the user rather than silently swallowed

## ntfy notification arrives without sound
Check both sides:
- HomeWatch sends the intended priority, title and tags
- The ntfy topic and server are correct
- Android notification permission is enabled
- The topic/channel has a sound and pop-up configuration
- Battery optimization is not delaying ntfy
- Do Not Disturb is not suppressing the channel

## Duplicate adult alerts
Confirm that notification state is persisted, not only kept in memory. A session must record whether its start and escalation notifications were already sent. Re-imported evidence and application restarts must not resend them.

## Device history splits across IP addresses
Use MAC address as the durable key where available. Merge or associate historical IP records with the same device rather than creating a new permanent device for every DHCP address.

## Evidence appears stronger than DNS supports
Review the wording. Present domains and timestamps as observations. Present likely activity, streaming or adult-session conclusions as inferences with confidence and reasons.