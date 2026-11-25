# Chromium patch series

The ordered series.json is tied to the development candidate's exact upstream commit. The first patch changes only the full/short product name metadata. It does not make a usable or independently installed VEIL browser. Store UTF-8 unified Git diffs named 0001-short-name.patch, etc., with SHA-256 over the exact committed bytes and a nonempty purpose. Set Git attributes to preserve LF for patch bytes. Never add a patch that disables sandbox, certificate checks, site isolation or protection failure handling.

Run `node scripts/chromium/patch-series.mjs` to validate the manifest and hashes. Once the pinned upstream checkout exists, run `node scripts/chromium/patch-series.mjs --check D:\veil-chromium\chromium\src`. The checker validates identity and tracked cleanliness, applies the ordered series to an isolated temporary Git index and object store, and reports the resulting tree identity. No real checkout/index changes, no build and no patch installation. Use a trusted dedicated checkout; this is not a hardened sandbox for untrusted Git repositories. A tree hash in the temporary store is an observation, not a published Git object.

The original C1 upstream build runner continues refusing modified sources. Actual patch materialization, separate patched build receipt, upstream rebase and real Chromium compatibility are later work. Synthetic tests exercise ordering, conflicts, hashes and original-checkout preservation. Existing security gates are unchanged.

## First product-name patch

Source: https://github.com/chromium/chromium/blob/1de57fa19722fa6d6841d4f596c394d015580656/chrome/app/theme/chromium/BRANDING (blob f8363d5b294fe210ebfdd18374e95b54d6ff5c17). Exact file was inspected at the pinned SHA and its Git blob hash independently computed. CI fetches the single file at the canonical upstream SHA, checks its blob identity and patch SHA256, applies to a temporary fixture, and verifies only the two intended metadata keys changed. No Chromium checkout, compilation, actual UI, profile isolation, or runtime verification. The fixture check is not a full series check against the real tree.

Do not run a patched browser yet. Localized strings, icons, Windows install identifiers, default profile path, singleton/registry boundaries, update/crash destinations and security gates remain separate work. Preserve upstream copyright. Existing WebView2 distribution is unchanged.

## Second UI product-name patch

PR #121 [CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45638116741) generated and applied the exact GRIT fixture diff (blob dea90cda0be32f0d6c94ce7950a00970aded6299, patch SHA256 91d3c60a1ad8dd2379aa3fd15935807ce217f049782b9b73fdc6f03253f2718d). Register 0002-veil-ui-product-name.patch verbatim from that evidence. CI now requires exact byte equality with regenerated patch and its manifest digest. Only the two non-translatable names change. Actual GRIT compilation and UI rendering are NOT RUN; localized text containing Chromium is not globally replaced.

## Source-only materialization (development, NOT RUN against Chromium)

Use two independently acquired clean pinned Git checkouts in separate directories, both with the canonical origin and no untracked files, no shared Git metadata or object alternates. This intentionally targets source-only copies before dependency sync; a gclient workspace containing untracked dependencies may be refused. Do not point it at an existing personal browser profile, legacy VEIL repository, or dependency-populated build tree. Both copies must be trusted, dedicated, and non-elevated. Avoid junctions and links; no claim of protection against hostile Git configuration or concurrent filesystem changes.

`node scripts/chromium/materialize-patches.mjs D:\\veil-upstream\\src D:\\veil-patched\\src` checks both pinned trees and returns a source-only plan. Append `--execute` explicitly to stage/apply the registered patches on the second checkout only. A matching final tree is source observation, not compilation or profile isolation. No commit, hooks, build, install, launch or automatic recovery; if interrupted preserve the destination and inspect it. The existing upstream build runner must still refuse this modified tree. A separate patched build runner and dependency verification remain future work.

Only 0001 and 0002 are registered; nine-field Windows identity candidate remains unregistered. No patched browser may be launched yet. Synthetic tests cover plan, actual target-only patch writes, conflict rejection and root/dirty checks; actual Chromium materialization NOT RUN.
