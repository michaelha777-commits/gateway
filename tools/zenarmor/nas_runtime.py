"""Stage HomeWatch for the installed runtime and update only application artifacts."""
import filecmp
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
from pathlib import Path


def publish(runtime, stage):
    options=json.loads((runtime/"HomeWatch3.runtimeconfig.json").read_text())["runtimeOptions"]
    target=json.loads((runtime/"HomeWatch3.deps.json").read_text())["runtimeTarget"]["name"]
    _,_,rid=target.partition("/")
    command=["dotnet","publish","HomeWatch3.csproj","-c","Release","-o",str(stage)]
    if rid:
        if not re.fullmatch(r"[A-Za-z0-9.-]+",rid):
            raise ValueError("Invalid installed runtime identifier.")
        command.extend(["-r",rid])
    if options.get("includedFrameworks"):
        if not rid:
            raise ValueError("Cannot establish the installed self-contained runtime identifier.")
        command.extend(["--self-contained","true"])
    elif options.get("framework") or options.get("frameworks"):
        command.extend(["--self-contained","false"])
    else:
        raise ValueError("Unknown installed .NET runtime configuration.")
    subprocess.run(command,check=True)
    tracked=subprocess.check_output(["git","ls-files","-z","--","wwwroot"]).decode().split("\0")
    (stage.parent/"static-files.json").write_text(json.dumps(tracked))


def install(stage, plan, backup):
    runtime=Path(plan["runtime"])
    content=Path(plan["content"])
    saved=backup/"runtime-files"
    saved.mkdir(mode=0o700)
    files={}
    def add(source,root,relative,label):
        dst=root/relative
        if dst.resolve()!=dst or (dst.exists() and not dst.is_file()):
            raise ValueError("Unexpected link or directory in application output.")
        if not dst.is_file() or not filecmp.cmp(source,dst,shallow=False):
            files[dst]=(source,saved/label/relative)
    tracked=set(json.loads((stage.parent/"static-files.json").read_text()))
    for name in ("HomeWatch3.dll","HomeWatch3.deps.json","HomeWatch3.runtimeconfig.json"):
        if not (stage/name).is_file():
            raise ValueError("Incomplete staged publish.")
    for source in stage.rglob("*"):
        if not source.is_file():
            continue
        relative=source.relative_to(stage)
        if relative.parts[0]=="wwwroot":
            if relative.as_posix() in tracked:
                add(source,content,relative,"content")
            continue
        if (source.suffix in (".dll",".so",".pdb") or ".so." in source.name
            or relative.as_posix() in ("HomeWatch3.deps.json","HomeWatch3.runtimeconfig.json")):
            add(source,runtime,relative,"runtime")
    # Keep the installed launcher, local settings, service definition and data.
    for dst,(_,old) in files.items():
        if dst.exists():
            old.parent.mkdir(parents=True,exist_ok=True)
            shutil.copy2(dst,old)
    def replace(source,dst,mode):
        missing=[]
        parent=dst.parent
        while not parent.exists():
            missing.append(parent)
            parent=parent.parent
        for directory in reversed(missing):
            directory.mkdir()
            directory.chmod(0o755)
        with tempfile.NamedTemporaryFile(dir=dst.parent,delete=False) as tmp:
            path=Path(tmp.name)
        try:
            shutil.copyfile(source,path)
            if dst.exists() and os.geteuid()==0:
                info=dst.stat()
                os.chown(path,info.st_uid,info.st_gid)
            path.chmod(mode)
            os.replace(path,dst)
        finally:
            if path.exists(): path.unlink()
    manifest=[]
    for dst,(_,old) in files.items():
        manifest.append({"target":str(dst),"original":str(old.relative_to(saved)) if dst.exists() else None})
    (saved/"files.json").write_text(json.dumps(manifest))
    changed=[]
    try:
        for dst,(source,old) in files.items():
            mode=stat.S_IMODE(dst.stat().st_mode) if dst.exists() else stat.S_IMODE(source.stat().st_mode)|0o444
            replace(source,dst,mode)
            changed.append(dst)
    except BaseException:
        for dst in reversed(changed):
            old=files[dst][1]
            if old.exists(): replace(old,dst,stat.S_IMODE(old.stat().st_mode))
            else: dst.unlink()
        raise


def restore(backup):
    saved=backup/"runtime-files"
    plan=json.loads((backup/"installation.json").read_text())
    roots=[Path(plan["runtime"]),Path(plan["content"])/"wwwroot"]
    for entry in reversed(json.loads((saved/"files.json").read_text())):
        target=Path(entry["target"])
        if target.resolve()!=target or not any(root in target.parents for root in roots):
            raise ValueError("Unexpected restore destination.")
        if entry["original"] is None:
            if target.is_file(): target.unlink()
            continue
        source=saved/entry["original"]
        if source.resolve()!=source or saved not in source.parents or not source.is_file():
            raise ValueError("Original runtime artifact unavailable.")
        with tempfile.NamedTemporaryFile(dir=target.parent,delete=False) as tmp:
            pending=Path(tmp.name)
        try:
            shutil.copy2(source,pending)
            if target.exists() and os.geteuid()==0:
                info=target.stat()
                os.chown(pending,info.st_uid,info.st_gid)
            os.replace(pending,target)
        finally:
            if pending.exists(): pending.unlink()


if __name__=="__main__":
    action,planfile,stage,*extra=sys.argv[1:]
    plan=json.loads(Path(planfile).read_text())
    if action=="publish": publish(Path(plan["runtime"]),Path(stage))
    elif action=="install": install(Path(stage),plan,Path(extra[0]))
    else: sys.exit("Unknown deployment step.")
