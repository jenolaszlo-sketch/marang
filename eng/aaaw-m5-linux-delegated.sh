#!/usr/bin/env bash
# Operator bootstrap for the existing lifecycle profile; no provider changes.
set -Eeuo pipefail
test "$(id -u)" = 0
test "$#" -ge 4
workspace=$(realpath "$1")
sdkroot=$(realpath "$2")
run_id=$3
shift 3
[[ "$run_id" =~ ^[a-zA-Z0-9-]+$ ]]
test -d "$workspace/.git"
test -x "$sdkroot/dotnet"
uid=$(stat -c %u "$workspace")
gid=$(stat -c %g "$workspace")
test "$uid" != 0
runner_home=$(getent passwd "$uid" | cut -d: -f6)
test -d "$runner_home"
test "$(stat -fc %T /sys/fs/cgroup)" = cgroup2fs
parent="/sys/fs/cgroup/aaaw-m5-$run_id"
test ! -e "$parent"
mkdir "$parent"
cleanup() {
  status=$?
  if [[ "$parent" != "/sys/fs/cgroup/aaaw-m5-$run_id" ]]; then exit 1; fi
  for _ in $(seq 1 50); do
    if grep -q '^populated 0$' "$parent/cgroup.events"; then break; fi
    sleep 0.2
  done
  cat "$parent/cgroup.events"
  if [[ -n "$(find "$parent" -mindepth 1 -maxdepth 1 -type d -print)" ]] \
      || ! grep -q '^populated 0$' "$parent/cgroup.events"; then
    echo 'M5 delegated lifecycle parent is not empty; retaining evidence and parent' >&2
    status=1
  else
    rmdir "$parent" || status=1
  fi
  exit "$status"
}
trap cleanup EXIT
test -f "$parent/cgroup.kill"
chown "$uid:$gid" "$parent" "$parent/cgroup.procs" "$parent/cgroup.subtree_control"
export GAGAMBA_LINUX_CGROUP="$parent"
export GAGAMBA_TEST_CGROUP_ROOT="$parent"
export GAGAMBA_QUALIFICATION=1
export QUALIFICATION_UID="$uid" QUALIFICATION_GID="$gid" QUALIFICATION_HOME="$runner_home"
export M5_WORKSPACE="$workspace" M5_SDKROOT="$sdkroot"
bash -c '
  echo $$ > "$GAGAMBA_LINUX_CGROUP/cgroup.procs"
  cd "$M5_WORKSPACE"
  exec setpriv --reuid="$QUALIFICATION_UID" --regid="$QUALIFICATION_GID" --clear-groups \
    env HOME="$QUALIFICATION_HOME" DOTNET_CLI_HOME="$QUALIFICATION_HOME" \
    DOTNET_ROOT="$M5_SDKROOT" PATH="$M5_SDKROOT:$PATH" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 MSBUILDDISABLENODEREUSE=1 \
    DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1 VSTEST_CONNECTION_TIMEOUT=180 "$@"
' m5-delegated "$@"
