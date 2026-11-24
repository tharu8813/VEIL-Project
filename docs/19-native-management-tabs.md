# Native internal management tabs

The existing WPF management controls are hosted in the browser tab area, not in a separate visible top-level window or privileged HTML. The browser address bar uses an exact veil:// allowlist; WebView navigation to veil: is rejected and host objects/web messages remain disabled. Restored management tabs show a native launch action and never replay an operation or unlock a store automatically.

Routes: settings (including search, network, security, permissions and site-data), bookmarks, history, passwords, vault, tabs. Ctrl+J keeps the existing downloads progress tab; Ctrl+Shift+J opens the vault. Existing native unlock/re-authentication, site permission prompts, destructive confirmations, OS file pickers and authenticated text preview remain separate trusted dialogs. Password management cancels on tab selection loss; fill must still pass the existing origin, active-tab and navigation-epoch checks. Close/navigation/transfer cancel the management surface.

Pending: Windows interactive and VM regression, all routes and shortcuts, direct input and repeated invocation, multiwindow/restore, close/transfer, password and vault cleanup, protected shutdown, download progress, DPI/IME. CI compilation and core checks are not proof of interactive or security verification. No release gate changes.


## Stage 1 stabilization: internal tab session persistence

- Assign the internal route and title before inserting/selecting the tab, so a synchronous selection save cannot publish a blank route. Save background internal tabs explicitly and initialize their hidden native landing surfaces without performing authentication or privileged actions.
- Extend the desktop smoke suite to check persisted selected/background native routes and removal on close. Align the download cancellation assertion with the persistent download manager panel: the active list must disappear, not the manager controls.
- Source review only; Windows interactive smoke and protected-profile VM regression remain NOT RUN. No security gate is changed.

## Stage 1 stabilization: native page close and repeated invocation

- Cancel the native page's pending action before the tab-close animation yields. Reject activation/completion on a closing, transferred, or rerouted tab. Do not recreate landing controls on a closing tab or navigate it while closing.
- Guard download-manager tab creation against repeated invocations during WebView initialization. Clear both active-download list bindings when a window closes to release the shared collection from the retired UI.
- Add Windows smoke assertions for immediate native-page cancellation and repeated download-manager invocation. Source review only; interactive Windows and VM tests remain NOT RUN. No cryptographic, authentication, or network policy changes; all release gates remain unchanged.
