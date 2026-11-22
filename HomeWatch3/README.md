# HomeWatch 4

HomeWatch 4 is an OPNsense-first network monitoring dashboard. It runs as a cross-platform ASP.NET Core 8 service with a browser UI and local SQLite history. The earlier Windows and AdGuard Home setup is documented separately in this repository.

Current application version: **4.0.0-alpha.2**. This upgrade retains the HomeWatch3 project directory, database, service name and configuration.

## Page evidence in HomeWatch 4

Adult Analysis displays a redacted URL when ntopng supplies an absolute HTTP(S) URL on an active flow. Query strings and fragments are removed, token-like path segments are redacted, and URLs containing credentials are rejected. A category, tag or video route can expose a page label from its URL slug. These labels describe the observed path. Playback, video titles and viewing duration require additional evidence. DNS and TLS SNI alone supply no path. Availability depends on the installed ntopng edition and traffic. The active-flow parser accepts records with missing throughput or protocol objects.

On phones the shared navigation scrolls horizontally and Adult Analysis presents flow details in stacked rows.

## Architecture and integrations

| Component | HomeWatch 3 behavior |
| --- | --- |
| ASP.NET Core, SQLite and MoneyPilot | The service hosts the UI and API, runs background collectors, and stores devices, source-neutral traffic events and alerts. MoneyPilot validates owner logins and bearer tokens. |
| OPNsense and Unbound | The connector reads DHCP leases, ARP/NDP, firewall and interface telemetry, route and gateway status, and system diagnostics. Unbound query reporting supplies DNS history and adult-domain monitoring. |
| ntopng Community | Periodic active-flow snapshots supply device-attributed application, endpoint, timing, traffic and TLS observations. Host lookups add nDPI application and risk context. |
| Suricata and ET Pro Telemetry | The OPNsense IDS alert API supplies Security page evidence and optional TLS enrichment. ET Pro Telemetry status is shown when the plugin is available. |
| Zenarmor | HomeWatch includes a classifier for Zenarmor web category labels. The optional HomeWatch4 collector reads approved SQLite reporting tables on OPNsense. See the setup below. |
| AdGuard Home | HomeWatch4 supports both Unbound and an optional AdGuard Home query-log reader. |
| ntfy | The notifications service publishes configured adult activity, new-device and IP-change events to the configured topic. Notification preferences persist in the data directory. |

The SQLite database is `homewatch3.db`. Startup creates the database and adds missing traffic columns to existing installations, including the local ntopng port used for Suricata correlation. Keep the data directory on persistent storage and retain it across updates.

## Device discovery, activity and notifications

- **Devices** combines OPNsense DHCP and ARP identity evidence with DNS and discovery context. Owners can rename devices, ignore them, override infrastructure classification and clear items from the new-device review queue. Identity inferred from partial network evidence can be incomplete.
- **Live Activity and History** correlate nearby DNS and ntopng observations for the same device. Live Activity promotes the strongest target available: exact URL, hostname/SNI, application or remote IP. Detected application and device details remain visible as context. Background activity visibility is configurable.
- **Traffic and Video Sessions** track observed traffic windows and merge overlapping video-session fragments by device and service. Video-session evidence is retained for 30 days with separate DNS and ntopng flow timelines. Infrastructure devices can be excluded. Observed session windows and bytes are evidence of network activity.
- **Adult Analysis** rebuilds byte totals from unique flows, distinguishes media delivery from browsing assets and ads, supports a persistent custom ad-domain list and exports credential-free history bundles. The dashboard renders Adult Analysis timestamps in the browser’s local time.
- **Notifications** lets owners enable or disable delivery, set per-event priorities and send a test notification. The defaults enable adult activity and device IP changes. New-device pushes are off by default. Configure `Ntfy:Enabled` and `Ntfy:TopicUrl` in runtime settings.

## Installation and updates

Install the .NET 8 SDK on the build host. A framework-dependent deployment needs the ASP.NET Core 8 Runtime on the destination and a build compatible with its OS and architecture. Install TShark on the runtime host only if you use offline capture analysis.

From a fresh clone, configure and run the service as follows:

```bash
git clone --branch homewatch4 --single-branch https://github.com/michaelha777-commits/gateway.git gateway
cd gateway
cp -n HomeWatch3/appsettings.example.json HomeWatch3/appsettings.json
# Configure HomeWatch3/appsettings.json before starting.
dotnet run --project HomeWatch3/HomeWatch3.csproj --configuration Release
```

Set the listen address and persistent data path under `HomeWatch`. Configure OPNsense credentials, explicitly enable `Ntopng:Enabled` for ntopng, and configure ntfy and MoneyPilot as needed. Verify that the HomeWatch service account can write the data directory. Keep credentials in local runtime configuration or environment variables.

To build deployable files, run `dotnet publish HomeWatch3/HomeWatch3.csproj -c Release -o /path/to/publish` with the appropriate destination for your setup. Keep local configuration and persistent data backed up and outside any directory replaced during deployment. On an existing installation, run `git pull --ff-only origin homewatch4` from the checkout, then rebuild or republish and restart the HomeWatch service using its existing launcher. Pulling the repository updates source files. Verify `/api/status`, sign in, and check the integration views after restarting.

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

If you choose the documented external port, the matching OPNsense rule forwards **WAN TCP 8445** to **`192.168.1.13:8445`**, matching the existing MoneyPilot `8444` pattern. Do not forward internal Kestrel port `8930` to the internet.

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

The collector deduplicates repeated polls of the same ntopng flow and correlates them with nearby Unbound DNS observations for the same device. Passive collection does not expose encrypted HTTPS paths, searches, video titles, or page contents. The separate offline TLS tool can expose redacted request metadata only when the uploaded capture has matching session secrets; it does not install a certificate, proxy live traffic, extract credentials, or persist the uploaded files.

On Live Activity, an observed exact URL or hostname/SNI is the large first line instead of being buried in secondary metadata. The detected application, device, address, activity window, confidence, encryption, and traffic remain visible as supporting context. Additional observed hostnames stay available under **Correlated evidence**. This visual priority does not turn a hostname into a full URL.

### Adult-history analysis export

The **History** and **Video Sessions** screens include **Export adult analysis**. The selected time range—and the selected device on History—produces a ZIP containing:

- `manifest.json` with filters, counts, evidence capabilities, and a no-secrets declaration
- `adult-sessions.json` with per-session DNS and ntopng flow evidence, observed windows, traffic-active estimates, and explicit unsupported fields for video count/title/genre
- `behavior-summary.json` with per-device, per-service, time-of-day, day-of-week, date, frequency, window-duration, and correlated-byte aggregates
- `raw-adult-events.json` and `adult-alerts.json`
- `sessions.csv` for a compact spreadsheet view
- `README.txt` explaining how to interpret and upload the bundle

The export excludes ignored devices, infrastructure devices, ignored domains, and domains marked safe by the user. It never includes a MoneyPilot password, token, cookie, TOTP secret, or JWT secret. Upload the entire ZIP for analysis rather than copying only the CSV; the JSON retains the evidence and uncertainty needed to assess each session responsibly.

Direct API example:

```text
GET /api/exports/adult-history?minutes=10080&timeZone=America%2FToronto
```

### Adult Analysis and corrected accounting

Open **Adult Analysis** from the main navigation. Its session totals are rebuilt from stable, unique ntopng flow IDs, so a flow seen in repeated polls or in two stitched session fragments contributes its byte counters once. When sampled OPNsense attribution overlaps exact flow telemetry, HomeWatch uses the stronger/larger counter rather than adding both observations. Legacy alpha.30 sessions with flow evidence are normalized on load.

The module separates each flow into one of these evidence roles:

- adult service
- media delivery
- browsing assets
- advertising / redirect
- adjacent adult service
- supporting traffic

Advertising is hidden by default. The page can show it temporarily, change the default, or add/remove persistent advertising-domain roots. Significant media transfers and media-delivery phases are useful behavioral indicators, but they are deliberately not presented as exact videos or watch duration.

`teamelevation.synology.me`, the configured HomeWatch public hostname, the configured MoneyPilot hostname, and any `HomeWatch:TrustedDomains` entries are excluded before classification. Historical adult events and alerts for those trusted roots are removed during startup.

Direct API examples:

```text
GET /api/adult-analysis?minutes=10080&includeAds=false&timeZone=America%2FToronto
GET /api/adult-analysis/settings
POST /api/adult-analysis/advertising-domains
```

## TLS Intelligence and offline capture analysis

Open **TLS** from the main navigation. The live flow view works automatically with the configured collectors.

### Passive TLS metadata

ntopng flow polling records device and endpoint addresses, both ports for new observations, flow timing, transport, application and directional bytes. TLS fields depend on what the upstream API supplies. HomeWatch recognizes SNI, TLS version, cipher, ALPN, client and server fingerprints such as JA3, JA3S or JA4 when supplied, and certificate subject and issuer. TLS fields can come from nested ntopng protocol objects and are preserved when later polls omit them. The dashboard labels fingerprints by client or server because upstream formats can vary.

### Suricata enrichment

TLS Intelligence checks up to 500 recent alerts through the existing OPNsense IDS API. It considers alerts with TLS or QUIC metadata and correlates them with a unique encrypted ntopng flow using the stored device attribution, both IP addresses, both ports, TCP or UDP, and the observed flow interval. Matching accepts either endpoint direction and accounts for ntopng's second-level timing. Alerts with ambiguous matches are skipped.

Suricata fills missing fields while keeping existing ntopng values, flow IDs and byte counts. Repeated alerts enrich the same row, and the dashboard marks rows that include Suricata metadata. Enrichment happens when the dashboard reads flows. Older rows without a local port remain visible and can be enriched after ntopng observes the flow again. ntopng rows remain available when the alert API is unavailable. The integration uses the current IDS feed and requires no routing changes or capture uploads.

The **Security** page can ignore an observed domain or a matched device using shared HomeWatch preferences. It reports ET Pro plugin and API access failures separately from Suricata alert collection. The page also uses the native OPNsense IDS API. It associates alert endpoints with known devices and shows TLS or QUIC SNI, HTTP host, certificate and fingerprint fields, signature, action and protocol when present. The optional `os-etpro-telemetry` plugin expands detection coverage according to the rules enabled in OPNsense and shares anonymized alert telemetry with Proofpoint under its enrollment terms.

### Experimental ntopng diagnostics

The authenticated **TLS metadata availability** panel reports field names, API structure and field counts. Its detailed live-flow probe checks whether the installed ntopng API exposes additional TLS fields and reports HTTP, HTML or JSON response failures without displaying raw traffic responses. The probe is a diagnostic tool. Results depend on ntopng version, edition and access permissions, and the regular collector uses the active-flow API.

### Offline TShark analysis

Upload a `.pcap`, `.pcapng` or `.cap` captured from traffic you administer. With a capture alone, TShark can report observable TLS handshake details such as SNI, version, cipher, ALPN, addresses and time. Supplying matching NSS-compatible TLS session keys can reveal HTTP host, method and redacted path when TShark can decrypt the capture. Use captures without embedded secrets for handshake-only analysis, since TShark can also read decryption secrets embedded in PCAPNG files.

HomeWatch excludes query strings, cookies, authorization headers and bodies from its returned request metadata and redacts token-like path segments. The analyzer attempts to delete its temporary input files when analysis finishes, including on failure. `EnableOfflineDecryption` controls availability of the entire offline analyzer, including handshake-only mode.

```json
"TlsInspection": {
  "EnableOfflineDecryption": true,
  "TSharkPath": "tshark",
  "MaximumCaptureMegabytes": 256,
  "MaximumKeyLogMegabytes": 8,
  "AnalysisTimeoutSeconds": 90
}
```

Set `TSharkPath` to the executable on the HomeWatch host or container. Captures can come from OPNsense Diagnostics or Wireshark/dumpcap. Session secrets come from an application or browser you administer using its supported TLS key-log facility.

### Connection status and coverage

HomeWatch4 counts returned encrypted flow rows after time, device, search and row limits and before the status filter. Coverage is enriched rows divided by that count. An observed SNI, version, cipher, ALPN, fingerprint or certificate field qualifies. A generic hostname or port alone does not.

| Status | Evidence |
| --- | --- |
| TLS metadata enriched | ntopng directly reported TLS fields or Suricata supplied missing fields for one matching flow. |
| QUIC traffic without matching TLS metadata | Explicit QUIC or HTTP/3 protocol or application evidence. UDP/443 alone is insufficient. |
| Suricata handshake not observed | The IDS query succeeded and the available observations contained no matching handshake for the connection. |
| Suricata metadata available but correlation unsuccessful | Plausible observations differ in time or ports, lack identifiers or match multiple flows. |
| Unknown / insufficient evidence | The feed is unavailable or the flow lacks identifiers for a decision. |

Set `TlsInspection:CorrelationToleranceSeconds` to adjust clock tolerance (default 2 seconds, bounded to 0–30). Endpoint order can be reversed. Both ports, addresses, transport and flow time must identify one device-attributed connection. HomeWatch retains up to 5,000 distinct handshake observations in process for up to 24 hours, from requests for the latest 500 alerts. The Suricata flow ID helps distinguish sensor records. It is not an ntopng ID. No verified NAT translation map is available, so changed addresses cannot authorize a match. ntopng metadata takes precedence.

Check the last successful collection timestamps and availability first. Attempts and matches summarize the candidate sample before display filters. A long-lived connection can predate the available alert window. Alert-only logging, collector downtime, ntopng polling intervals, process restarts and missing handshake fields limit coverage. Suricata is read on dashboard requests. No certificates, uploads or decryption are required.

## Limitations and verification

- Passive observation can show domains, applications and connection metadata when the upstream source exposes them. Encrypted or missing handshakes and alert-only Suricata coverage leave gaps. DNS, SNI and flow evidence alone cannot identify full HTTPS paths, searches, video titles, genres, page contents or exact watch duration.
- Zenarmor ingestion requires the restricted reader described below. Router actions such as reservations or temporary blocking remain future work. The adult history export uses local evidence and does not send it to external content classifiers.
- PR #23 was tested locally with SQLite and a mock OPNsense IDS API. The tests covered matching, duplicate alerts, ambiguity, older rows, search and IDS outages. Live Suricata field coverage and correlation on the Synology deployment still need verification. Verify the installed ntopng diagnostic probe and the local TShark build separately if you use them.

## Selected endpoints

- `GET /api/status`
- `POST /api/auth/login`
- `POST /api/auth/forgot-password`
- `POST /api/auth/logout`
- `GET /api/auth/me`
- `GET /api/ntopng/status`
- `GET /api/opnsense/status`
- `GET /api/ntopng/flows`
- `GET /api/ntopng/tls-diagnostics`
- `GET /api/ntopng/live-tls-probe`
- `GET /api/telemetry/status`
- `GET /api/history?minutes=60`
- `GET /api/history/summary?minutes=60`
- `GET /api/exports/adult-history?minutes=10080&timeZone=America%2FToronto`
- `GET /api/adult-analysis?minutes=1440&includeAds=false`
- `GET /api/adult-analysis/settings`
- `PUT /api/adult-analysis/settings`
- `POST /api/adult-analysis/advertising-domains`
- `DELETE /api/adult-analysis/advertising-domains?domain=pemsrv.com`
- `GET /api/tls-inspection/capabilities`
- `GET /api/tls-inspection/flows?minutes=60`
- `POST /api/tls-inspection/analyze`
- `GET /api/opnsense/ids/status`
- `GET /api/opnsense/ids/alerts?limit=250`
- `GET /api/opnsense/etpro/status`
- `GET /api/devices/{id}/ntopng`
- `GET /api/notifications/settings`
- `PUT /api/notifications/settings`
- `POST /api/notifications/test?priority=high`
- `GET /api/events?limit=100`

## Recent Changes

| Date | Changes |
| --- | --- |
| 2026-11-21 | Added per-flow TLS enrichment status, coverage filtering, bounded clock tolerance, retained handshake observations and collection diagnostics. |
| 2026-11-21 | HomeWatch 4 alpha adds observed URL and page-route evidence and mobile layouts. |
| 2026-11-21 | Activity history suppresses the ntopng TooEarly placeholder. Adult Analysis lists recently active sessions first and includes matching ntopng flows recorded under other categories. Live Activity provides direct ignore controls and Devices lists ignored devices last. |
| 2026-11-21 | Added Suricata TLS enrichment and local-port correlation in PR [#23](https://github.com/michaelha777-commits/gateway/pull/23) ([`58b494a`](https://github.com/michaelha777-commits/gateway/commit/58b494a)). Enabled passive offline PCAP inspection without session keys in PR [#22](https://github.com/michaelha777-commits/gateway/pull/22) ([`f9ba9ac`](https://github.com/michaelha777-commits/gateway/commit/f9ba9ac)). |
| 2026-11-21 | Preserved TLS values across polls, recognized JA4 and parsed nested ntopng protocol fields ([`9569831`](https://github.com/michaelha777-commits/gateway/commit/9569831), [`f92325d`](https://github.com/michaelha777-commits/gateway/commit/f92325d)). Added authenticated schema diagnostics, dashboard display, API structure reporting and a detailed-flow probe with safer response error reporting ([`974d478`](https://github.com/michaelha777-commits/gateway/commit/974d478), [`7db7505`](https://github.com/michaelha777-commits/gateway/commit/7db7505), [`b68ed31`](https://github.com/michaelha777-commits/gateway/commit/b68ed31), [`7b1a2ee`](https://github.com/michaelha777-commits/gateway/commit/7b1a2ee), [`6ff83e8`](https://github.com/michaelha777-commits/gateway/commit/6ff83e8)). |
| 2026-08-16 | Added adult history export and TLS Intelligence, corrected adult flow accounting and local timestamps, repaired the traffic-session monitor source and bumped to alpha.32 ([`5ff1a5f`](https://github.com/michaelha777-commits/gateway/commit/5ff1a5f), [`22a263c`](https://github.com/michaelha777-commits/gateway/commit/22a263c), [`46fb028`](https://github.com/michaelha777-commits/gateway/commit/46fb028), [`08d745b`](https://github.com/michaelha777-commits/gateway/commit/08d745b), [`b483061`](https://github.com/michaelha777-commits/gateway/commit/b483061)). |
| 2026-08-13 | Added shared MoneyPilot authentication and Synology HTTPS reverse-proxy support ([`da0a85d`](https://github.com/michaelha777-commits/gateway/commit/da0a85d), [`43d49ba`](https://github.com/michaelha777-commits/gateway/commit/43d49ba)). |
| 2026-08-12 | Promoted hostnames in Live Activity and removed duplicate application context ([`49ac146`](https://github.com/michaelha777-commits/gateway/commit/49ac146), [`79e877d`](https://github.com/michaelha777-commits/gateway/commit/79e877d)). |
| 2026-08-11 | Added configurable ntfy rules, improved ntopng URL visibility and added HomeWatch 3 to GitHub validation ([`660a390`](https://github.com/michaelha777-commits/gateway/commit/660a390), [`8b53022`](https://github.com/michaelha777-commits/gateway/commit/8b53022), [`559dfde`](https://github.com/michaelha777-commits/gateway/commit/559dfde)). |
| 2026-08-10 | Added Suricata Security evidence, device reviews, OPNsense device identity and infrastructure overrides, and fixed duplicate video-session fragments ([`4cdc525`](https://github.com/michaelha777-commits/gateway/commit/4cdc525), [`a133dc5`](https://github.com/michaelha777-commits/gateway/commit/a133dc5), [`1173bb8`](https://github.com/michaelha777-commits/gateway/commit/1173bb8), [`0c6c432`](https://github.com/michaelha777-commits/gateway/commit/0c6c432), [`a730234`](https://github.com/michaelha777-commits/gateway/commit/a730234)). |

## Documentation in development PRs

Every HomeWatch 3 development PR that changes functionality, configuration, architecture or deployment instructions must update this README in the same PR. Record user-visible changes in **Recent Changes** and identify experimental features and verification still pending.

## Current priorities

1. Verify per-source Zenarmor coverage against live reporting records and monitor NAS disk growth.
2. Improve session explanations while keeping DNS and flow claims conservative.
3. Strengthen device identity and DHCP-reservation guidance.
4. Make background collection and retention controls more configurable.
5. Add carefully scoped router actions such as reservations and temporary blocking.

## Evidence rule

HomeWatch must describe network evidence conservatively. Network metadata can support statements about observed domains, applications, categories and traffic timing, but must not claim to reveal complete HTTPS URLs, search terms, page contents, video titles, or definitive viewing duration unless an upstream source explicitly provides those facts.


## Recent changes on homewatch4

### 2026-11-21

The changes in [PR #28](https://github.com/michaelha777-commits/gateway/pull/28) build on the collector implementation in `085cc25` and the TLS correlation work in `b220e0f`. The HomeWatch4 branch incorporates the passive TLS coverage work from `70a70db` and adds a read-only Zenarmor collector, retained evidence search, device identity history and ntfy delivery history. Development PRs that change functionality, configuration, architecture or deployment must update this README.

### Zenarmor collection and setup

Zenarmor Free on OPNsense stores reporting databases under `/usr/local/datastore/sqlite/`. The operator verified seven tables. HomeWatch reads DNS observations from `dns_all`, connections and security tags from `conn_all` and `conn_all_security_tags`, and optional HTTP, TLS, alert and SIP observations from their corresponding `*_all` tables. The reader checks the installed schema, requires an integer primary key named `id` and a `start_time`, and projects allowlisted columns. An incompatible source has its own error state. The device's connection application category and the DNS domain category retain their separate meanings.

1. On OPNsense, install the repository's `tools/zenarmor/read_reports.py` in a root-owned directory at `/usr/local/libexec/homewatch4/read_reports.py`. Verify the Python 3 executable path and grant the existing collector account read access to the approved reporting databases. Keep the script and its parent directories unwritable by the collector.
2. On the existing collector public key entry for the OPNsense account `HomeWatch4`, replace the SQLite shell forced command with `restrict,command="/usr/local/bin/python3 -I /usr/local/libexec/homewatch4/read_reports.py"`, using the verified interpreter path. Preserve the public key and any existing client address restrictions. The original NAS private key stays on the NAS.
3. In the service account's environment set `Zenarmor__KeyPath` to the absolute collector private-key path if needed. Verify the OPNsense host key in that account's known_hosts. From that account, pipe `{"source":"connections","operation":"schema"}` into `ssh -T -o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes -i ~/.ssh/homewatch4_collector HomeWatch4@192.168.1.1`. Repeat for the other sources before enabling collection.
4. Enable the collector in **Zenarmor**. Defaults are 60 seconds between polls, 250 rows per batch and 90 days of raw Zenarmor retention. Configure these with `Zenarmor__PollSeconds`, `Zenarmor__BatchSize` and `Zenarmor__RetentionDays`. Check individual source cursors, retained records and last synchronization after backfill and after a restart.

The collector commits source rows, checkpoints and aggregate updates together in `zenarmor.db` under the configured HomeWatch data path. It detects source file replacement and cursor resets. It reads by increasing ID, so edits to older rows on the appliance may fall outside incremental collection. Raw retention excludes alerts and security tags. Hourly and daily counts persist. Related DNS and connection observations may describe the same traffic. Reported DNS activity establishes a query, and reported TLS SNI establishes a hostname. An HTTP observation can additionally supply a method and redacted path when present.

### History, devices and notifications

History can filter retained traffic by local dates, device, source, category and search text. It supports paged results and CSV or JSON export. Reverse DNS is retained and can be included explicitly. The interface displays Toronto local time across daylight-saving transitions. Device profiles retain DHCP identity observations, owner and labels, reversible device groups and change history. A locally administered MAC is an identity clue and requires operator judgment before grouping. Session details distinguish observed network activity from evidence of media delivery. The ntfy page exposes priorities, quiet hours and a local delivery journal.

### Upgrade, backup and rollback

The deployment branch is `homewatch4`. The application directory remains `HomeWatch3/` and the systemd service remains `homewatch3.service`. Verify the unit's working directory, executable and effective `HomeWatch:DataPath` before updating. Record the previous Git revision. In the repository, run `python3 tools/zenarmor/backup.py /actual/service/data/path /new/private/backup/path` using the verified paths. Each SQLite copy receives an integrity check and the manifest records its schema version. Back up the unit, local app configuration and persisted settings separately in protected storage. The first identity migration also takes an integrity-checked snapshot of the resolved core database.

For rollback, stop the service, preserve a separate copy of the entire failed data directory including SQLite journals, then restore the matching database backup and saved settings into a fresh data directory with the original ownership and permissions. Check out and build the recorded previous application revision. Restart the unit, check its logs, verify authentication and compare historical records with the backup. Restoring an older backup reverts writes made after its creation.

### Verification and coverage limits

Local synthetic-source integration exercises backfill, partial batches, independent source failures, restart, rotation, correlation and duplicate suppression. The operator verified the appliance table inventory and connection schema. Live post-upgrade writes from each source, field availability, historical coverage, notification delivery, mobile behavior and NAS resource use require separate verification. A response from port 8930 identifies the running application but does not identify its Git revision. Zenarmor TLS evidence has its own source view and can fill hostname, version and fingerprints on an existing flow. Matching requires a known device MAC, an unambiguous timed transport and port tuple, and compatible evidence. Zenarmor contributes no additional bytes to the matched flow. The TLS coverage percentage and Enrichment status filter count the matched flow once, including when Zenarmor is its only source of TLS metadata. The row lists contributing sources. Exact encrypted URLs, video titles, viewing duration and adult subcategories require direct supporting evidence.

AdGuard Home can contribute recent query-log records to retained traffic history. Set `AdGuard__Enabled=true` and `AdGuard__BaseUrl` to its API origin. Inject optional `AdGuard__Username` and `AdGuard__Password` through the service environment. Credentialed requests require HTTPS. Each poll reads up to 500 recent DNS records and deduplicates overlapping responses. Longer outages or intervals with more than 500 requests can leave gaps. Live Activity filters cover device, category, source and policy. Its Manage menu contains domain and device ignore actions. The Zenarmor evidence view accepts an inclusive Toronto calendar-date range.

Notification preferences include recent Zenarmor alert signatures, recent policy blocks, a failed AdGuard collector, unavailable OPNsense leases or Zenarmor sources, and low storage on the HomeWatch data volume. These additional events default to disabled. The storage threshold defaults to 512 MiB and can be set through `HomeWatch__FreeSpaceWarningMiB`. The included `tools/zenarmor/operations_notify.py` runs independently of the web service. To receive downtime alerts, schedule `python3 /volume1/web/homewatch/tools/zenarmor/operations_notify.py /actual/service/data/path service-failure` in Synology Task Scheduler. Set the task's protected `Ntfy__Enabled` and `Ntfy__TopicUrl` to the service's values. It honors the saved per-event toggle, priority and quiet hours, and records delivery in the local journal. The NAS update helper invokes the same notifier for a failed SQLite backup. Configure the same protected environment for that shell.

Before deploying, run `sh ../tools/zenarmor/deploy-nas.sh --preflight` from the HomeWatch3 directory. You can also run this from a separate checkout of the same branch. Preflight inspects the service, schema and backup destination without installing files, creating a backup or restarting the unit. It requires a matching open database device/inode on the running worker. An idle connection gives an inconclusive result and stops the operation. Retry during normal HomeWatch activity. The read-only schema check uses the checkpointed SQLite database. A fresh schema still confined to the WAL may also require a later retry.

The documented persistent directory is `/volume1/web/homewatch-data` when selected by `HomeWatch:DataPath`. The application default is `data` beside the installed assembly. A positive preflight reports the runtime, content root, database path and device/inode, core tables and intended backup directory. Deployment repeats the identity checks and takes a verified backup before touching runtime files.

The helper compares the effective systemd ExecStart and WorkingDirectory with the running assembly, arguments and working directory before backup and restart. Direct apphost and DLL launches and simple fixed-path shell launchers are supported. A changed unit, changed launcher or dynamic command that cannot be resolved stops deployment. The helper snapshots the databases, settings and effective unit before staging a publish that matches the installed runtime. It rechecks the worker and database identity, installs application artifacts into the installed directory and tracked web assets into the effective content root, then restarts the existing unit and checks port 8930. The current launcher, MoneyPilot settings and service configuration stay in place. The private backup also contains copies of replaced application files under `runtime-files`.

Core raw traffic retention is opt-in for existing installations. After verifying the backup, set `History__EnableRawRetention=true` and optionally `History__RawRetentionDays` (default 90). Each transaction folds up to 1,000 expired rows into permanent UTC hourly counts and traffic totals per device, source and category before pruning the corresponding raw rows. Daily totals can be grouped from those hours. Alerts and investigations have separate retention. Leave retention disabled to preserve the existing raw history.

#### Acceptance status by workstream

| Specification sections | Delivered | Remaining verification or scope |
| --- | --- | --- |
| 1–5. Environment and schema | Targets `homewatch4`, runs from `HomeWatch3/`, and uses seven operator-confirmed table names. DNS and connection categories retain their original types. | Confirm the restricted account's effective permissions and optional columns on each live source. |
| 6–8. Collection and correlation | Per-source backfill, checkpoints, retry, transactional raw evidence and summaries. Supports connection UUID and Community ID correlation. AdGuard imports recent queries. | Compare real appliance totals, measure backlog and disk usage. The system has source-specific evidence views and does not yet expose a single cross-source evidence index. |
| 9–11. Identity, Live and History | Device ownership, labels, identity observations, reversible groups and audit trail. Live filters and contextual actions. Retained history supports SQL filtering, keyset pagination and export. | Verify household aliases, historical DHCP reassignments and identity confidence on live devices. Grouped summaries and raw evidence use different query paths. |
| 12–14. Adult, sessions and TLS | Time-bounded adult-session evidence, application versus domain categories, session idle handling, passive Suricata and Zenarmor TLS matching, collision rejection and byte preservation. | Verify exported fields, media phases and category availability on representative appliance traffic. Encrypted paths or titles need independent direct evidence. |
| 15–16. Security and ntfy | Zenarmor acknowledgement and investigation history, existing Suricata alert view, collector health, event priorities, quiet hours and delivery journal. | Security retains distinct Suricata alerts read from the existing alerts endpoint and searches them alongside Zenarmor alerts and tags. Acknowledgements and notes remain linked to each source event. Suricata archiving depends on reads of the alerts endpoint. Schedule the included independent service monitor on the NAS and verify both failure notifications. |
| 17–19. Reporting, time and retention | Source drill-down, date filters, trends, storage reporting, Toronto display and DST tests. Configurable raw Zenarmor retention, persistent summaries and alert retention. | Benchmark multi-month data. Enable core retention after approving the backup and retention window. Per-source byte totals may overlap across collectors. |
| 20–21. Access and mobile | Existing MoneyPilot owner authentication, rate limits, same-origin mutation checks, mobile navigation and dashboard layout tests. | Additional investigator and read-only roles require an authorization model. Verify the actual reverse proxy, certificate and handheld browsers. |
| 22–25. Migration and operations | Versioned identity and collector schemas, verified SQLite snapshots, rollback instructions, synthetic service tests, NAS update helper and GitHub validation. | A maintenance-window restore drill and post-deployment resource, notification and collector verification require NAS access. |

For every HomeWatch4 development PR, inspect GitHub conversation comments and review threads before completion. Verify findings against the source, add regression coverage for confirmed defects, and record the outcome on the review.

### Recovery after activation failure

The helper prints the private backup path before installing. It records replaced files and newly created file paths in `runtime-files/files.json`. A failed final unit check, restart, database identity check or status check triggers file restoration. The helper attempts a recovery restart only when it can still establish the original database and unit target. It checks the previous health version and authentication state. Database and operator settings are preserved during file recovery.

If recovery reports that the service remains down, keep the backup and check `systemctl status homewatch3.service` and `journalctl -u homewatch3.service` locally. Compare the unit and working directory against the protected unit snapshot and `installation.json`. If file restoration was interrupted, repeat `python3 -B ../tools/zenarmor/nas_activation.py restore-files /actual/private/backup` as the original deployment account with access to that backup. Correct the unit or runtime failure before an operator restarts the verified prior installation. Then check the local health endpoint and run preflight again. Restoring the SQLite backup is a separate operator decision and reverts subsequent writes.
