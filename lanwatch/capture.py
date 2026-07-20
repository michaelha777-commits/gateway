import threading
from datetime import datetime

from .models import Observation


HTTP_METHODS = {b"GET", b"POST", b"PUT", b"DELETE", b"HEAD", b"OPTIONS", b"PATCH", b"CONNECT"}


def extract_http_request(payload):
    """Return (host, URL) for a plaintext HTTP request, otherwise None."""
    try:
        head = payload.split(b"\r\n\r\n", 1)[0]
        lines = head.split(b"\r\n")
        request = lines[0].split(b" ")
        if len(request) < 2 or request[0].upper() not in HTTP_METHODS:
            return None
        target = request[1].decode("latin-1", errors="replace")
        headers = {}
        for line in lines[1:]:
            if b":" in line:
                key, value = line.split(b":", 1)
                headers[key.strip().lower()] = value.strip()
        host = headers.get(b"host", b"").decode("ascii", errors="ignore").lower()
        if request[0].upper() == b"CONNECT":
            host = target.split(":", 1)[0].lower()
            return (host, target) if host else None
        if target.startswith(("http://", "https://")):
            from urllib.parse import urlsplit
            parsed = urlsplit(target)
            return (parsed.hostname or host, target)
        return (host.split(":", 1)[0], f"http://{host}{target}") if host else None
    except Exception:
        return None


def extract_tls_sni(data):
    """Extract the first SNI hostname from a TLS ClientHello record."""
    try:
        if len(data) < 9 or data[0] != 0x16 or data[5] != 0x01:
            return ""
        pos = 9 + 2 + 32
        session_len = data[pos]; pos += 1 + session_len
        cipher_len = int.from_bytes(data[pos:pos + 2], "big"); pos += 2 + cipher_len
        compression_len = data[pos]; pos += 1 + compression_len
        extensions_len = int.from_bytes(data[pos:pos + 2], "big"); pos += 2
        end = min(len(data), pos + extensions_len)
        while pos + 4 <= end:
            kind = int.from_bytes(data[pos:pos + 2], "big")
            size = int.from_bytes(data[pos + 2:pos + 4], "big")
            value = data[pos + 4:pos + 4 + size]
            if kind == 0 and len(value) >= 5:
                name_len = int.from_bytes(value[3:5], "big")
                return value[5:5 + name_len].decode("ascii", errors="ignore").lower()
            pos += 4 + size
    except (IndexError, ValueError):
        pass
    return ""


class DnsCapture:
    def __init__(self, on_observation, on_status):
        self.on_observation = on_observation
        self.on_status = on_status
        self._sniffer = None
        self._lock = threading.Lock()

    @property
    def running(self):
        return self._sniffer is not None

    def start(self, interface=None):
        with self._lock:
            if self._sniffer is not None:
                return
            try:
                from scapy.all import AsyncSniffer
                self._sniffer = AsyncSniffer(
                    iface=interface or None,
                    filter="port 53 or tcp port 80 or tcp port 443",
                    prn=self._handle_packet,
                    store=False,
                )
                self._sniffer.start()
                target = interface or "default adapter"
                self.on_status(f"Capturing DNS queries on {target}")
            except Exception as exc:
                self._sniffer = None
                self.on_status(f"Capture unavailable: {exc}")

    def stop(self):
        with self._lock:
            sniffer, self._sniffer = self._sniffer, None
        if sniffer:
            try:
                sniffer.stop()
            except Exception:
                pass
        self.on_status("Capture paused")

    def _handle_packet(self, packet):
        try:
            from scapy.layers.dns import DNS, DNSQR
            from scapy.layers.inet import IP, TCP
            from scapy.layers.l2 import Ether
            from scapy.packet import Raw
            if not packet.haslayer(IP):
                return
            mac = packet[Ether].src.lower() if packet.haslayer(Ether) else "unknown"
            if packet.haslayer(DNS) and packet[DNS].qr == 0 and packet.haslayer(DNSQR):
                domain = packet[DNSQR].qname.decode("ascii", errors="ignore").rstrip(".").lower()
                if domain:
                    self.on_observation(Observation(datetime.now(), packet[IP].src, mac, domain, "DNS"))
                return
            if not packet.haslayer(TCP) or not packet.haslayer(Raw):
                return
            payload = bytes(packet[Raw].load)
            http = extract_http_request(payload)
            if http:
                host, url = http
                self.on_observation(Observation(datetime.now(), packet[IP].src, mac, host, "HTTP URL", url))
                return
            sni = extract_tls_sni(payload)
            if sni:
                self.on_observation(Observation(datetime.now(), packet[IP].src, mac, sni, "TLS SNI"))
        except Exception as exc:
            self.on_status(f"Packet parse warning: {exc}")
