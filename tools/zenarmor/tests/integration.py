"""Real .NET service and SQLite integration with synthetic reporting DBs and a local SSH process double."""
import json
import os
import shutil
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.request
from contextlib import closing
from pathlib import Path

root=Path(__file__).resolve().parents[3]
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from read_reports import DNS
dotnet=os.getenv("DOTNET_HOST_PATH") or shutil.which("dotnet")
transport=Path(__file__).parent/"transport/bin/Release/net8.0"/("Transport.exe" if os.name=="nt" else "Transport")
with tempfile.TemporaryDirectory(dir=root.parent,ignore_cleanup_errors=True) as tmp:
    data=Path(tmp)
    fixture=data/"dns_all.sqlite"
    stamp=int(time.time()*1000)
    def seed():
        with closing(sqlite3.connect(fixture)) as db,db:
            db.execute("CREATE TABLE dns_all(id INTEGER PRIMARY KEY,start_time INTEGER,"+",".join(DNS[2:])+")")
            db.executemany("INSERT INTO dns_all(id,start_time,query,domain_categories,device_id,conn_uuid) VALUES(?,?,?,?,?,?)",
                [(i,stamp+i,"Example.TEST.","Technology","device-1","fixture-uuid") for i in range(1,4)])
    seed()
    with closing(sqlite3.connect(data/"conn_all.sqlite")) as db,db:
        db.execute("CREATE TABLE conn_all(id INTEGER PRIMARY KEY,start_time INTEGER,app_category TEXT,device_id TEXT,conn_uuid TEXT)")
        db.execute("INSERT INTO conn_all VALUES(1,?,'Web','device-1','fixture-uuid')",(stamp,))
    with socket.socket() as sock:
        sock.bind(("127.0.0.1",0));port=sock.getsockname()[1]
    url=f"http://127.0.0.1:{port}"
    env=os.environ|dict(TEST_PYTHON=sys.executable,TEST_READER=str(Path(__file__).with_name("feed.py")),TEST_DATA=tmp,
        HomeWatch__DataPath=str(data/"state"),HomeWatch__ListenUrl=url,Authentication__Enabled="false",Zenarmor__Enabled="true",
        Zenarmor__BatchSize="2",Zenarmor__PollSeconds="5",Zenarmor__SshPath=str(transport))
    def get(path,method="GET"):
        with urllib.request.urlopen(urllib.request.Request(url+path,method=method),timeout=5) as r:
            b=r.read();return json.loads(b) if b else None
    def wait(check):
        until=time.monotonic()+45
        while time.monotonic()<until:
            try:
                if check():return
            except OSError:pass
            time.sleep(.25)
        raise AssertionError("Timed out waiting for "+str(check))
    with (data/"run.log").open("w") as log:
        def start():
            return subprocess.Popen([dotnet,str(root/"HomeWatch3/bin/Release/net8.0/HomeWatch3.dll")],cwd=root/"HomeWatch3",env=env,stdout=log,stderr=log)
        proc=start()
        try:
            wait(lambda:get("/api/zenarmor/status")["imported"]==3)
            rows=get("/api/zenarmor/dns?domain=example.test")["rows"]
            assert [r["evidence"]["id"] for r in rows]==[3,2,1]
            assert rows[0]["evidence"]["query"]=="Example.TEST."
            wait(lambda:len(get("/api/zenarmor/related/dns/0/1"))==4)
            assert get("/api/zenarmor/categories")[0]["observations"]==3
            assert all(r["category"]=="Technology" for r in get("/api/zenarmor/categories"))
            wait(lambda:any(x["source"]=="tls" and x["error"] for x in get("/api/zenarmor/sources")))
            proc.terminate();proc.wait(timeout=10)
            fixture.rename(data/"prior.sqlite");seed()
            with closing(sqlite3.connect(fixture)) as db,db:
                db.execute("INSERT INTO dns_all(id,start_time,query,domain_categories) VALUES(4,?,'next.test','New')",(stamp+4,))
            proc=start()
            wait(lambda:get("/api/zenarmor/status")["imported"]==4)
            assert len(get("/api/zenarmor/dns")["rows"])==4
            get("/api/zenarmor/enabled/false","PUT");assert not get("/api/zenarmor/status")["enabled"]
            assert sum(x["observations"] for x in get("/api/zenarmor/categories"))==4
            print("PASS: backfill, partial batches, timestamps, source isolation, related evidence, restart, rotation, deduplication and disable")
        except Exception:
            log.flush(); print((data/"run.log").read_text(errors="replace")[-2500:]);raise
        finally:
            proc.terminate();proc.wait(timeout=10)
