import threading
from datetime import datetime

from .models import Observation


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
                    filter="udp port 53 or tcp port 53",
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
            from scapy.layers.inet import IP
            from scapy.layers.l2 import Ether
            if not packet.haslayer(DNS) or packet[DNS].qr != 0:
                return
            if not packet.haslayer(DNSQR) or not packet.haslayer(IP):
                return
            raw_name = packet[DNSQR].qname
            domain = raw_name.decode("idna", errors="ignore").rstrip(".").lower()
            if not domain:
                return
            mac = packet[Ether].src.lower() if packet.haslayer(Ether) else "unknown"
            self.on_observation(Observation(
                datetime.now(), packet[IP].src, mac, domain, "DNS"
            ))
        except Exception as exc:
            self.on_status(f"Packet parse warning: {exc}")
