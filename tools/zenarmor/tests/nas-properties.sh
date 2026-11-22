#!/bin/sh
# Exercise the deployment helper against the systemctl interface on older DSM.
set -eu
cd HomeWatch3
helper=../tools/zenarmor/deploy-nas.sh
sh -n "$helper"
sandbox=$(mktemp -d)
trap 'rm -rf "$sandbox"' EXIT
export PATH="$sandbox:$PATH" calls="$sandbox/calls"
# pid 0 and an unexpected ExecStart must both stop before build or restart.
cat > "$sandbox/systemctl" <<'MOCK'
#!/bin/sh
printf '%s\n' "$*" >> "$calls"
case "$*" in
  'show -p MainPID homewatch3.service') printf 'MainPID=%s\n' "$test_pid" ;;
  'show -p ExecStart homewatch3.service') echo 'ExecStart={ path=/other/bin ; argv[]=/other/bin arg=x=y ; }' ;;
  *) exit 2 ;;
esac
MOCK
cat > "$sandbox/python3" <<'MOCK'
#!/bin/sh
[ "$1" = ../tools/zenarmor/nas_database.py ] && [ "$2" = 12345 ] || exit 3
echo 'fixture worker rejected' >&2
exit 1
MOCK
cat > "$sandbox/sudo" <<'MOCK'
#!/bin/sh
[ "$1" = -n ] && [ "$2" = python3 ] || exit 3
shift
exec "$@"
MOCK
chmod +x "$sandbox/systemctl" "$sandbox/python3" "$sandbox/sudo"
export test_pid=0
if sh "$helper" >"$sandbox/output" 2>&1; then exit 1; fi
grep -Fq 'running service is required' "$sandbox/output"
test_pid=12345
if sh "$helper" >"$sandbox/output" 2>&1; then exit 1; fi
grep -Fq 'fixture worker rejected' "$sandbox/output"
grep -Fxq 'show -p MainPID homewatch3.service' "$calls"
