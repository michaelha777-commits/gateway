import sqlite3
import threading
from pathlib import Path

from .models import Device, Observation


class Store:
    def __init__(self, path: Path):
        path.parent.mkdir(parents=True, exist_ok=True)
        self._connection = sqlite3.connect(path, check_same_thread=False)
        self._connection.row_factory = sqlite3.Row
        self._lock = threading.Lock()
        with self._connection:
            self._connection.executescript(
                """
                CREATE TABLE IF NOT EXISTS devices (
                    ip TEXT PRIMARY KEY,
                    mac TEXT NOT NULL,
                    hostname TEXT NOT NULL DEFAULT '',
                    last_seen TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
                CREATE TABLE IF NOT EXISTS observations (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    observed_at TEXT NOT NULL,
                    source_ip TEXT NOT NULL,
                    source_mac TEXT NOT NULL,
                    domain TEXT NOT NULL,
                    query_type TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS observations_source_time
                    ON observations(source_ip, observed_at DESC);
                """
            )

    def upsert_device(self, device: Device) -> None:
        with self._lock, self._connection:
            self._connection.execute(
                """INSERT INTO devices(ip, mac, hostname, last_seen)
                   VALUES (?, ?, ?, CURRENT_TIMESTAMP)
                   ON CONFLICT(ip) DO UPDATE SET mac=excluded.mac,
                   hostname=CASE WHEN excluded.hostname='' THEN devices.hostname
                                 ELSE excluded.hostname END,
                   last_seen=CURRENT_TIMESTAMP""",
                (device.ip, device.mac, device.hostname),
            )

    def add_observation(self, item: Observation) -> None:
        with self._lock, self._connection:
            self._connection.execute(
                """INSERT INTO observations
                   (observed_at, source_ip, source_mac, domain, query_type)
                   VALUES (?, ?, ?, ?, ?)""",
                (item.observed_at.isoformat(), item.source_ip, item.source_mac,
                 item.domain, item.query_type),
            )

    def devices(self):
        with self._lock:
            return self._connection.execute(
                """SELECT d.ip, d.mac, d.hostname, d.last_seen,
                          COUNT(o.id) AS requests,
                          COUNT(DISTINCT o.domain) AS domains
                   FROM devices d LEFT JOIN observations o ON o.source_ip=d.ip
                   GROUP BY d.ip ORDER BY d.ip"""
            ).fetchall()

    def close(self):
        with self._lock:
            self._connection.close()

    def observations(self, source_ip="", search="", limit=1000):
        clauses, values = [], []
        if source_ip:
            clauses.append("source_ip = ?")
            values.append(source_ip)
        if search:
            clauses.append("domain LIKE ?")
            values.append(f"%{search}%")
        where = " WHERE " + " AND ".join(clauses) if clauses else ""
        values.append(limit)
        with self._lock:
            return self._connection.execute(
                "SELECT * FROM observations" + where
                + " ORDER BY observed_at DESC LIMIT ?", values
            ).fetchall()
