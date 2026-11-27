#!/usr/bin/env bash
# Runs the complete backend test suite in partitions, one test host process at a time (2026-11-27).
#
# Why: in one process the test host's memory grows by gigabytes within minutes (4.4 GB after one minute at 8 threads on a 32 GB / 24-core
# machine); the machine ran out of memory twice and CI_DEF_3_the_connect_callback_lets_an_allowed_host_through timed out (a 20 s timer firing
# after 39–48 s). Partitioned, every partition peaked at 0.6–3.9 GB, all 7,646 tests ran and none failed. See
# AIAssisted/docs/birknext-backend-regression-stability-2026-11-26.md.
#
# The partitions are built from the test project's folders, so a new test folder is always included: everything outside Services, each group
# of Services subfolders, and the classes directly in the Services namespace. Their totals must add up to the whole suite.
#
# Usage: ./test-backend-partitioned.sh [maxParallelThreads=4]   (after building BirkNext.Api.Tests)
# Each partition line ends with its test host's peak memory (Windows; «?» elsewhere). Memory profile: AIAssisted/docs/birknext-backend-regression-stability-2026-11-26.md.
set -u
threads="${1:-4}"
here="$(cd "$(dirname "$0")" && pwd)"
tests="$here/BirkNext.Api.Tests"
bin="$tests/bin/Debug/net8.0"
ns="BirkNext.Api.Tests.Services"
[ -f "$bin/BirkNext.Api.Tests.dll" ] || { echo "Build BirkNext.Api.Tests first (bin/Debug/net8.0)." >&2; exit 2; }

mapfile -t subs < <(cd "$tests/Services" && for d in */; do d="${d%/}"; grep -rqs "\[Fact\]\|\[Theory\]" --include=*.cs "$d" && echo "$d"; done)
partitions=("outside-services|FullyQualifiedName!~$ns.")
group=""; count=0; index=1
for d in "${subs[@]}"; do
  group="$group|FullyQualifiedName~$ns.$d."; count=$((count + 1))
  if [ "$count" -ge 8 ]; then partitions+=("services-$index|${group#|}"); index=$((index + 1)); group=""; count=0; fi
done
[ -n "$group" ] && partitions+=("services-$index|${group#|}")
root="FullyQualifiedName~$ns."; for d in "${subs[@]}"; do root="$root&FullyQualifiedName!~$ns.$d."; done
partitions+=("services-root|$root")

# Peak memory of the partition's test host (Windows only, read-only on processes): PowerShell samples every 2 s until the partition ends.
sampler=""
peak_start() {
  sampler=""; command -v powershell >/dev/null 2>&1 || return 0
  local dir; dir="$(cygpath -w /tmp 2>/dev/null || echo /tmp)"
  rm -f "/tmp/birknext-peak-$1" "/tmp/birknext-peak-$1.stop"
  powershell -NoProfile -Command "\$s=Get-Date; \$m=0; while (-not (Test-Path '$dir\birknext-peak-$1.stop')) { Get-Process testhost -ErrorAction SilentlyContinue | Where-Object { \$_.StartTime -ge \$s } | ForEach-Object { if (\$_.PeakWorkingSet64 -gt \$m) { \$m = \$_.PeakWorkingSet64 } }; Start-Sleep -Seconds 2 }; [int](\$m/1MB) | Set-Content -Encoding ascii '$dir\birknext-peak-$1'" &
  sampler=$!
}
peak_stop() {
  peak="?"; [ -n "$sampler" ] || return 0
  touch "/tmp/birknext-peak-$1.stop"; wait "$sampler"
  peak=$(tr -cd '0-9' < "/tmp/birknext-peak-$1" 2>/dev/null); peak="${peak:-?}"
}

failed=0; total=0
cd "$bin"
for p in "${partitions[@]}"; do
  name="${p%%|*}"; filter="${p#*|}"
  peak_start "$name"
  line=$(dotnet test BirkNext.Api.Tests.dll --filter "$filter" -- xUnit.MaxParallelThreads="$threads" 2>&1 | tee "/tmp/birknext-backend-$name.log" | grep -E "Passed!|Failed!|No test matches" | tail -1)
  peak_stop "$name"
  echo "$name: ${line:-NO SUMMARY} · peak test host ${peak} MB"
  grep -E "^\s+Failed " "/tmp/birknext-backend-$name.log" || true
  case "$line" in *"Failed!"*|"") failed=1;; esac
  t=$(echo "$line" | sed -nE 's/.*Total: *([0-9]+).*/\1/p'); total=$((total + ${t:-0}))
done
echo "Total tests run: $total"
exit $failed
