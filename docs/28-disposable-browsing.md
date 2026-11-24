# 28. Disposable browsing (Stage 2-6)

Ctrl+Shift+N or the menu starts a fresh private session, with a random profile name and verified WebView2 InPrivate controller options before navigation. All its tabs share that named session; a new private window gets a fresh session. The regular engine environment and applied network/SmartScreen policy are reused; exceptions to HTTPS/threat warnings are session-local. Engine termination and protected-session shutdown remain shared.

Private session data is memory-only, no state file, no saved history, closed-tab stack, bookmarks, local suggestions or persistent manager access. Permissions are denied, downloads are cancelled without vault replay or shared result records. Download scratch remains inside protected profile root. Tab tear-off/merge is disabled in private mode; no transfer between sessions.

Closing the last private tab closes its window, requests deletion only for its verified random private profile, then disposes views. WebView2 can create profile folders even in InPrivate mode; deletion is engine-managed and may be delayed or fail. Scratch and OS artifacts may remain until normal profile cleanup; crash recovery, physical writes and cleanup have NOT RUN. No claim of anonymity, forensic erasure, or RAM-only operation. See Microsoft WebView2 multi-profile support and CoreWebView2Profile.Delete documentation.

Synthetic desktop smoke adds normal/private profile, cookie/localStorage separation, no persisted history, manager denial and close assertions. Actual Windows/VM execution NOT RUN; release gates unchanged.
