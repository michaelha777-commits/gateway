# LAN URL Watch

LAN URL Watch is a Windows desktop MVP that correlates LAN devices with the
internet domains they request. It uses passive DNS capture, the Windows ARP
table, and a local SQLite database.

## What it can and cannot see

- It records the requested **domain** (for example `video.example.com`).
- It cannot recover the path or query string of an HTTPS URL because those are
  encrypted.
- A normal PC on a switched network does not receive other devices' unicast
  traffic. To monitor the whole LAN, run this on the gateway, use a managed
  switch mirror/SPAN port, or later connect the app to your router's DNS logs.
- DNS-over-HTTPS and DNS-over-TLS are encrypted and will not appear as DNS
  queries. Router-enforced DNS is the practical answer for managed networks.

Only monitor a network you own or are authorized to administer.

## Run on Windows

1. Install [Npcap](https://npcap.com/) in WinPcap API-compatible mode.
2. Open **PowerShell as Administrator** in this folder.
3. Create an environment and install the dependency:

   ```powershell
   py -m venv .venv
   .\.venv\Scripts\Activate.ps1
   pip install -r requirements.txt
   py -m lanwatch
   ```

The app works in inventory-only mode if Scapy or Npcap is absent. The status
bar explains why capture is unavailable.

## Recommended gateway layout

Use two network adapters in the dedicated PC:

```text
Internet/router -> WAN adapter | Windows gateway | LAN adapter -> switch/access point -> devices
```

1. In Windows, share the WAN adapter to the LAN adapter using Internet
   Connection Sharing (or configure Windows routing/NAT explicitly).
2. Ensure clients receive the gateway PC's LAN address as their default gateway.
3. Ensure clients use a DNS resolver reachable through the gateway. For the
   most complete results, advertise a resolver on the gateway and prevent
   clients from bypassing it according to your network policy.
4. Start LAN URL Watch as Administrator and choose the **LAN-facing adapter**
   in the toolbar. Do not select the WAN adapter; it may lose the client MAC
   address after routing.

Internet Connection Sharing commonly uses `192.168.137.1/24` on the LAN side,
but Windows may choose a different subnet. Confirm it with `ipconfig`.

## Current MVP

- Live device inventory from `arp -a`
- Live DNS query capture through Npcap/Scapy
- Per-device domain history and request counts
- Search, pause/resume, and CSV export
- Local SQLite persistence in `%LOCALAPPDATA%\LanUrlWatch\lanwatch.db`

## Next production steps

The best production architecture is a router/DNS integration (OpenWrt,
OPNsense, Pi-hole, or AdGuard Home). It sees all routed clients reliably and
does not require decrypting HTTPS. A full-URL mode would require an explicitly
configured HTTPS proxy and installing a private CA on every managed device;
that should be a separate opt-in feature with strong security controls.
