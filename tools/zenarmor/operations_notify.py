"""Send operator-enabled service-down or backup-failure notifications outside HomeWatch."""
import argparse
from contextlib import closing
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
from zoneinfo import ZoneInfo

MESSAGES = {
    "service-failure": "The HomeWatch systemd service is inactive.",
    "backup-failure": "A HomeWatch backup operation failed.",
}


def notify(data, event, topic, current=None):
    preferences = data / "ntfy-notifications.json"
    if not topic or not preferences.is_file():
        return
    settings = json.loads(preferences.read_text(encoding="utf-8-sig"))
    entry = settings.get("Events", {}).get(event, {})
    if not settings.get("Enabled", True) or not entry.get("Enabled", False):
        return
    now = current or datetime.now(ZoneInfo("America/Toronto"))
    start, end = settings.get("QuietStart"), settings.get("QuietEnd")
    if start is not None and end is not None:
        if start < end and start <= now.hour < end or start > end and (now.hour >= start or now.hour < end):
            return
    priority = entry.get("Priority", "default")
    if priority not in {"min", "low", "default", "high", "max"}:
        priority = "default"
    stamp = int(time.time())
    fingerprint = hashlib.sha256(event.encode()).hexdigest()
    message = MESSAGES[event]
    with closing(sqlite3.connect(data/"ntfy-delivery.db",timeout=5)) as db:
        db.execute("CREATE TABLE IF NOT EXISTS delivery(id INTEGER PRIMARY KEY, stamp INTEGER NOT NULL,event TEXT,priority TEXT,fingerprint TEXT,outcome TEXT)")
        db.execute("BEGIN IMMEDIATE")
        if db.execute("SELECT 1 FROM delivery WHERE fingerprint=? AND stamp>=? AND outcome IN ('pending','delivered') LIMIT 1",(fingerprint,stamp-300)).fetchone():
            db.rollback()
            return
        row=db.execute("INSERT INTO delivery(stamp,event,priority,fingerprint,outcome) VALUES(?,?,?,?,'pending')",(stamp,event,priority,fingerprint))
        db.commit()
        request=Request(topic,data=message.encode(),headers={"Priority":priority,"Title":"HomeWatch service health"})
        try:
            with urlopen(request,timeout=10) as result:
                outcome="delivered" if 200<=result.status<300 else "HTTP "+str(result.status)
        except HTTPError as ex: outcome="HTTP "+str(ex.code)
        except (TimeoutError, URLError): outcome="connection failed"
        db.execute("UPDATE delivery SET outcome=? WHERE id=?",(outcome,row.lastrowid))
        db.commit()
        if outcome!="delivered": raise RuntimeError(outcome)


if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("data",type=Path)
    parser.add_argument("event",choices=MESSAGES)
    args=parser.parse_args()
    if args.event=="service-failure" and subprocess.run(["systemctl","is-active","--quiet","homewatch3.service"]).returncode==0:
        raise SystemExit(0)
    if os.getenv("Ntfy__Enabled","true").lower()=="true":
        notify(args.data,args.event,os.getenv("Ntfy__TopicUrl"))
