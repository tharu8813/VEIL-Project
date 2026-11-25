# C1 runtime and sandbox evidence (NOT RUN)

Scope: original Chromium, not VEIL. Run only on an isolated, dedicated Windows x64 machine, standard user, disposable profile, no real accounts/data, no production credentials. Network should be isolated by the test environment; the smoke runner does not enforce or verify that. Do not use sandbox-disabling switches or alter the system sandbox policy to make a test pass.

## Synthetic smoke

After an observed build, use Test-UpstreamRuntime.ps1 with the build's actual receipt.json and configuration. Default is read-only plan; -Execute is explicit. It verifies the receipt and executable hash, starts the upstream binary with a unique disposable profile, and checks a local HTML/JavaScript DOM marker with bounded wait and logs. It does not validate network, child cleanup, renderer tokens or site isolation. Preserve output and inspect any remaining processes; do not kill unrelated Chrome processes. A receipt is a local observation, not signed provenance.

## Interactive verification (manual, separate)

1. Match executable SHA, Chromium/depot_tools revisions, GN args and build receipt; record Windows build, user privilege level, configuration and test time. Use a distinct fresh profile. Launch the actual executable as a normal user with no sandbox or site-isolation overrides.
2. Serve controlled synthetic pages on two different sites in the isolated network; include a cross-site iframe. Record URLs and server logs. Confirm the main and cross-site frame process identities using Chromium's task manager / internal process information. Record actual renderer PIDs and process types, not only the browser PID. Verify the observed site separation with the selected revision's documented policy and repeat after navigation.
3. Use Windows Process Explorer or an equivalent trusted token-inspection tool to inspect each identified renderer and a browser-process control. Record PID, executable path, integrity level, restricted token state, job restrictions and AppContainer/capability state where applicable, using revision-specific sandbox expectations; do not require every utility process to use the same policy. Capture the actual command line and check for sandbox-disabling options. An absent flag or multiple PIDs alone is insufficient proof.
4. Record observations, raw evidence location/hash, expected-vs-observed outcome, and any unsupported measurement. An elevated inspector may be needed to read tokens; Chromium itself must remain non-elevated. If token access, PID association, site separation or expected policy cannot be established, mark the corresponding item NOT RUN/INCONCLUSIVE, not PASS.
5. Close the disposable browser and verify test process cleanup independently. Retain synthetic logs locally; do not upload machine-specific or real personal data. Do not mark C1 complete until required source, dependency/toolchain, Debug/Release build, runtime and sandbox evidence has been reviewed.

| Evidence | Current state |
|---|---|
| Synthetic DOM/JS execution | NOT RUN |
| Interactive browsing | NOT RUN |
| Actual renderer token inspection | NOT RUN |
| Cross-site frame isolation | NOT RUN |
| Process cleanup | NOT RUN |
| VEIL release gates | Unchanged, not approved |
