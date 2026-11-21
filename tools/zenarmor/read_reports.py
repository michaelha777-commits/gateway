#!/usr/bin/env python3
"""Forced-command reader for approved Zenarmor SQLite reporting sources."""
import json
import os
from urllib.parse import urlsplit
import sqlite3
import sys
from pathlib import Path

ROOT = Path("/usr/local/datastore/sqlite")
DNS = ("id", "start_time", "query", "domain_categories", "device_id", "device_name",
       "src_hwaddr", "ip_src_saddr", "conn_uuid", "community_id", "is_blocked")
COMMON = ("id","start_time","end_time","device_id","device_name","src_hwaddr","ip_src_saddr","ip_dst_saddr",
          "ip_src_port","ip_dst_port","transport_proto","conn_uuid","community_id","is_blocked","interface")
CONNECTION = COMMON + ("app_category","app_name","app_proto","dst_hostname","src_nbytes","dst_nbytes",
                       "security_tags","encryption","handshake_result","direction","src_direction","dst_direction")
SOURCES = {
    "dns": ("dns_all.sqlite","dns_all",DNS),
    "connections": ("conn_all.sqlite","conn_all",CONNECTION),
    "security-tags": ("conn_all.sqlite","conn_all_security_tags",COMMON+("security_tags","tag","category","severity")),
    "http": ("http_all.sqlite","http_all",COMMON+("host","uri","method","category")),
    "tls": ("tls_all.sqlite","tls_all",COMMON+("sni","dst_hostname","host","tls_version","version","ja3","ja3s","ja4","category")),
    "alerts": ("alert_all.sqlite","alert_all",COMMON+("severity","category","signature","security_tags","action")),
    "sip": ("sip_all.sqlite","sip_all",COMMON+("app_name","method","response_code")),
}
MAX_ID = 2**63 - 1


def execute(request, root=ROOT):
    if type(request) is not dict or request.get("source") not in SOURCES:
        raise ValueError("unsupported source")
    operation = request.get("operation")
    keys = {"source", "operation"}
    if operation == "batch":
        keys.update(("cursor", "limit"))
        cursor, limit = request.get("cursor"), request.get("limit")
        if type(cursor) is not int or not 0 <= cursor <= MAX_ID:
            raise ValueError("invalid cursor")
        if type(limit) is not int or not 1 <= limit <= 500:
            raise ValueError("invalid batch size")
    elif operation != "schema":
        raise ValueError("unsupported operation")
    if set(request) != keys:
        raise ValueError("unexpected fields")
    filename, table, approved = SOURCES[request["source"]]
    path = root / filename
    stat = path.stat()
    generation = f"{stat.st_dev}:{stat.st_ino}"
    connection = sqlite3.connect(path.as_uri() + "?mode=ro", uri=True, timeout=2)
    try:
        connection.execute("PRAGMA query_only=ON")
        connection.execute("PRAGMA trusted_schema=OFF")
        if hasattr(connection, "setlimit"):
            connection.setlimit(sqlite3.SQLITE_LIMIT_LENGTH, 64 * 1024)
        connection.row_factory = sqlite3.Row
        columns = list(connection.execute(f"PRAGMA table_info({table})"))
        if not columns:
            raise ValueError("approved table missing")
        if operation == "schema":
            return {"source":request["source"], "generation":generation,
                    "columns":[{"name":r["name"],"type":r["type"]} for r in columns]}
        names = {c["name"] for c in columns}
        if sum(bool(c["pk"]) for c in columns)!=1 or not any(c["name"]=="id" and c["pk"]==1 and c["type"].upper()=="INTEGER" for c in columns) or "start_time" not in names:
            raise ValueError("unsupported checkpoint schema")
        selected = [c for c in approved if c in names]
        if request["source"]=="dns" and selected!=list(DNS):
            raise ValueError("incomplete DNS schema")
        connection.execute("BEGIN")
        maximum = connection.execute(f"SELECT max(id) FROM {table}").fetchone()[0] or 0
        anchor = connection.execute(f"SELECT start_time FROM {table} WHERE id=?", (cursor,)).fetchone()
        records = [dict(row) for row in connection.execute(
            f"SELECT {','.join(selected)} FROM {table} WHERE id>? ORDER BY id LIMIT ?", (cursor, limit))]
        for row in records:
            if row.get("uri"):
                uri=urlsplit(str(row["uri"]))
                # Keep only the observed path. Discard query, fragment and authority on the appliance.
                parts=uri.path.split("/")
                previous=""
                for i,part in enumerate(parts):
                    if previous in {"token","auth","password","session","key","secret"} or len(part)>48:
                        parts[i]="[redacted]"
                    previous=part.lower()
                row["uri"]="/".join(parts)[:512]
        if path.stat().st_ino != stat.st_ino:
            raise ValueError("source rotated during read")
        return {"generation": generation, "maximum": maximum, "anchor": anchor[0] if anchor else None,
                "records": records}
    finally:
        connection.close()


def main():
    # sshd runs this executable with restrict,command=... on the dedicated key.
    if os.environ.get("SSH_ORIGINAL_COMMAND", "") or sys.stdin.isatty():
        return 2
    try:
        message = sys.stdin.buffer.read(1025)
        if len(message) > 1024:
            raise ValueError("request too large")
        reply = json.dumps(execute(json.loads(message)), ensure_ascii=True, allow_nan=False)
        if len(reply) > 4 * 1024 * 1024:
            raise ValueError("reply too large")
        print(reply)
        return 0
    except (ValueError, TypeError, KeyError, OSError, sqlite3.Error):
        print("Collector request rejected or reporting source unavailable.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
