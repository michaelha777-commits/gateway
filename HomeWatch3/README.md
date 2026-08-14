# HomeWatch 3

HomeWatch 3 is the OPNsense-first rebuild of HomeWatch. It keeps the useful ASP.NET Core + SQLite foundation from HomeWatch 2, but removes AdGuard Home and Windows-specific assumptions from the new runtime.

Current release: **3.0.0-alpha.29**

## Current architecture

- ASP.NET Core 8, cross-platform
- SQLite local history
- MoneyPilot-backed owner authentication with shared credentials, TOTP, and bearer tokens
- OPNsense connector abstraction
- ntfy as a first-class notification service
- Source-neutral DNS and ntopng flow observations stored as raw facts
- Activity views correlate nearby DNS and flow signals without duplicating them as separate visits
- Live Activity gives the strongest observed target—exact URL, hostname/SNI, application, or remote IP—the primary visual position, with the detected application and device shown as context
- Video-session evidence is retained for 30 days with separate DNS and ntopng flow timelines
- HomeWatch2 remains untouched as a reference implementation

## Data model

Initial tables:

- `Devices`
- `TrafficEvents`
- `Alerts`

`TrafficEvents` is intentionally source-neutral. Future collectors can normalize observations from OPNsense, Zenarmor, Unbound, and other sources into the same model.

## Synology target

The default listen port is `8930`. The sample configuration uses `/volume1/web/homewatch-data` for persistent data so runtime state is separate from the Git checkout.

The intended public URL is **`https://teamelevation.synology.me:8445`**. Synology terminates HTTPS on port `8445` and proxies the request over the NAS loopback interface to `http://127.0.0.1:8930`. MoneyPilot remains on `https://teamelevation.synology.me:8444`.

Copy `appsettings.example.json` to `appsettings.json`, then configure:

- OPNsense URL
- OPNsense API key
- OPNsense API secret
- ntopng URL and a dedicated read-only username/password
- ntfy topic URL
- MoneyPilot authentication URL only when its local runtime is not using the default discovered port

Do not commit real secrets.

### Synology HTTPS reverse proxy

In DSM 7, go to **Control Panel → Login Portal → Advanced → Reverse Proxy** and create this rule:

| Setting | Source | Destination |
| --- | --- | --- |
| Protocol | HTTPS | HTTP |
| Hostname | `teamelevation.synology.me` | `127.0.0.1` |
| Port | `8445` | `8930` |

Name the rule **HomeWatch 3**. Enable HTTP/2 and HSTS when those checkboxes are available. In **Control Panel → Security → Certificate → Settings**, assign the existing `teamelevation.synology.me` certificate to the new HomeWatch reverse-proxy service. TLS certificates validate the hostname, so the same certificate works on both ports `8444` and `8445`.

For external access, OPNsense must forward **WAN TCP 8445** to **`192.168.1.13:8445`**, matching the existing MoneyPilot `8444` pattern. Do not forward internal Kestrel port `8930` to the internet.

HomeWatch processes `X-Forwarded-For` and `X-Forwarded-Proto` only from a single loopback proxy. This makes ASP.NET Core recognize the original request as HTTPS and mark the HomeWatch session cookie `Secure`, while rejecting spoofed forwarded headers from LAN clients. The forwarded-header middleware must remain before authentication in `Program.cs`.

After creating the proxy and restarting HomeWatch, verify from the NAS:

```bash
curl -sk --resolve teamelevation.synology.me:8445:127.0.0.1 \
  https://teamelevation.synology.me:8445/api/status
```

Then verify externally by opening:

```text
https://teamelevation.synology.me:8445
```

### Shared MoneyPilot authentication

Every HomeWatch page and data API is protected by the same owner login used by MoneyPilot. The public exceptions are the login/password-reset endpoints and `GET /api/status`, which remains available for service health checks.

The HomeWatch sign-in screen asks for the same:

- MoneyPilot owner email
- MoneyPilot password
- current six-digit authenticator code
- optional **Remember this device for 30 days** setting

HomeWatch forwards the sign-in over the NAS loopback interface to MoneyPilot's `POST /api/auth/login`. MoneyPilot performs its existing bcrypt password and TOTP checks and issues its normal 12-hour or 30-day JWT. HomeWatch returns that original JWT unchanged, stores it in a HomeWatch-only HttpOnly `SameSite=Strict` cookie, and also accepts it through the standard `Authorization: Bearer <token>` header. This means a token issued by MoneyPilot is the same token accepted by HomeWatch.

HomeWatch does not copy or store the password hash, password, TOTP secret, or JWT signing secret. It validates presented tokens against MoneyPilot's authenticated `GET /api/auth/me` endpoint and caches successful validations briefly by a SHA-256 token fingerprint. Password recovery is also proxied to MoneyPilot, so a password changed from either application's reset screen immediately becomes the shared password.

By default, HomeWatch reads only the `PORT` value from `/volume1/moneypilot-data/app/server/.env` and connects to MoneyPilot at `http://127.0.0.1:<PORT>`. It never reads the authentication secrets from that file. If the runtime uses another address, configure the untracked HomeWatch `appsettings.json`:

```json
"Authentication": {
  "Enabled": true,
  "MoneyPilotBaseUrl": "http://127.0.0.1:3000",
  "MoneyPilotEnvironmentFile": "/volume1/moneypilot-data/app/server/.env",
  "AllowInvalidCertificate": false,
  "CookieName": "homewatch.session",
  "ValidationCacheSeconds": 120
}
```

Do not put a password, password hash, TOTP secret, JWT secret, or live token in this section. When `MoneyPilotBaseUrl` is blank, the runtime environment file and then port `3000` are used. Successful token checks are cached for 15–300 seconds and never beyond the JWT's observed expiry.

The browser-to-HomeWatch connection also carries the login credentials. Use a Synology HTTPS reverse proxy for HomeWatch whenever it is accessed from another device; the login screen warns when a non-loopback connection is plain HTTP. The loopback connection from HomeWatch to MoneyPilot remains inside the NAS.

### ntopng connector

HomeWatch can read live host identity, nDPI application totals, traffic direction, categories, flow counts, alerts, risk score, and active-flow metadata from an ntopng Community instance. Active flows add server hostnames/SNI when ntopng observed them, application/protocol names, destination IP/port/country, duration, and directional byte counts. Credentials are used only by the ASP.NET Core server and are never returned to the browser. If a known device changes IP, HomeWatch uses its MAC address to locate the device in ntopng's active-host table before requesting its current traffic details.

Configure the `Ntopng` section in the Synology's untracked `appsettings.json`:

```json
"Ntopng": {
  "Enabled": true,
  "BaseUrl": "http://192.168.1.1:3000",
  "Username": "homewatch",
  "Password": "replace-with-the-ntopng-password",
  "InterfaceId": 0,
  "EnableFlowTelemetry": true,
  "FlowPollSeconds": 10,
  "FlowPageSize": 500,
  "AllowInvalidCertificate": false
}
```

Use a dedicated ntopng user with access only to the monitored LAN interface. The interface ID is visible in ntopng URLs as `ifid`; it can also be verified with `GET /lua/rest/v2/get/ntopng/interfaces.lua`. `FlowPollSeconds` is clamped to 5–60 seconds and `FlowPageSize` to 50–1000 rows. If HTTPS is enabled with a locally issued certificate, set `AllowInvalidCertificate` only when certificate validation cannot be configured correctly.

### URL visibility levels

Activity and session screens label every observation with the strongest evidence actually available:

- **Exact URL** — only when an upstream source explicitly supplies one.
- **Hostname / SNI** — a DNS name or TLS server name, such as `www.youtube.com`.
- **Application only** — an nDPI service name without an observable hostname.
- **IP only** — destination address and port when neither a hostname nor application was identified.

The collector deduplicates repeated polls of the same ntopng flow and correlates them with nearby Unbound DNS observations for the same device. It does not perform TLS interception and cannot expose encrypted HTTPS paths, searches, video titles, or page contents.

On Live Activity, an observed exact URL or hostname/SNI is the large first line instead of being buried in secondary metadata. The detected application, device, address, activity window, confidence, encryption, and traffic remain visible as supporting context. Additional observed hostnames stay available under **Correlated evidence**. This visual priority does not turn a hostname into a full URL.

### Suricata and ET Pro Telemetry evidence

The Security page reads OPNsense's native Suricata alert API with the existing OPNsense credentials. It correlates alert source and destination addresses with HomeWatch devices, then displays TLS SNI, QUIC SNI, HTTP host, certificate, fingerprint, protocol, action and signature fields when the alert record contains them.

Install `os-etpro-telemetry` in OPNsense and activate its rule categories to expand security detections. ET Pro Telemetry shares anonymized alert telemetry with Proofpoint under its own enrollment terms. It does not decrypt HTTPS or make every TLS connection appear in the alert log; Zenarmor and ntopng remain the broad application/flow sources.

## Initial endpoints

- `GET /api/status`
- `POST /api/auth/login`
- `POST /api/auth/forgot-password`
- `POST /api/auth/logout`
- `GET /api/auth/me`
- `GET /api/ntopng/status`
- `GET /api/opnsense/status`
- `GET /api/ntopng/flows`
- `GET /api/telemetry/status`
- `GET /api/history?minutes=60`
- `GET /api/history/summary?minutes=60`
- `GET /api/opnsense/ids/status`
- `GET /api/opnsense/ids/alerts?limit=250`
- `GET /api/opnsense/etpro/status`
- `GET /api/devices/{id}/ntopng`
- `GET /api/notifications/settings`
- `PUT /api/notifications/settings`
- `POST /api/notifications/test?priority=high`
- `GET /api/events?limit=100`

## Current priorities

1. Expand Zenarmor-supported enrichment when a stable export path is available.
2. Improve session explanations while keeping DNS and flow claims conservative.
3. Strengthen device identity and DHCP-reservation guidance.
4. Make background collection and retention controls more configurable.
5. Add carefully scoped router actions such as reservations and temporary blocking.

## Evidence rule

HomeWatch must describe network evidence conservatively. Network metadata can support statements about observed domains, applications, categories and traffic timing, but must not claim to reveal complete HTTPS URLs, search terms, page contents, video titles, or definitive viewing duration unless an upstream source explicitly provides those facts.
