"""Exercise the exact bare dotnet run service shape observed on UPAPA, on a CI fixture."""
import os
import shutil
import signal
import sqlite3
from contextlib import closing
import socket
import subprocess
import tempfile
import time
from pathlib import Path
from urllib.request import urlopen

from nas_database import installation
from nas_unit import verify

project=Path("HomeWatch3").resolve()
dotnet=shutil.which("dotnet")
with tempfile.TemporaryDirectory() as scratch:
    root=Path(scratch)
    unit=root/"fixture.service"
    unit.touch()
    with socket.socket() as sock:
        sock.bind(("127.0.0.1",0))
        url="http://127.0.0.1:%d"%sock.getsockname()[1]
    subprocess.run([dotnet,"build","-c","Debug"],cwd=project,check=True)
    time.sleep(1.1)
    proc=subprocess.Popen([dotnet,"run"],cwd=project,start_new_session=True,
        env=dict(os.environ,HomeWatch__DataPath=str(root/"state"),HomeWatch__ListenUrl=url))
    try:
        for _ in range(300):
            if proc.poll() is not None: raise RuntimeError("SDK launcher exited")
            try:
                with urlopen(url+"/api/status",timeout=1): break
            except OSError: time.sleep(.2)
        else: raise RuntimeError("Fixture failed to start")
        # Model an established installation: move the freshly generated schema
        # out of the WAL in this disposable CI database before immutable inspection.
        with closing(sqlite3.connect(root/"state/homewatch3.db")) as fixture:
            busy,frames,done=fixture.execute("PRAGMA wal_checkpoint(FULL)").fetchone()
            assert not busy and frames==done
        plan=installation(proc.pid,require_open=True)
        props={"NeedDaemonReload":"no","FragmentPath":str(unit),"DropInPaths":"","EnvironmentFiles":None,
            "ExecStart":"{ path="+dotnet+" ; argv[]="+dotnet+" run ; ignore_errors=no ; }","WorkingDirectory":str(project)}
        assert verify(plan,proc.pid,props.__getitem__,preflight=True)=="dotnet-run"
        try: verify(plan,proc.pid,props.__getitem__)
        except ValueError as e: assert "rebuild" in str(e)
        else: raise AssertionError("Published deployment accepted a source-building service")
        props["WorkingDirectory"]=str(root)
        try: verify(plan,proc.pid,props.__getitem__,preflight=True)
        except ValueError as e: assert "differs" in str(e)
        else: raise AssertionError("Accepted unrelated working directory")
    finally:
        if proc.poll() is None: os.killpg(proc.pid,signal.SIGTERM)
        proc.wait(timeout=20)
