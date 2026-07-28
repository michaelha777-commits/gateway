import re
import subprocess

from .models import Device

ARP_ROW = re.compile(
    r"^\s*(?P<ip>\d{1,3}(?:\.\d{1,3}){3})\s+"
    r"(?P<mac>[0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})\s+"
)


def discover_arp_devices():
    result = subprocess.run(
        ["arp", "-a"], capture_output=True, text=True,
        encoding="utf-8", errors="replace", creationflags=0x08000000
    )
    devices = []
    for line in result.stdout.splitlines():
        match = ARP_ROW.match(line)
        if match:
            devices.append(Device(
                match.group("ip"), match.group("mac").replace("-", ":").lower()
            ))
    return devices

