#!/usr/bin/env bash
# Runs every veil-security test (integration targets, unit tests, doctests) one at a time, serially,
# and reports each test's wall time (console and GitHub step summary). Fails if any test fails or if
# fewer tests than expected were found, so an empty run can never pass.
#   bash scripts/rust-timed-tests.sh            (extra cargo flags as arguments, e.g. --offline)
set -euo pipefail
manifest=crates/veil-security/Cargo.toml
summary=${GITHUB_STEP_SUMMARY:-/dev/null}
min_tests=13
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cargo test --locked --manifest-path "$manifest" --no-run --message-format=json "$@" > "$work/build.json"
mapfile -t exes < <(node -e '
    const fs = require("fs");
    for (const line of fs.readFileSync(process.argv[1], "utf8").split(/\r?\n/)) {
        if (!line.startsWith("{")) continue;
        const m = JSON.parse(line);
        if (m.reason === "compiler-artifact" && m.executable && m.profile.test) console.log(m.executable);
    }' "$work/build.json" | tr -d '\r')
if [ "${#exes[@]}" -lt 5 ]; then echo "expected at least 5 test executables, found ${#exes[@]}" >&2; exit 1; fi

failed=0
total_ms=0
count=0
seconds() { awk -v ms="$1" 'BEGIN { printf "%.2f", ms / 1000 }'; }
{ echo '| Test | Seconds | Result |'; echo '|---|---:|---|'; } >> "$summary"
for exe in "${exes[@]}"; do
    target=$(basename "$exe" | sed -E 's/-[0-9a-f]+(\.exe)?$//')
    mapfile -t tests < <("$exe" --list --format terse | sed -n 's/: test\r\{0,1\}$//p')
    for test in "${tests[@]}"; do
        start=$(date +%s%N)
        if (cd crates/veil-security && "$exe" --exact "$test" --test-threads=1 --quiet) > "$work/test.log" 2>&1; then
            result=ok
        else
            result=FAIL
            failed=1
            cat "$work/test.log"
        fi
        ms=$(( ($(date +%s%N) - start) / 1000000 ))
        total_ms=$((total_ms + ms))
        count=$((count + 1))
        printf '%8ss  %-4s %s::%s\n' "$(seconds "$ms")" "$result" "$target" "$test"
        echo "| \`$target::$test\` | $(seconds "$ms") | $result |" >> "$summary"
    done
done
echo "| **total ($count tests)** | $(seconds "$total_ms") | |" >> "$summary"
echo "total: $count tests, $(seconds "$total_ms")s"
if [ "$count" -lt "$min_tests" ]; then echo "expected at least $min_tests tests, ran $count" >&2; failed=1; fi

# Doctests are not separate executables.
cargo test --locked --manifest-path "$manifest" --doc "$@"
exit $failed
