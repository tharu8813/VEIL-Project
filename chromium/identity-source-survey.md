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

## Canonical audit result and next candidate (2026-11-25)

PR #120 [source audit CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45637262859) observed all eight files with no missing paths. PR #119 metadata fixture and both legacy Windows/Rust jobs passed. Both PRs merged. Observations:

- chromium_install_modes.h (a6d969df56d13d94eaa968934f909d6fd23b5f2f): product path line 24, Safe Browsing name line 27, empty Google Update app GUID lines 55–56, application names and HTML/PDF ProgIDs lines 57–65. Further COM GUID/registry/installer review required before changes. Do not change Safe Browsing identifier by assumption.
- user_data_dir.cc (6b231841bf68c1abd8087bb1c0ea70f3ef3c9ece): default User Data at line 102; command-line and policy inputs at lines 63–70 and 113–120. Merely changing the default path cannot guarantee isolation.
- chrome_paths_win.cc (9ab342785936f8e9c8f64ac5088dedcf276058ce) lines 42–53: default path uses install subdirectory.
- process_singleton_win.cc (7671e78815ae39310f4916abbdb4c5a06a65fc08) lines 451–481: startup mutex and per-user-data-directory window lookup. Separate identity requires actual concurrent-run checks.
- chromium_strings.grd: product name IDs at lines 288–299 are non-translatable, with separate Chrome for Testing branch. Korean XTB contains 576 matching lines, not 576 confirmed product names; do not globally substitute.

A read-only CI candidate generator now retrieves and verifies the exact GRIT blob, replaces only the two plain Chromium names, preserves Chrome for Testing and other messages, and applies the generated diff to a temporary fixture. The resulting diff and hash are printed for review. It is NOT registered in the patch series until reviewed; actual GRIT compilation, translated messages, profile isolation, and runtime remain NOT RUN. Public GitHub file requests were again rate limited, and canonical Gitiles was not accessible from the chat web tool, so full-file evidence is deferred to this CI.

## UI patch registration and development identity candidate

PR #121 [CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45638116741) verified the exact GRIT diff and SHA256, and merged at b8249315084da182aa9b0ff006e4750a4542f385. The verified diff is now registered as patch 0002, pending new CI. The next review-only identity generator targets seven observed Windows constants in the pinned header and proposes a deliberately separate development namespace (VEIL Chromium Dev / VEILChromiumDev / VEILDevHTM / VEILDevPDF), not a final release brand. It retains Safe Browsing and Google Update/COM identities. This is not a complete separation: COM, registry policy, singleton, command-line user-data override, paths, installer and launch isolation must still be reviewed. Do not register this identity candidate or launch a patched browser on the basis of fixture results alone.

## Development identity review and collision audit (2026-11-25)

PR #122 [identity candidate CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45639070721) verified a seven-field header patch against blob a6d969df56d13d94eaa968934f909d6fd23b5f2f; candidate SHA256 c5a0cfe4e1bc18b6bb0ab86945984a45a5ff8a6c0e8e7c04ba0259e3d6f6c165. [UI CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45639070383) verified registered patch 0002. Both checks and existing CI passed, merged at 64023d7e4fb5f40140733661d9eb542e12032d8b.

Review found unchanged direct_launch_url_scheme = chromium and an upstream Active Setup GUID in the exact diff. The seven-field candidate remains deliberately unregistered: changing only product directory and ProgIDs does not prevent installer, COM, policy or protocol collisions. Expanded read-only audit pins the five previously observed additional blob identities and inspects additional installer, registry and path-policy source candidates, explicitly reporting missing files. No new product patch is added. Results pending CI.

Acceptance before registration of a complete development identity patch: inspect every install constant and consumer, distinguish class IDs from shared interface IIDs, determine which identities must be unique, preserve Safe Browsing and avoid Google Update changes, and verify policy overrides and profile launch restrictions separately. A development namespace is not proof of encrypted profile storage. No installation or patched-browser launch authorized.

## Expanded audit failure and retry fix

PR #123 [initial audit CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45639952727) observed 8/15 files. Six requests returned HTTP 503, including three required known blobs; the optional chrome/browser/policy/policy_path_parser.cc returned 404. The required-source check correctly failed. The observed pinned identity header additionally exposes toast activator/elevator/tracing service CLSIDs, elevator/tracing IIDs and old tracing IID compatibility. Do not assign replacement GUIDs without reviewing their class registrations, IDL/typelib consumers and compatibility semantics. Three additional source blobs observed: install_modes.cc bdd057bf4a266f30a3d9d93eef1ebcc1853bcf83, install_static/policy_path_parser.cc 2befa3a77fc70d92733c87abdede88fa30230319, installer/util/install_util.cc c9b812b50eb9594318d0ab81b215d2f1730f9635. The optional 404 is retained as explicit unknown rather than asserted evidence.

Retry correction: maximum three attempts per path, 12-second request timeout, 1/2-second delays for transient HTTP or connection/timeout failures only. Permanent 404, invalid blob identity, decoding and other non-transient failures are not retried. Required evidence still fails closed on exhaustion; optional missing sources remain visible in report. Four synthetic retry/error regressions added. New CI not yet observed. No identity patch registration, installation, build or runtime approval.

## Expanded audit result and nine-field review candidate (2026-11-26)

PR #123 corrected [audit CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45640276794): 7/7 parser/retry tests PASS; 14/15 paths observed, all eight required blobs matched, optional chrome/browser/policy/policy_path_parser.cc 404. Existing CI and upstream identity PASS; merged at 89855c4d4976bed50f78fff7ef02e973921f61f5. install_constants.h (5791763ae4833c331c3e70a5d2768dfb580aeffb) distinguishes Active Setup GUID (114–117), toast/elevator/tracing CLSIDs (119–137), elevator IID/TypeLib and old-IID compatibility (127–134). install_modes.cc (bdd057bf4a266f30a3d9d93eef1ebcc1853bcf83) line 29 constructs Software registry path using product name. shell_util.cc (3710cb78f57047096c2f2651739a089f060cd6f8) lines 129–139 derives browser/PDF ProgIDs from install constants, 216–217 uses capabilities key. installer/util/install_util.cc (c9b812b50eb9594318d0ab81b215d2f1730f9635) lines 127–128 uses GetActiveSetupPath. user_data_dir.cc lines 83–100 uses LOCALAPPDATA with temporary-path fallback; policy and command-line overrides remain relevant.

Extend the review-only seven-field candidate to nine fields: direct launch URL scheme veil-chromium-dev and Active Setup GUID {C0D85D7A-6F11-49E9-A4B1-2D8F6C11B7E3}. These are proposed development-only values, not a reserved or verified OS namespace. They must not be registered on a real machine; URL handling and installer consumers need runtime tests. Preserve upstream COM CLSIDs/IIDs and old IID lists for now; do not claim isolation. Candidate generator verifies exact source blob and unique expected old values, and tests assert unchanged Safe Browsing, empty app GUID and representative COM fields. Actual source fixture result is pending CI. Patch series stays at two registered entries.

Development profile acceptance additionally requires dedicated explicitly selected directory, refusal of existing legacy/Chromium profile and unsafe links, evaluation of policy overrides and temporary fallback, separate browser singleton and no access to production data. These requirements are not implemented yet. No patched browser launch or install approval.

## COM and profile-path boundary follow-up (2026-11-26)

PR #123 corrected audit job 139296687330 reported all 14 previously observed blob identities; the six formerly optional observed files are now required exact matches. The remaining original 404 and ten new paths are discovery hypotheses, not verified files. New survey targets startup path selection, path service, registry policy, notification activator and elevation IDL/class/installer consumers. Missing discovery files remain explicit NOT_OBSERVED; required source failures still fail closed. CI timeout increased for bounded retries across the larger survey. New CI NOT RUN.

Profile isolation design constraints: select an explicit dedicated development root before Chromium profile initialization; inspect both HKLM/HKCU policy precedence and command-line handling; do not permit a temporary fallback or legacy/Chrome/Chromium profile reuse; require path identity and reparse-point checks in the actual Windows integration. The browser process and children must agree on the resolved profile root, and singleton ownership must be tested with concurrent independent products. Reject unapproved launch/installation; these are design requirements only, no implementation or runtime evidence.

COM audit must distinguish unique registration CLSIDs from IDL interfaces/typelibs and compatibility IIDs. Do not globally replace GUIDs or register an unverified candidate. The nine-field development identity candidate remains unregistered; two patches only. Actual OS registration, installer, runtime, sandbox and profile isolation remain NOT RUN.
