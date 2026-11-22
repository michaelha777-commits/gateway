"""Locate the Release worker in the service process tree and verify its SQLite database."""
import json
import os
import re
import sqlite3
import sys
from contextlib import closing
from pathlib import Path


def descendants(proc, pid):
    pending = [str(pid)]
    seen = set()
    while pending:
        current = pending.pop()
        if current in seen:
            continue
        seen.add(current)
        process = proc / current
        if not process.is_dir():
            continue
        yield process
        files = list(process.glob("task/*/children"))
        if not files:
            # Some DSM kernels omit the per-thread children file.
            for status in proc.glob("[0-9]*/status"):
                try:
                    with status.open() as stream:
                        if any(line.split()==["PPid:",current] for line in stream if line.startswith("PPid:")):
                            pending.append(status.parent.name)
                except (FileNotFoundError, PermissionError):
                    continue
        for children in files:
            try:
                pending.extend(children.read_text().split())
            except FileNotFoundError:
                continue


def arguments(process):
    return [os.fsdecode(s) for s in (process/"cmdline").read_bytes().split(b"\0") if s]


def is_worker(process, cwd, args, output):
    host = output / "HomeWatch3"
    dll = output / "HomeWatch3.dll"
    executable = (process/"exe").resolve(strict=True)
    if executable == host:
        return True
    if executable.name == "dotnet" and any((cwd/a).resolve() == dll for a in args[1:] if a.endswith("/HomeWatch3.dll") or a=="HomeWatch3.dll"):
        return True
    with (process/"maps").open(errors="replace") as maps:
        return any(line.rstrip().split(None,5)[-1:] == [str(dll)] for line in maps)


def field(mapping, name):
    return next((v for k,v in mapping.items() if k.lower()==name.lower()), None)


def settings(path):
    if not path.exists():
        return {}
    # ASP.NET JSON settings accept comments and trailing commas. Preserve quoted strings.
    text = path.read_text(encoding="utf-8-sig")
    string = r'"(?:\\.|[^"\\])*"'
    text = re.sub(string+r'|//[^\n]*|/\*.*?\*/', lambda m:m[0] if m[0][0]=='"' else ' ', text, flags=re.S)
    text = re.sub(string+r'|,(?=\s*[}\]])',lambda m:m[0] if m[0][0]=='"' else '',text)
    return json.loads(text)


def configured(process, cwd, args, output):
    env = dict(item.split("=",1) for item in arguments_env(process) if "=" in item)
    opts = {}
    for i,arg in enumerate(args):
        if arg.startswith("--"):
            key,sep,value=arg[2:].partition("=")
            if sep or i+1<len(args):
                opts[key.lower()]=value if sep else args[i+1]
    environment = opts.get("environment") or field(env,"DOTNET_ENVIRONMENT") or field(env,"ASPNETCORE_ENVIRONMENT") or "Production"
    root = cwd / (opts.get("contentroot") or field(env,"DOTNET_CONTENTROOT") or field(env,"ASPNETCORE_CONTENTROOT") or ".")
    value = None
    for path in (root/"appsettings.json",root/("appsettings."+environment+".json")):
        conf = settings(path)
        nested = field(conf,"HomeWatch") or {}
        candidate = field(conf,"HomeWatch:DataPath")
        if candidate is None:
            candidate = field(nested,"DataPath")
        if candidate is not None:
            value = candidate
    for candidate in (field(env,"HomeWatch__DataPath"),field(env,"HomeWatch:DataPath"),opts.get("homewatch:datapath")):
        if candidate is not None:
            value = candidate
    directory = cwd/value if value and value.strip() else output/"data"
    return (directory/"homewatch3.db").resolve()


def arguments_env(process):
    return [os.fsdecode(s) for s in (process/"environ").read_bytes().split(b"\0") if s]


def discover(pid, app, proc=Path("/proc")):
    output = (app/"bin/Release/net8.0").resolve()
    workers = []
    for process in descendants(proc,pid):
        try:
            cwd=(process/"cwd").resolve(strict=True)
            args=arguments(process)
            if is_worker(process,cwd,args,output):
                workers.append((process,cwd,args))
        except FileNotFoundError:
            continue
    if len(workers)!=1:
        raise ValueError("Expected one running HomeWatch worker from this checkout's Release build, found %d." % len(workers))
    process,cwd,args=workers[0]
    opened=set()
    for link in (process/"fd").iterdir():
        try:
            target=link.resolve(strict=True)
        except FileNotFoundError:
            continue
        if target.name=="homewatch3.db":
            opened.add(target)
    if len(opened)>1:
        raise ValueError("The HomeWatch worker has multiple core databases open.")
    path=opened.pop() if opened else configured(process,cwd,args,output)
    if not path.is_file():
        raise ValueError("The running worker's database path does not contain an existing homewatch3.db.")
    with closing(sqlite3.connect(path.as_uri()+"?mode=ro",uri=True)) as db:
        names={r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    if not {"Devices","TrafficEvents","Alerts"} <= names:
        raise ValueError("The database is missing the HomeWatch core tables.")
    return path.parent


if __name__=="__main__":
    try:
        print(discover(int(sys.argv[1]),Path(sys.argv[2])))
    except (OSError,sqlite3.Error,ValueError) as error:
        # A settings parse error must never echo a configuration value.
        sys.exit(str(error) if type(error) is ValueError else "HomeWatch database discovery failed (%s)." % type(error).__name__)
