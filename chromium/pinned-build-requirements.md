# Pinned build requirements observation

PR #177 exact head 1df39032a6c1ec95c1d13714753fb5d9e7cd1ca9: all three checks PASS; main merge 1afadca392130ee729bc498d45e990ddeb97cd87. Hosted Windows reported 73 GiB free, below 100 GiB preflight minimum. Dedicated Windows machine remains unobserved.

Direct canonical file retrieval was unavailable in this development session; GitHub mirror queries returned HTTP 429. Do not guess exact pinned VS/SDK versions from main. New independent read-only CI observes exact candidate revision's root and docs/build tree identities, ordinary blobs for Windows build instructions, vs_toolchain.py and DEPS, validates actual bytes using existing bounded Gitiles blob check, records SHA256 and bounded line samples. Does not execute upstream code, evaluate DEPS, sync or compile. Tree IDs are observed from canonical TLS endpoint rather than independently pinned/recomputed root tree. Source samples require manual review; no approval or requirement compatibility inferred. Exact-head CI NOT RUN at commit. C0 IN PROGRESS / C1 preparation only. Existing WebView2, Rust, broker, installer and release gates unchanged.


## Follow-up: verify tree contents (2026-11-26)

Original PR head 3ddd82dccc3f8dc1691e57859795d77685e83bb2 was independently reconstructed in Linux and all three copied Git blob identities matched; 2/2 original tests PASS. Canonical source network access still unavailable (DNS failure in isolated Linux); Actions latest attempt 4/4 failed with no runnable steps, cause unconfirmed. No CI bypass.

Recompute canonical Git tree object SHA-1 from entry modes, UTF-8 names, Git directory ordering and object IDs. Reject malformed mode/type and mismatched content hash rather than trusting a reported tree ID alone. Local Python 3.13.5 / Git 2.47.3 offline tests 3/3 PASS, including independent git mktree --missing -z oracle and altered hash/mode/type refusal. Tested modified script blob 9c380f496ae74eb17a910664b15ba072c0b56efd and test blob 7cae0ffae9512805b697c2f2e65b804db28f3e0b. Root tree identity still comes from canonical TLS response, not independently reconstructed commit object; real Gitiles schema and actual upstream source remain unverified. New exact-head CI pending, do not merge before verified checks. No production or gate changes.


## Offline exact Git object observation (2026-11-26)

Latest head 9f6e3ed828f5235bb0e1fc4c474aa09edd5c355d still failed all four Actions checks before observable steps; no merge. New optional local path on an existing trusted Git repository: `python scripts/chromium/observe_local_build_requirements.py D:\veil-chromium\chromium\src`. Reads candidate commit directly, verifies commit/tree/blob raw Git hashes, ignores worktree and HEAD, disables replacement objects and lazy network fetch (Git 2.47 tested), restricts Git transport protocols; no sync or upstream code execution. Missing objects refuse. Trust the local Git binary, repository and host; not a hostile-repository sandbox, not proof of remote origin, full dependency graph or toolchain compatibility. Existing remote CI remains mandatory and unchanged in scope.

Independent Linux Python 3.13.5 / Git 2.47.3 tests 3/3 PASS in actual temporary Git repositories: pinned bytes after HEAD/worktree changes, missing/wrong commit/type refusal, symlink refusal and replace-ref ignored. Tested script blob 7dae9c2deb62c9883387d4d899f72def4d65c296 and test blob 875276e0dcc5493d5d9cab5b24bb58c33fc5ec5c. CI added test step; new exact-head CI NOT RUN. Actual Chromium objects and Windows build NOT RUN. No existing product changes.


## Integrated local and simulated remote observations (2026-11-26)

Head 6e6192f60045f623c8cc6a66173044c5b9b0af9c: all four checks failed within 1–3 seconds, CI remains blocked, no merge. Exact pinned upstream guide/toolchain source URLs remain inaccessible in this session. Rechecked local copies against the exact-head Git blob IDs; existing requirements and local-object tests 6/6 PASS in Linux Python 3.13.5 / Git 2.47.3. Promoted the separate Git fixture harness into a checked-in unittest suite, including cross-comparison of remote-simulated and local reports for one actual synthetic Git commit; 5/5 PASS. Tested new integration file Git blob 9c7c141ac52e4bbe05e249c95302d9a73c9c2d8b. Tests assert six expected fetches, identical common fields and SHA256 digests, no upstream execution, altered tree, substituted valid child tree, altered blob and redirect refusal. Added a CI step before actual remote observation, without weakening any existing checks. Total locally executed checked-in cases 11/11 PASS; real Chromium Gitiles schema/source, toolchain, build and runtime remain NOT RUN. No production source or gate changes. New exact-head CI NOT RUN at commit.


## Actual pinned mirror source observation (2026-11-26)

The public chromium/chromium GitHub mirror became accessible through its repository API. Observed exact candidate revision 1de57fa19722fa6d6841d4f596c394d015580656 (158.0.8127.89), NOT main. Canonical Gitiles and raw GitHub network access from the isolated Linux environment still failed (DNS); web fetch also unavailable. This is supplementary mirror evidence, not a replacement for mandatory canonical CI. Recomputed canonical Git tree SHA-1 from actual mirror tree entries for all five trees below and independently recomputed each file's Git blob SHA-1 from full UTF-8 content. All five tree and four blob identities matched their mirror-reported IDs and parent entries; both regular file modes 100644 and 100755 are supported. The API commit metadata binds the revision to the reported root tree; reconstructing the raw commit from normalized API fields did not reproduce its hash, so raw commit identity and canonical origin are NOT independently verified here.

| Tree | Recomputed SHA-1 |
| --- | --- |
| root | 6adf815f2b7215f9eca17083d05e99d86e7767c7 |
| docs | b11b75619c098b95d8bc2dcbfeb80e4b09c2db87 |
| build | ab3a6e3f8575df2843dd8605426b04b10b1e9818 |
| build/toolchain | 9375afdd9f9ac815fe49ec0a8b98ca965b4686de |
| build/toolchain/win | bf931e3b44817e88bd680b6afcb06cc6701614cf |

| File | Recomputed Git blob SHA-1 | Bytes | Mode |
| --- | --- | ---: | --- |
| docs/windows_build_instructions.md | 224fe5df98bcdb612e1682791957327ea06b5b8a | 24059 | 100644 |
| build/vs_toolchain.py | 869b7649288a2f41cb27a76e091bf22a6646bda0 | 28446 | 100755 |
| DEPS | b35a8409181b8861279ee465e6084c459ecc1c45 | 191941 | 100644 |
| build/toolchain/win/setup_toolchain.py | 34be00a458a4d52808c6df3b6267c530ec82183c | 15120 | 100644 |

Read-only requirement review of these four files:
- Guide: x86-64 Windows 10+, 8 GB RAM minimum (more than 16 GB recommended), 100 GB free on NTFS, VS 2026 >= 18.0.0 with NativeDesktop C++ and MFC/ATL; Windows SDK installer release 10.0.28000.2270 and debugging tools >= 10.0.26100.3323. Clang-cl uses Visual Studio headers/libraries/tools. GN and Siso are the build tools. VEIL retains its more conservative 100 GiB preflight and 300 GiB recommended capacity.
- Both toolchain Python files use SDK_VERSION 10.0.28000.0, distinct from the SDK installer release number. The primary script maps VS 2026 to VC145 and references toolchain package hash 3bfcb536c8. Older VS entries in the code do not establish supported builds of this revision. For external local toolchains, guide specifies DEPOT_TOOLS_WIN_TOOLCHAIN=0.
- DEPS lines 2257–2258 pin the in-tree third_party/depot_tools to b86fc981b72ab966f8260f73b4eca600b3b12677. This is distinct from the VEIL external bootstrap candidate 2960e876ad78502c616e27e1a89b9e01ca77ca7e; do not automatically replace either pin or infer compatibility. DEPS also declares Siso revision dfc32e4edca158cff2b64a1658ee3940f0116b3a (line 285), the in-tree depot_tools self-update disable hook and Windows toolchain update hook (lines 4266–4272, 4336–4339). No hook or DEPS execution occurred; full dependency graph and pre-DEPS/hook review remain required.

Source references:
- https://github.com/chromium/chromium/blob/1de57fa19722fa6d6841d4f596c394d015580656/docs/windows_build_instructions.md
- https://github.com/chromium/chromium/blob/1de57fa19722fa6d6841d4f596c394d015580656/build/vs_toolchain.py
- https://github.com/chromium/chromium/blob/1de57fa19722fa6d6841d4f596c394d015580656/DEPS
- https://github.com/chromium/chromium/blob/1de57fa19722fa6d6841d4f596c394d015580656/build/toolchain/win/setup_toolchain.py

Prior head 72ffcb80f96812843f6605c45215bc951641df73: four checks failed after retries, no CI bypass. This documentation-only update records actual source evidence; no code, candidate pin, policy, hook execution, production configuration, or release gate change. Prior local fixture suite 11/11 is historical, not a new exact-head run. Actual Windows readiness, dependency compatibility, canonical source CI, upstream build and sandbox remain NOT RUN / not established. C0 IN PROGRESS; C1 preparation only.


## Include the nested Windows setup toolchain source (2026-11-26)

Exact head c003ce440cd72a0e0373bf81422a82a486fc9524: all four checks failed; all four job log requests returned 404 BlobNotFound and no step details were available. Root cause is not established; do not bypass CI. Copilot review unavailable, no inline threads. Source review found the fourth mirror-observed file, build/toolchain/win/setup_toolchain.py, absent from the executable observation scope. Expand both canonical and local read-only observers to five parent-linked trees and four source files, requiring ordinary directory modes and matching nested tree identities. Extend fixtures for missing nested file and valid-but-substituted nested tree refusal. Actual canonical CI, new regression tests, dedicated Windows machine, dependency graph, build/runtime/sandbox NOT RUN at commit. No production code, pin or release gate change.
