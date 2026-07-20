from dataclasses import dataclass
from datetime import datetime


@dataclass(frozen=True)
class Device:
    ip: str
    mac: str
    hostname: str = ""


@dataclass(frozen=True)
class Observation:
    observed_at: datetime
    source_ip: str
    source_mac: str
    domain: str
    query_type: str = "DNS"

