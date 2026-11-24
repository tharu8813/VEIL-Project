# Native internal management tabs

The existing WPF management controls are hosted in the browser tab area, not in a separate visible top-level window or privileged HTML. The browser address bar uses an exact veil:// allowlist; WebView navigation to veil: is rejected and host objects/web messages remain disabled. Restored management tabs show a native launch action and never replay an operation or unlock a store automatically.

Routes: settings (including search, network, security, permissions and site-data), bookmarks, history, passwords, vault, tabs. Ctrl+J keeps the existing downloads progress tab; Ctrl+Shift+J opens the vault. Existing native unlock/re-authentication, site permission prompts, destructive confirmations, OS file pickers and authenticated text preview remain separate trusted dialogs. Password management cancels on tab selection loss; fill must still pass the existing origin, active-tab and navigation-epoch checks. Close/navigation/transfer cancel the management surface.

Pending: Windows interactive and VM regression, all routes and shortcuts, direct input and repeated invocation, multiwindow/restore, close/transfer, password and vault cleanup, protected shutdown, download progress, DPI/IME. CI compilation and core checks are not proof of interactive or security verification. No release gate changes.
