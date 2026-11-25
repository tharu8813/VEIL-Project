# C2 Windows identity and localized branding source survey

Status: preliminary source observations only, 2026-11-25. No product patch in this PR. C0 IN PROGRESS; C1 actual build NOT RUN; independent profile/branding runtime NOT RUN.

Pinned revision: 1de57fa19722fa6d6841d4f596c394d015580656.

## Confirmed source entry points

- chrome/install_static/install_modes.h (blob d2a8f592a71cca272e8d86c547803bf34a49921a, lines 38–48): the non-Google branding branch includes chromium_install_modes.h; it defines brand identity, product path and install modes. A product name metadata change alone does not replace these.
- chrome/install_static/install_util.cc (blob f7798f7da79cee7e0a5b1e2170921820ecd31707, lines 505–517): path construction uses company/product path plus install suffix. Lines 330–334 and 393 are additional identity consumers. Do not globally replace Chromium strings.
- chrome/app/chromium_strings.grd (blob dea90cda0be32f0d6c94ce7950a00970aded6299): separate distribution-localized resource definitions exist; exact message keys and translations must be checked before editing.
- PR #119's upstream CI (45636839542) verified only two BRANDING metadata keys against the original file. The Windows/Rust run was still in progress when last checked; no reliance on its merge.

Several upstream API calls were rate limited (429); chromium_install_modes.cc was not found. Do not infer its content or patch unseen files. The audit workflow requests the fixed candidate's relevant source files directly from canonical Gitiles, verifies known blob identities, reports line references and missing paths, and does not execute upstream code. Its results are pending CI.

## Required before first patched browser launch

- Select non-colliding Windows product/application/installer identity and profile root; inspect actual install constants, OS integration and user-data path consumers. Verify the legacy VEIL, Chromium and Chrome profiles remain untouched.
- Verify process singleton, default profile location, policy/registry roots, ProgIDs, shortcuts and crash/update endpoints; no Google-branded service or default-browser registration changes by assumption.
- Apply localized names only after GRIT resource IDs, translation dependencies and compilation behavior are reviewed. Preserve Chromium attribution.
- Require dedicated synthetic-data profile, normal user, no real credentials. Run two independently identified products simultaneously and collect file/process evidence. No claim of protected storage or release approval until the actual security gates are met.

This audit is not an isolation implementation, and an observed line or successful CI does not prove runtime behavior.
