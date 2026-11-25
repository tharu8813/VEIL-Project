# Chromium upstream candidate

- Observation: 2026-11-25 (KST).
- Official Chrome release announcement (2026-11-23): Windows/macOS 158.0.8127.88/.89: https://chromereleases.googleblog.com/2026/11/stable-channel-update-for-desktop_01734282054.html
- 159.0.8154.33/.34 was described as early Stable in the November release archive, not the general Stable baseline: https://chromereleases.googleblog.com/2026/11/
- Initial candidate: 158.0.8127.89; fallback candidate 158.0.8127.88 only after confirming exact source correspondence. This is **not a pin**. Official tag pages could not be retrieved in the planning environment.
- Run the exact-tag resolver on an internet-connected trusted machine and review the resulting commit against the official source. Record the chosen commit, toolchain and DEPS evidence only after verification. Recheck Stable and security patches immediately before pinning; do not freeze an outdated candidate for production.
- Tag resolution, source checkout, DEPS/toolchain compatibility, build, runtime, sandbox: NOT RUN.
