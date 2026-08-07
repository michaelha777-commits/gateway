# HomeWatch 3

HomeWatch 3 is the OPNsense-first rebuild of HomeWatch. It keeps the useful ASP.NET Core + SQLite foundation from HomeWatch 2, but removes AdGuard Home and Windows-specific assumptions from the new runtime.

## Phase 1 architecture

- ASP.NET Core 8, cross-platform
- SQLite local history
- OPNsense connector abstraction
- ntfy as a first-class notification service
- Raw traffic events stored as facts
- Sessions are not persisted as authoritative objects; later UI/analysis layers derive them from nearby events
- HomeWatch2 remains untouched as a reference implementation

## Data model

Initial tables:

- `Devices`
- `TrafficEvents`
- `Alerts`

`TrafficEvents` is intentionally source-neutral. Future collectors can normalize observations from OPNsense, Zenarmor, Unbound, and other sources into the same model.

## Synology target

The default listen port is `8930`. The sample configuration uses `/volume1/web/homewatch-data` for persistent data so runtime state is separate from the Git checkout.

Copy `appsettings.example.json` to `appsettings.json`, then configure:

- OPNsense URL
- OPNsense API key
- OPNsense API secret
- ntfy topic URL

Do not commit real secrets.

## Initial endpoints

- `GET /api/status`
- `GET /api/opnsense/status`
- `POST /api/notifications/test`
- `GET /api/events?limit=100`

## Next implementation phases

1. OPNsense device discovery: DHCP leases, neighbors/ARP, interfaces and gateways.
2. Zenarmor collector for web/app/category observations.
3. Event normalization and device correlation by MAC/IP.
4. Alert rules, starting with adult-content and new-device notifications through ntfy.
5. Live feed, device history and investigation UI based on raw events.
6. Router controls such as reservations and temporary site/device blocking.

## Evidence rule

HomeWatch must describe network evidence conservatively. Network metadata can support statements about observed domains, applications, categories and traffic timing, but must not claim to reveal complete HTTPS URLs, search terms, page contents, video titles, or definitive viewing duration unless an upstream source explicitly provides those facts.
