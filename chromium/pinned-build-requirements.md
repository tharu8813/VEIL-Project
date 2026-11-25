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
