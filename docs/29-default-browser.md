# 29. Windows browser registration (Stage 2-7)

Opt-in administrator command after a verified signed internal installation:

```powershell
.\scripts\package-maintenance.ps1 -RegisterBrowser -SignerThumbprint <PIN>
```

Registers only VEIL's HKLM RegisteredApplications/Capabilities and HTTP(S) ProgID, using the installed executable and quoted `--open-url "%1"` command. No UserChoice write, forced default, local file association or custom privileged protocol. Windows Settings > 기본 앱 is the only default selection surface. VEIL settings opens the registered-app page. Remove cleans matching VEIL registration, preserving profile/volume. Registration is not automatically enabled for developer builds.

External arguments are accepted only by the registered installed product path, strict absolute HTTP(S) without credentials, raw controls/whitespace/quotes/backslashes, maximum 8192 characters. Cold launch follows the normal protected unlock and engine policy, then opens a tab. Same Windows user/logon-session local mutex and current-user-only named pipe forward to an unlocked instance. The pipe allows only a bounded validated address or an empty activation, not flags or privileged actions. Busy/starting/ending instances refuse; never launch a second unlock or fall back to unprotected storage. Private windows are not used for external links.

Privacy limit: Windows shell and process command lines can expose the external URL to the OS, diagnostics or local processes before VEIL starts. Same-user processes can request ordinary web navigation; this is not an authentication boundary against compromised local accounts. Registration is optional and only for synthetic internal testing until release gates pass.

References: https://learn.microsoft.com/windows/win32/shell/default-programs and https://learn.microsoft.com/windows/apps/develop/launch/launch-default-apps-settings.

Static registration contract and synthetic URL-policy smoke added. Real Windows default selection, signed installation, cold/warm activation, pipe contention/lock/uninstall, command-line/physical traces and profile behavior NOT RUN. No release gate change.
