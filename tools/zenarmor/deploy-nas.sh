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
# Read the service process tree with its existing access level.
run_backup() {
  if [ -r "/proc/$pid/fd" ] && [ "$(id -u)" = "$(stat -c %u "/proc/$pid")" ]; then "$@"; else sudo -n "$@"; fi
}
if [ "${1:-}" = "--preflight-user" ] && [ "$#" -eq 1 ]; then
  exec python3 -B ../tools/zenarmor/nas_activation.py preflight
fi
if [ "${1:-}" = "--preflight" ]; then
  # Directory readability does not establish access to a worker's fd links or environ.
  python3 -B ../tools/zenarmor/nas_activation.py preflight && exit 0
  rc=$?
  if [ "$rc" -eq 77 ] && [ "$(id -u)" != 0 ]; then
    echo "Retrying the read-only preflight with cached sudo authorization." >&2
    exec sudo -n python3 -B ../tools/zenarmor/nas_activation.py preflight
  fi
  exit "$rc"
fi
[ "$#" -eq 0 ] || { echo "Unknown deployment option." >&2; exit 2; }
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
plan="$scratch/installation.json"
run_backup python3 ../tools/zenarmor/nas_database.py "$pid" >"$plan"
data=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["data"])' "$plan")
verify_installation() {
  current=$(unit_property MainPID)
  [ "$current" = "$pid" ] || { echo "The service PID changed during the update." >&2; return 1; }
  run_backup python3 ../tools/zenarmor/nas_database.py "$current" >"$scratch/current.json" && cmp -s "$plan" "$scratch/current.json"
}
verify_installation || { echo "The worker or database changed before backup." >&2; exit 1; }
backup="$data/backups/upgrade-$(date -u +%Y%m%dT%H%M%SZ)"
if ! run_backup python3 ../tools/zenarmor/backup.py "$data" "$backup"; then
  python3 ../tools/zenarmor/operations_notify.py "$data" backup-failure || true
  exit 1
fi
# Settings and service configuration can contain secrets. Keep them inside the private backup.
run_backup python3 - "$data" "$backup" "$plan" <<'PY'
from pathlib import Path
import json, shutil, sys, subprocess
data, backup, planfile=map(Path,sys.argv[1:])
plan=json.loads(planfile.read_text())
for directory,label,pattern in [(data,"settings","*.json"),(Path(plan["content"]),"config","appsettings*.json"),(Path(plan["runtime"]),"runtime-config","appsettings*.json")]:
    dest=backup/label
    dest.mkdir(mode=0o700)
    for file in directory.glob(pattern):
        if file.is_file(): shutil.copy2(file,dest/file.name)
with (backup/"homewatch3.service.txt").open("wb") as out:
    subprocess.run(["systemctl","cat","homewatch3.service"],stdout=out,check=True)
PY
python3 ../tools/zenarmor/nas_runtime.py publish "$plan" "$scratch/publish"
verify_installation || { echo "Installed worker or database changed since backup." >&2; exit 1; }
printf 'Verified database backup %s\n' "$backup"
run_backup python3 -B ../tools/zenarmor/nas_activation.py activate "$plan" "$scratch/publish" "$backup"
printf 'Verified HomeWatch 4 4.0.0-alpha.2. Revision %s. Backup %s\n' "$(git rev-parse HEAD)" "$backup"
