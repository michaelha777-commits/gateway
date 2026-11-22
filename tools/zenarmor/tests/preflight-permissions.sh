#!/bin/sh
# Run the actual --preflight dispatch with recorded permission outcomes.
set -eu
cd HomeWatch3
dir=$(mktemp -d)
trap 'rm -rf "$dir"' EXIT
export PATH="$dir:$PATH" trace="$dir/trace"
cat > "$dir/systemctl" <<'MOCK'
#!/bin/sh
[ "$*" = 'show -p MainPID homewatch3.service' ] && echo MainPID=123
MOCK
cat > "$dir/id" <<'MOCK'
#!/bin/sh
echo 1234
MOCK
cat > "$dir/python3" <<'MOCK'
#!/bin/sh
[ "$*" = '-B ../tools/zenarmor/nas_activation.py preflight' ] || exit 11
echo inspect >> "$trace"
exit "$outcome"
MOCK
cat > "$dir/sudo" <<'MOCK'
#!/bin/sh
[ "$*" = '-n python3 -B ../tools/zenarmor/nas_activation.py preflight' ] || exit 12
echo sudo >> "$trace"
outcome=0; export outcome
shift; exec "$@"
MOCK
chmod +x "$dir"/*
outcome=77; export outcome
sh ../tools/zenarmor/deploy-nas.sh --preflight
[ "$(cat "$trace")" = "$(printf 'inspect\nsudo\ninspect')" ]
: > "$trace"
outcome=1
if sh ../tools/zenarmor/deploy-nas.sh --preflight; then exit 1; fi
[ "$(cat "$trace")" = inspect ]

: > "$trace"
outcome=77
rc=0
sh ../tools/zenarmor/deploy-nas.sh --preflight-user || rc=$?
[ "$rc" -eq 77 ] && [ "$(cat "$trace")" = inspect ] || exit 1
