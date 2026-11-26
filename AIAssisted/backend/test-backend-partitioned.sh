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

failed=0; total=0
cd "$bin"
for p in "${partitions[@]}"; do
  name="${p%%|*}"; filter="${p#*|}"
  line=$(dotnet test BirkNext.Api.Tests.dll --filter "$filter" -- xUnit.MaxParallelThreads="$threads" 2>&1 | tee "/tmp/birknext-backend-$name.log" | grep -E "Passed!|Failed!|No test matches" | tail -1)
  echo "$name: ${line:-NO SUMMARY}"
  grep -E "^\s+Failed " "/tmp/birknext-backend-$name.log" || true
  case "$line" in *"Failed!"*|"") failed=1;; esac
  t=$(echo "$line" | sed -nE 's/.*Total: *([0-9]+).*/\1/p'); total=$((total + ${t:-0}))
done
echo "Total tests run: $total"
exit $failed
