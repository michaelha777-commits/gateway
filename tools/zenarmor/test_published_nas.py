"""Exercise the real published HomeWatch app under both supported Linux launchers."""
from contextlib import closing
import json
import os
from pathlib import Path
import shutil
import sys
import socket
import sqlite3
import subprocess
import tempfile
import time
from urllib.request import urlopen
from nas_database import installation
from nas_runtime import install
from backup import copy_verified


def main():
    with tempfile.TemporaryDirectory() as folder:
        base=Path(folder)
        runtime=base/"installed publish"
        content=base/"service content"
        work=base/"systemd working directory"
        for p in (content,work):
            p.mkdir()
        self_contained="--self-contained" in sys.argv
        publish=["dotnet","publish","HomeWatch3/HomeWatch3.csproj","-c","Release","-o",str(runtime)]
        if self_contained: publish.extend(["-r","linux-x64","--self-contained","true"])
        subprocess.run(publish,check=True)
        with socket.socket() as sock:
            sock.bind(("127.0.0.1",0))
            port=sock.getsockname()[1]
        url="http://127.0.0.1:%d"%port
        data=work/"persistent state"
        config=content/"appsettings.json"
        config.write_text(json.dumps({"HomeWatch":{"DataPath":"persistent state","ListenUrl":url},
                                    "Authentication":{"Enabled":True}}))
        env=dict(os.environ,DOTNET_CONTENTROOT=str(content))
        commands=[[str(runtime/"HomeWatch3")]]
        if not self_contained: commands.append(["dotnet","exec",os.path.relpath(runtime/"HomeWatch3.dll",work)])
        unit=base/"homewatch3.service";unit.touch()
        time.sleep(1.1)  # Linux btime has one-second precision.
        for command in commands:
            proc=subprocess.Popen(command,cwd=work,env=env,stdout=subprocess.DEVNULL)
            try:
                for _ in range(100):
                    if proc.poll() is not None: raise RuntimeError("Fixture service exited")
                    try:
                        with urlopen(url+"/api/status",timeout=1) as response:
                            assert json.load(response)["authentication"]["enabled"]
                        break
                    except OSError: time.sleep(0.1)
                else: raise RuntimeError("Fixture service did not start")
                plan=installation(proc.pid)
                assert plan["runtime"]==str(runtime)
                assert plan["content"]==str(content)
                assert plan["data"]==str(data)
                from nas_unit import verify
                exe=shutil.which(command[0])
                properties=dict(ExecStart="{ path="+exe+" ; argv[]="+" ".join(command)+" ; ignore_errors=no ; }",
                    WorkingDirectory=str(work),NeedDaemonReload="no",FragmentPath=str(unit),DropInPaths="",EnvironmentFiles="")
                verify(plan,proc.pid,properties.__getitem__)
                properties["ExecStart"]="{ path=/other/HomeWatch3 ; argv[]=/other/HomeWatch3 ; ignore_errors=no ; }"
                try: verify(plan,proc.pid,properties.__getitem__)
                except ValueError as ex: assert "different runtime" in str(ex)
                else: raise AssertionError("accepted a different restart target")
            finally:
                proc.terminate()
                proc.wait(timeout=15)
        backup=base/"backup";backup.mkdir()
        copy_verified(data/"homewatch3.db",backup/"homewatch3.db")
        with closing(sqlite3.connect(backup/"homewatch3.db")) as db:
            assert db.execute("PRAGMA integrity_check").fetchone()==("ok",)
        # Application installation only runs against private fixture files here.
        stage=base/"stage"
        (stage/"wwwroot").mkdir(parents=True)
        (stage/"wwwroot"/"fixture.js").write_text("new UI")
        for name in ("HomeWatch3.dll","HomeWatch3.deps.json","HomeWatch3.runtimeconfig.json"):
            shutil.copy2(runtime/name,stage/name)
        (stage/"appsettings.json").write_text("must not install")
        (stage/"homewatch3.db").write_text("must not install")
        (base/"static-files.json").write_text('["wwwroot/fixture.js"]')
        prior=config.read_bytes()
        install(stage,plan,backup)
        assert (content/"wwwroot/fixture.js").read_text()=="new UI"
        assert config.read_bytes()==prior and not (runtime/"homewatch3.db").exists()
        assert (data/"homewatch3.db").exists()
        print("Passed published apphost, dotnet exec, separate content and working directories, verified backup and settings preservation.")


if __name__=="__main__":
    main()
