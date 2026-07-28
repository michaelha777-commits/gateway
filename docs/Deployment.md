# HomeWatch Deployment

## Canonical endpoints
- Public URL: `https://teamelevation.synology.me/`
- Public port: `443`
- Internal HomeWatch port: `8920`

## Reverse-proxy path
1. A remote client connects to `teamelevation.synology.me` using HTTPS on TCP 443.
2. The Synology NAS terminates TLS.
3. The Synology reverse proxy forwards the request to the internal Windows HomeWatch host on port 8920.
4. HomeWatch serves the ASP.NET Core application.

## Required deployment rules
- Do not forward port 8920 directly from the router to the Windows computer.
- Keep the Synology reverse proxy as the only intended public path.
- Preserve forwarded headers so HomeWatch can correctly identify HTTPS and client context.
- Ensure WebSocket support if a future UI feature requires it.
- Use a valid certificate for `teamelevation.synology.me` and renew it automatically.
- Keep credentials and API keys outside source control.

## Post-deployment verification
After a deployment:
1. Confirm the application builds successfully in GitHub Actions.
2. Confirm the Windows service or process is running on port 8920.
3. Confirm the internal URL works from the LAN.
4. Confirm `https://teamelevation.synology.me/` loads externally on port 443.
5. Confirm the visible version or commit identifier matches the intended deployment.
6. Confirm AdGuard ingestion is active.
7. Send a test ntfy notification.
8. Check that acknowledgement and ignore actions persist.

## Troubleshooting order
When the external URL fails, isolate the problem in this sequence:
1. HomeWatch process and local port 8920
2. Windows firewall
3. LAN connectivity from Synology to Windows host
4. Synology reverse-proxy target and headers
5. Certificate and hostname
6. Router/NAT path for TCP 443
7. ISP or DNS issues

## Configuration records to maintain
The repository documentation should record only non-secret deployment facts. Passwords, tokens, API keys and private certificates must not be committed.