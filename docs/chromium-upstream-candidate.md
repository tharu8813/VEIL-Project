# Chromium upstream candidate

- Observation: 2026-11-25 (KST).
- Official Chrome release announcement (2026-11-23): Windows/macOS 158.0.8127.88/.89: https://chromereleases.googleblog.com/2026/11/stable-channel-update-for-desktop_01734282054.html
- 159.0.8154.33/.34 was described as early Stable in the November release archive, not the general Stable baseline: https://chromereleases.googleblog.com/2026/11/
- Initial candidate: 158.0.8127.89; fallback candidate 158.0.8127.88 only after confirming exact source correspondence. This is **not a pin**. Official tag pages could not be retrieved in the planning environment.
- Run the exact-tag resolver on an internet-connected trusted machine and review the resulting commit against the official source. Record the chosen commit, toolchain and DEPS evidence only after verification. Recheck Stable and security patches immediately before pinning; do not freeze an outdated candidate for production.
- Tag resolution, source checkout, DEPS/toolchain compatibility, build, runtime, sandbox: NOT RUN.

## Follow-up observation (2026-11-25)

Official Chromium codesearch Gitiles mirror: https://chromium.googlesource.com/codesearch/chromium/src/+/refs/tags/158.0.8127.89 ; commit 1de57fa19722fa6d6841d4f596c394d015580656, tree 6adf815f2b7215f9eca17083d05e99d86e7767c7. Earlier unavailability referred to canonical tag URL. This is a reviewed development candidate, not canonical Git verification or a production pin. Recheck current Stable before production. See ../chromium/candidate.json and verify-candidate.mjs.

## Canonical identity verification (2026-11-25)

PR #115 CI https://github.com/tharu8813/VEIL-Project/actions/runs/45623627714 actually returned REMOTE_MATCH_ONLY for 1de57fa19722fa6d6841d4f596c394d015580656 and fetched/verified depot_tools commit 2960e876ad78502c616e27e1a89b9e01ca77ca7e. This is source identity evidence only. The manifest retains its conservative candidate status and rechecks at sync time. Full dependency checkout, toolchain compatibility and build are NOT RUN.
