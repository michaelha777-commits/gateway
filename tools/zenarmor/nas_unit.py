"""Verify that systemd's effective next start selects the currently inspected runtime."""
import os
from pathlib import Path
import re
import shlex
import subprocess

from nas_database import arguments, started_at


def unit_property(name):
    command=["systemctl","show","--all","-p",name,"homewatch3.service"]
    try:
        value=subprocess.check_output(command,stderr=subprocess.PIPE,env=dict(os.environ,LC_ALL="C"),universal_newlines=True).rstrip("\n")
    except subprocess.CalledProcessError as error:
        detail=(error.stderr or "").strip()
        if name=="EnvironmentFiles" and re.fullmatch(r"(?:Unknown|Unsupported) property [\"']?EnvironmentFiles[\"']?\.?",detail):
            return None
        raise ValueError("Cannot query effective unit property "+name) from None
    if name=="EnvironmentFiles" and not value:
        # DSM can return success and zero bytes for this unsupported property.
        return None
    key,sep,result=value.partition("=")
    if not sep or key!=name:
        raise ValueError("Cannot read effective unit property "+name)
    return result


def stable_file(path,since):
    if not path.is_absolute() or max(path.lstat().st_ctime,path.stat().st_ctime)>since:
        raise ValueError("Service launch configuration changed after startup or cannot be verified.")


def definition_environment_files(definitions):
    """Interpret only static EnvironmentFile directives in systemd load order."""
    entries=[]
    for definition in definitions:
        section=""
        for line in definition.read_text(encoding="utf-8-sig").splitlines():
            line=line.strip()
            if not line or line.startswith(("#",";")):
                continue
            if line.endswith(chr(92)) or line.startswith(".include"):
                raise ValueError("Cannot verify a continued or included legacy unit definition.")
            if line.startswith("["):
                section=line
                continue
            key,sep,value=line.partition("=")
            if section!="[Service]" or key.strip()!="EnvironmentFile":
                continue
            if not sep:
                raise ValueError("Malformed EnvironmentFile directive.")
            value=value.strip()
            if not value:
                entries.clear()
                continue
            # Accept literal paths and one quoted pathname. Escapes, specifiers
            # and globs require systemd expansion which the old API cannot report.
            if any(ch in value for ch in ("%","$","*","?","[","]",chr(92))):
                raise ValueError("Cannot verify a dynamic legacy EnvironmentFile path.")
            parts=shlex.split(value)
            if len(parts)!=1:
                raise ValueError("Cannot verify the legacy EnvironmentFile path.")
            path=parts[0]
            entries.append((Path(path[1:] if path.startswith("-") else path),path.startswith("-")))
    return entries


def verify_environment_files(reported,definitions,since):
    if reported is None:
        entries=definition_environment_files(definitions)
    else:
        tokens=shlex.split(reported)
        entries=[]
        while tokens:
            path=Path(tokens.pop(0))
            optional=False
            if tokens and tokens[0] in ("(ignore_errors=yes)","(ignore_errors=no)"):
                optional=tokens.pop(0)=="(ignore_errors=yes)"
            entries.append((path,optional))
    for path,optional in entries:
        if not path.is_absolute():
            raise ValueError("Cannot verify a non-absolute EnvironmentFile path.")
        try:
            path.stat()
        except FileNotFoundError:
            if not optional or path.is_symlink():
                raise ValueError("A required or linked EnvironmentFile is missing.")
            stable_file(path.parent,since)
        else:
            stable_file(path,since)


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


def inspect_project_launcher(plan,pid,args,cwd,proc):
    """Identify the current SDK-run worker. A fresh SDK build is a separate operation."""
    launcher=proc/str(pid)
    child=proc/str(plan["worker"])
    executable=Path(args[0])
    if (not executable.is_absolute() or executable.resolve(strict=True)!=(launcher/"exe").resolve(strict=True)
            or arguments(launcher)[1:]!=["run"] or (launcher/"cwd").resolve(strict=True)!=cwd):
        raise ValueError("The running SDK launcher differs from the effective unit.")
    if not any(line.split()==["PPid:",str(pid)] for line in (child/"status").read_text().splitlines()):
        raise ValueError("The observed worker is not the direct child of dotnet run.")
    if (child/"cwd").resolve(strict=True)!=cwd or list(cwd.glob("*.*proj"))!=[cwd/"HomeWatch3.csproj"]:
        raise ValueError("The SDK working directory does not identify one HomeWatch project.")
    expected=cwd/"bin/Debug/net8.0/HomeWatch3"
    if Path(plan["runtime"])!=expected.parent or (child/"exe").resolve(strict=True)!=expected.resolve(strict=True):
        raise ValueError("The running worker does not match the project's default Debug apphost.")
    manifest=cwd/"obj/Debug/net8.0/HomeWatch3.csproj.FileListAbsolute.txt"
    if str(expected.with_suffix(".dll")) not in manifest.read_text().splitlines():
        raise ValueError("The project build manifest does not identify the running assembly.")
    return "dotnet-run"


def verify(plan,pid,prop=unit_property,proc=Path("/proc"),preflight=False):
    since=started_at(proc/str(pid),proc)
    if prop("NeedDaemonReload").lower() not in ("no","false"):
        raise ValueError("The installed unit has pending configuration changes.")
    fragment=prop("FragmentPath")
    if not fragment:
        raise ValueError("Cannot establish the installed service unit.")
    definitions=[Path(fragment)]+[Path(name) for name in shlex.split(prop("DropInPaths"))]
    for definition in definitions:
        stable_file(definition,since)
    verify_environment_files(prop("EnvironmentFiles"),definitions,since)
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
    if Path(program).name=="dotnet" and args[1:]==["run"]:
        if not preflight:
            raise ValueError("dotnet run can rebuild on restart. Published-runtime activation is unsupported for this unit.")
        return inspect_project_launcher(plan,pid,args,cwd,proc)
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
