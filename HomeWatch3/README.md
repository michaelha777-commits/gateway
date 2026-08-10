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
- ntopng URL and a dedicated read-only username/password
- ntfy topic URL

Do not commit real secrets.

### ntopng connector

HomeWatch can read live host identity, nDPI application totals, traffic direction, categories, flow counts, alerts, and risk score from an ntopng Community instance. Credentials are used only by the ASP.NET Core server and are never returned to the browser.

Configure the `Ntopng` section in the Synology's untracked `appsettings.json`:

```json
"Ntopng": {
  "Enabled": true,
  "BaseUrl": "http://192.168.1.1:3000",
  "Username": "homewatch",
  "Password": "replace-with-the-ntopng-password",
  "InterfaceId": 0,
  "AllowInvalidCertificate": false
}
```

Use a dedicated ntopng user with access only to the monitored LAN interface. The interface ID is visible in ntopng URLs as `ifid`; it can also be verified with `GET /lua/rest/v2/get/ntopng/interfaces.lua`. If HTTPS is enabled with a locally issued certificate, set `AllowInvalidCertificate` only when certificate validation cannot be configured correctly.

## Initial endpoints

- `GET /api/status`
- `GET /api/ntopng/status`
- `GET /api/opnsense/status`
- `GET /api/devices/{id}/ntopng`
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
