"""Consistent SQLite backup. Run against the confirmed service data path."""
import argparse
import json
import os
import sqlite3
from contextlib import closing
from pathlib import Path


def copy_verified(source, destination):
    if not source.is_file() or destination.exists():
        raise ValueError("Source must exist and destination must be new")
    with closing(sqlite3.connect(source.resolve().as_uri()+"?mode=ro", uri=True)) as old:
        with closing(sqlite3.connect(destination)) as new:
            old.backup(new)
            if new.execute("PRAGMA integrity_check").fetchall() != [("ok",)]:
                raise ValueError("Backup integrity check failed")
            return {"database":source.name,"bytes":destination.stat().st_size,
                    "schemaVersion":new.execute("PRAGMA user_version").fetchone()[0]}


if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("data",type=Path)
    parser.add_argument("backup",type=Path)
    args=parser.parse_args()
    os.umask(0o077)
    args.backup.mkdir(mode=0o700,parents=True,exist_ok=False)
    dbs=[args.data/"homewatch3.db"]
    for name in ("zenarmor.db", "ntfy-delivery.db"):
        if (args.data/name).is_file():
            dbs.append(args.data/name)
    records=[copy_verified(path,args.backup/path.name) for path in dbs]
    (args.backup/"manifest.json").write_text(json.dumps(records,indent=2))
    print(json.dumps(records))
