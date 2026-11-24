# Vault setup wizard regression

Root cause found by source inspection: the first and final setup steps added the same WPF TextBlock to different StackPanel instances. Replacing ContentControl.Content does not detach a child from its original StackPanel. The final transition therefore throws when creating the confirmation page. The validation message now has one permanent parent outside the changing page content; the entire page can scroll.

The browser also defers capture out of WebView2 DownloadStarting before a vault modal can appear, keeps the engine request cancelled, provides explicit vault-wait/transfer-wait status, checks shutdown before opening, and reports a returned creation failure to the user. No cryptography, recovery confirmation, download protocol, or fallback policy is relaxed.

Added a Windows smoke path using synthetic credentials and the actual wizard button events: reject empty/mismatched passwords, advance to recovery display, advance to recovery confirmation, reject incorrect code, accept normalized correct code and close. Test is part of the existing write-stage desktop smoke, not a substitute for a manual first-download test. No user credentials are logged or persisted by this wizard smoke. Existing protected-profile and vault tests remain required. 
