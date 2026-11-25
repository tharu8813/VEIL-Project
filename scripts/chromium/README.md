# Chromium Phase C0 diagnostics

Run from a clean VEIL checkout using Node.js:

    node scripts/chromium/inventory.mjs

The result contains tracked paths, line numbers and commit/dirty state; it is heuristic and is not a dependency graph or security validation. Do not publish machine-specific diagnostics or sensitive data.

On the Windows build machine (PowerShell):

    powershell -NoProfile -File scripts/chromium/Get-BuildReadiness.ps1 -Workspace D:\chromium

This is read-only and reports observed tools, VS/SDK and disk information. It does not install dependencies or establish revision compatibility. Confirm the exact selected revision's build documentation before installing or syncing. Build, runtime and sandbox remain NOT RUN.

See [the roadmap](../../docs/chromium-transition-roadmap.md). No Chromium pin, source sync or build is performed by these scripts.

## Upstream candidate lookup (network, read-only)

On a trusted machine with Git, run the exact tag resolver:

    node scripts/chromium/resolve-tag.mjs 158.0.8127.89

It queries only the official source remote; no checkout, hooks or build. It rejects absent or ambiguous refs and reports a candidate commit, not a release approval. Recheck the official Stable announcement first. Synthetic parser tests:

    node --test tests/chromium/resolve-tag.test.mjs

The existing Windows CI runs inventory, readiness observation and parser tests. It does not resolve the external tag or prove local-machine readiness.
