#!/bin/sh
# Run from the NAS application checkout after pulling the homewatch4 branch.
set -eu
umask 077
unit=homewatch3.service
app=$(pwd -P)
# Older systemd versions on Synology return properties as Name=value.
unit_property() {
  property=$(systemctl show -p "$1" "$unit") || return 1
  case "$property" in
    "$1="*) printf '%s\n' "${property#*=}" ;;
    *) echo "Could not read systemd property $1." >&2; return 1 ;;
  esac
}
pid=$(unit_property MainPID)
case "$pid" in ''|*[!0-9]*) echo "Invalid service PID." >&2; exit 1 ;; esac
[ "$pid" -gt 1 ] || { echo "The running service is required to verify its active database." >&2; exit 1; }
# Inspect only open file links. Do not print the environment or authentication settings.
run_backup() {
  if [ -r "/proc/$pid/fd" ] && [ "$(id -u)" = "$(stat -c %u "/proc/$pid")" ]; then "$@"; else sudo -n "$@"; fi
}
data=$(run_backup python3 - "$pid" <<'PY'
from pathlib import Path
import sys
paths=set()
for link in (Path("/proc")/sys.argv[1]/"fd").iterdir():
    try: path=link.resolve(strict=True)
    except (FileNotFoundError, OSError): continue
    if path.name=="homewatch3.db": paths.add(path.parent)
if len(paths)!=1: sys.exit("Could not verify a single active HomeWatch database directory.")
print(paths.pop())
PY
)
exec_start=$(unit_property ExecStart)
case "$exec_start" in
  *"$app/bin/Release/net8.0/HomeWatch3.dll"*|*"$app/bin/Release/net8.0/HomeWatch3 "*) ;;
  *) echo "The unit uses a different build path. Inspect its ExecStart before deploying." >&2; exit 1 ;;
esac
backup="$data/backups/upgrade-$(date -u +%Y%m%dT%H%M%SZ)"
if ! run_backup python3 ../tools/zenarmor/backup.py "$data" "$backup"; then
  python3 ../tools/zenarmor/operations_notify.py "$data" backup-failure || true
  exit 1
fi
# Settings and service configuration can contain secrets. Keep them inside the private backup.
run_backup python3 - "$data" "$backup" "$app" <<'PY'
from pathlib import Path
import shutil, sys, subprocess
data, backup, app=map(Path,sys.argv[1:])
for directory,label,pattern in [(data,"settings","*.json"),(app,"config","appsettings*.json")]:
    dest=backup/label
    dest.mkdir(mode=0o700)
    for file in directory.glob(pattern):
        if file.is_file(): shutil.copy2(file,dest/file.name)
with (backup/"homewatch3.service.txt").open("wb") as out:
    subprocess.run(["systemctl","cat","homewatch3.service"],stdout=out,check=True)
PY
dotnet build -c Release --no-restore
sudo -n systemctl restart "$unit"
sleep 15
systemctl is-active "$unit"
status=$(curl -fsS http://127.0.0.1:8930/api/status)
printf '%s' "$status" | python3 -c 'import json,sys; s=json.load(sys.stdin); print(s["application"],s["version"]); sys.exit(0 if s["application"]=="HomeWatch 4" and s["version"]=="4.0.0-alpha.2" else 1)'
printf '\nRevision %s. Backup %s\n' "$(git rev-parse HEAD)" "$backup"
