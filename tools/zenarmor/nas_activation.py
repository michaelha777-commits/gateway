"""Read-only production checks and guarded activation of a verified backup."""
import json
import os
import shutil
import sqlite3
from contextlib import closing
import subprocess
import sys
import time
from pathlib import Path
from urllib.request import urlopen

from nas_database import installation
from nas_unit import verify, unit_property
from nas_runtime import install, restore

UNIT="homewatch3.service"


def live():
    pid=int(unit_property("MainPID"))
    if pid<=1: raise ValueError("HomeWatch service has no active MainPID.")
    plan=installation(pid,require_open=True)
    verify(plan,pid)
    if int(unit_property("MainPID"))!=pid: raise ValueError("Service changed during inspection.")
    return plan


def destination(plan):
    data=Path(plan["data"])
    parent=data/"backups"
    if parent.resolve()!=parent or (parent.exists() and not parent.is_dir()):
        raise ValueError("Unexpected backup destination.")
    existing=parent if parent.exists() else data
    if not os.access(existing,os.W_OK|os.X_OK):
        raise PermissionError(13,"Cannot access backup destination",str(existing))
    if os.statvfs(existing).f_flag & os.ST_RDONLY:
        raise ValueError("Backup destination is not writable by the backup account.")
    minimum=sum(file.stat().st_size for name in ("homewatch3.db","zenarmor.db","ntfy-delivery.db")
                for file in (data/name,data/(name+"-wal")) if file.is_file())
    if shutil.disk_usage(existing).free<=minimum:
        raise ValueError("Insufficient free space for database snapshots.")
    return parent


def health():
    with urlopen("http://127.0.0.1:8930/api/status",timeout=4) as response:
        result=json.load(response)
    if result.get("application")!="HomeWatch 4" or not result.get("version"):
        raise ValueError("Unexpected HomeWatch health response.")
    return result["version"],result["authentication"]["enabled"]


def restart():
    command=["systemctl","restart",UNIT]
    subprocess.run(command if os.geteuid()==0 else ["sudo","-n"]+command,check=True)


def confirm(plan,expected):
    for _ in range(15):
        try:
            current=live()
            if any(current[k]!=plan[k] for k in ("runtime","content","data","database_id")):
                raise RuntimeError("Different database or runtime after restart.")
            if health()==expected: return
        except (OSError,ValueError,subprocess.SubprocessError):
            pass
        time.sleep(1)
    raise RuntimeError("Could not verify service health and original database.")


def activate(plan,stage,backup):
    if live()!=plan or destination(plan)!=backup.parent:
        raise ValueError("Installation or backup destination changed.")
    recorded=json.loads((backup/"manifest.json").read_text())
    if not any(row["database"]=="homewatch3.db" for row in recorded):
        raise ValueError("Verified core backup missing.")
    snapshot=(backup/"homewatch3.db").resolve(strict=True)
    with closing(sqlite3.connect(snapshot.as_uri()+"?mode=ro&immutable=1",uri=True)) as db:
        tables={row[0] for row in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        if not {"Devices","TrafficEvents","Alerts"}<=tables or db.execute("PRAGMA integrity_check").fetchall()!=[("ok",)]:
            raise ValueError("Backup schema or integrity check failed.")
    previous=health()
    (backup/"installation.json").write_text(json.dumps(plan))
    attempted=False
    try:
        install(stage,plan,backup)
        if live()!=plan: raise ValueError("Unit or worker changed before restart.")
        attempted=True
        restart()
        confirm(plan,("4.0.0-alpha.2",previous[1]))
    except BaseException:
        if (backup/"runtime-files/files.json").exists():
            restore(backup)
            print("Prior runtime files restored from "+str(backup),file=sys.stderr)
            if attempted:
                # Never restart a newly configured or unidentified service during recovery.
                try:
                    current=live()
                    if any(current[k]!=plan[k] for k in ("runtime","data","database_id","content")):
                        raise ValueError("Recovery target differs from the backed-up installation.")
                    restart()
                except Exception:
                    raise RuntimeError("Files restored. Service recovery requires operator verification of the unit and database.")
            confirm(plan,previous)
        raise


if __name__=="__main__":
    try:
        action=sys.argv[1]
        if action=="preflight":
            plan=live()
            parent=destination(plan)
            print(json.dumps({"unit":UNIT,"runtime":plan["runtime"],"content_root":plan["content"],
                  "database":str(Path(plan["data"])/"homewatch3.db"),"database_device_inode":plan["database_id"],
                  "schema":["Devices","TrafficEvents","Alerts"],"backup_directory":str(parent),
                  "expected_launch_verified":True,"backup_created":False},indent=2))
        elif action=="activate":
            planfile,stage,backup=map(Path,sys.argv[2:])
            activate(json.loads(planfile.read_text()),stage,backup)
        elif action=="restore-files":
            restore(Path(sys.argv[2]).resolve())
        else: sys.exit("Unknown action.")
    except PermissionError as error:
        # Report the filesystem object, never its contents or the service environment.
        print("Permission denied during HomeWatch "+action+" at "+ascii(error.filename or "service metadata")+".",file=sys.stderr)
        sys.exit(77)
    except Exception as error:
        sys.exit(str(error) if type(error) in (ValueError,RuntimeError) else "HomeWatch operation failed ("+type(error).__name__+").")
