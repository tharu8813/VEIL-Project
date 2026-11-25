# Pinned build requirements observation

PR #177 exact head 1df39032a6c1ec95c1d13714753fb5d9e7cd1ca9: all three checks PASS; main merge 1afadca392130ee729bc498d45e990ddeb97cd87. Hosted Windows reported 73 GiB free, below 100 GiB preflight minimum. Dedicated Windows machine remains unobserved.

Direct canonical file retrieval was unavailable in this development session; GitHub mirror queries returned HTTP 429. Do not guess exact pinned VS/SDK versions from main. New independent read-only CI observes exact candidate revision's root and docs/build tree identities, ordinary blobs for Windows build instructions, vs_toolchain.py and DEPS, validates actual bytes using existing bounded Gitiles blob check, records SHA256 and bounded line samples. Does not execute upstream code, evaluate DEPS, sync or compile. Tree IDs are observed from canonical TLS endpoint rather than independently pinned/recomputed root tree. Source samples require manual review; no approval or requirement compatibility inferred. Exact-head CI NOT RUN at commit. C0 IN PROGRESS / C1 preparation only. Existing WebView2, Rust, broker, installer and release gates unchanged.


## Follow-up: verify tree contents (2026-11-26)

Original PR head 3ddd82dccc3f8dc1691e57859795d77685e83bb2 was independently reconstructed in Linux and all three copied Git blob identities matched; 2/2 original tests PASS. Canonical source network access still unavailable (DNS failure in isolated Linux); Actions latest attempt 4/4 failed with no runnable steps, cause unconfirmed. No CI bypass.

Recompute canonical Git tree object SHA-1 from entry modes, UTF-8 names, Git directory ordering and object IDs. Reject malformed mode/type and mismatched content hash rather than trusting a reported tree ID alone. Local Python 3.13.5 / Git 2.47.3 offline tests 3/3 PASS, including independent git mktree --missing -z oracle and altered hash/mode/type refusal. Tested modified script blob 9c380f496ae74eb17a910664b15ba072c0b56efd and test blob 7cae0ffae9512805b697c2f2e65b804db28f3e0b. Root tree identity still comes from canonical TLS response, not independently reconstructed commit object; real Gitiles schema and actual upstream source remain unverified. New exact-head CI pending, do not merge before verified checks. No production or gate changes.
