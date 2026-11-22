"""Verify that systemd's effective next start selects the currently inspected runtime."""
import os
from pathlib import Path
import re
import shlex
import subprocess

from nas_database import arguments, started_at


def unit_property(name):
    value=subprocess.check_output(["systemctl","show","--all","-p",name,"homewatch3.service"],universal_newlines=True).rstrip("\n")
    key,sep,result=value.partition("=")
    if not sep or key!=name:
        raise ValueError("Cannot read effective unit property "+name)
    return result


def stable_file(path,since):
    if not path.is_absolute() or max(path.lstat().st_ctime,path.stat().st_ctime)>since:
        raise ValueError("Service launch configuration changed after startup or cannot be verified.")


def command_target(command,cwd,since,depth=0):
    if not command or depth>2:
        raise ValueError("Cannot establish the launcher's HomeWatch entry point.")
    program=Path(command[0])
    if program.name=="dotnet":
        args=command[1:]
        if args[:1]==["exec"]:
            args=args[1:]
            while args and args[0] in ("--depsfile","--runtimeconfig","--additionalprobingpath","--additional-deps","--fx-version","--roll-forward"):
                args=args[2:]
        if args and Path(args[0]).name=="HomeWatch3.dll":
            return (cwd/args[0]).resolve(),args[1:],cwd
    if program.name=="HomeWatch3":
        return (cwd/program).resolve().parent/"HomeWatch3.dll",command[1:],cwd
    shell=program.name in ("sh","bash","dash")
    if shell and len(command)==3 and command[1]=="-c":
        lines=[command[2]]
    elif (shell and len(command)==2) or (len(command)==1 and program.is_absolute()):
        script=(cwd/(command[1] if shell else program)).resolve()
        stable_file(script,since)
        lines=script.read_text().splitlines()
    else:
        raise ValueError("The effective service launcher cannot be statically verified.")
    action=[]
    for line in lines:
        line=line.strip()
        if not line or line.startswith("#") or line in ("set -e","set -eu"): continue
        if action or any(c in line for c in "$`|&;<>*?[]{}()\\"):
            raise ValueError("The service launcher contains a dynamic command.")
        tokens=shlex.split(line)
        if tokens[:1]==["cd"] and len(tokens)==2:
            cwd=(cwd/tokens[1]).resolve(strict=True)
        else:
            action=tokens[1:] if tokens[:1]==["exec"] else tokens
    return command_target(action,cwd,since,depth+1)


def verify(plan,pid,prop=unit_property,proc=Path("/proc")):
    since=started_at(proc/str(pid),proc)
    if prop("NeedDaemonReload").lower() not in ("no","false"):
        raise ValueError("The installed unit has pending configuration changes.")
    fragment=prop("FragmentPath")
    if not fragment:
        raise ValueError("Cannot establish the installed service unit.")
    for file in [fragment]+shlex.split(prop("DropInPaths")):
        stable_file(Path(file),since)
    # systemctl renders EnvironmentFiles as path (ignore_errors=yes/no).
    envfiles=re.sub(r" \(ignore_errors=(?:yes|no)\)","",prop("EnvironmentFiles"))
    for file in shlex.split(envfiles):
        path=Path(file)
        stable_file(path if path.exists() else path.parent,since)
    raw=prop("ExecStart")
    match=re.fullmatch(r"\{\s*path=(.*?)\s*;\s*argv\[\]=(.*?)\s*;\s*ignore_errors=.*?\s*\}",raw)
    if not match or raw.count("argv[]=")!=1:
        raise ValueError("Cannot verify the effective systemd ExecStart command.")
    program,argv=match.groups()
    args=shlex.split(argv)
    running=arguments(proc/plan["worker"])
    # Some systemd versions print unquoted argument paths. Recover exact boundaries
    # only when they agree with the running command and unchanged unit files.
    if " ".join(running)==argv: args=running
    if not args: raise ValueError("Empty unit launch command.")
    args=[program]+args[1:]
    cwd=Path(prop("WorkingDirectory") or "/").resolve(strict=True)
    target,restart_args,workdir=command_target(args,cwd,since)
    if target != Path(plan["runtime"])/"HomeWatch3.dll" or workdir!=(proc/plan["worker"]/"cwd").resolve(strict=True):
        raise ValueError("The installed unit would restart into a different runtime or working directory.")
    from nas_database import worker_runtime
    observed=worker_runtime(proc/plan["worker"],workdir,running)
    if not observed or restart_args!=observed[1]:
        raise ValueError("The effective restart arguments differ from the running HomeWatch worker.")

if __name__=="__main__":
    import json,sys
    plan=json.loads(Path(sys.argv[1]).read_text())
    verify(plan,int(sys.argv[2]))
